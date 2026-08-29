using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

public sealed record EpubReaderNavigationItem(string Title, string Target, int ChapterIndex);

public sealed record EpubReaderDocument(
    string RootPath,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<EpubReaderNavigationItem> Navigation,
    IReadOnlyList<string> ChapterTitles);

public sealed class EpubReaderPreparationService
{
    private const string ExtractionReadyFileName = ".kkindle-extracted";
    // Bump whenever sanitization changes. Existing reader caches otherwise
    // keep stale sanitized markup indefinitely.
    private const string ExtractionFormatVersion = "69";
    private const string ContentSecurityPolicyBase =
        "default-src 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'; " +
        "connect-src 'none'; form-action 'none'; img-src 'self' file:; " +
        "font-src 'self' file: data:; style-src 'self' 'unsafe-inline' file:; " +
        "media-src 'none'; worker-src 'none'; frame-ancestors 'none';";

    private static readonly Regex CssUrlPattern = new(
        """url\s*\(\s*(?<quote>['"]?)(?<value>[^)'"]+)\k<quote>\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CssImportPattern = new(
        "@import\\s+[^;]+;?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HtmlNamedEntityPattern = new(
        "&[A-Za-z][A-Za-z0-9]+;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly AppPaths _paths;

    public EpubReaderPreparationService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<EpubReaderDocument> PrepareAsync(
        string epubPath,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Concat(sha256.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (cacheKey.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");

        var cacheRoot = Path.GetFullPath(Path.Combine(_paths.ReaderCache, cacheKey));
        EnsureContainedPath(_paths.ReaderCache, cacheRoot);
        Directory.CreateDirectory(cacheRoot);

        var extractionReadyPath = Path.Combine(cacheRoot, ExtractionReadyFileName);
        var extractionReady = await IsExtractionReadyAsync(
            extractionReadyPath,
            cacheKey,
            cancellationToken);
        if (!extractionReady)
        {
            // Re-extract on every format-version mismatch. Re-sanitizing an
            // already transformed cache cannot restore content removed by an
            // older sanitizer and would leave bridge changes version-skewed.
            await ExtractSafelyAsync(epubPath, cacheRoot, cancellationToken);

            await SanitizeExtractedResourcesAsync(cacheRoot, cancellationToken);
            await File.WriteAllTextAsync(
                extractionReadyPath,
                $"{cacheKey}\n{ExtractionFormatVersion}",
                Encoding.UTF8,
                cancellationToken);
        }

        var containerPath = Path.Combine(cacheRoot, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");

        var container = await LoadXmlAsync(containerPath, cancellationToken);
        var packageRelativePath = container
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
            .Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(packageRelativePath))
            throw new InvalidDataException("EPUB 没有声明内容清单。");

        var packagePath = ResolveContainedPath(cacheRoot, packageRelativePath);
        if (!File.Exists(packagePath))
            throw new InvalidDataException("EPUB 内容清单不存在。");

        var package = await LoadXmlAsync(packagePath, cancellationToken);
        var manifest = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(element => new ManifestItem(
                element.Attribute("id")?.Value,
                element.Attribute("href")?.Value,
                element.Attribute("media-type")?.Value,
                element.Attribute("properties")?.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, item => item, StringComparer.Ordinal);

        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        var chapters = new List<string>();
        foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idRef = itemRef.Attribute("idref")?.Value;
            if (idRef is null || !manifest.TryGetValue(idRef, out var item)) continue;
            if (item.MediaType is not ("application/xhtml+xml" or "text/html")) continue;

            var href = Uri.UnescapeDataString(item.Href!.Split('#')[0]);
            var chapterPath = ResolveContainedPath(packageDirectory, href);
            EnsureContainedPath(cacheRoot, chapterPath);
            if (File.Exists(chapterPath)) chapters.Add(chapterPath);
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("EPUB 没有可阅读的章节。");

        var navigation = await ReadNavigationAsync(
            package,
            manifest,
            packageDirectory,
            cacheRoot,
            chapters,
            cancellationToken);
        if (navigation.Count == 0)
        {
            navigation = chapters
                .Select((chapter, index) => new EpubReaderNavigationItem(
                    $"第 {index + 1} 章",
                    new Uri(chapter).AbsoluteUri,
                    index))
                .ToList();
        }

        var chapterTitles = new List<string>(chapters.Count);
        foreach (var chapter in chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chapterTitles.Add(await ReadChapterTitleAsync(chapter, cancellationToken));
        }

        await ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(chapters, chapterTitles, cancellationToken);

        return new EpubReaderDocument(cacheRoot, chapters, navigation, chapterTitles);
    }

    private static async Task<List<EpubReaderNavigationItem>> ReadNavigationAsync(
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        CancellationToken cancellationToken)
    {
        var navItem = manifest.Values.FirstOrDefault(item =>
            HasToken(item.Properties, "nav"));
        if (navItem is not null)
        {
            var navPath = ResolveContainedPath(packageDirectory, Uri.UnescapeDataString(navItem.Href!.Split('#')[0]));
            EnsureContainedPath(cacheRoot, navPath);
            if (File.Exists(navPath))
            {
                var navDocument = await LoadXmlAsync(navPath, cancellationToken);
                var navElements = navDocument.Descendants()
                    .Where(element => element.Name.LocalName.Equals("nav", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var explicitToc = navElements
                    .Where(IsTocNavigationElement)
                    .Select(element => CreateNavigationItems(
                        GetNavigationLinks(element),
                        navPath,
                        cacheRoot,
                        chapters))
                    .OrderByDescending(items => items.Count)
                    .FirstOrDefault(items => items.Count > 0);
                if (explicitToc is not null) return explicitToc;

                var inferredToc = navElements
                    .Where(element => !IsKnownNonTocNavigationElement(element))
                    .Select(element => CreateNavigationItems(
                        GetNavigationLinks(element),
                        navPath,
                        cacheRoot,
                        chapters))
                    .OrderByDescending(items => items.Count)
                    .FirstOrDefault(items => items.Count > 0);
                if (inferredToc is not null)
                {
                    return inferredToc;
                }
            }
        }

        var guideToc = package.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("reference", StringComparison.OrdinalIgnoreCase)
                && HasToken(GetAttributeValue(element, "type"), "toc"));
        var guideHref = GetAttributeValue(guideToc, "href");
        if (!string.IsNullOrWhiteSpace(guideHref))
        {
            var guidePathPart = guideHref.Split('#', 2)[0].Split('?', 2)[0];
            var guidePath = ResolveContainedPath(
                packageDirectory,
                Uri.UnescapeDataString(guidePathPart));
            EnsureContainedPath(cacheRoot, guidePath);
            if (File.Exists(guidePath))
            {
                var guideDocument = await LoadXmlAsync(guidePath, cancellationToken);
                var guideItems = CreateNavigationItems(
                    GetNavigationLinks(guideDocument.Root),
                    guidePath,
                    cacheRoot,
                    chapters);
                if (guideItems.Count > 0) return guideItems;
            }
        }

        var spineTocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?
            .Attribute("toc")?.Value;
        if (spineTocId is null || !manifest.TryGetValue(spineTocId, out var ncxItem)) return [];

        var ncxPath = ResolveContainedPath(packageDirectory, Uri.UnescapeDataString(ncxItem.Href!.Split('#')[0]));
        EnsureContainedPath(cacheRoot, ncxPath);
        if (!File.Exists(ncxPath)) return [];

        var ncx = await LoadXmlAsync(ncxPath, cancellationToken);
        return CreateNavigationItems(
            ncx.Descendants().Where(element => element.Name.LocalName == "navPoint")
                .Select(element =>
                {
                    var title = element.Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "navLabel")?
                        .Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "text")?.Value;
                    var href = element.Elements().FirstOrDefault(child => child.Name.LocalName == "content")?
                        .Attribute("src")?.Value;
                    return (Title: NormalizeTitle(title), Href: href);
                }),
            ncxPath,
            cacheRoot,
            chapters);
    }

    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyList<string> chapters)
    {
        var result = new List<EpubReaderNavigationItem>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (title, href) in source)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && !absolute.IsFile) continue;

            var parts = href.Split('#', 2);
            var pathPart = parts[0].Split('?', 2)[0];
            var targetPath = pathPart.Length == 0
                ? navigationDocumentPath
                : ResolveContainedPath(Path.GetDirectoryName(navigationDocumentPath)!, Uri.UnescapeDataString(pathPart));
            EnsureContainedPath(cacheRoot, targetPath);
            var chapterIndex = chapters.ToList().FindIndex(chapter =>
                Path.GetFullPath(chapter).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase));
            if (chapterIndex < 0 || !File.Exists(targetPath)) continue;

            var target = new Uri(targetPath).AbsoluteUri;
            if (parts.Length == 2 && parts[1].Length > 0) target += $"#{parts[1]}";
            var fragmentKey = parts.Length == 2 ? DecodeNavigationFragment(parts[1]) : string.Empty;
            if (!targets.Add($"{chapterIndex}\0{fragmentKey}")) continue;
            result.Add(new EpubReaderNavigationItem(title, target, chapterIndex));
        }
        return result;
    }

    private static IEnumerable<(string Title, string? Href)> GetNavigationLinks(XElement? navigation) =>
        navigation?.Descendants()
            .Where(element => element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
            .Select(element => (
                Title: NormalizeTitle(element.Value),
                Href: element.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value))
        ?? [];

    private static bool IsTocNavigationElement(XElement element) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase)
            && HasToken(attribute.Value, "toc"))
        || HasToken(element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))?.Value, "doc-toc")
        || HasTocHint(GetAttributeValue(element, "id"))
        || HasTocHint(GetAttributeValue(element, "class"));

    private static bool IsKnownNonTocNavigationElement(XElement element)
    {
        var metadata = element.Attributes()
            .Where(attribute => new[] { "type", "role", "id", "class" }.Any(name =>
                attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(attribute => attribute.Value);
        return metadata.Any(value => Regex.IsMatch(
            value,
            @"(?:^|[\s_-])(landmarks?|page[-_]?list|doc[-_]?pagelist)(?:$|[\s_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetAttributeValue(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool HasTocHint(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(
            value,
            @"(?:^|[\s_-])(toc|table[-_]?of[-_]?contents?)(?:$|[\s_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DecodeNavigationFragment(string fragment)
    {
        try { return Uri.UnescapeDataString(fragment); }
        catch { return fragment; }
    }

    private static async Task<string> ReadChapterTitleAsync(
        string chapterPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await LoadXmlAsync(chapterPath, cancellationToken);
            var heading = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("h1", StringComparison.OrdinalIgnoreCase));
            var title = NormalizeTitle(heading?.Value);
            if (title.Length == 0)
            {
                title = NormalizeTitle(document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value);
            }

            return title.ToLowerInvariant() switch
            {
                "cover" => "封面",
                "table of contents" => "目录",
                _ => title
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeTitle(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private const int ChapterTitlePreviewMaxLength = 20;

    private static readonly HashSet<string> BodyPreviewSkippedElements = new(StringComparer.OrdinalIgnoreCase)
    { "head", "title", "script", "style" };

    // Calibre-style conversions stamp the same book-level <title> on every
    // split file, so spine chapters missing from the book's own TOC (front
    // matter such as author bios and dedications) would all show one identical
    // label. Give each member of a duplicate group a title derived from its
    // first body line instead.
    private static async Task ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(
        IReadOnlyList<string> chapters,
        List<string> chapterTitles,
        CancellationToken cancellationToken)
    {
        var duplicates = chapterTitles
            .Where(title => title.Length > 0)
            .GroupBy(title => title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicates.Count == 0) return;

        for (var index = 0; index < chapterTitles.Count; index++)
        {
            if (!duplicates.Contains(chapterTitles[index])) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var preview = TruncateChapterTitle(
                await ReadChapterBodyPreviewAsync(chapters[index], cancellationToken));
            // A preview that collides again would just move the duplication;
            // keep the original label when no distinct first line exists.
            if (preview.Length > 0 && !duplicates.Contains(preview))
                chapterTitles[index] = preview;
        }
    }

    private static async Task<string> ReadChapterBodyPreviewAsync(
        string chapterPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await LoadXmlAsync(chapterPath, cancellationToken);
            var blockPreview = document
                .Descendants()
                .Where(element =>
                    IsBodyPreviewElement(element)
                    && element.Name.LocalName is
                        "p" or "div" or "section" or "article" or "li"
                        or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                .Select(element => NormalizeTitle(element.Value))
                .FirstOrDefault(value => value.Length > 0);
            if (!string.IsNullOrWhiteSpace(blockPreview))
                return blockPreview;

            return document
                .DescendantNodes()
                .OfType<XText>()
                .Where(text => text.Parent is XElement parent && IsBodyPreviewElement(parent))
                .Select(text => NormalizeTitle(text.Value))
                .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool IsBodyPreviewElement(XElement element) =>
        !BodyPreviewSkippedElements.Contains(element.Name.LocalName)
        && element.Ancestors().All(ancestor => !BodyPreviewSkippedElements.Contains(ancestor.Name.LocalName));

    private static string TruncateChapterTitle(string value)
    {
        value = NormalizeTitle(value);
        return value.Length <= ChapterTitlePreviewMaxLength
            ? value
            : value[..ChapterTitlePreviewMaxLength].TrimEnd() + "…";
    }

    private sealed record ManifestItem(string? Id, string? Href, string? MediaType, string? Properties);

    private static async Task ExtractSafelyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.FullName)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<XDocument> LoadXmlAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (XmlException)
        {
            // A number of EPUB 2 generators emit HTML entities such as
            // &nbsp; while declaring XHTML. External DTD resolution stays
            // disabled; decode only entities known by the platform and retry.
            var xml = await File.ReadAllTextAsync(path, cancellationToken);
            var normalized = HtmlNamedEntityPattern.Replace(xml, match =>
            {
                var entityName = match.Value.AsSpan(1, match.Value.Length - 2);
                if (entityName.Equals("amp", StringComparison.Ordinal)
                    || entityName.Equals("lt", StringComparison.Ordinal)
                    || entityName.Equals("gt", StringComparison.Ordinal)
                    || entityName.Equals("quot", StringComparison.Ordinal)
                    || entityName.Equals("apos", StringComparison.Ordinal))
                    return match.Value;

                var decoded = WebUtility.HtmlDecode(match.Value);
                return string.Equals(decoded, match.Value, StringComparison.Ordinal)
                    ? match.Value
                    : decoded;
            });
            if (string.Equals(xml, normalized, StringComparison.Ordinal)) throw;

            using var textReader = new StringReader(normalized);
            using var reader = XmlReader.Create(textReader, CreateSecureXmlReaderSettings());
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
    }

    private static XmlReaderSettings CreateSecureXmlReaderSettings() => new()
    {
        Async = true,
        // Standard EPUB XHTML commonly carries a DOCTYPE. Ignore it
        // without resolving entities; the null resolver keeps external
        // DTDs and entities out of the reader process.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null
    };

    private static async Task<bool> IsExtractionReadyAsync(
        string markerPath,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return false;
        var marker = await File.ReadAllTextAsync(markerPath, cancellationToken);
        return string.Equals(
            marker.Trim(),
            $"{cacheKey}\n{ExtractionFormatVersion}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SanitizeExtractedResourcesAsync(
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var htmlFiles = Directory.EnumerateFiles(cacheRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var path in htmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeHtmlFileAsync(path, cacheRoot, cancellationToken);
        }

        var cssFiles = Directory.EnumerateFiles(cacheRoot, "*.css", SearchOption.AllDirectories).ToArray();
        foreach (var path in cssFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeCssFileAsync(path, cacheRoot, cancellationToken);
        }
    }

    private static async Task SanitizeHtmlFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(path, cancellationToken);
        var root = document.Root ?? throw new InvalidDataException("EPUB HTML 缺少根元素。");
        var namespaceName = root.Name.Namespace;
        var elements = root.DescendantsAndSelf().ToArray();
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localName = element.Name.LocalName;
            if (localName is "script" or "object" or "iframe" or "frame" or "embed" or "applet" or "base")
            {
                element.Remove();
                continue;
            }

            if (localName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                element.Remove();
                continue;
            }

            EpubReaderImageReferenceNormalizer.NormalizeHtmlImageReferences(
                element,
                path,
                cacheRoot);

            foreach (var attribute in element.Attributes().ToArray())
            {
                var attributeName = attribute.Name.LocalName;
                if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attributeName is "background")
                {
                    attribute.Remove();
                    continue;
                }

                if (attributeName == "srcset")
                {
                    var sanitizedSrcSet = EpubReaderImageReferenceNormalizer.NormalizeSrcSetAttribute(
                        element,
                        path,
                        cacheRoot);
                    if (string.IsNullOrWhiteSpace(sanitizedSrcSet))
                        attribute.Remove();
                    else
                        attribute.Value = sanitizedSrcSet;
                    continue;
                }

                if (attributeName is "src" or "href" or "action" or "poster" or "data"
                    or "cite" or "formaction" or "xlink:href")
                {
                    if (!IsSafeLocalReference(attribute.Value, path, cacheRoot))
                        attribute.Remove();
                }
                else if (attributeName == "style")
                {
                    var css = SanitizeCss(attribute.Value, path, cacheRoot);
                    if (string.IsNullOrWhiteSpace(css)) attribute.Remove();
                    else attribute.Value = css;
                }
            }

            // Some EPUBs use a remote 24x24 image as the complete label of a
            // local footnote link. Removing the unsafe URL while keeping the
            // now source-less <img> leaves a visible empty square in Chromium.
            // Preserve the note action with a compact text marker; discard
            // other source-less images instead of rendering broken placeholders.
            if (localName == "img" && !element.Attributes().Any(attribute =>
                    attribute.Name.LocalName is "src" or "xlink:href"
                    && !string.IsNullOrWhiteSpace(attribute.Value)))
            {
                var parent = element.Parent;
                if (parent is not null
                    && parent.Name.LocalName == "a"
                    && IsFootnoteReference(parent))
                {
                    element.ReplaceWith(
                        new XElement(
                            namespaceName + "sup",
                            new XAttribute("class", "kkindle-footnote-marker"),
                            "注"));
                }
                else
                {
                    element.Remove();
                }
                continue;
            }

            var styleText = element.Name.LocalName == "style" ? element.Value : null;
            if (styleText is not null)
                element.Value = SanitizeCss(styleText, path, cacheRoot);
        }

        // Mark vertical inline units in the serialized XHTML itself. Single
        // digits get an explicit upright cell so their glyph direction and
        // baseline are independent of the surrounding mixed CJK text. The
        // bridge repeats this defensively for dynamically inserted content.
        MarkVerticalInlineRuns(root);

        var head = root.Elements().FirstOrDefault(element => element.Name.LocalName == "head");
        if (head is null)
        {
            head = new XElement(namespaceName + "head");
            root.AddFirst(head);
        }

        head.Elements()
            .Where(element => element.Name.LocalName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "Content-Security-Policy",
                    StringComparison.OrdinalIgnoreCase))
            .Remove();

        // The self-drawn engine renders the sanitized XHTML directly; the
        // WebView bridge script and its CSP nonce are no longer injected.
        await WriteXmlAsync(document, path, cancellationToken);
    }

    private static void MarkVerticalInlineRuns(XElement root)
    {
        var body = root.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null) return;
        // Preserve publication text nodes byte-for-byte. CSS Writing Modes
        // needs the original adjacent characters to shape vertical CJK,
        // punctuation, Latin and digits as a single native run.
        body.SetAttributeValue("data-kkindle-vertical-inline-prepared", ExtractionFormatVersion);
    }

    private static bool IsFootnoteReference(XElement element)
    {
        if (IsFootnoteBacklink(element))
            return false;

        var metadata = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id" or "href")
                .Select(attribute => attribute.Value));
        return Regex.IsMatch(
            metadata,
            @"\b(noteref|doc-noteref|footnote|endnote|note[-_]?ref|fn[-_]?ref)\b|(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsFootnoteBacklink(XElement element)
    {
        var id = element.Attribute("id")?.Value?.Trim();
        var href = element.Attribute("href")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
            return false;

        var hash = href.IndexOf('#');
        if (hash < 0 || hash + 1 >= href.Length)
            return false;

        var fragment = href[(hash + 1)..];
        var query = fragment.IndexOfAny(['?', '#']);
        if (query >= 0) fragment = fragment[..query];
        return id.EndsWith("n", StringComparison.OrdinalIgnoreCase)
            && !fragment.EndsWith("n", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SanitizeCssFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var css = await File.ReadAllTextAsync(path, cancellationToken);
        var sanitized = SanitizeCss(css, path, cacheRoot);
        if (!string.Equals(css, sanitized, StringComparison.Ordinal))
            await File.WriteAllTextAsync(path, sanitized, Encoding.UTF8, cancellationToken);
    }

    private static string SanitizeCss(string css, string sourcePath, string cacheRoot)
    {
        var sanitized = CssImportPattern.Replace(css, string.Empty);
        return CssUrlPattern.Replace(sanitized, match =>
        {
            var value = match.Groups["value"].Value.Trim();
            return IsSafeLocalReference(value, sourcePath, cacheRoot) ? match.Value : string.Empty;
        });
    }

    private static bool IsSafeLocalReference(string value, string sourcePath, string cacheRoot) =>
        EpubReaderImageReferenceNormalizer.IsSafeLocalReference(value, sourcePath, cacheRoot);

    private static async Task WriteXmlAsync(
        XDocument document,
        string path,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
            document.Save(writer, SaveOptions.DisableFormatting);
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        EnsureContainedPath(root, fullPath);
        return fullPath;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
    }
}
