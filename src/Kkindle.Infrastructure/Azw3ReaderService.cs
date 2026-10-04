// KF8 reconstruction follows KindleUnpack; see ThirdParty/KindleUnpack/NOTICE.md.
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using static Kkindle.Infrastructure.Azw3Binary;

namespace Kkindle.Infrastructure;

/// <summary>Builds Kreader resources from unencrypted KF8 without an external runtime.</summary>
public static class Azw3ReaderService
{
    private sealed record Part(byte[] Data, int Start, int End);
    private sealed record Fragment(int Insert, int File, int Length);
    private sealed record Navigation(string Title, string Target, int Level, int ReadingOrder);
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly Regex KindleUri = new(@"kindle:(?:embed|flow):([0-9A-V]+)(?:\?[^\s'""<>)]*)?", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex PositionUri = new(@"kindle:pos:fid:([0-9A-V]+):off:([0-9A-V]+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static async Task PrepareEpubAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(sourcePath).Length > 512L * 1024 * 1024) throw new InvalidDataException("AZW3 文件过大，无法创建阅读缓存。");
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        try
        {
            await Task.Run(() => Prepare(bytes, Path.GetFileNameWithoutExtension(sourcePath), destinationPath, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is OverflowException or IndexOutOfRangeException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("AZW3 文件结构损坏或超出阅读器支持的范围。", exception);
        }
    }

    private static void Prepare(byte[] data, string fallbackTitle, string destination, CancellationToken token)
    {
        if (data.Length < 78 || Encoding.ASCII.GetString(Slice(data, 60, 8)) != "BOOKMOBI") throw Invalid();
        var recordCount = U16(data, 76);
        var offsets = new int[recordCount + 1];
        for (var i = 0; i < recordCount; i++)
        {
            offsets[i] = Int(data, 78 + 8 * i);
            if (offsets[i] < 78 + recordCount * 8 || offsets[i] >= data.Length || (i > 0 && offsets[i] <= offsets[i - 1])) throw Invalid();
        }
        offsets[recordCount] = data.Length;
        byte[] Record(int number)
        {
            token.ThrowIfCancellationRequested();
            if (number < 0 || number >= recordCount) throw Invalid();
            return Slice(data, offsets[number], offsets[number + 1] - offsets[number]);
        }
        var firstHeader = Record(0);
        var start = 0;
        var header = firstHeader;
        if (!IsKf8(header))
        {
            // Hybrid MOBI/KF8 containers separate the second header with BOUNDARY.
            start = -1;
            for (var i = 1; i < recordCount - 1; i++)
                if (Magic(Record(i), "BOUNDARY") && IsKf8(Record(i + 1))) { start = i + 1; break; }
            if (start < 0) throw new NotSupportedException("此文件不是支持的 KF8/AZW3 书籍。");
            header = Record(start);
        }
        if (U16(header, 12) != 0) throw new NotSupportedException("此 AZW3 受 DRM 保护，无法在 Kreader 中打开。");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = U32(header, 28) == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(1252);
        var title = encoding.GetString(Slice(header, Int(header, 84), Int(header, 88))).Trim('\0');
        if (string.IsNullOrWhiteSpace(title)) title = fallbackTitle;
        var author = "未知作者";
        var exth = 16 + Int(header, 20);
        if (exth <= header.Length - 12 && Magic(Slice(header, exth, 4), "EXTH"))
        {
            var p = exth + 12;
            var n = Int(header, exth + 8);
            if (n > 4096) throw Invalid();
            for (var i = 0; i < n; i++)
            {
                var type = U32(header, p); var length = Int(header, p + 4);
                if (length < 8) throw Invalid();
                var value = Slice(header, p + 8, length - 8);
                if (type == 100) author = encoding.GetString(value);
                if (type == 503) title = encoding.GetString(value);
                p += length;
            }
        }
        var compression = U16(header, 0);
        var textRecords = U16(header, 8);
        var declaredLength = Int(header, 4);
        if (declaredLength > MaximumOutput || textRecords == 0) throw Invalid();
        var flags = U16(header, 242);
        var huff = compression == 0x4448 ? new HuffDic(Record, start + Int(header, 112), Int(header, 116), token) : null;
        using var rawStream = new MemoryStream();
        for (var i = 1; i <= textRecords; i++)
        {
            var record = Record(start + i);
            var end = record.Length;
            for (var trailer = flags >> 1; trailer != 0; trailer >>= 1)
            {
                if ((trailer & 1) == 0) continue;
                var size = 0;
                for (var p = Math.Max(0, end - 4); p < end; p++)
                {
                    if ((record[p] & 128) != 0) size = 0;
                    size = (size << 7) | (record[p] & 127);
                }
                if (size <= 0 || size > end) throw Invalid();
                end -= size;
            }
            if ((flags & 1) != 0)
            {
                if (end <= 0) throw Invalid();
                end -= (record[end - 1] & 3) + 1;
            }
            var compressed = Slice(record, 0, end);
            var expanded = compression switch { 1 => compressed, 2 => PalmDoc(compressed), 0x4448 => huff!.Decode(compressed), _ => throw new NotSupportedException("AZW3 使用了不支持的文本压缩方式。") };
            if (rawStream.Length + expanded.Length > MaximumOutput) throw Invalid();
            rawStream.Write(expanded);
        }
        var raw = rawStream.ToArray();
        if (raw.Length < declaredLength) throw Invalid();
        raw = Slice(raw, 0, declaredLength);
        var flows = new List<byte[]> { raw };
        if (U32(header, 192) != uint.MaxValue && U32(header, 196) > 1)
        {
            var fdst = Record(start + Int(header, 192));
            if (!Magic(fdst, "FDST")) throw Invalid();
            var count = Int(fdst, 8);
            if (count > 100000) throw Invalid();
            flows.Clear();
            for (var i = 0; i < count; i++)
            {
                var from = Int(fdst, 12 + i * 8); var to = Int(fdst, 16 + i * 8);
                flows.Add(Slice(raw, from, to - from));
            }
        }
        var skeleton = ReadIndex(Record, start + Int(header, 252), encoding, token);
        var fragmentIndex = ReadIndex(Record, start + Int(header, 248), encoding, token);
        var fragments = fragmentIndex.Entries.Select(e => new Fragment(int.Parse(e.Text, System.Globalization.CultureInfo.InvariantCulture), e.Value(3), e.Value(6, 1))).ToList();
        var parts = new List<Part>();
        var fragmentNumber = 0;
        foreach (var skel in skeleton.Entries)
        {
            token.ThrowIfCancellationRequested();
            var position = skel.Value(6); var length = skel.Value(6, 1); var count = skel.Value(1);
            var body = Slice(flows[0], position, length);
            var cursor = position + length;
            for (var j = 0; j < count; j++)
            {
                if (fragmentNumber >= fragments.Count) throw Invalid();
                var fragment = fragments[fragmentNumber++];
                var insertion = fragment.Insert - position;
                if (insertion < 0 || insertion > body.Length) throw Invalid();
                var payload = Slice(flows[0], cursor, fragment.Length);
                if (body.Length + payload.Length > MaximumOutput) throw Invalid();
                var combined = new byte[body.Length + payload.Length];
                body.AsSpan(0, insertion).CopyTo(combined);
                payload.CopyTo(combined, insertion);
                body.AsSpan(insertion).CopyTo(combined.AsSpan(insertion + payload.Length));
                body = combined;
                cursor += fragment.Length;
            }
            parts.Add(new Part(body, position, cursor));
        }
        if (parts.Count == 0 || fragmentNumber != fragments.Count) throw Invalid();
        var resources = new Dictionary<int, (string Path, string Media, byte[] Data)>();
        var firstResource = U32(firstHeader, 108) == uint.MaxValue ? recordCount : Int(firstHeader, 108);
        if (firstResource < 0 || firstResource > recordCount) throw Invalid();
        for (var r = firstResource; r < recordCount; r++)
        {
            var resource = Record(r);
            if (Magic(resource, "FONT")) resource = DecodeFont(resource);
            var type = ResourceType(resource);
            if (type is not null) resources[r - firstResource + 1] = ($"resource-{r - firstResource + 1}.{type.Value.Extension}", type.Value.Media, resource);
        }
        var flowNames = new Dictionary<int, (string Path, string Media)>();
        for (var i = 1; i < flows.Count; i++)
        {
            var svg = encoding.GetString(flows[i]).Contains("<svg", StringComparison.OrdinalIgnoreCase);
            flowNames[i] = ($"flow-{i}." + (svg ? "svg" : "css"), svg ? "image/svg+xml" : "text/css");
        }
        string ResolvePosition(int fid, int offset)
        {
            if (fid < 0 || fid >= fragments.Count) throw Invalid();
            var fragment = fragments[fid];
            var position = checked(fragment.Insert + offset);
            var index = parts.FindIndex(p => position >= p.Start && position < p.End);
            if (index < 0) index = fragment.File;
            if (index < 0 || index >= parts.Count) throw Invalid();
            var part = parts[index];
            var offsetInPart = Math.Clamp(position - part.Start, 0, part.Data.Length);
            var markup = Encoding.Latin1.GetString(part.Data);
            var nextClose = markup.IndexOf('>', offsetInPart);
            var nextOpen = markup.IndexOf('<', offsetInPart);
            if (nextClose >= 0 && (nextOpen == offsetInPart || nextOpen < 0 || nextClose < nextOpen))
                offsetInPart = nextClose + 1;
            var prefix = markup[..offsetInPart];
            var tags = Regex.Matches(prefix, @"<[^>]+>");
            var anchor = "";
            for (var t = tags.Count - 1; t >= 0; t--)
            {
                var tag = tags[t].Value;
                if (tag.StartsWith("<body", StringComparison.OrdinalIgnoreCase)) break;
                var match = Regex.Match(tag, "\\s(?:id|name)\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                if (match.Success) { anchor = match.Groups[1].Value; break; }
                match = Regex.Match(tag, "\\said\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                if (match.Success) { anchor = "aid-" + match.Groups[1].Value; break; }
            }
            return $"part-{index:D5}.xhtml" + (anchor.Length > 0 ? "#" + anchor : "");
        }
        string Rewrite(string content)
        {
            content = PositionUri.Replace(content, m => ResolvePosition(Base32(m.Groups[1].Value), Base32(m.Groups[2].Value)));
            return KindleUri.Replace(content, m =>
            {
                var number = Base32(m.Groups[1].Value);
                if (m.Value.StartsWith("kindle:embed:", StringComparison.OrdinalIgnoreCase))
                    return resources.TryGetValue(number, out var resource) ? resource.Path : "";
                return flowNames.TryGetValue(number, out var flow) ? flow.Path : "";
            });
        }
        var navigation = new List<Navigation>();
        if (U32(header, 244) != uint.MaxValue)
        {
            var ncx = ReadIndex(Record, start + Int(header, 244), encoding, token);
            foreach (var entry in ncx.Entries)
            {
                if (!entry.Tags.ContainsKey(6)) continue;
                var label = ncx.Strings.GetValueOrDefault(entry.Value(3), entry.Text);
                var fragment = fragments[entry.Value(6)];
                var offset = entry.Value(6, 1);
                navigation.Add(new Navigation(
                    label,
                    ResolvePosition(entry.Value(6), offset),
                    Math.Clamp(entry.Value(4), 0, 32),
                    checked(fragment.Insert + offset)));
            }

            // Kindle's NCX index can group entries by hierarchy depth instead
            // of emitting a depth-first sequence. EPUB nav requires a parent
            // to precede its children, otherwise the following volume's
            // chapters can become children of a trailing entry such as
            // "封底". The fragment position is the book's actual reading order.
            navigation = navigation
                .Select((item, index) => (Item: item, Index: index))
                .OrderBy(entry => entry.Item.ReadingOrder)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Item)
                .ToList();
        }
        var documents = new List<XDocument>();
        for (var i = 0; i < parts.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var doc = new HtmlDocument();
            doc.LoadHtml(Rewrite(encoding.GetString(parts[i].Data)));
            foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
            {
                var aid = node.GetAttributeValue("aid", "");
                if (aid.Length > 0 && node.GetAttributeValue("id", "").Length == 0) node.SetAttributeValue("id", "aid-" + aid);
            }
            var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
            var htmlBody = new XElement(Html + "body", body.ChildNodes.Select(n => ToXml(n, Html)).Where(n => n is not null));
            var htmlHead = new XElement(Html + "head", new XElement(Html + "title", title));
            var head = doc.DocumentNode.SelectSingleNode("//head");
            if (head is not null) htmlHead.Add(head.ChildNodes.Where(n => n.Name != "title").Select(n => ToXml(n, Html)).Where(n => n is not null));
            documents.Add(new XDocument(new XElement(Html + "html", htmlHead, htmlBody)));
        }
        if (navigation.Count == 0)
            for (var i = 0; i < documents.Count; i++)
                navigation.Add(new Navigation(documents[i].Descendants().FirstOrDefault(n => n.Name.LocalName is "h1" or "h2" or "h3")?.Value ?? $"第 {i + 1} 章", $"part-{i:D5}.xhtml", 0, i));
        WriteEpub(destination, title, author, documents, navigation, resources.Values.ToList(), flowNames.Select(p => (p.Value.Path, p.Value.Media, Encoding.UTF8.GetBytes(Rewrite(encoding.GetString(flows[p.Key]))))).ToList(), token);
    }

    private static bool IsKf8(byte[] record) => record.Length >= 264 && Encoding.ASCII.GetString(record, 16, 4) == "MOBI" && U32(record, 36) == 8;
    private static int Base32(string value)
    {
        var number = 0;
        foreach (var c in value.ToUpperInvariant())
        {
            var digit = "0123456789ABCDEFGHIJKLMNOPQRSTUV".IndexOf(c);
            if (digit < 0) throw Invalid();
            number = checked(number * 32 + digit);
        }
        return number;
    }
    private static (string Extension, string Media)? ResourceType(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 1, 0, 0 }) || Magic(bytes, "true") || Magic(bytes, "ttcf")) return ("ttf", "font/ttf");
        if (Magic(bytes, "OTTO")) return ("otf", "font/otf");
        if (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 })) return ("jpg", "image/jpeg");
        if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ("png", "image/png");
        if (Magic(bytes, "GIF8")) return ("gif", "image/gif");
        if (Magic(bytes, "BM")) return ("bmp", "image/bmp");
        if (Magic(bytes, "RIFF") && bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ("webp", "image/webp");
        return null;
    }
    private static byte[] DecodeFont(byte[] data)
    {
        var expected = Int(data, 4); var flags = U32(data, 8); var start = Int(data, 12);
        if (expected > 32 * 1024 * 1024) throw Invalid();
        var font = Slice(data, start, data.Length - start);
        if ((flags & 2) != 0)
        {
            var key = Slice(data, Int(data, 20), Int(data, 16));
            if (key.Length == 0) throw Invalid();
            for (var i = 0; i < Math.Min(1040, font.Length); i++) font[i] ^= key[i % key.Length];
        }
        if ((flags & 1) != 0)
        {
            using var source = new MemoryStream(font);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                if (output.Length + read > expected) throw Invalid();
                output.Write(buffer, 0, read);
            }
            font = output.ToArray();
        }
        if (font.Length != expected) throw Invalid();
        return font;
    }
    private static XNode? ToXml(HtmlNode node, XNamespace ns, int depth = 0)
    {
        if (depth > 128) throw Invalid();
        if (node.NodeType == HtmlNodeType.Text) return new XText(Clean(HtmlEntity.DeEntitize(node.InnerText)));
        if (node.NodeType != HtmlNodeType.Element || node.Name.StartsWith('?') || node.Name.StartsWith('!')) return null;
        if (node.Name == "svg") ns = "http://www.w3.org/2000/svg";
        var name = node.Name.Contains(':') ? node.Name.Split(':').Last() : node.Name;
        if (name.Length == 0) return null;
        var element = new XElement(ns + XmlConvert.EncodeLocalName(name));
        foreach (var attr in node.Attributes)
        {
            if (attr.Name == "xmlns" || attr.Name.StartsWith("xmlns:")) continue;
            XName attributeName = attr.Name == "xlink:href" ? XName.Get("href", "http://www.w3.org/1999/xlink")
                : attr.Name == "xml:lang" ? XNamespace.Xml + "lang" : XmlConvert.EncodeLocalName(attr.Name.Replace(':', '-'));
            element.SetAttributeValue(attributeName, Clean(HtmlEntity.DeEntitize(attr.Value)));
        }
        element.Add(node.ChildNodes.Select(child => ToXml(child, ns, depth + 1)).Where(n => n is not null));
        return element;
    }
    private static string Clean(string value) => string.Concat(value.EnumerateRunes().Where(r => r.Value > 65535 || XmlConvert.IsXmlChar((char)r.Value)).Select(r => r.ToString()));

    private static void WriteEpub(string destination, string title, string author, List<XDocument> documents, List<Navigation> navigation,
        List<(string Path, string Media, byte[] Data)> resources, List<(string Path, string Media, byte[] Data)> flows, CancellationToken token)
    {
        var created = false;
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            created = true;
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            void Write(string path, byte[] bytes, CompressionLevel compression = CompressionLevel.Optimal)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(path, compression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
            void Text(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));
            Write("mimetype", "application/epub+zip"u8.ToArray(), CompressionLevel.NoCompression);
            Text("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            var manifest = new XElement(Opf + "manifest", new XElement(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")));
            var spine = new XElement(Opf + "spine");
            for (var i = 0; i < documents.Count; i++)
            {
                var name = $"part-{i:D5}.xhtml";
                Text("OEBPS/" + name, documents[i].ToString());
                manifest.Add(new XElement(Opf + "item", new XAttribute("id", $"p{i}"), new XAttribute("href", name), new XAttribute("media-type", "application/xhtml+xml")));
                spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", $"p{i}")));
            }
            var resourceId = 0;
            foreach (var resource in resources.Concat(flows))
            {
                Write("OEBPS/" + resource.Path, resource.Data);
                manifest.Add(new XElement(Opf + "item", new XAttribute("id", $"r{resourceId++}"), new XAttribute("href", resource.Path), new XAttribute("media-type", resource.Media)));
            }
            var rootList = new XElement(Html + "ol");
            var lists = new List<XElement> { rootList };
            foreach (var item in navigation)
            {
                var level = Math.Min(item.Level, lists.Count);
                while (lists.Count > level + 1) lists.RemoveAt(lists.Count - 1);
                if (lists.Count == level)
                {
                    var parent = lists[^1].Elements().LastOrDefault();
                    if (parent is not null) { var list = new XElement(Html + "ol"); parent.Add(list); lists.Add(list); }
                }
                lists[^1].Add(new XElement(Html + "li", new XElement(Html + "a", new XAttribute("href", item.Target), Clean(item.Title))));
            }
            XNamespace epub = "http://www.idpf.org/2007/ops";
            Text("OEBPS/nav.xhtml", new XDocument(new XElement(Html + "html", new XElement(Html + "head", new XElement(Html + "title", Clean(title))),
                new XElement(Html + "body", new XElement(Html + "nav", new XAttribute(epub + "type", "toc"), rootList)))).ToString());
            Text("OEBPS/content.opf", new XDocument(new XElement(Opf + "package", new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "book-id"),
                new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc), new XElement(Dc + "identifier", new XAttribute("id", "book-id"), "urn:azw3:" + Clean(title)),
                    new XElement(Dc + "title", Clean(title)), new XElement(Dc + "creator", Clean(author)), new XElement(Dc + "language", "zh"), new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), "2000-01-01T00:00:00Z")), manifest, spine)).ToString());
        }
        catch
        {
            if (created && File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }
}
