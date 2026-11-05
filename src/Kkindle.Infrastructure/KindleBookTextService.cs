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

    /// <summary>
    /// Kindle stores a highlight as a closed byte range: the end position is the
    /// last byte of the last selected character, so the first excluded byte is
    /// the next character boundary. An end on markup (a "&lt;" directly after the
    /// text) excludes that markup. Everything else in this type uses half-open
    /// ranges; convert only at the device boundary.
    /// </summary>
    public long DeviceEndToExclusive(long end)
    {
        if (end < 0) return 0;
        if (end >= Text.LongLength) return Text.LongLength;
        var position = checked((int)end);
        var opening = Array.LastIndexOf(Text, (byte)'<', position);
        var closing = Array.LastIndexOf(Text, (byte)'>', position);
        if (opening > closing) return opening;
        position++;
        while (position < Text.Length && IsContinuation(Text[position])) position++;
        return position;
    }

    /// <summary>
    /// Earlier versions wrote their own highlights with an exclusive end. In a
    /// multi-byte encoding that end is provably such a record when it sits on a
    /// lead byte, because a closed end never points at the start of a multi-byte
    /// character.
    /// </summary>
    public long OwnedDeviceEndToExclusive(long end)
    {
        if (end > 0 && end < Text.LongLength && IsMultiByteLead(Text[checked((int)end)])) return end;
        return DeviceEndToExclusive(end);
    }

    /// <summary>Start of the last character of a closed range, used to anchor notes.</summary>
    public long LastCharacterStart(long start, long closedEnd)
    {
        if (Text.Length == 0) return start;
        var position = checked((int)Math.Clamp(closedEnd, start, Text.LongLength - 1));
        if (Text[position] == (byte)'<' && position > start) position--;
        while (position > start && IsContinuation(Text[position])) position--;
        return position;
    }

    public string ExtractDeviceRange(long start, long end, bool writtenByKkindle = false) =>
        ExtractRange(start, writtenByKkindle ? OwnedDeviceEndToExclusive(end) : DeviceEndToExclusive(end));

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

    /// <summary>
    /// Maps reader text to a half-open byte range. The search runs on visible
    /// text only, so a quote can never resolve into a tag, an attribute or the
    /// document head. Only a single occurrence is ever accepted.
    /// </summary>
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

        var visible = GetVisibleIndex();
        if (visible is null) return TryFindRawRange(selectedText, prefix, suffix, out start, out end);
        var selected = StripWhitespace(selectedText);
        if (selected.Length == 0) return false;
        var occurrences = new List<int>();
        for (var offset = 0; offset <= visible.Text.Length - selected.Length;)
        {
            var index = visible.Text.IndexOf(selected, offset, StringComparison.Ordinal);
            if (index < 0) break;
            occurrences.Add(index);
            offset = index + 1;
        }
        var found = SelectOccurrence(visible.Text, occurrences, selected.Length,
            StripWhitespace(prefix ?? string.Empty), StripWhitespace(suffix ?? string.Empty));
        if (found < 0) return false;
        start = visible.Starts[found];
        end = visible.Ends[found + selected.Length - 1];
        return end > start;
    }

    /// <summary>
    /// The reader's context can differ slightly from the book markup, so shorter
    /// context is tried when nothing matches. More than one survivor is ambiguous;
    /// shorter context could only add candidates, so the search stops there.
    /// </summary>
    private static int SelectOccurrence(string text, List<int> occurrences, int length, string prefix, string suffix)
    {
        if (occurrences.Count == 0) return -1;
        if (occurrences.Count == 1) return occurrences[0];
        int[] sizes = [int.MaxValue, 24, 12, 6];
        foreach (var (usePrefix, useSuffix) in new[] { (true, true), (true, false), (false, true) })
        {
            if ((usePrefix && prefix.Length == 0) || (useSuffix && suffix.Length == 0)) continue;
            foreach (var size in sizes)
            {
                var before = usePrefix && prefix.Length > size ? prefix[^size..] : usePrefix ? prefix : string.Empty;
                var after = useSuffix && suffix.Length > size ? suffix[..size] : useSuffix ? suffix : string.Empty;
                var match = -1;
                var count = 0;
                foreach (var occurrence in occurrences)
                {
                    if (!text.AsSpan(0, occurrence).EndsWith(before, StringComparison.Ordinal)) continue;
                    if (!text.AsSpan(occurrence + length).StartsWith(after, StringComparison.Ordinal)) continue;
                    match = occurrence;
                    count++;
                }
                if (count == 1) return match;
                if (count > 1) return -1;
            }
        }
        return -1;
    }

    /// <summary>Only for text that cannot be decoded and therefore has no visible index.</summary>
    private bool TryFindRawRange(string selectedText, string? prefix, string? suffix, out long start, out long end)
    {
        start = 0;
        end = 0;
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
        if (context.Length <= selectedText.Length) return false;
        var contextMatches = FindAll(Text, Encoding.GetBytes(context), maximum: 2);
        if (contextMatches.Count != 1) return false;
        start = contextMatches[0] + Encoding.GetBytes((prefix ?? string.Empty).Trim()).LongLength;
        end = start + selected.LongLength;
        return true;
    }

    private VisibleTextIndex? _visibleIndex;
    private byte[]? _visibleIndexFailedSource;

    private VisibleTextIndex? GetVisibleIndex()
    {
        // "with" copies these fields, so a copy holding different bytes must rebuild.
        if (_visibleIndex is { } cached && ReferenceEquals(cached.Source, Text)) return cached;
        if (ReferenceEquals(_visibleIndexFailedSource, Text)) return null;
        _visibleIndex = BuildVisibleIndex();
        _visibleIndexFailedSource = _visibleIndex is null ? Text : null;
        return _visibleIndex;
    }

    private VisibleTextIndex? BuildVisibleIndex()
    {
        string source;
        try { source = Encoding.GetString(Text); }
        catch (DecoderFallbackException) { return null; }
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
            if (node.Ancestors().Any(parent => parent.Name is "head" or "style" or "script" or "title")) continue;
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
                if (sourceStart < 0 || sourceEnd > source.Length) return null;
                foreach (var character in decoded)
                {
                    if (char.IsWhiteSpace(character)) continue;
                    visible.Append(character); starts.Add(byteOffsets[sourceStart]); ends.Add(byteOffsets[sourceEnd]);
                }
                index += units;
            }
        }
        return new VisibleTextIndex(Text, visible.ToString(), starts.ToArray(), ends.ToArray());
    }

    private sealed record VisibleTextIndex(byte[] Source, string Text, int[] Starts, int[] Ends);

    private static string StripWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            if (!char.IsWhiteSpace(character)) builder.Append(character);
        return builder.ToString();
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

    // Only UTF-8 has multi-byte characters here: in Windows-1252 every byte is a
    // complete character, and 0x80-0xBF are ordinary letters and punctuation.
    private bool IsContinuation(byte value) => Encoding.CodePage == 65001 && (value & 0xC0) == 0x80;

    private bool IsMultiByteLead(byte value) => Encoding.CodePage == 65001 && value >= 0xC0;

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
