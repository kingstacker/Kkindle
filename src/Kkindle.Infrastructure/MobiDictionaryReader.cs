using System.Text;
using static Kkindle.Infrastructure.Azw3Binary;

namespace Kkindle.Infrastructure;

// Reuse the reader's bounded PalmDOC/HUFF decompression without book layout conversion.
internal static class MobiDictionaryReader
{
    internal static string? Read(byte[] data, CancellationToken token, IProgress<DictionaryImportProgress>? progress)
    {
        if (data.Length < 78 || Encoding.ASCII.GetString(data, 60, 8) != "BOOKMOBI") return null;
        var count = U16(data, 76);
        if (count < 2 || 78L + count * 8L > data.Length) throw Invalid();
        var offsets = Enumerable.Range(0, count).Select(i => Int(data, 78 + i * 8)).Append(data.Length).ToArray();
        for (var i = 0; i < count; i++)
            if (offsets[i] < 78 + count * 8 || offsets[i] >= offsets[i + 1]) throw Invalid();
        byte[] Record(int i) => i >= 0 && i < count ? Slice(data, offsets[i], offsets[i + 1] - offsets[i]) : throw Invalid();
        var header = Record(0);
        if (header.Length < 132 || !Magic(Slice(header, 16, 4), "MOBI")) return null;
        if (U32(header, 36) > 7) return null; // KF8 needs skeleton reconstruction; retain converter fallback.
        if (U16(header, 12) != 0) throw new NotSupportedException("受 DRM 保护的词典无法导入。");
        var compression = U16(header, 0);
        if (compression is not (1 or 2 or 0x4448)) return null;
        var records = U16(header, 8);
        var length = Int(header, 4);
        if (records >= count || length > MaximumOutput) throw Invalid();
        var flags = header.Length >= 244 && Int(header, 20) >= 228 ? U16(header, 242) : 0;
        var huff = compression == 0x4448 ? new HuffDic(Record, Int(header, 112), Int(header, 116), token) : null;
        using var output = new MemoryStream();
        for (var i = 1; i <= records; i++)
        {
            token.ThrowIfCancellationRequested();
            var record = Record(i);
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
            var payload = Slice(record, 0, end);
            var expanded = compression switch { 1 => payload, 2 => PalmDoc(payload), _ => huff!.Decode(payload) };
            if (output.Length + expanded.Length > MaximumOutput) throw Invalid();
            output.Write(expanded);
            if (i % 64 == 0 || i == records) progress?.Report(new("读取词典", Percent: i * 100.0 / records));
        }
        if (output.Length < length) throw Invalid();
        var encoding = U32(header, 28) == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(1252);
        return encoding.GetString(output.GetBuffer(), 0, length);
    }
}

