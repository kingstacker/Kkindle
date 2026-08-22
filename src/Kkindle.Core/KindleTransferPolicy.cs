using System.Text;

namespace Kkindle.Core;

public static class KindleTransferPolicy
{
    private const int MaximumFileNameUtf8Bytes = 120;
    private static readonly string[] PreferredFormats = ["azw3", "mobi", "epub", "pdf"];
    private static readonly char[] SubtitleSeparators = ['（', '(', '【', '['];

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files)
    {
        if (files is null) return null;
        var available = files.ToArray();
        foreach (var format in PreferredFormats)
        {
            var match = available.FirstOrDefault(file =>
                string.Equals(file.Format.Trim().TrimStart('.'), format, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
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
