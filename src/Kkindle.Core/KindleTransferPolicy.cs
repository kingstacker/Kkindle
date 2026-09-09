using System.Text;

namespace Kkindle.Core;

public static class KindleTransferPolicy
{
    private const int MaximumFileNameUtf8Bytes = 120;
    private static readonly string[] PreferredFormats = ["azw3", "mobi", "epub", "pdf"];
    private static readonly char[] SubtitleSeparators = ['（', '(', '【', '['];

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files) =>
        GetCandidates(files).FirstOrDefault();

    // Keep every Kindle-compatible file available to an explicit send action.
    // A book can contain several editions of the same format, such as the
    // original, translated, bilingual, and pinyin EPUBs.
    public static IReadOnlyList<BookFile> GetCandidates(IEnumerable<BookFile>? files)
    {
        if (files is null) return [];

        var available = files.ToArray();
        return available
            .Where(file => PreferredFormats.Contains(
                file.Format.Trim().TrimStart('.'),
                StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(PinyinBookPolicy.IsGeneratedPinyinVersion)
            .ThenBy(file => Array.IndexOf(PreferredFormats, file.Format.Trim().TrimStart('.').ToLowerInvariant()))
            .ToArray();
    }

    public static bool RequiresConversionToAzw3(BookFile file) =>
        file.Format.Trim().TrimStart('.').ToLowerInvariant() is "mobi" or "epub";

    public static bool RequiresLegacyMetadataRepair(BookFile file, string sourcePath)
    {
        if (!file.Format.Trim().TrimStart('.').Equals("azw3", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return stem.Equals("converted", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 32 && stem.All(Uri.IsHexDigit));
    }

    public static string CreateSafeFileName(string? title, string extension)
    {
        extension = extension.StartsWith('.') ? extension : $".{extension}";
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string((title ?? string.Empty)
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        var separatorIndex = stem.IndexOfAny(SubtitleSeparators);
        if (separatorIndex >= 2) stem = stem[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(stem)) stem = "book";

        var byteBudget = MaximumFileNameUtf8Bytes - Encoding.UTF8.GetByteCount(extension);
        var builder = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in stem.EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > byteBudget) break;
            builder.Append(rune.ToString());
            usedBytes += rune.Utf8SequenceLength;
        }

        var safeStem = builder.ToString().Trim().TrimEnd('.');
        return $"{(safeStem.Length == 0 ? "book" : safeStem)}{extension.ToLowerInvariant()}";
    }
}
