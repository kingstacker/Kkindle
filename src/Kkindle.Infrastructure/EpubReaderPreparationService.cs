using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Kkindle.Infrastructure;

// Level is the entry's depth in the book's own table of contents: 0 for a
// top-level part or chapter, 1 for its children, and so on. The reader rail
// indents and folds by this value, so it must survive extraction rather than
// being flattened away.
public sealed record EpubReaderNavigationItem(
    string Title,
    string Target,
    int ChapterIndex,
    int Level = 0);

public sealed record EpubReaderDocument(
    string RootPath,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<EpubReaderNavigationItem> Navigation,
    IReadOnlyList<string> ChapterTitles);

public sealed class EpubReaderPreparationService
{
    private const string ExtractionReadyFileName = ".kkindle-extracted";
    private const string ReaderIndexFileName = ".kkindle-reader-index.json";
    private const string ReaderIndexFormatVersion = "1";
    private const int PhysicalTocFullScanChapterLimit = 256;
    // Bump whenever sanitization changes. Existing reader caches otherwise
    // keep stale sanitized markup indefinitely.
    private const string ExtractionFormatVersion = "70";
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
    private static readonly Regex HiddenContentPattern = new(
        @"<(script|style|noscript|svg|math|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BlockBreakPattern = new(
        @"<(br\s*/?|/p|/div|/li|/h[1-6]|/section|/tr|/blockquote)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(
        "<[^>]*>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly XNamespace XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private readonly AppPaths _paths;

    private static readonly JsonSerializerOptions ReaderIndexJsonOptions = new(JsonSerializerDefaults.Web);

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
        if (extractionReady
            && await TryLoadReaderIndexAsync(cacheRoot, cancellationToken) is { } cachedDocument)
        {
            return cachedDocument;
        }

        if (!extractionReady)
        {
            TryDeleteFile(Path.Combine(cacheRoot, ReaderIndexFileName));
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
            var chapterPath = ResolvePublicationPath(packageDirectory, cacheRoot, href);
            if (File.Exists(chapterPath)) chapters.Add(chapterPath);
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("EPUB 没有可阅读的章节。");

        var chapterIndexByPath = CreateChapterIndexLookup(chapters);

        var navigation = await ReadNavigationAsync(
            package,
            manifest,
            packageDirectory,
            cacheRoot,
            chapters,
            chapterIndexByPath,
            cancellationToken);
        var hasAuthoritativeNavigation = navigation.Count > 0;
        if (navigation.Count == 0)
        {
            navigation = chapters
                .Select((chapter, index) => new EpubReaderNavigationItem(
                    $"第 {index + 1} 章",
                    new Uri(chapter).AbsoluteUri,
                    index))
                .ToList();
        }

        var authoritativeNavigation = hasAuthoritativeNavigation ? navigation : [];
        var chapterTitles = await ReadChapterTitlesAsync(
            chapters,
            authoritativeNavigation,
            cancellationToken);

        await ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(
            chapters,
            chapterTitles,
            authoritativeNavigation,
            cancellationToken);

        var document = new EpubReaderDocument(cacheRoot, chapters, navigation, chapterTitles);
        await WriteReaderIndexAsync(cacheRoot, document, cancellationToken);
        return document;
    }

    private sealed record PhysicalTocPage(
        int ChapterIndex,
        string Title,
        IReadOnlyList<EpubReaderNavigationItem> Items);

    private static IReadOnlyDictionary<string, int> CreateChapterIndexLookup(
        IReadOnlyList<string> chapters)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < chapters.Count; index++)
        {
            var path = Path.GetFullPath(chapters[index]);
            // Match the previous FindIndex behavior when a malformed EPUB
            // repeats one spine document: the first occurrence wins.
            result.TryAdd(path, index);
        }

        return result;
    }

    private static int GetMetadataConcurrency() =>
        Math.Clamp(Environment.ProcessorCount, 1, 4);

    private static async Task<List<EpubReaderNavigationItem>> ReadNavigationAsync(
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        var navigation = new List<EpubReaderNavigationItem>();
        var hasNavigation = false;
        var navItem = manifest.Values.FirstOrDefault(item =>
            HasToken(item.Properties, "nav"));
        if (navItem is not null)
        {
            var navPath = ResolvePublicationPath(
                packageDirectory,
                cacheRoot,
                Uri.UnescapeDataString(navItem.Href!.Split('#')[0]));
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
                        chapterIndexByPath))
                    .OrderByDescending(items => items.Count)
                    .FirstOrDefault(items => items.Count > 0);
                if (explicitToc is not null)
                {
                    navigation = explicitToc;
                    hasNavigation = true;
                }

                if (!hasNavigation)
                {
                    var inferredToc = navElements
                        .Where(element => !IsKnownNonTocNavigationElement(element))
                        .Select(element => CreateNavigationItems(
                            GetNavigationLinks(element),
                            navPath,
                            cacheRoot,
                            chapterIndexByPath))
                        .OrderByDescending(items => items.Count)
                        .FirstOrDefault(items => items.Count > 0);
                    if (inferredToc is not null)
                    {
                        navigation = inferredToc;
                        hasNavigation = true;
                    }
                }
            }
        }

        // EPUB 2 offers two navigation sources and neither is reliably the
        // better one: the NCX is the only source that carries nesting, but
        // some producers ship an NCX whose targets are wrong while the page
        // named by <guide type="toc"> is correct. Read both and keep the one
        // that reaches more of the book, preferring the NCX on a tie so its
        // hierarchy survives.
        var ncxItems = new List<EpubReaderNavigationItem>();
        var ncxCollisions = 0;
        if (!hasNavigation)
        {
            var spineTocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?
                .Attribute("toc")?.Value;
            if (spineTocId is not null && manifest.TryGetValue(spineTocId, out var ncxItem))
            {
                var ncxPath = ResolvePublicationPath(
                    packageDirectory,
                    cacheRoot,
                    Uri.UnescapeDataString(ncxItem.Href!.Split('#')[0]));
                if (File.Exists(ncxPath))
                {
                    var ncx = await LoadXmlAsync(ncxPath, cancellationToken);
                    var navMap = ncx.Descendants()
                        .FirstOrDefault(element => element.Name.LocalName == "navMap");
                    ncxItems = CreateNavigationItems(
                        navMap is null ? [] : ReadNcxNavigationLinks(navMap),
                        ncxPath,
                        cacheRoot,
                        chapterIndexByPath,
                        out ncxCollisions);
                }
            }
        }

        if (!hasNavigation)
        {
            var guideToc = package.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("reference", StringComparison.OrdinalIgnoreCase)
                    && HasToken(GetAttributeValue(element, "type"), "toc"));
            var guideHref = GetAttributeValue(guideToc, "href");
            if (!string.IsNullOrWhiteSpace(guideHref))
            {
                var guidePathPart = guideHref.Split('#', 2)[0].Split('?', 2)[0];
                var guidePath = ResolvePublicationPath(
                    packageDirectory,
                    cacheRoot,
                    Uri.UnescapeDataString(guidePathPart));
                if (File.Exists(guidePath))
                {
                    var guideDocument = await LoadXmlAsync(guidePath, cancellationToken);
                    var guideItems = CreateNavigationItems(
                        GetNavigationLinks(guideDocument.Root),
                        guidePath,
                        cacheRoot,
                        chapterIndexByPath,
                        out var guideCollisions);
                    if (IsBetterNavigationSource(
                            guideItems,
                            guideCollisions,
                            ncxItems,
                            ncxCollisions))
                    {
                        navigation = guideItems;
                        hasNavigation = guideItems.Count > 0;
                        ncxItems = [];
                    }
                }
            }
        }

        if (!hasNavigation && ncxItems.Count > 0)
        {
            navigation = ncxItems;
            hasNavigation = true;
        }

        return await MergePhysicalTocNavigationAsync(
            navigation,
            cacheRoot,
            chapters,
            chapterIndexByPath,
            cancellationToken);
    }

    private static async Task<List<EpubReaderNavigationItem>> MergePhysicalTocNavigationAsync(
        List<EpubReaderNavigationItem> navigation,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        // Once the authoritative TOC already reaches every spine document,
        // a second pass over every chapter cannot add a readable chapter. It
        // is especially expensive for large novels whose NCX lists every
        // chapter, so keep the physical-TOC compatibility pass only for
        // incomplete navigation sources.
        if (navigation.Count > 0
            && CountDistinctChapters(navigation) >= chapters.Count)
        {
            return navigation;
        }

        var tocPages = await ReadPhysicalTocPagesAsync(
            cacheRoot,
            chapters,
            navigation,
            chapterIndexByPath,
            cancellationToken);
        if (tocPages.Count == 0)
            return navigation;

        var result = new List<EpubReaderNavigationItem>(navigation);
        var keys = new HashSet<string>(
            result.Select(GetNavigationKey),
            StringComparer.OrdinalIgnoreCase);

        // The first physical TOC is the book-level directory. Sub-TOC pages
        // are content sources only: their chapter links are added below, but
        // the pages themselves should not create repeated "目录" rows.
        var rootTocPage = tocPages[0];
        // Some EPUBs already publish the root TOC in NCX/guide, often with a
        // heading fragment. It is still the same physical page, so do not add
        // a second synthetic page-level entry just because the fragments differ.
        if (!result.Any(item => item.ChapterIndex == rootTocPage.ChapterIndex))
        {
            AddUniqueNavigationItem(
                result,
                keys,
                new EpubReaderNavigationItem(
                    rootTocPage.Title,
                    new Uri(chapters[rootTocPage.ChapterIndex]).AbsoluteUri,
                    rootTocPage.ChapterIndex));
        }

        foreach (var tocPage in tocPages)
        {
            // A TOC page that only repeats chapters the navigation already
            // reaches contributes nothing: its links normally carry different
            // anchors, so they survive deduplication and double the rail while
            // flattening the nesting the NCX supplied. Merge a page only when
            // it opens chapters the navigation misses, which is what makes
            // per-volume sub-TOC pages worth reading.
            var covered = result.Select(item => item.ChapterIndex).ToHashSet();
            if (tocPage.Items.All(item => covered.Contains(item.ChapterIndex)))
                continue;

            foreach (var item in tocPage.Items)
                AddUniqueNavigationItem(result, keys, item);
        }

        return OrderNavigationTree(result);
    }

    // Ordering has to respect nesting: a part heading shares its chapter index
    // with its first child, so a flat sort by chapter index would scatter
    // parents into the middle of their own subtrees. Sort the top-level
    // entries and carry each subtree along intact. A flat list (no entry above
    // level 0) degenerates to exactly the previous chapter-index ordering.
    internal static List<EpubReaderNavigationItem> OrderNavigationTree(
        IReadOnlyList<EpubReaderNavigationItem> items)
    {
        if (items.Count == 0) return [];

        var roots = new List<(int SortKey, int Order, List<EpubReaderNavigationItem> Subtree)>();
        for (var index = 0; index < items.Count;)
        {
            var subtree = new List<EpubReaderNavigationItem> { items[index] };
            var rootLevel = items[index].Level;
            var next = index + 1;
            while (next < items.Count && items[next].Level > rootLevel)
                subtree.Add(items[next++]);

            roots.Add((subtree.Min(item => item.ChapterIndex), roots.Count, subtree));
            index = next;
        }

        return roots
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.Order)
            .SelectMany(entry => entry.Subtree)
            .ToList();
    }

    private static async Task<List<PhysicalTocPage>> ReadPhysicalTocPagesAsync(
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        var candidateIndexes = GetPhysicalTocCandidateIndexes(
            chapters,
            navigation,
            chapterIndexByPath);
        var pages = new PhysicalTocPage?[candidateIndexes.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidateIndexes.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (resultIndex, token) =>
            {
                var chapterIndex = candidateIndexes[resultIndex];
                try
                {
                    var document = await LoadXmlAsync(chapters[chapterIndex], token);
                    var links = GetNavigationLinks(document.Root).ToArray();
                    if (!IsPhysicalTocPage(document, links))
                        return;

                    var items = CreateNavigationItems(
                        links,
                        chapters[chapterIndex],
                        cacheRoot,
                        chapterIndexByPath);
                    if (items.Count == 0)
                        return;

                    pages[resultIndex] = new PhysicalTocPage(
                        chapterIndex,
                        GetPhysicalTocTitle(document),
                        items);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A broken auxiliary XHTML page must not prevent the rest
                    // of the EPUB from opening or erase its NCX navigation.
                }
            });

        return pages
            .Where(page => page is not null)
            .Select(page => page!)
            .OrderBy(page => page.ChapterIndex)
            .ToList();
    }

    private static IReadOnlyList<int> GetPhysicalTocCandidateIndexes(
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        IReadOnlyDictionary<string, int> chapterIndexByPath)
    {
        if (chapters.Count <= PhysicalTocFullScanChapterLimit)
            return Enumerable.Range(0, chapters.Count).ToArray();

        var candidates = new HashSet<int>();
        var earlyLimit = Math.Min(PhysicalTocFullScanChapterLimit, chapters.Count);
        foreach (var item in navigation)
        {
            if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                || !target.IsFile
                || !chapterIndexByPath.TryGetValue(
                    Path.GetFullPath(target.LocalPath),
                    out var chapterIndex))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(chapters[chapterIndex]);
            if (chapterIndex < earlyLimit
                || IsPhysicalTocFileName(fileName)
                || IsTocHeading(item.Title))
            {
                candidates.Add(chapterIndex);
            }
        }

        // Physical TOCs are normally named explicitly. Keep the early spine
        // window as a compatibility fallback for publishers that use generic
        // filenames, while avoiding a full XML parse of a 10k-chapter novel.
        for (var chapterIndex = 0; chapterIndex < earlyLimit; chapterIndex++)
            candidates.Add(chapterIndex);

        for (var chapterIndex = 0; chapterIndex < chapters.Count; chapterIndex++)
        {
            var fileName = Path.GetFileNameWithoutExtension(chapters[chapterIndex]);
            if (IsPhysicalTocFileName(fileName))
                candidates.Add(chapterIndex);
        }

        return candidates.OrderBy(index => index).ToArray();
    }

    private static bool IsPhysicalTocFileName(string? value)
    {
        var compact = new string((value ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character)
                || character is >= '\u3400' and <= '\u9fff')
            .ToArray());
        return compact.Contains("toc", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("contents", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("tableofcontents", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("目录", StringComparison.Ordinal)
            || compact.Contains("目錄", StringComparison.Ordinal)
            || compact.Contains("目次", StringComparison.Ordinal);
    }

    private static bool IsPhysicalTocPage(
        XDocument document,
        IReadOnlyCollection<(string Title, string? Href, int Level)> links)
    {
        if (links.Count < 2)
            return false;
        if (document.Descendants().Any(IsTocNavigationElement))
            return true;

        return document.Descendants()
            .Where(element => element.Name.LocalName is
                "title" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                or "p" or "div" or "section")
            .Any(element => IsTocHeading(element.Value));
    }

    private static string GetPhysicalTocTitle(XDocument document)
    {
        var visibleTitle = document.Descendants()
            .Where(element => element.Name.LocalName is
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                or "p" or "div" or "section")
            .Select(element => NormalizeTitle(element.Value))
            .FirstOrDefault(IsTocHeading);
        if (!string.IsNullOrWhiteSpace(visibleTitle))
            return visibleTitle;

        return document.Descendants()
            .Where(element => element.Name.LocalName == "title")
            .Select(element => NormalizeTitle(element.Value))
            .FirstOrDefault(IsTocHeading)
            ?? "目录";
    }

    private static bool IsTocHeading(string value)
    {
        var compact = new string(value
            .Where(character => char.IsLetterOrDigit(character)
                || character is >= '\u3400' and <= '\u9fff')
            .ToArray());
        return compact.Equals("目录", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目录页", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目錄", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目錄頁", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目次", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("contents", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("tableofcontents", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDistinctChapters(IReadOnlyList<EpubReaderNavigationItem> items) =>
        items.Select(item => item.ChapterIndex).Distinct().Count();

    // Reaching more of the book decides it. Only when both sources open the
    // same number of chapters does the collision count break the tie, and an
    // exact tie keeps the NCX so its nesting survives.
    private static bool IsBetterNavigationSource(
        IReadOnlyList<EpubReaderNavigationItem> candidate,
        int candidateCollisions,
        IReadOnlyList<EpubReaderNavigationItem> incumbent,
        int incumbentCollisions)
    {
        if (candidate.Count == 0) return false;
        if (incumbent.Count == 0) return true;

        var candidateChapters = CountDistinctChapters(candidate);
        var incumbentChapters = CountDistinctChapters(incumbent);
        if (candidateChapters != incumbentChapters)
            return candidateChapters > incumbentChapters;
        return candidateCollisions < incumbentCollisions;
    }

    private static string GetNavigationKey(EpubReaderNavigationItem item)
    {
        if (Uri.TryCreate(item.Target, UriKind.Absolute, out var target))
            return $"{item.ChapterIndex}\0{target.Fragment}\0{item.Level}";
        return $"{item.ChapterIndex}\0{item.Target}\0{item.Level}";
    }

    private static void AddUniqueNavigationItem(
        List<EpubReaderNavigationItem> items,
        HashSet<string> keys,
        EpubReaderNavigationItem item)
    {
        if (keys.Add(GetNavigationKey(item)))
        {
            items.Add(item);
            return;
        }
    }

    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href, int Level)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyDictionary<string, int> chapterIndexByPath) =>
        CreateNavigationItems(source, navigationDocumentPath, cacheRoot, chapterIndexByPath, out _);

    // collidedEntries counts entries discarded because another entry already
    // claimed the same target. A navigation source that keeps pointing several
    // labels at one location is describing the book worse than one that does
    // not, which is the tie-breaker between an EPUB 2 NCX and its guide page.
    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href, int Level)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        out int collidedEntries)
    {
        collidedEntries = 0;
        var result = new List<EpubReaderNavigationItem>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (title, href, level) in source)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;
            if (IsFootnoteNavigationEntry(title, href)) continue;
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && !absolute.IsFile) continue;

            var parts = href.Split('#', 2);
            var pathPart = parts[0].Split('?', 2)[0];
            var targetPath = pathPart.Length == 0
                ? navigationDocumentPath
                : ResolvePublicationPath(
                    Path.GetDirectoryName(navigationDocumentPath)!,
                    cacheRoot,
                    Uri.UnescapeDataString(pathPart));
            if (!chapterIndexByPath.TryGetValue(Path.GetFullPath(targetPath), out var chapterIndex)
                || !File.Exists(targetPath))
            {
                continue;
            }

            var target = new Uri(targetPath).AbsoluteUri;
            if (parts.Length == 2 && parts[1].Length > 0) target += $"#{parts[1]}";
            var fragmentKey = parts.Length == 2 ? DecodeNavigationFragment(parts[1]) : string.Empty;
            // A part heading and its first chapter routinely share one target.
            // Keying on depth as well keeps the child entry that a purely
            // positional key would silently drop.
            if (!targets.Add($"{chapterIndex}\0{fragmentKey}\0{level}"))
            {
                collidedEntries++;
                continue;
            }

            result.Add(new EpubReaderNavigationItem(title, target, chapterIndex, level));
        }
        return result;
    }

    private static IEnumerable<(string Title, string? Href, int Level)> GetNavigationLinks(XElement? navigation)
    {
        if (navigation is null) return [];
        var anchors = navigation.Descendants()
            .Where(element => element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
            .Where(element => !IsFootnoteLink(element))
            .ToArray();
        if (anchors.Length == 0) return [];

        // Nesting depth comes from the enclosing list elements, which is how
        // both EPUB 3 nav documents and hand-authored TOC pages express
        // hierarchy. Normalize against the shallowest link so a TOC wrapped in
        // an extra <ol> still starts at level 0.
        var depths = anchors.Select(anchor => CountListAncestors(anchor, navigation)).ToArray();
        var baseline = depths.Min();
        return anchors.Select((anchor, index) => (
            Title: NormalizeTitle(anchor.Value),
            Href: anchor.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value,
            Level: ClampNavigationLevel(depths[index] - baseline)));
    }

    private const int MaximumNavigationLevel = 4;

    private static int ClampNavigationLevel(int level) =>
        Math.Clamp(level, 0, MaximumNavigationLevel);

    private static int CountListAncestors(XElement element, XElement root)
    {
        var depth = 0;
        for (var ancestor = element.Parent; ancestor is not null && ancestor != root; ancestor = ancestor.Parent)
        {
            if (ancestor.Name.LocalName is "ol" or "ul") depth++;
        }
        return depth;
    }

    // NCX hierarchy lives in navPoint nesting. Walk it depth-first so the
    // emitted order matches the printed table of contents.
    private static IEnumerable<(string Title, string? Href, int Level)> ReadNcxNavigationLinks(
        XElement container,
        int level = 0)
    {
        foreach (var navPoint in container.Elements()
            .Where(element => element.Name.LocalName == "navPoint"))
        {
            var title = navPoint.Elements()
                .FirstOrDefault(child => child.Name.LocalName == "navLabel")?
                .Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "text")?.Value;
            var href = navPoint.Elements().FirstOrDefault(child => child.Name.LocalName == "content")?
                .Attribute("src")?.Value;
            yield return (NormalizeTitle(title), href, ClampNavigationLevel(level));

            foreach (var child in ReadNcxNavigationLinks(navPoint, level + 1))
                yield return child;
        }
    }

    private static bool IsFootnoteLink(XElement element)
    {
        if (HasToken(GetAttributeValue(element, "rel"), "footnote")
            || HasToken(GetAttributeValue(element, "rel"), "noteref")
            || HasToken(GetAttributeValue(element, "role"), "doc-noteref")
            || HasToken(GetAttributeValue(element, "type"), "noteref"))
        {
            return true;
        }

        var id = GetAttributeValue(element, "id");
        var href = GetAttributeValue(element, "href");
        var fragment = href?.Split('#', 2).ElementAtOrDefault(1);
        return IsFootnoteMarker(element.Value)
            && (LooksLikeFootnoteIdentifier(id) || LooksLikeFootnoteIdentifier(fragment));
    }

    private static bool IsFootnoteNavigationEntry(string title, string href)
    {
        var fragment = href.Split('#', 2).ElementAtOrDefault(1);
        return LooksLikeFootnoteIdentifier(fragment)
            || (IsFootnoteMarker(title)
                && LooksLikeFootnoteIdentifier(Path.GetFileNameWithoutExtension(
                    href.Split('#', 2)[0].Split('?', 2)[0])));
    }

    private static bool IsFootnoteMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(
            value.Trim(),
            @"^(?:\[\s*\d+\s*\]|［\s*\d+\s*］)$",
            RegexOptions.CultureInvariant);

    private static bool LooksLikeFootnoteIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(
            value.Trim(),
            @"^(?:note|notes|fn|footnote|footnotes)[-_]?\d+[a-z]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        && !Regex.IsMatch(
            value,
            @"(?:^|[_-])sigil[_-]toc[_-]id(?:[_-]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
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
            await using var stream = new FileStream(
                chapterPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            var documentTitle = string.Empty;
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase)
                    && documentTitle.Length == 0
                    && !reader.IsEmptyElement)
                {
                    documentTitle = NormalizeTitle(
                        await ReadXmlElementTextAsync(reader, cancellationToken));
                    continue;
                }

                if (!reader.LocalName.Equals("h1", StringComparison.OrdinalIgnoreCase)
                    || reader.IsEmptyElement)
                {
                    continue;
                }

                var heading = NormalizeTitle(
                    await ReadXmlElementTextAsync(reader, cancellationToken));
                if (heading.Length > 0)
                    return NormalizeChapterTitle(heading);
            }

            return NormalizeChapterTitle(documentTitle);
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

    private static async Task<List<string>> ReadChapterTitlesAsync(
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        CancellationToken cancellationToken)
    {
        var titles = new string[chapters.Count];
        Array.Fill(titles, string.Empty);

        // An EPUB TOC already contains the display title for most novel
        // chapters. Reuse it and only inspect spine files that the TOC does
        // not cover (cover, title page, dedication, etc.).
        foreach (var item in navigation)
        {
            if (item.ChapterIndex < 0
                || item.ChapterIndex >= titles.Length
                || string.IsNullOrWhiteSpace(item.Title)
                || titles[item.ChapterIndex].Length > 0)
            {
                continue;
            }

            titles[item.ChapterIndex] = NormalizeTitle(item.Title);
        }

        var missingIndexes = Enumerable.Range(0, chapters.Count)
            .Where(index => titles[index].Length == 0)
            .ToArray();
        await Parallel.ForEachAsync(
            missingIndexes,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (index, token) =>
            {
                titles[index] = await ReadChapterTitleAsync(chapters[index], token);
            });

        return titles.ToList();
    }

    private static async Task<string> ReadXmlElementTextAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        using var subtree = reader.ReadSubtree();
        var builder = new StringBuilder();
        while (await subtree.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType is XmlNodeType.Text
                or XmlNodeType.CDATA
                or XmlNodeType.Whitespace
                or XmlNodeType.SignificantWhitespace)
            {
                builder.Append(subtree.Value);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeChapterTitle(string title) =>
        title.ToLowerInvariant() switch
        {
            "cover" => "封面",
            "table of contents" => "目录",
            _ => title
        };

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
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        CancellationToken cancellationToken)
    {
        var authoritativeIndexes = navigation
            .Select(item => item.ChapterIndex)
            .ToHashSet();
        var duplicates = chapterTitles
            .Select((title, index) => (title, index))
            .Where(item => item.title.Length > 0 && !authoritativeIndexes.Contains(item.index))
            .GroupBy(item => item.title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicates.Count == 0) return;

        var duplicateIndexes = Enumerable.Range(0, chapterTitles.Count)
            .Where(index => !authoritativeIndexes.Contains(index)
                && duplicates.Contains(chapterTitles[index]))
            .ToArray();
        var previews = new string[chapterTitles.Count];
        await Parallel.ForEachAsync(
            duplicateIndexes,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (index, token) =>
            {
                previews[index] = TruncateChapterTitle(
                    await ReadChapterBodyPreviewAsync(chapters[index], token));
            });

        foreach (var index in duplicateIndexes)
        {
            var preview = previews[index];
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
            await using var stream = new FileStream(
                chapterPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var localName = reader.LocalName;
                if (BodyPreviewSkippedElements.Contains(localName))
                {
                    if (!reader.IsEmptyElement)
                        reader.Skip();
                    continue;
                }

                if (localName is not (
                    "p" or "div" or "section" or "article" or "li"
                    or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"))
                {
                    continue;
                }

                if (reader.IsEmptyElement)
                    continue;

                var preview = NormalizeTitle(
                    await ReadXmlElementTextAsync(reader, cancellationToken));
                if (preview.Length > 0)
                    return preview;
            }

            return string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { return string.Empty; }
    }

    private static string TruncateChapterTitle(string value)
    {
        value = NormalizeTitle(value);
        return value.Length <= ChapterTitlePreviewMaxLength
            ? value
            : value[..ChapterTitlePreviewMaxLength].TrimEnd() + "…";
    }

    private sealed record ReaderIndexCacheDocument(
        string FormatVersion,
        List<string>? Chapters,
        List<ReaderIndexNavigationItem>? Navigation,
        List<string>? ChapterTitles);

    private sealed record ReaderIndexNavigationItem(
        string Title,
        string TargetPath,
        string Fragment,
        int ChapterIndex,
        int Level);

    private static async Task<EpubReaderDocument?> TryLoadReaderIndexAsync(
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(cacheRoot, ReaderIndexFileName);
        if (!File.Exists(indexPath)) return null;

        try
        {
            await using var stream = new FileStream(
                indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var cache = await JsonSerializer.DeserializeAsync<ReaderIndexCacheDocument>(
                stream,
                ReaderIndexJsonOptions,
                cancellationToken);
            if (cache is null
                || !string.Equals(cache.FormatVersion, ReaderIndexFormatVersion, StringComparison.Ordinal)
                || cache.Chapters is null
                || cache.Navigation is null
                || cache.ChapterTitles is null
                || cache.ChapterTitles.Count != cache.Chapters.Count
                || cache.Chapters.Count == 0)
            {
                return null;
            }

            var chapters = new List<string>(cache.Chapters.Count);
            foreach (var relativePath in cache.Chapters)
            {
                if (string.IsNullOrWhiteSpace(relativePath)) return null;
                var chapterPath = ResolveContainedPath(cacheRoot, relativePath);
                if (!File.Exists(chapterPath)) return null;
                chapters.Add(chapterPath);
            }

            var chapterIndexByPath = CreateChapterIndexLookup(chapters);
            var navigation = new List<EpubReaderNavigationItem>(cache.Navigation.Count);
            foreach (var item in cache.Navigation)
            {
                if (item is null
                    || item.ChapterIndex < 0
                    || item.ChapterIndex >= chapters.Count
                    || string.IsNullOrWhiteSpace(item.Title)
                    || string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    return null;
                }

                var targetPath = ResolveContainedPath(cacheRoot, item.TargetPath);
                if (!File.Exists(targetPath)
                    || !chapterIndexByPath.TryGetValue(targetPath, out var actualChapterIndex)
                    || actualChapterIndex != item.ChapterIndex)
                {
                    return null;
                }

                var fragment = item.Fragment ?? string.Empty;
                if (fragment.Length > 0 && !fragment.StartsWith('#'))
                    fragment = $"#{fragment}";
                navigation.Add(new EpubReaderNavigationItem(
                    item.Title,
                    new Uri(targetPath).AbsoluteUri + fragment,
                    item.ChapterIndex,
                    item.Level));
            }

            return new EpubReaderDocument(
                cacheRoot,
                chapters,
                navigation,
                cache.ChapterTitles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A partial or manually edited metadata file is disposable. The
            // normal preparation path rebuilds it without affecting the book.
            return null;
        }
    }

    private static async Task WriteReaderIndexAsync(
        string cacheRoot,
        EpubReaderDocument document,
        CancellationToken cancellationToken)
    {
        var cachedNavigation = new List<ReaderIndexNavigationItem>(document.Navigation.Count);
        foreach (var item in document.Navigation)
        {
            if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                || !target.IsFile)
            {
                return;
            }

            var targetPath = Path.GetFullPath(target.LocalPath);
            EnsureContainedPath(cacheRoot, targetPath);
            cachedNavigation.Add(new ReaderIndexNavigationItem(
                item.Title,
                Path.GetRelativePath(cacheRoot, targetPath).Replace('\\', '/'),
                target.Fragment,
                item.ChapterIndex,
                item.Level));
        }

        var cache = new ReaderIndexCacheDocument(
            ReaderIndexFormatVersion,
            document.Chapters
                .Select(chapter => Path.GetRelativePath(cacheRoot, chapter).Replace('\\', '/'))
                .ToList(),
            cachedNavigation,
            document.ChapterTitles.ToList());
        var indexPath = Path.Combine(cacheRoot, ReaderIndexFileName);
        var temporaryPath = Path.Combine(
            cacheRoot,
            $"{ReaderIndexFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    cache,
                    ReaderIndexJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, indexPath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The index is an optional acceleration layer. A read-only cache
            // or an interrupted replacement must not prevent opening the EPUB.
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private sealed record ManifestItem(string? Id, string? Href, string? MediaType, string? Properties);

    private static async Task ExtractSafelyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExtractWithZipArchiveAsync(epubPath, destinationRoot, cancellationToken);
            return;
        }
        catch (InvalidDataException)
        {
            // Some publisher and store pipelines ship EPUBs whose end-of-
            // central-directory record disagrees with the actual entry count.
            // The archive itself is readable, so fall back rather than
            // rejecting the book outright.
        }

        try
        {
            await ExtractFromCentralDirectoryAsync(epubPath, destinationRoot, cancellationToken);
            return;
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            // The central directory itself is unusable. Local file headers
            // are self-describing, so read the members sequentially.
        }

        await ExtractSequentiallyAsync(epubPath, destinationRoot, cancellationToken);
    }

    private static async Task ExtractSequentiallyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = new ICSharpCode.SharpZipLib.Zip.ZipInputStream(input);
        var extracted = 0;
        while (archive.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await archive.CopyToAsync(output, 81920, cancellationToken);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidDataException("EPUB 压缩包已损坏，无法读取。");
    }

    private static async Task ExtractWithZipArchiveAsync(
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

    private static async Task ExtractFromCentralDirectoryAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        // SharpZipLib addresses entries through the central directory, so a
        // single unreadable member (a corrupt embedded font, a truncated
        // image) can be skipped instead of aborting the whole book.
        using var archive = new ICSharpCode.SharpZipLib.Zip.ZipFile(epubPath);
        var extracted = 0;
        foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in archive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                await using var source = archive.GetInputStream(entry);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(output, 81920, cancellationToken);
                extracted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception
                is ICSharpCode.SharpZipLib.SharpZipBaseException
                or InvalidDataException
                or NotSupportedException)
            {
                // Leave nothing half-written: a truncated XHTML page would
                // otherwise reach the sanitizer as plausible-looking markup.
                TryDeleteFile(destination);
            }
        }

        if (extracted == 0)
            throw new InvalidDataException("EPUB 压缩包已损坏，无法读取。");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<XDocument> LoadXmlAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (XmlException originalFailure)
        {
            var markup = await File.ReadAllTextAsync(path, cancellationToken);

            // A number of EPUB 2 generators emit HTML entities such as
            // &nbsp; while declaring XHTML. External DTD resolution stays
            // disabled; decode only entities known by the platform and retry.
            var normalized = DecodeKnownHtmlEntities(markup);
            if (!string.Equals(markup, normalized, StringComparison.Ordinal))
            {
                try { return ParseXml(normalized); }
                catch (XmlException) { }
            }

            // Publisher tooling also ships genuine tag soup under an .xhtml
            // extension: unclosed elements, stray end tags, unquoted
            // attributes. Repair it with the HTML parser instead of failing
            // the whole book because one auxiliary page is malformed.
            return TryLoadRepairedHtml(normalized)
                ?? throw new XmlException(originalFailure.Message, originalFailure);
        }
    }

    private static string DecodeKnownHtmlEntities(string markup) =>
        HtmlNamedEntityPattern.Replace(markup, match =>
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

    private static XDocument ParseXml(string markup)
    {
        using var textReader = new StringReader(markup);
        using var reader = XmlReader.Create(textReader, CreateSecureXmlReaderSettings(asynchronous: false));
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static XDocument? TryLoadRepairedHtml(string markup)
    {
        try
        {
            var html = new HtmlDocument
            {
                OptionOutputAsXml = true,
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true,
                OptionWriteEmptyNodes = true
            };
            html.LoadHtml(markup);

            // Serialize the document element alone: the XML writer needs a
            // single root, and a DOCTYPE or trailing comment would otherwise
            // travel with it.
            var root = html.DocumentNode.SelectSingleNode("//html")
                ?? html.DocumentNode.ChildNodes.FirstOrDefault(node =>
                    node.NodeType == HtmlNodeType.Element);
            if (root is null) return null;

            var builder = new StringBuilder();
            using (var writer = new StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture))
                root.WriteTo(writer);
            return ParseXml(builder.ToString());
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static XmlReaderSettings CreateSecureXmlReaderSettings(bool asynchronous = true) => new()
    {
        Async = asynchronous,
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
        await Parallel.ForEachAsync(
            htmlFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (path, token) =>
                await SanitizeHtmlFileSafelyAsync(path, cacheRoot, token));

        var cssFiles = Directory.EnumerateFiles(cacheRoot, "*.css", SearchOption.AllDirectories).ToArray();
        await Parallel.ForEachAsync(
            cssFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (path, token) =>
                await SanitizeCssFileAsync(path, cacheRoot, token));
    }

    // One unrepairable page must never cost the reader the whole book. The
    // extraction marker is only written after sanitization completes, so a
    // rethrow here would also make the failure permanent: every later open
    // re-extracts and fails again at the same file.
    private static async Task SanitizeHtmlFileSafelyAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await SanitizeHtmlFileAsync(path, cacheRoot, cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException)
        {
        }

        // The renderer parses the cached XHTML directly, so leaving the
        // original markup in place would only move the failure downstream.
        // Replace it with a well-formed, script-free text rendition.
        await WriteDegradedTextDocumentAsync(path, cancellationToken);
    }

    private static async Task WriteDegradedTextDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string markup;
        try { markup = await File.ReadAllTextAsync(path, cancellationToken); }
        catch (IOException) { markup = string.Empty; }

        var text = HiddenContentPattern.Replace(markup, " ");
        text = BlockBreakPattern.Replace(text, "\n");
        text = HtmlTagPattern.Replace(text, " ");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');

        var body = new XElement(XhtmlNamespace + "body");
        foreach (var line in text.Split('\n'))
        {
            var paragraph = string.Join(
                ' ',
                line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (paragraph.Length == 0) continue;
            body.Add(new XElement(XhtmlNamespace + "p", paragraph));
        }

        var document = new XDocument(
            new XElement(
                XhtmlNamespace + "html",
                new XElement(XhtmlNamespace + "head"),
                body));
        document.Root!.Element(XhtmlNamespace + "body")!
            .SetAttributeValue("data-kkindle-degraded", "html-parse-failed");
        MarkVerticalInlineRuns(document.Root!);
        await WriteXmlAsync(document, path, cancellationToken);
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

    // Manifest and navigation hrefs are relative to the document that
    // declares them, and legally climb out of the package directory: an OPF
    // under OEBPS/ may reference "../toc.ncx" at the archive root. Only the
    // extraction root is a real security boundary, so resolve against the
    // declaring document and verify containment against the cache root.
    private static string ResolvePublicationPath(
        string baseDirectory,
        string cacheRoot,
        string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
        EnsureContainedPath(cacheRoot, fullPath);
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
