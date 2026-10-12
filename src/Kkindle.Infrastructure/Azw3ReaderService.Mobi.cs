using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;
using static Kkindle.Infrastructure.Azw3Binary;

namespace Kkindle.Infrastructure;

public static partial class Azw3ReaderService
{
    private static void PrepareLegacyMobi(byte[] header, Func<int, byte[]> record, int recordCount,
        string fallbackTitle, string destination, CancellationToken token)
    {
        if (header.Length < 132 || !Magic(Slice(header, 16, 4), "MOBI")) throw Invalid();
        if (U32(header, 36) > 7) throw new NotSupportedException("此 MOBI 版本暂不支持。");
        if (U16(header, 12) != 0) throw new NotSupportedException("此 MOBI 受 DRM 保护，无法在 Kreader 中打开。");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = U32(header, 28) switch
        {
            65001 => new UTF8Encoding(false, true),
            1252 => Encoding.GetEncoding(1252),
            var code => throw new NotSupportedException($"MOBI 使用了不支持的字符编码：{code}。")
        };
        var title = encoding.GetString(Slice(header, Int(header, 84), Int(header, 88))).Trim('\0');
        if (string.IsNullOrWhiteSpace(title)) title = fallbackTitle;
        var author = "未知作者";
        var headerLength = Int(header, 20);
        if (headerLength < 116 || 16L + headerLength > header.Length) throw Invalid();
        var exth = 16 + headerLength;
        if (exth <= header.Length - 12 && Magic(Slice(header, exth, 4), "EXTH"))
        {
            var end = checked(exth + Int(header, exth + 4));
            var count = Int(header, exth + 8);
            if (end > header.Length || count > 4096) throw Invalid();
            var p = exth + 12;
            for (var i = 0; i < count; i++)
            {
                var type = U32(header, p); var length = Int(header, p + 4);
                if (length < 8 || p + (long)length > end) throw Invalid();
                if (type is 100 or 503)
                {
                    var value = encoding.GetString(Slice(header, p + 8, length - 8)).Trim('\0');
                    if (type == 100) author = value;
                    if (type == 503 && !string.IsNullOrWhiteSpace(value)) title = value;
                }
                p += length;
            }
        }
        var textRecords = U16(header, 8);
        var declaredLength = Int(header, 4);
        if (textRecords == 0 || textRecords >= recordCount || declaredLength > MaximumOutput) throw Invalid();
        var compression = U16(header, 0);
        var flags = headerLength >= 228 ? U16(header, 242) : 0;
        var huff = compression == 0x4448 ? new HuffDic(record, Int(header, 112), Int(header, 116), token) : null;
        using var output = new MemoryStream();
        for (var i = 1; i <= textRecords; i++)
        {
            token.ThrowIfCancellationRequested();
            var bytes = record(i);
            var end = bytes.Length;
            for (var trailers = flags >> 1; trailers != 0; trailers >>= 1)
            {
                if ((trailers & 1) == 0) continue;
                var size = 0;
                for (var p = Math.Max(0, end - 4); p < end; p++)
                {
                    if ((bytes[p] & 128) != 0) size = 0;
                    size = (size << 7) | (bytes[p] & 127);
                }
                if (size <= 0 || size > end) throw Invalid();
                end -= size;
            }
            if ((flags & 1) != 0)
            {
                if (end == 0) throw Invalid();
                end -= (bytes[end - 1] & 3) + 1;
            }
            var payload = Slice(bytes, 0, end);
            var expanded = compression switch
            {
                1 => payload,
                2 => PalmDoc(payload),
                0x4448 => huff!.Decode(payload),
                _ => throw new NotSupportedException("MOBI 使用了不支持的文本压缩方式。")
            };
            if (output.Length + expanded.Length > MaximumOutput) throw Invalid();
            output.Write(expanded);
        }
        if (output.Length < declaredLength) throw Invalid();
        var raw = Slice(output.ToArray(), 0, declaredLength);
        var indexedNavigation = new List<Navigation>();
        if (headerLength >= 232 && U32(header, 244) != uint.MaxValue)
        {
            var index = ReadIndex(record, Int(header, 244), encoding, token);
            foreach (var entry in index.Entries)
            {
                if (!entry.Tags.ContainsKey(1)) continue;
                var position = entry.Value(1);
                indexedNavigation.Add(new Navigation(index.Strings.GetValueOrDefault(entry.Value(3), entry.Text),
                    $"part-00000.xhtml#mobi-pos-{position}", Math.Clamp(entry.Value(4), 0, 32), position));
            }
            indexedNavigation = indexedNavigation.OrderBy(n => n.ReadingOrder).ToList();
        }
        // filepos is a byte offset, not a character offset (CJK/UTF-8 differ).
        var byteMarkup = Encoding.Latin1.GetString(raw);
        var positions = Regex.Matches(byteMarkup, @"\bfilepos\s*=\s*['"" ]?(\d+)",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Concat(indexedNavigation.Select(n => n.ReadingOrder)).Distinct().Order().ToArray();
        var insertions = new SortedDictionary<int, List<int>>();
        foreach (var position in positions)
        {
            if (position < 0 || position >= raw.Length) throw Invalid();
            var insertion = position;
            var open = byteMarkup.LastIndexOf('<', position);
            var close = byteMarkup.LastIndexOf('>', position);
            // Put the anchor before an opening tag, or immediately after the containing tag.
            if (open > close)
            {
                var next = byteMarkup.IndexOf('>', position);
                if (next < 0) throw Invalid();
                insertion = byteMarkup[open + 1] == '/' ? open : next + 1;
            }
            // Never split a UTF-8 code point.
            if (U32(header, 28) == 65001)
                while (insertion < raw.Length && (raw[insertion] & 0xC0) == 0x80) insertion++;
            if (!insertions.TryGetValue(insertion, out var anchors)) insertions[insertion] = anchors = [];
            anchors.Add(position);
        }
        var markup = new StringBuilder();
        var cursor = 0;
        foreach (var insertion in insertions)
        {
            markup.Append(encoding.GetString(raw, cursor, insertion.Key - cursor));
            foreach (var position in insertion.Value) markup.Append($"<a id=\"mobi-pos-{position}\"></a>");
            cursor = insertion.Key;
        }
        markup.Append(encoding.GetString(raw, cursor, raw.Length - cursor));
        var doc = new HtmlDocument();
        doc.LoadHtml(markup.ToString());
        var resources = new List<(string Path, string Media, byte[] Data)>();
        var imageNames = new Dictionary<int, string>();
        var firstResource = U32(header, 108) == uint.MaxValue ? recordCount : Int(header, 108);
        if (firstResource < 0 || firstResource > recordCount) throw Invalid();
        for (var i = firstResource; i < recordCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var bytes = record(i);
            var type = ResourceType(bytes);
            if (type is null) continue;
            var number = i - firstResource + 1;
            var name = $"resource-{number}.{type.Value.Extension}";
            imageNames[number] = name;
            resources.Add((name, type.Value.Media, bytes));
        }
        var navigation = new List<Navigation>(indexedNavigation);
        foreach (var node in doc.DocumentNode.Descendants().ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (node.NodeType != HtmlNodeType.Element) continue;
            var filepos = node.GetAttributeValue("filepos", "");
            if (int.TryParse(filepos, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            {
                node.SetAttributeValue("href", $"part-00000.xhtml#mobi-pos-{position}");
                node.Attributes.Remove("filepos");
                var label = HtmlEntity.DeEntitize(node.InnerText).Trim();
                if (indexedNavigation.Count == 0 && node.Name == "a" && label.Length > 0)
                    navigation.Add(new Navigation(label, $"part-00000.xhtml#mobi-pos-{position}", 0, position));
            }
            if (node.Name == "img" && int.TryParse(node.GetAttributeValue("recindex", ""), out var image))
            {
                if (imageNames.TryGetValue(image, out var name)) node.SetAttributeValue("src", name);
                node.Attributes.Remove("recindex");
            }
            if (node.Name == "mbp:pagebreak")
            {
                node.Name = "div";
                node.SetAttributeValue("style", "break-before:page;page-break-before:always");
            }
            if (node.GetAttributeValue("name", "") is { Length: > 0 } anchor && node.GetAttributeValue("id", "").Length == 0)
                node.SetAttributeValue("id", anchor);
        }
        // Use the physical contents when available; headings provide a useful fallback.
        if (navigation.Count == 0)
        {
            foreach (var heading in doc.DocumentNode.Descendants().Where(n => n.Name is "h1" or "h2" or "h3"))
            {
                var id = heading.GetAttributeValue("id", "");
                if (id.Length == 0) { id = $"mobi-heading-{navigation.Count}"; heading.SetAttributeValue("id", id); }
                navigation.Add(new Navigation(HtmlEntity.DeEntitize(heading.InnerText).Trim(), $"part-00000.xhtml#{id}", 0, navigation.Count));
            }
        }
        if (navigation.Count == 0) navigation.Add(new Navigation(title, "part-00000.xhtml", 0, 0));
        navigation = navigation.DistinctBy(n => n.Target).ToList();
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var head = doc.DocumentNode.SelectSingleNode("//head");
        var htmlHead = new XElement(Html + "head", new XElement(Html + "title", title));
        if (head is not null) htmlHead.Add(head.ChildNodes.Where(n => n.Name != "title").Select(n => ToXml(n, Html)).Where(n => n is not null));
        var xmlBody = new XElement(Html + "body", body.ChildNodes.Select(n => ToXml(n, Html)).Where(n => n is not null));
        var documents = SplitMobiElement(xmlBody, token)
            .Select(part => new XDocument(new XElement(Html + "html", new XElement(htmlHead), part))).ToList();
        var anchorsToParts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < documents.Count; i++)
            foreach (var element in documents[i].Descendants())
                if (element.Attribute("id") is { } id) anchorsToParts.TryAdd(id.Value, $"part-{i:D5}.xhtml");
        string RewriteTarget(string target)
        {
            var separator = target.IndexOf('#');
            if (separator < 0) return target;
            var file = target[..separator];
            var anchor = target[(separator + 1)..];
            return (file.Length == 0 || file == "part-00000.xhtml") && anchorsToParts.TryGetValue(anchor, out var part)
                ? part + "#" + anchor : target;
        }
        foreach (var link in documents.SelectMany(d => d.Descendants()).Attributes("href"))
            link.Value = RewriteTarget(link.Value);
        navigation = navigation.Select(n => n with { Target = RewriteTarget(n.Target) }).ToList();
        WriteEpub(destination, title, author, documents, navigation, resources, [], token);
    }

    // Keep large legacy books responsive while retaining their containing markup,
    // all text, and cross-part anchors. Never split inside a text node.
    private static IEnumerable<XElement> SplitMobiElement(XElement element, CancellationToken token)
    {
        const int maximumPartCharacters = 64 * 1024;
        token.ThrowIfCancellationRequested();
        if (!element.HasElements || element.ToString(SaveOptions.DisableFormatting).Length <= maximumPartCharacters)
        {
            yield return new XElement(element);
            yield break;
        }
        XElement NewPart(bool first) => new(element.Name, element.Attributes()
            .Where(a => first || a.Name.LocalName is not ("id" or "name")));
        var part = NewPart(true);
        var length = 0;
        foreach (var child in element.Nodes())
        {
            var pieces = child is XElement nested ? SplitMobiElement(nested, token).Cast<XNode>() : new[] { child };
            foreach (var piece in pieces)
            {
                var size = piece.ToString(SaveOptions.DisableFormatting).Length;
                if (length > 0 && length + size > maximumPartCharacters)
                {
                    yield return part;
                    part = NewPart(false);
                    length = 0;
                }
                // LINQ to XML copies attached nodes, preserving the source tree.
                part.Add(piece);
                length += size;
            }
        }
        if (part.Nodes().Any()) yield return part;
    }
}
