using System.Buffers.Binary;
using System.Text;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed record KindleThumbnail(string FileName, byte[] JpegBytes);

public static class KindleThumbnailService
{
    private const int MaximumHeaderBytes = 2 * 1024 * 1024;
    private const int MaximumExthRecords = 4096;
    private const int AsinRecordType = 113;
    private const int CdeTypeRecordType = 501;
    private const int LanguageRecordType = 524;
    private const int OverrideKindleFontsRecordType = 528;
    private const string DefaultCdeType = "EBOK";

    public static async Task<KindleThumbnail?> CreateAsync(
        string bookPath,
        IMetadataService metadataService,
        CancellationToken cancellationToken = default,
        string? coverOverridePath = null)
    {
        var fileName = await ReadThumbnailFileNameAsync(bookPath, cancellationToken);
        if (fileName is null) return null;

        // A library-managed cover (e.g. a freshly matched Douban one) wins
        // over whatever is still embedded in the book file.
        if (!string.IsNullOrWhiteSpace(coverOverridePath))
        {
            try
            {
                var overrideBytes = await File.ReadAllBytesAsync(coverOverridePath, cancellationToken);
                if (overrideBytes.Length > 0)
                    return new KindleThumbnail(fileName, overrideBytes);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var metadata = await metadataService.ReadMetadataAsync(bookPath, cancellationToken);
        return metadata.CoverBytes is { Length: > 0 } cover
            ? new KindleThumbnail(fileName, cover)
            : null;
    }

    public static async Task<string?> ReadThumbnailFileNameAsync(
        string bookPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            bookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            useAsync: true);
        var bytes = new byte[(int)Math.Min(stream.Length, MaximumHeaderBytes)];
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(totalRead), cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }
        return ReadThumbnailFileName(bytes.AsSpan(0, totalRead));
    }

    public static async Task<bool> IsKindleReadyAzw3Async(
        string bookPath,
        CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(bookPath).Equals(".azw3", StringComparison.OrdinalIgnoreCase)) return false;
        var info = new FileInfo(bookPath);
        if (!info.Exists || info.Length < 1024) return false;

        var header = new byte[68];
        await using (var stream = new FileStream(
            bookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            useAsync: true))
        {
            var totalRead = 0;
            while (totalRead < header.Length)
            {
                var read = await stream.ReadAsync(header.AsMemory(totalRead), cancellationToken);
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead < header.Length || !header.AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8)) return false;
        }

        var thumbnailName = await ReadThumbnailFileNameAsync(bookPath, cancellationToken);
        if (thumbnailName is null) return false;

        await using var metadataStream = new FileStream(
            bookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            useAsync: true);
        var metadataBytes = new byte[(int)Math.Min(metadataStream.Length, MaximumHeaderBytes)];
        var metadataRead = 0;
        while (metadataRead < metadataBytes.Length)
        {
            var read = await metadataStream.ReadAsync(metadataBytes.AsMemory(metadataRead), cancellationToken);
            if (read == 0) break;
            metadataRead += read;
        }
        return !RequiresCjkFontCompatibilityRebuild(metadataBytes.AsSpan(0, metadataRead));
    }

    public static bool RequiresCjkFontCompatibilityRebuild(ReadOnlySpan<byte> bytes)
    {
        foreach (var records in EnumerateExthRecords(bytes))
        {
            string? language = null;
            var overridesKindleFonts = false;
            foreach (var record in records)
            {
                var value = Encoding.UTF8.GetString(record.Value).Trim('\0', ' ');
                if (record.Type == LanguageRecordType) language = value;
                else if (record.Type == OverrideKindleFontsRecordType)
                    overridesKindleFonts = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            if (language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true)
                return !overridesKindleFonts;
        }
        return false;
    }

    public static string? ReadThumbnailFileName(ReadOnlySpan<byte> bytes)
    {
        foreach (var records in EnumerateExthRecords(bytes))
        {
            string? asin = null;
            string? cdeType = null;
            foreach (var record in records)
            {
                var value = Encoding.UTF8.GetString(record.Value).Trim('\0', ' ');
                if (record.Type == AsinRecordType) asin = value;
                else if (record.Type == CdeTypeRecordType) cdeType = value;
            }

            if (IsSafeIdentifier(asin))
            {
                cdeType = IsSafeIdentifier(cdeType) ? cdeType : DefaultCdeType;
                return $"thumbnail_{asin}_{cdeType}_portrait.jpg";
            }
        }
        return null;
    }

    private static IEnumerable<IReadOnlyList<ExthRecord>> EnumerateExthRecords(ReadOnlySpan<byte> bytes)
    {
        var result = new List<IReadOnlyList<ExthRecord>>();
        for (var offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if (!bytes.Slice(offset, 4).SequenceEqual("EXTH"u8)) continue;
            var headerLength = ReadUInt32(bytes, offset + 4);
            var recordCount = ReadUInt32(bytes, offset + 8);
            if (headerLength < 12 || headerLength > bytes.Length - offset || recordCount > MaximumExthRecords)
                continue;

            var records = new List<ExthRecord>((int)recordCount);
            var recordOffset = offset + 12;
            var headerEnd = offset + (int)headerLength;
            for (var index = 0; index < recordCount && recordOffset <= headerEnd - 8; index++)
            {
                var recordType = ReadUInt32(bytes, recordOffset);
                var recordLength = ReadUInt32(bytes, recordOffset + 4);
                if (recordLength < 8 || recordLength > headerEnd - recordOffset) break;
                records.Add(new ExthRecord(
                    recordType,
                    bytes.Slice(recordOffset + 8, (int)recordLength - 8).ToArray()));
                recordOffset += (int)recordLength;
            }
            result.Add(records);
            offset = headerEnd - 1;
        }
        return result;
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':');

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));

    private sealed record ExthRecord(uint Type, byte[] Value);
}
