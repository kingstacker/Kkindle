using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Kkindle.Core;
using static Kkindle.Infrastructure.Azw3Binary;

namespace Kkindle.Infrastructure;

internal sealed partial record KindleRawBookText(
    byte[] Text,
    Encoding Encoding,
    string PalmName,
    string? Asin,
    uint UniqueId,
    string Title,
    string Author,
    byte[]? UnassembledText = null)
{
    public string ExtractLegacyRange(long start, long end) => UnassembledText is null
        ? string.Empty : (this with { Text = UnassembledText, UnassembledText = null }).ExtractRange(start, end);
    public string CreateGuid() => $"{PalmName}:{UniqueId:X8}";

    public bool Matches(KindleAnnotationBookIdentity identity) =>
        string.Equals(identity.BookId, CreateBookId(), StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(Asin)
            && string.Equals(identity.Asin, Asin, StringComparison.OrdinalIgnoreCase)
            && string.Equals(identity.Guid, CreateGuid(), StringComparison.OrdinalIgnoreCase));

    public string? CreateBookId() =>
        string.IsNullOrWhiteSpace(Asin) || string.IsNullOrWhiteSpace(PalmName)
            ? null
            : $"{Asin}-EBOK-{CreateGuid()}-0";

    public string ExtractRange(long start, long end)
    {
        if (start < 0 || end <= start || end > Text.LongLength) return string.Empty;
        var actualStart = checked((int)start);
        var actualEnd = checked((int)end);
        if (actualStart > Text.Length || actualEnd > Text.Length) return string.Empty;

        while (actualStart > 0 && IsContinuation(Text[actualStart])) actualStart--;
        while (actualEnd < Text.Length && IsContinuation(Text[actualEnd])) actualEnd++;
        // Only discard a leading fragment when the byte position is proven to
        // be inside markup. A balanced inline tag after visible text must not
        // cause that preceding text to be discarded.
        var opening = Array.LastIndexOf(Text, (byte)'<', actualStart);
        var closing = Array.LastIndexOf(Text, (byte)'>', actualStart);
        if (opening > closing)
        {
            var tagEnd = Array.IndexOf(Text, (byte)'>', actualStart);
            if (tagEnd < 0 || tagEnd >= actualEnd) return string.Empty;
            actualStart = tagEnd + 1;
        }
        if (actualEnd <= actualStart) return string.Empty;

        string decoded;
        try
        {
            decoded = Encoding.GetString(Text, actualStart, actualEnd - actualStart);
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
        var document = new HtmlDocument();
        document.LoadHtml(decoded);
        var plain = HtmlEntity.DeEntitize(document.DocumentNode.InnerText);
        return WhitespaceRegex().Replace(plain, " ").Trim();
    }

    public bool TryFindRange(
        string selectedText,
        string? prefix,
        string? suffix,
        out long start,
        out long end)
    {
        start = 0;
        end = 0;
        selectedText = selectedText.Trim();
        if (selectedText.Length == 0) return false;

        var selected = Encoding.GetBytes(selectedText);
        var matches = FindAll(Text, selected, maximum: 3);
        if (matches.Count == 1)
        {
            start = matches[0];
            end = start + selected.LongLength;
            return true;
        }

        var context = string.Concat(
            string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim(),
            selectedText,
            string.IsNullOrWhiteSpace(suffix) ? string.Empty : suffix.Trim());
        if (context.Length > selectedText.Length)
        {
            var contextBytes = Encoding.GetBytes(context);
            var contextMatches = FindAll(Text, contextBytes, maximum: 2);
            if (contextMatches.Count == 1)
            {
                var selectedInContext = Encoding.GetBytes(
                    (prefix ?? string.Empty).Trim()).LongLength;
                start = contextMatches[0] + selectedInContext;
                end = start + selected.LongLength;
                return true;
            }
        }

        return TryFindVisibleRange(selectedText, prefix, suffix, out start, out end);
    }

    private bool TryFindVisibleRange(string selected, string? prefix, string? suffix,
        out long start, out long end)
    {
        start = end = 0;
        string source;
        try { source = Encoding.GetString(Text); }
        catch (DecoderFallbackException) { return false; }
        var byteOffsets = new int[source.Length + 1];
        for (var index = 0; index < source.Length;)
        {
            var length = char.IsHighSurrogate(source[index]) && index + 1 < source.Length
                && char.IsLowSurrogate(source[index + 1]) ? 2 : 1;
            for (var unit = 1; unit < length; unit++) byteOffsets[index + unit] = byteOffsets[index];
            byteOffsets[index + length] = byteOffsets[index] + Encoding.GetByteCount(source.AsSpan(index, length));
            index += length;
        }
        var document = new HtmlDocument();
        document.LoadHtml(source);
        var visible = new StringBuilder();
        var starts = new List<int>();
        var ends = new List<int>();
        foreach (var node in document.DocumentNode.Descendants().OfType<HtmlTextNode>())
        {
            if (node.Ancestors().Any(parent => parent.Name is "head" or "style" or "script")) continue;
            var text = node.Text;
            for (var index = 0; index < text.Length;)
            {
                var units = char.IsHighSurrogate(text[index]) && index + 1 < text.Length
                    && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                var decoded = text.Substring(index, units);
                if (text[index] == '&')
                {
                    var semicolon = text.IndexOf(';', index, Math.Min(32, text.Length - index));
                    if (semicolon >= 0)
                    {
                        var entity = text[index..(semicolon + 1)];
                        var replacement = HtmlEntity.DeEntitize(entity);
                        if (replacement != entity) { units = entity.Length; decoded = replacement; }
                    }
                }
                var sourceStart = node.StreamPosition + index;
                var sourceEnd = sourceStart + units;
                if (sourceStart < 0 || sourceEnd > source.Length) return false;
                foreach (var character in decoded)
                {
                    if (char.IsWhiteSpace(character)) continue;
                    visible.Append(character); starts.Add(byteOffsets[sourceStart]); ends.Add(byteOffsets[sourceEnd]);
                }
                index += units;
            }
        }
        var textValue = visible.ToString();
        selected = WhitespaceRegex().Replace(selected, string.Empty);
        var prefixValue = WhitespaceRegex().Replace(prefix ?? string.Empty, string.Empty);
        var suffixValue = WhitespaceRegex().Replace(suffix ?? string.Empty, string.Empty);
        var found = -1;
        for (var offset = 0; offset <= textValue.Length - selected.Length;)
        {
            var index = textValue.IndexOf(selected, offset, StringComparison.Ordinal);
            if (index < 0) break;
            offset = index + Math.Max(1, selected.Length);
            if (prefixValue.Length > 0 && !textValue[..index].TrimEnd().EndsWith(prefixValue, StringComparison.Ordinal)) continue;
            if (suffixValue.Length > 0 && !textValue[(index + selected.Length)..].TrimStart().StartsWith(suffixValue, StringComparison.Ordinal)) continue;
            if (found >= 0) return false;
            found = index;
        }
        if (found < 0) return false;
        start = starts[found]; end = ends[found + selected.Length - 1];
        return end > start;
    }

    private static List<long> FindAll(byte[] source, byte[] value, int maximum)
    {
        var result = new List<long>(maximum);
        if (value.Length == 0) return result;
        var offset = 0;
        while (offset <= source.Length - value.Length)
        {
            var index = source.AsSpan(offset).IndexOf(value);
            if (index < 0) break;
            result.Add(offset + (long)index);
            if (result.Count >= maximum) break;
            offset += index + Math.Max(1, value.Length);
        }
        return result;
    }

    private static bool IsContinuation(byte value) => (value & 0xC0) == 0x80;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal static class KindleBookTextService
{
    private const int MaximumFileBytes = 512 * 1024 * 1024;

    public static async Task<KindleRawBookText> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("设备书籍不存在。", sourcePath);
        if (info.Length > MaximumFileBytes)
            throw new InvalidDataException("Kindle 书籍过大，无法建立批注位置映射。");
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        return await Task.Run(() => Parse(bytes, Path.GetFileNameWithoutExtension(sourcePath), cancellationToken),
            cancellationToken);
    }

    private static KindleRawBookText Parse(byte[] data, string fallbackTitle, CancellationToken cancellationToken)
    {
        if (data.Length < 78 || Encoding.ASCII.GetString(data, 60, 8) != "BOOKMOBI") throw Invalid();
        var recordCount = U16(data, 76);
        if (recordCount < 2 || 78L + recordCount * 8L > data.Length) throw Invalid();
        var offsets = new int[recordCount + 1];
        for (var index = 0; index < recordCount; index++)
        {
            offsets[index] = Int(data, 78 + index * 8);
            if (offsets[index] < 78 + recordCount * 8
                || offsets[index] >= data.Length
                || (index > 0 && offsets[index] <= offsets[index - 1])) throw Invalid();
        }
        offsets[recordCount] = data.Length;

        byte[] Record(int number)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number < 0 || number >= recordCount) throw Invalid();
            return Slice(data, offsets[number], offsets[number + 1] - offsets[number]);
        }

        var firstHeader = Record(0);
        var start = 0;
        var header = firstHeader;
        if (!IsKf8(header))
        {
            start = -1;
            for (var index = 1; index < recordCount - 1; index++)
            {
                if (!Magic(Record(index), "BOUNDARY") || !IsKf8(Record(index + 1))) continue;
                start = index + 1;
                break;
            }
            if (start < 0)
            {
                header = firstHeader;
                start = 0;
            }
            else
            {
                header = Record(start);
            }
        }
        if (header.Length < 132 || !Magic(Slice(header, 16, 4), "MOBI")) throw Invalid();
        if (U16(header, 12) != 0)
            throw new NotSupportedException("此 Kindle 书籍受 DRM 保护，无法同步批注位置。");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = U32(header, 28) switch
        {
            65001 => new UTF8Encoding(false, true),
            1252 => Encoding.GetEncoding(1252),
            var code => throw new NotSupportedException(
                $"Kindle 书籍使用了暂不支持的字符编码：{code}。")
        };
        var title = encoding.GetString(Slice(header, Int(header, 84), Int(header, 88))).Trim('\0');
        if (string.IsNullOrWhiteSpace(title)) title = fallbackTitle;
        var author = string.Empty;
        string? asin = null;
        var headerLength = Int(header, 20);
        var exth = 16 + headerLength;
        if (headerLength >= 116 && exth <= header.Length - 12
            && Magic(Slice(header, exth, 4), "EXTH"))
        {
            var end = checked(exth + Int(header, exth + 4));
            var count = Int(header, exth + 8);
            if (end > header.Length || count > 4096) throw Invalid();
            var position = exth + 12;
            for (var index = 0; index < count; index++)
            {
                if (position > end - 8) throw Invalid();
                var type = U32(header, position);
                var length = Int(header, position + 4);
                if (length < 8 || position + (long)length > end) throw Invalid();
                if (type is 100 or 113 or 503)
                {
                    var value = encoding.GetString(Slice(header, position + 8, length - 8)).Trim('\0');
                    if (type == 100 && author.Length == 0) author = value;
                    if (type == 113 && asin is null) asin = value;
                    if (type == 503 && !string.IsNullOrWhiteSpace(value)) title = value;
                }
                position += length;
            }
        }

        var compression = U16(header, 0);
        var textRecords = U16(header, 8);
        var declaredLength = Int(header, 4);
        if (textRecords == 0 || textRecords >= recordCount || declaredLength > MaximumOutput) throw Invalid();
        var flags = headerLength >= 228 ? U16(header, 242) : 0;
        var huff = compression == 0x4448
            ? new HuffDic(Record, start + Int(header, 112), Int(header, 116), cancellationToken)
            : null;
        using var output = new MemoryStream();
        for (var index = 1; index <= textRecords; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var textRecord = Record(start + index);
            var end = textRecord.Length;
            for (var trailers = flags >> 1; trailers != 0; trailers >>= 1)
            {
                if ((trailers & 1) == 0) continue;
                var size = 0;
                for (var position = Math.Max(0, end - 4); position < end; position++)
                {
                    if ((textRecord[position] & 128) != 0) size = 0;
                    size = (size << 7) | (textRecord[position] & 127);
                }
                if (size <= 0 || size > end) throw Invalid();
                end -= size;
            }
            if ((flags & 1) != 0)
            {
                if (end == 0) throw Invalid();
                end -= (textRecord[end - 1] & 3) + 1;
            }
            var payload = Slice(textRecord, 0, end);
            var expanded = compression switch
            {
                1 => payload,
                2 => PalmDoc(payload),
                0x4448 => huff!.Decode(payload),
                _ => throw new NotSupportedException("Kindle 书籍使用了暂不支持的文本压缩方式。")
            };
            if (output.Length + expanded.Length > MaximumOutput) throw Invalid();
            output.Write(expanded);
        }
        if (output.Length < declaredLength) throw Invalid();
        var raw = Slice(output.ToArray(), 0, declaredLength);
        byte[]? unassembled = null;
        if (IsKf8(header))
        {
            unassembled = raw;
            var mainFlow = raw;
            if (U32(header, 192) != uint.MaxValue && U32(header, 196) > 1)
            {
                var fdst = Record(start + Int(header, 192));
                if (!Magic(fdst, "FDST") || Int(fdst, 8) < 1) throw Invalid();
                var from = Int(fdst, 12);
                var to = Int(fdst, 16);
                mainFlow = Slice(raw, from, to - from);
            }
            var (parts, _) = ReconstructKf8MainFlow(mainFlow,
                ReadIndex(Record, start + Int(header, 252), encoding, cancellationToken),
                ReadIndex(Record, start + Int(header, 248), encoding, cancellationToken), cancellationToken);
            using var assembled = new MemoryStream();
            foreach (var part in parts) assembled.Write(part.Data);
            raw = assembled.ToArray();
        }
        var palmName = Encoding.Latin1.GetString(data, 0, 32).TrimEnd('\0', ' ');
        return new KindleRawBookText(
            raw,
            encoding,
            string.IsNullOrWhiteSpace(palmName) ? fallbackTitle : palmName,
            string.IsNullOrWhiteSpace(asin) ? null : asin,
            U32(header, 32),
            title.Trim(),
            author.Trim(),
            unassembled);
    }

    private static bool IsKf8(byte[] record) =>
        record.Length >= 264
        && Encoding.ASCII.GetString(record, 16, 4) == "MOBI"
        && U32(record, 36) == 8;
}
