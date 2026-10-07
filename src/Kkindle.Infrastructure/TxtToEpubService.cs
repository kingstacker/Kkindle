using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

/// <summary>Converts plain text to a deterministic, self-contained EPUB without Calibre.</summary>
public static class TxtToEpubService
{
    private static readonly Regex NumberedHeading = new(
        @"^(?:第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[章回节卷部篇](?:\s*[:：、.．\-—]?\s*.*)?|Chapter\s+\d+(?:\s*[:.\-–—]?\s*.*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SpecialHeading = new(
        @"^(?:序章|序言|序|楔子|前言|引子|尾声|终章|后记|结束语)(?:\s+.{1,30})?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex VolumeHeading = new(@"^第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[卷部篇]", RegexOptions.CultureInvariant);
    private static readonly Regex NumberedListPrefix = new(@"^(?<index>\d+)[.．、]\s*(?=第\s*(?<chapter>\d+)\s*章)", RegexOptions.CultureInvariant);
    private static readonly Regex SpacedNumberedHeading = new(@"^第\s*[零〇一二三四五六七八九十百千万两\d]+\s*[章回节卷部篇]\s+", RegexOptions.CultureInvariant);
    private static readonly Regex ArabicNumberedHeading = new(@"^第\s*\d+\s*[章回节卷部篇]", RegexOptions.CultureInvariant);
    private static readonly Regex AuthorPattern = new(
        @"(?:作者|著者|作\s*者)\s*[:：]\s*(?<author>[^\s,，;；。]+(?:\s*[/、&＆]\s*[^\s,，;；。]+)*)|(?:^|\s)Author\s*[:：]\s*(?<english>[^\s,，;；。]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private sealed record Chapter(string Title, List<string> Lines, bool Volume = false);

    public static async Task ConvertAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var text = Decode(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("TXT 文件没有可导入的正文。");
        // Invalid XML control characters cannot be represented in an EPUB document.
        text = string.Concat(text.EnumerateRunes().Where(r => r.Value > 0xFFFF || XmlConvert.IsXmlChar((char)r.Value)).Select(r => r.ToString()));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("TXT 文件没有可导入的正文。");
        var title = Path.GetFileNameWithoutExtension(sourcePath);
        var author = ExtractAuthorBeforeFirstChapter(text);
        var chapters = Split(text, cancellationToken);
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

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        try { return reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
        }
    }

    private static List<Chapter> Split(string text, CancellationToken cancellationToken)
    {
        var chapters = new List<Chapter>();
        var current = new Chapter("前言", []);
        var length = 0;
        var hasHeadings = false;
        var sectionTitle = current.Title;
        var part = 1;
        foreach (var line in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var heading = line.Trim().TrimStart('\uFEFF');
            var prefix = NumberedListPrefix.Match(heading);
            if (prefix.Success && prefix.Groups["index"].Value == prefix.Groups["chapter"].Value)
                heading = heading[prefix.Length..];
            var isHeading = heading.Length <= 60 && heading.Length > 0
                // Questions and exclamations are common in novel chapter titles.
                // Keep sentence separators as a conservative prose guard.
                && (!Regex.IsMatch(heading, @"[。；;，,]") || SpacedNumberedHeading.IsMatch(heading) || ArabicNumberedHeading.IsMatch(heading))
                && (NumberedHeading.IsMatch(heading) || SpecialHeading.IsMatch(heading));
            if (isHeading)
            {
                hasHeadings = true;
                if (current.Lines.Any(l => !string.IsNullOrWhiteSpace(l)) || chapters.Count > 0 || current.Title != "前言") chapters.Add(current);
                current = new Chapter(heading, [], VolumeHeading.IsMatch(heading));
                sectionTitle = heading;
                part = 1;
                length = 0;
            }
            else
            {
                // Bound individual XHTML files, including books with no recognizable headings.
                if (length >= 60_000 && current.Lines.Count > 0)
                {
                    chapters.Add(current);
                    current = new Chapter($"{sectionTitle}（续 {++part}）", []);
                    length = 0;
                }
                current.Lines.Add(line);
                length += line.Length;
            }
        }
        if (current.Lines.Any(l => !string.IsNullOrWhiteSpace(l)) || current.Title != "前言") chapters.Add(current);
        if (!hasHeadings)
            for (var i = 0; i < chapters.Count; i++)
                chapters[i] = chapters[i] with { Title = chapters.Count == 1 ? "正文" : $"第 {i + 1} 部分" };
        return chapters;
    }

    private static string? ExtractAuthorBeforeFirstChapter(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var heading = line.Trim().TrimStart('\uFEFF');
            if (heading.Length is > 0 and <= 60 && NumberedHeading.IsMatch(heading)) return null;
            var match = AuthorPattern.Match(line);
            var author = match.Groups["author"].Success ? match.Groups["author"].Value : match.Groups["english"].Value;
            if (match.Success && !string.IsNullOrWhiteSpace(author)) return author.Trim();
        }
        return null;
    }

    private static void Write(ZipArchive archive, string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, compression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
