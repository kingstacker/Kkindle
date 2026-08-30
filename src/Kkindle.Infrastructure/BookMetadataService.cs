using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class BookMetadataService : IMetadataService
{
    private const uint AuthorRecordType = 100;
    private const uint KindleCoverOffsetRecordType = 201;

    public async Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".epub"
            ? await ReadEpubAsync(path, cancellationToken)
            : await ReadFallbackAsync(path, extension, cancellationToken);
    }

    private static async Task<BookMetadata> ReadFallbackAsync(
        string path,
        string extension,
        CancellationToken cancellationToken)
    {
        var title = CleanFileTitle(Path.GetFileNameWithoutExtension(path));
        byte[]? coverBytes = null;
        var authors = "未知作者";
        if (extension is ".mobi" or ".azw" or ".azw3" or ".prc" or ".kfx")
        {
            var kindleMetadata = await ReadKindleMetadataAsync(path, cancellationToken);
            if (!string.IsNullOrWhiteSpace(kindleMetadata.Title)) title = kindleMetadata.Title;
            if (!string.IsNullOrWhiteSpace(kindleMetadata.Authors)) authors = kindleMetadata.Authors;
            coverBytes = kindleMetadata.CoverBytes;
        }
        return new BookMetadata
        {
            Title = title,
            Authors = authors,
            CoverBytes = coverBytes,
            CoverExtension = ".jpg"
        };
    }

    private static async Task<BookMetadata> ReadEpubAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var container = archive.GetEntry("META-INF/container.xml");
            if (container is null) return await ReadFallbackAsync(path, ".epub", cancellationToken);

            await using var containerStream = container.Open();
            var containerXml = await XDocument.LoadAsync(containerStream, LoadOptions.None, cancellationToken);
            var rootFile = containerXml.Descendants().FirstOrDefault(x => x.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(rootFile)) return await ReadFallbackAsync(path, ".epub", cancellationToken);

            var opfEntry = archive.GetEntry(rootFile.Replace('\\', '/'));
            if (opfEntry is null) return await ReadFallbackAsync(path, ".epub", cancellationToken);
            await using var opfStream = opfEntry.Open();
            var opf = await XDocument.LoadAsync(opfStream, LoadOptions.None, cancellationToken);
            var metadata = opf.Descendants().FirstOrDefault(x => x.Name.LocalName == "metadata");
            if (metadata is null) return await ReadFallbackAsync(path, ".epub", cancellationToken);

            var title = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value?.Trim();
            var creators = metadata.Elements().Where(x => x.Name.LocalName == "creator").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToList();
            var description = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "description")?.Value?.Trim();
            var series = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && string.Equals((string?)x.Attribute("name"), "calibre:series", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
            var seriesIndexText = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && string.Equals((string?)x.Attribute("name"), "calibre:series_index", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
            _ = double.TryParse(seriesIndexText, out var seriesIndex);

            var manifest = opf.Descendants().FirstOrDefault(x => x.Name.LocalName == "manifest")?.Elements()
                .Where(x => x.Name.LocalName == "item")
                .Select(x => new ManifestItem(
                    (string?)x.Attribute("id") ?? string.Empty,
                    (string?)x.Attribute("href") ?? string.Empty,
                    (string?)x.Attribute("media-type") ?? string.Empty,
                    (string?)x.Attribute("properties") ?? string.Empty))
                .ToList() ?? [];

            var coverId = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && string.Equals((string?)x.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
            var coverItem = manifest.FirstOrDefault(x => x.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image", StringComparer.OrdinalIgnoreCase))
                ?? manifest.FirstOrDefault(x => x.Id == coverId)
                ?? manifest.FirstOrDefault(x => x.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

            byte[]? coverBytes = null;
            var coverExtension = ".jpg";
            if (coverItem is not null)
            {
                var baseDirectory = Path.GetDirectoryName(rootFile)?.Replace('\\', '/') ?? string.Empty;
                var coverPath = CombineZipPath(baseDirectory, Uri.UnescapeDataString(coverItem.Href));
                var coverEntry = archive.GetEntry(coverPath);
                if (coverEntry is not null)
                {
                    await using var coverStream = coverEntry.Open();
                    await using var buffer = new MemoryStream();
                    await coverStream.CopyToAsync(buffer, cancellationToken);
                    coverBytes = buffer.ToArray();
                    coverExtension = coverItem.MediaType switch
                    {
                        "image/png" => ".png",
                        "image/webp" => ".webp",
                        _ => ".jpg"
                    };
                }
            }

            return new BookMetadata
            {
                Title = title,
                Authors = creators.Count == 0 ? "未知作者" : string.Join(", ", creators),
                Series = string.IsNullOrWhiteSpace(series) ? null : series,
                SeriesIndex = double.TryParse(seriesIndexText, out _) ? seriesIndex : null,
                Description = description,
                CoverBytes = coverBytes,
                CoverExtension = coverExtension
            };
        }
        catch (InvalidDataException)
        {
            return await ReadFallbackAsync(path, ".epub", cancellationToken);
        }
        catch (XmlException)
        {
            return await ReadFallbackAsync(path, ".epub", cancellationToken);
        }
    }

    private static string CleanFileTitle(string fileName)
    {
        var title = Regex.Replace(
            fileName,
            @"\s*\([^)]*(?:z-library|z-lib|1lib)[^)]*\)\s*",
            " ",
            RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"_?[0-9A-F]{32}$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return title.Replace('_', ' ').Trim();
    }

    private static async Task<(string? Title, string? Authors, byte[]? CoverBytes)> ReadKindleMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const long maximumContainerSize = 128L * 1024 * 1024;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumContainerSize)
            return (null, null, null);

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        TryReadKindleTextMetadata(bytes, out var title, out var authors);
        var cover = TryReadKindleCoverJpeg(bytes)
            ?? ReadLargestEmbeddedJpeg(bytes, cancellationToken);
        return (title, authors, cover);
    }

    private static bool TryReadKindleTextMetadata(
        ReadOnlySpan<byte> bytes,
        out string? title,
        out string? authors)
    {
        title = null;
        authors = null;
        if (!TryReadPalmDatabaseRecords(bytes, out var records)) return false;

        foreach (var record in records)
        {
            var data = bytes.Slice(record.Start, record.Length);
            var mobiOffset = IndexOf(data, "MOBI"u8);
            if (mobiOffset < 0 || mobiOffset > data.Length - 0x4C) continue;

            var encoding = ReadUInt32(data, mobiOffset + 0x0C);
            var titleOffset = ReadUInt32(data, mobiOffset + 0x44);
            var titleLength = ReadUInt32(data, mobiOffset + 0x48);
            if (titleLength > 0
                && titleOffset <= (uint)data.Length
                && titleLength <= (uint)data.Length - titleOffset)
            {
                title = DecodeKindleText(data.Slice((int)titleOffset, (int)titleLength), encoding);
            }

            var authorValues = ReadExthTextValues(data, AuthorRecordType, encoding);
            if (authorValues.Count > 0)
                authors = string.Join(" / ", authorValues.Distinct(StringComparer.CurrentCultureIgnoreCase));
            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(authors)) return true;
        }
        return false;
    }

    private static List<string> ReadExthTextValues(
        ReadOnlySpan<byte> bytes,
        uint recordType,
        uint textEncoding)
    {
        var values = new List<string>();
        for (var offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if (!bytes.Slice(offset, 4).SequenceEqual("EXTH"u8)) continue;
            var headerLength = ReadUInt32(bytes, offset + 4);
            var recordCount = ReadUInt32(bytes, offset + 8);
            if (headerLength < 12 || headerLength > bytes.Length - offset || recordCount > 4096)
                continue;

            var recordOffset = offset + 12;
            var headerEnd = offset + (int)headerLength;
            for (var index = 0; index < recordCount && recordOffset <= headerEnd - 8; index++)
            {
                var currentType = ReadUInt32(bytes, recordOffset);
                var recordLength = ReadUInt32(bytes, recordOffset + 4);
                if (recordLength < 8 || recordLength > headerEnd - recordOffset) break;
                if (currentType == recordType)
                {
                    var value = DecodeKindleText(
                        bytes.Slice(recordOffset + 8, (int)recordLength - 8),
                        textEncoding);
                    if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                }
                recordOffset += (int)recordLength;
            }
            offset = headerEnd - 1;
        }
        return values;
    }

    private static string DecodeKindleText(ReadOnlySpan<byte> value, uint textEncoding)
    {
        var encoding = textEncoding == 65001 ? Encoding.UTF8 : Encoding.Latin1;
        return encoding.GetString(value).Trim('\0', ' ', '\r', '\n', '\t');
    }

    private static byte[]? TryReadKindleCoverJpeg(ReadOnlySpan<byte> bytes)
    {
        if (!TryReadPalmDatabaseRecords(bytes, out var records)) return null;
        var firstRecord = bytes.Slice(records[0].Start, records[0].Length);
        var mobiOffset = IndexOf(firstRecord, "MOBI"u8);
        if (mobiOffset < 0 || mobiOffset > firstRecord.Length - 0x70) return null;

        var firstImageIndex = ReadUInt32(firstRecord, mobiOffset + 0x5C);
        var coverRecordIndex = (ulong)firstImageIndex;
        if (TryReadExthUInt32(firstRecord, KindleCoverOffsetRecordType, out var coverOffset))
            coverRecordIndex += coverOffset;
        if (coverRecordIndex >= (ulong)records.Count) return null;

        var coverRecord = records[(int)coverRecordIndex];
        return ReadFirstEmbeddedJpeg(bytes.Slice(coverRecord.Start, coverRecord.Length));
    }

    private static bool TryReadPalmDatabaseRecords(
        ReadOnlySpan<byte> bytes,
        out List<(int Start, int Length)> records)
    {
        records = [];
        const int recordTableOffset = 78;
        const int recordEntryLength = 8;
        if (bytes.Length < recordTableOffset) return false;

        var recordCount = ReadUInt16(bytes, 76);
        if (recordCount == 0) return false;
        var tableLength = recordCount * recordEntryLength;
        if (tableLength > bytes.Length - recordTableOffset) return false;

        var recordDataStart = recordTableOffset + tableLength;
        var starts = new int[recordCount];
        for (var index = 0; index < recordCount; index++)
        {
            var recordOffset = recordTableOffset + index * recordEntryLength;
            var start = ReadInt32(bytes, recordOffset);
            if (start < recordDataStart || start > bytes.Length) return false;
            if (index > 0 && start <= starts[index - 1]) return false;
            starts[index] = start;
        }

        for (var index = 0; index < starts.Length; index++)
        {
            var end = index + 1 < starts.Length ? starts[index + 1] : bytes.Length;
            if (end <= starts[index]) return false;
            records.Add((starts[index], end - starts[index]));
        }
        return records.Count > 0;
    }

    private static bool TryReadExthUInt32(ReadOnlySpan<byte> bytes, uint recordType, out uint value)
    {
        value = 0;
        for (var offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if (!bytes.Slice(offset, 4).SequenceEqual("EXTH"u8)) continue;

            var headerLength = ReadUInt32(bytes, offset + 4);
            var recordCount = ReadUInt32(bytes, offset + 8);
            if (headerLength < 12 || headerLength > bytes.Length - offset || recordCount > 4096)
                continue;

            var recordOffset = offset + 12;
            var headerEnd = offset + (int)headerLength;
            for (var index = 0; index < recordCount && recordOffset <= headerEnd - 8; index++)
            {
                var currentType = ReadUInt32(bytes, recordOffset);
                var recordLength = ReadUInt32(bytes, recordOffset + 4);
                if (recordLength < 8 || recordLength > headerEnd - recordOffset) break;
                if (currentType == recordType && recordLength >= 12)
                {
                    value = ReadUInt32(bytes, recordOffset + 8);
                    return true;
                }
                recordOffset += (int)recordLength;
            }

            offset = headerEnd - 1;
        }
        return false;
    }

    private static byte[]? ReadFirstEmbeddedJpeg(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index <= bytes.Length - 3; index++)
        {
            if (bytes[index] != 0xFF || bytes[index + 1] != 0xD8 || bytes[index + 2] != 0xFF) continue;
            for (var end = index + 3; end < bytes.Length - 1; end++)
            {
                if (bytes[end] != 0xFF || bytes[end + 1] != 0xD9) continue;
                return bytes.Slice(index, end + 2 - index).ToArray();
            }
        }
        return null;
    }

    private static byte[]? ReadLargestEmbeddedJpeg(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        byte[]? largest = null;
        for (var index = 0; index < bytes.Length - 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[index] != 0xFF || bytes[index + 1] != 0xD8 || bytes[index + 2] != 0xFF) continue;

            for (var end = index + 3; end < bytes.Length - 1; end++)
            {
                if (bytes[end] != 0xFF || bytes[end + 1] != 0xD9) continue;
                var length = end + 2 - index;
                if (length >= 8 * 1024 && (largest is null || length > largest.Length))
                    largest = bytes.Slice(index, length).ToArray();
                index = end + 1;
                break;
            }
        }
        return largest;
    }

    private static int IndexOf(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0) return 0;
        for (var offset = 0; offset <= bytes.Length - value.Length; offset++)
        {
            if (bytes.Slice(offset, value.Length).SequenceEqual(value)) return offset;
        }
        return -1;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        checked((int)ReadUInt32(bytes, offset));

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));

    private static string CombineZipPath(string directory, string relative)
    {
        var combined = string.IsNullOrEmpty(directory) ? relative : $"{directory.TrimEnd('/')}/{relative.TrimStart('/')}";
        var parts = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else if (part != "..") parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private sealed record ManifestItem(string Id, string Href, string MediaType, string Properties);
}
