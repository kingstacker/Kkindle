using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Converts plain text to a deterministic, self-contained EPUB without Calibre.</summary>
public static class TxtToEpubService
{
    private static readonly Regex NumberedHeading = new(
        @"^(?:第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[章回节卷部篇](?:\s*[:：、.．\-—]?\s*.*)?|Chapter\s+\d+(?:\s*[:.\-–—]?\s*.*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ChineseNumberedHeading = new(
        @"^第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[章回节卷部篇](?:\s*[:：、.．\-—]?\s*.*)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex EnglishChapterHeading = new(
        @"^Chapter\s+\d+(?:\s*[:.\-–—]?\s*.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SpecialHeading = new(
        @"^(?:序章|序言|序|楔子|前言|引子|尾声|终章|后记|结束语)(?:\s+.{1,30})?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex VolumeHeading = new(@"^第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[卷部篇]", RegexOptions.CultureInvariant);
    private static readonly Regex NumberedListPrefix = new(@"^(?<index>\d+)[.．、]\s*(?=第\s*(?<chapter>\d+)\s*章)", RegexOptions.CultureInvariant);
    private static readonly Regex SpacedNumberedHeading = new(@"^第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[章回节卷部篇]\s+", RegexOptions.CultureInvariant);
    private static readonly Regex ArabicNumberedHeading = new(@"^第\s*\d+\s*[章回节卷部篇]", RegexOptions.CultureInvariant);
    private static readonly Regex SentencePunctuation = new(@"[。；;，,]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex AuthorPattern = new(
        @"(?:作者|著者|作\s*者)\s*[:：]\s*(?<author>[^\s,，;；。]+(?:\s*[/、&＆]\s*[^\s,，;；。]+)*)|(?:^|\s)Author\s*[:：]\s*(?<english>[^\s,，;；。]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private sealed record Chapter(string Title, List<string> Lines, bool Volume = false, int StartLine = 1, int CharacterCount = 0);

    public static Task ConvertAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        ConvertCoreAsync(sourcePath, destinationPath, TxtImportRules.Default, cancellationToken);

    public static Task ConvertWithRulesAsync(
        string sourcePath,
        string destinationPath,
        TxtImportRules rules,
        CancellationToken cancellationToken = default) =>
        ConvertCoreAsync(sourcePath, destinationPath, rules, cancellationToken);

    /// <summary>
    /// Reads and analyzes the book away from the UI thread, then returns only
    /// one bounded page of chapter summaries. The source remains untouched.
    /// </summary>
    public static async Task<TxtImportPreview> PreviewAsync(
        string sourcePath,
        TxtImportRules? rules = null,
        int pageIndex = 0,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize), "预览每页需显示 1 到 200 个目录项。");
        rules ??= TxtImportRules.Default;
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = Decode(bytes, rules.Encoding);
            var text = NormalizeText(decoded.Text, cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("TXT 文件没有可导入的正文。");
            var chapters = ParseChapters(text, rules, cancellationToken, includeLines: false, pageIndex, pageSize, out var chapterCount);
            return new TxtImportPreview(
                Path.GetFileNameWithoutExtension(sourcePath),
                decoded.Encoding,
                pageIndex,
                pageSize,
                chapterCount,
                chapters.Select((chapter, index) => new TxtChapterPreviewItem(
                    checked(pageIndex * pageSize + index + 1),
                    chapter.StartLine,
                    chapter.Title,
                    chapter.Volume,
                    chapter.CharacterCount)).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConvertCoreAsync(
        string sourcePath,
        string destinationPath,
        TxtImportRules rules,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(rules);
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => ConvertFromBytes(sourcePath, destinationPath, bytes, rules, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static void ConvertFromBytes(
        string sourcePath,
        string destinationPath,
        byte[] bytes,
        TxtImportRules rules,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = NormalizeText(Decode(bytes, rules.Encoding).Text, cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("TXT 文件没有可导入的正文。");
        var title = Path.GetFileNameWithoutExtension(sourcePath);
        var author = ExtractAuthorBeforeFirstChapter(text, rules, cancellationToken);
        var chapters = ParseChapters(text, rules, cancellationToken, includeLines: true, pageIndex: 0, pageSize: int.MaxValue, out _);
        var cover = TitleCoverService.CreatePng(title);
        var identifier = "urn:sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + text))).ToLowerInvariant();
        var created = false;
        try
        {
            using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write);
            created = true;
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            Write(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Write(archive, "META-INF/container.xml", new XDocument(new XElement(XName.Get("container", "urn:oasis:names:tc:opendocument:xmlns:container"),
                new XAttribute("version", "1.0"), new XElement(XName.Get("rootfiles", "urn:oasis:names:tc:opendocument:xmlns:container"),
                    new XElement(XName.Get("rootfile", "urn:oasis:names:tc:opendocument:xmlns:container"), new XAttribute("full-path", "OEBPS/content.opf"), new XAttribute("media-type", "application/oebps-package+xml"))))).ToString());
            Write(archive, "OEBPS/style.css", "body { line-height: 1.6; } p { white-space: pre-wrap; text-indent: 2em; margin: 0.5em 0; } h1 { text-align: center; }");
            var manifest = new XElement(Opf + "manifest",
                new XElement(Opf + "item", new XAttribute("id", "cover"), new XAttribute("href", "cover.png"), new XAttribute("media-type", "image/png"), new XAttribute("properties", "cover-image")),
                new XElement(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
                new XElement(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "style.css"), new XAttribute("media-type", "text/css")));
            var coverEntry = archive.CreateEntry("OEBPS/cover.png", CompressionLevel.NoCompression);
            coverEntry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var coverStream = coverEntry.Open()) coverStream.Write(cover);
            var spine = new XElement(Opf + "spine");
            var toc = new XElement(Xhtml + "ol");
            XElement? volumeList = null;
            for (var i = 0; i < chapters.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = chapters[i];
                var name = $"chapter-{i + 1:D5}.xhtml";
                var body = new XElement(Xhtml + "body", new XElement(Xhtml + "h1", chapter.Title));
                foreach (var line in chapter.Lines)
                    body.Add(string.IsNullOrWhiteSpace(line) ? new XElement(Xhtml + "p", new XElement(Xhtml + "br")) : new XElement(Xhtml + "p", line));
                Write(archive, "OEBPS/" + name, Document(chapter.Title, body).ToString());
                manifest.Add(new XElement(Opf + "item", new XAttribute("id", $"c{i}"), new XAttribute("href", name), new XAttribute("media-type", "application/xhtml+xml")));
                spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", $"c{i}")));
                var entry = new XElement(Xhtml + "li", new XElement(Xhtml + "a", new XAttribute("href", name), chapter.Title));
                if (chapter.Volume)
                {
                    toc.Add(entry);
                    volumeList = new XElement(Xhtml + "ol");
                    entry.Add(volumeList);
                }
                else (volumeList ?? toc).Add(entry);
            }
            XNamespace epub = "http://www.idpf.org/2007/ops";
            Write(archive, "OEBPS/nav.xhtml", Document(title, new XElement(Xhtml + "body",
                new XElement(Xhtml + "nav", new XAttribute(XNamespace.Xmlns + "epub", epub), new XAttribute(epub + "type", "toc"), new XAttribute("id", "toc"), new XElement(Xhtml + "h1", "目录"), toc))).ToString());
            var package = new XElement(Opf + "package", new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "book-id"),
                new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc),
                    new XElement(Dc + "identifier", new XAttribute("id", "book-id"), identifier), new XElement(Dc + "title", title),
                    new XElement(Dc + "language", "zh"), new XElement(Dc + "creator", author ?? "未知作者"),
                    new XElement(Opf + "meta", new XAttribute("name", "cover"), new XAttribute("content", "cover")),
                    new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), "2000-01-01T00:00:00Z")), manifest, spine);
            Write(archive, "OEBPS/content.opf", new XDocument(package).ToString());
        }
        catch
        {
            if (created && File.Exists(destinationPath)) File.Delete(destinationPath);
            throw;
        }
    }

    private static XDocument Document(string title, XElement body) => new(new XElement(Xhtml + "html",
        new XAttribute(XNamespace.Xml + "lang", "zh"), new XElement(Xhtml + "head", new XElement(Xhtml + "title", title),
            new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("href", "style.css"), new XAttribute("type", "text/css"))), body));

    private static string NormalizeText(string text, CancellationToken cancellationToken)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // Invalid XML control characters cannot be represented in an EPUB document.
        var normalized = new StringBuilder(text.Length);
        var checkedCharacters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((checkedCharacters++ & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (rune.Value > 0xFFFF || XmlConvert.IsXmlChar((char)rune.Value)) normalized.Append(rune.ToString());
        }
        cancellationToken.ThrowIfCancellationRequested();
        return normalized.ToString();
    }

    private static DecodedText Decode(byte[] bytes, TxtTextEncoding requestedEncoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (requestedEncoding == TxtTextEncoding.Auto)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            try
            {
                var text = reader.ReadToEnd();
                return new DecodedText(text, FromCodePage(reader.CurrentEncoding.CodePage));
            }
            catch (DecoderFallbackException)
            {
                var gb18030 = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return new DecodedText(gb18030.GetString(bytes), TxtTextEncoding.Gb18030);
            }
        }

        var selectedEncoding = requestedEncoding switch
        {
            TxtTextEncoding.Utf8 => new UTF8Encoding(false, true),
            TxtTextEncoding.Utf16LittleEndian => new UnicodeEncoding(false, true, true),
            TxtTextEncoding.Utf16BigEndian => new UnicodeEncoding(true, true, true),
            TxtTextEncoding.Gb18030 => Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new ArgumentOutOfRangeException(nameof(requestedEncoding))
        };
        using var explicitStream = new MemoryStream(bytes);
        using var explicitReader = new StreamReader(explicitStream, selectedEncoding, detectEncodingFromByteOrderMarks: true);
        var explicitText = explicitReader.ReadToEnd();
        return new DecodedText(explicitText, requestedEncoding);
    }

    private static TxtTextEncoding FromCodePage(int codePage) => codePage switch
    {
        1200 => TxtTextEncoding.Utf16LittleEndian,
        1201 => TxtTextEncoding.Utf16BigEndian,
        54936 => TxtTextEncoding.Gb18030,
        _ => TxtTextEncoding.Utf8
    };

    private static List<Chapter> ParseChapters(
        string text,
        TxtImportRules rules,
        CancellationToken cancellationToken,
        bool includeLines,
        int pageIndex,
        int pageSize,
        out int chapterCount)
    {
        var chapters = new List<Chapter>();
        var selectedStart = (long)pageIndex * pageSize;
        var selectedEnd = selectedStart + pageSize;
        var customHeading = CreateCustomHeadingRegex(rules);
        var corrections = (rules.Corrections ?? Array.Empty<TxtChapterCorrection>())
            .Where(correction => correction.LineNumber > 0)
            .GroupBy(correction => correction.LineNumber)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var current = new ChapterBuilder("前言", 1, false, includeLines);
        var hasHeadings = false;
        var sectionTitle = current.Title;
        var part = 1;
        var completedChapterCount = 0;

        void FinishCurrent()
        {
            var index = completedChapterCount++;
            if (includeLines || (index >= selectedStart && index < selectedEnd))
            {
                chapters.Add(new Chapter(
                    current.Title,
                    includeLines ? current.Lines : [],
                    current.Volume,
                    current.StartLine,
                    current.CharacterCount));
            }
        }

        var lineNumber = 0;
        foreach (var line in EnumerateLines(text))
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();
            var heading = NormalizeHeading(line);
            var isHeading = IsHeading(heading, rules, customHeading);
            var isVolume = rules.RecognizeVolumes && VolumeHeading.IsMatch(heading);
            if (corrections.TryGetValue(lineNumber, out var lineCorrections))
            {
                foreach (var correction in lineCorrections)
                {
                    if (correction.IsHeading.HasValue) isHeading = correction.IsHeading.Value;
                    if (!string.IsNullOrWhiteSpace(correction.Title)) heading = correction.Title.Trim().TrimStart('\uFEFF');
                    if (correction.IsVolume.HasValue) isVolume = correction.IsVolume.Value;
                }
            }
            if (!isHeading) isVolume = false;

            if (isHeading)
            {
                hasHeadings = true;
                if (current.HasNonWhitespace || completedChapterCount > 0 || current.Title != "前言") FinishCurrent();
                current = new ChapterBuilder(heading, lineNumber, isVolume, includeLines);
                sectionTitle = heading;
                part = 1;
            }
            else
            {
                // Bound individual XHTML files, including books with no recognizable headings.
                if (current.CharacterCount >= 60_000 && current.LineCount > 0)
                {
                    FinishCurrent();
                    current = new ChapterBuilder($"{sectionTitle}（续 {++part}）", lineNumber, false, includeLines);
                }
                current.AddLine(line);
            }
        }

        if (current.HasNonWhitespace || current.Title != "前言") FinishCurrent();
        if (!hasHeadings)
        {
            for (var i = 0; i < chapters.Count; i++)
            {
                var globalNumber = selectedStart + i + 1;
                chapters[i] = chapters[i] with
                {
                    Title = completedChapterCount == 1 ? "正文" : $"第 {globalNumber} 部分"
                };
            }
        }
        chapterCount = completedChapterCount;
        return chapters;
    }

    private static string NormalizeHeading(string line)
    {
        var heading = line.Trim().TrimStart('\uFEFF');
        var prefix = NumberedListPrefix.Match(heading);
        if (prefix.Success && prefix.Groups["index"].Value == prefix.Groups["chapter"].Value)
            heading = heading[prefix.Length..];
        return heading;
    }

    private static Regex? CreateCustomHeadingRegex(TxtImportRules rules)
    {
        if (rules.HeadingPreset != TxtHeadingRulePreset.CustomRegex) return null;
        if (string.IsNullOrWhiteSpace(rules.CustomHeadingPattern))
            throw new ArgumentException("自定义目录规则不能为空。", nameof(rules));
        if (rules.CustomHeadingPattern.Length > 256)
            throw new ArgumentException("自定义目录规则不能超过 256 个字符。", nameof(rules));
        return new Regex(rules.CustomHeadingPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    private static bool IsHeading(string heading, TxtImportRules rules, Regex? customHeading)
    {
        if (heading.Length is 0 or > 60) return false;
        var matchesRule = rules.HeadingPreset switch
        {
            TxtHeadingRulePreset.Automatic => NumberedHeading.IsMatch(heading) || SpecialHeading.IsMatch(heading),
            TxtHeadingRulePreset.ChineseNumbered => ChineseNumberedHeading.IsMatch(heading),
            TxtHeadingRulePreset.EnglishChapter => EnglishChapterHeading.IsMatch(heading),
            TxtHeadingRulePreset.SpecialHeadings => SpecialHeading.IsMatch(heading),
            TxtHeadingRulePreset.NoHeadings => false,
            TxtHeadingRulePreset.CustomRegex => customHeading!.IsMatch(heading),
            _ => false
        };
        // Questions and exclamations are common in novel chapter titles.
        // Keep sentence separators as a conservative prose guard.
        return matchesRule
            && (!SentencePunctuation.IsMatch(heading) || SpacedNumberedHeading.IsMatch(heading) || ArabicNumberedHeading.IsMatch(heading));
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            yield return text[start..index];
            start = index + 1;
        }
        yield return text[start..];
    }

    private static string? ExtractAuthorBeforeFirstChapter(string text, TxtImportRules rules, CancellationToken cancellationToken)
    {
        var customHeading = CreateCustomHeadingRegex(rules);
        var lineNumber = 0;
        var scannedCharacters = 0;
        foreach (var line in EnumerateLines(text))
        {
            if (++lineNumber > 20_000 || scannedCharacters >= 1_000_000) break;
            cancellationToken.ThrowIfCancellationRequested();
            scannedCharacters += line.Length;
            var heading = NormalizeHeading(line);
            if (heading.Length is > 0 and <= 60)
            {
                var isNumbered = rules.HeadingPreset switch
                {
                    TxtHeadingRulePreset.Automatic => NumberedHeading.IsMatch(heading),
                    TxtHeadingRulePreset.ChineseNumbered => ChineseNumberedHeading.IsMatch(heading),
                    TxtHeadingRulePreset.EnglishChapter => EnglishChapterHeading.IsMatch(heading),
                    TxtHeadingRulePreset.CustomRegex => customHeading!.IsMatch(heading),
                    _ => false
                };
                if (isNumbered) return null;
            }
            var match = AuthorPattern.Match(line);
            var author = match.Groups["author"].Success ? match.Groups["author"].Value : match.Groups["english"].Value;
            if (match.Success && !string.IsNullOrWhiteSpace(author)) return author.Trim();
        }
        return null;
    }

    private sealed record DecodedText(string Text, TxtTextEncoding Encoding);

    private sealed class ChapterBuilder(string title, int startLine, bool volume, bool includeLines)
    {
        public string Title { get; } = title;
        public int StartLine { get; } = startLine;
        public bool Volume { get; } = volume;
        public List<string> Lines { get; } = [];
        public bool HasNonWhitespace { get; private set; }
        public int LineCount { get; private set; }
        public int CharacterCount { get; private set; }

        public void AddLine(string line)
        {
            if (includeLines) Lines.Add(line);
            LineCount++;
            CharacterCount += line.Length;
            if (!string.IsNullOrWhiteSpace(line)) HasNonWhitespace = true;
        }
    }

    private static void Write(ZipArchive archive, string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, compression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
