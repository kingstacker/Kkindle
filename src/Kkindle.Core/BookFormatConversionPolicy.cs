namespace Kkindle.Core;

public static class BookFormatConversionPolicy
{
    private static readonly string[] DefaultReaderFormats = ["epub", "azw3"];

    public static bool IsConvertibleFormat(string? format) => Normalize(format) is "epub" or "azw3" or "pdf" or "mobi";

    public static bool IsCalibreInputFormat(string? format) =>
        IsConvertibleFormat(format) || Normalize(format) is "azw" or "prc" or "kfx";

    public static BookFile? SelectSource(
        IEnumerable<BookFile>? files,
        string? targetFormat)
    {
        if (files is null) return null;

        var target = Normalize(targetFormat);
        return files
            .Where(file => IsConvertibleFormat(file.Format)
                && !string.Equals(Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => GetPriority(file.Format))
            .FirstOrDefault();
    }

    public static string Normalize(string? format) =>
        format?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;

    public static IReadOnlyList<string> GetMissingDefaultReaderFormats(IEnumerable<BookFile>? files)
    {
        var existing = (files ?? [])
            .Select(file => Normalize(file.Format))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DefaultReaderFormats.Where(format => !existing.Contains(format)).ToArray();
    }

    private static int GetPriority(string? format) => Normalize(format) switch
    {
        "epub" => 0,
        "azw3" => 1,
        "pdf" => 2,
        "mobi" => 3,
        _ => 4
    };
}
