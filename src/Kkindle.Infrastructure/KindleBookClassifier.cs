using System.Buffers.Binary;
using System.Text;

namespace Kkindle.Infrastructure;

internal static class KindleBookClassifier
{
    private const int MaximumHeaderBytes = 2 * 1024 * 1024;
    private const int MaximumExthRecords = 4096;
    private const int SubjectRecordType = 105;

    public static async Task<bool> IsDictionaryAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".azw" or ".azw3" or ".mobi" or ".prc" or ".kfx")) return false;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var length = (int)Math.Min(stream.Length, MaximumHeaderBytes);
        if (length < 20) return false;

        var bytes = new byte[length];
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }

        return ContainsDictionarySubject(bytes.AsSpan(0, totalRead));
    }

    private static bool ContainsDictionarySubject(ReadOnlySpan<byte> bytes)
    {
        for (var offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if (!bytes.Slice(offset, 4).SequenceEqual("EXTH"u8)) continue;

            var headerLength = ReadUInt32(bytes, offset + 4);
            var recordCount = ReadUInt32(bytes, offset + 8);
            if (headerLength < 12 || headerLength > bytes.Length - offset || recordCount > MaximumExthRecords) continue;

            var recordOffset = offset + 12;
            var headerEnd = offset + (int)headerLength;
            for (var index = 0; index < recordCount && recordOffset <= headerEnd - 8; index++)
            {
                var recordType = ReadUInt32(bytes, recordOffset);
                var recordLength = ReadUInt32(bytes, recordOffset + 4);
                if (recordLength < 8 || recordLength > headerEnd - recordOffset) break;
                if (recordType == SubjectRecordType)
                {
                    var subject = Encoding.UTF8.GetString(bytes.Slice(recordOffset + 8, (int)recordLength - 8));
                    if (subject.Contains("Dictionaries", StringComparison.OrdinalIgnoreCase)
                        || subject.Equals("Dictionary", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                recordOffset += (int)recordLength;
            }
        }
        return false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
}
