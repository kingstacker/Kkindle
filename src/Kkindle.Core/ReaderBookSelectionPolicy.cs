namespace Kkindle.Core;

// Reader format selection belongs to the domain layer so every UI entry point
// (details, context menu, grid and list) uses the same capability rules.
public static class ReaderBookSelectionPolicy
{
    public static bool IsSupportedFormat(string? format) => GetPriority(format) < 4;

    public static IReadOnlyList<BookFile> GetSupportedFiles(IEnumerable<BookFile>? files)
    {
        if (files is null) return [];

        return files
            .Where(file => IsSupportedFormat(file.Format))
            .OrderByDescending(PinyinBookPolicy.IsGeneratedPinyinVersion)
            .ThenBy(file => GetPriority(file.Format))
            .ToArray();
    }

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files)
        => GetSupportedFiles(files).FirstOrDefault();

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files, string? preferredFormat)
    {
        var supported = GetSupportedFiles(files);
        var preferred = preferredFormat?.Trim().TrimStart('.');
        return supported.FirstOrDefault(file => string.Equals(file.Format, preferred, StringComparison.OrdinalIgnoreCase))
            ?? supported.FirstOrDefault();
    }

    public static BookFile? SelectEpub(IEnumerable<BookFile>? files) =>
        files?.FirstOrDefault(file =>
            string.Equals(file.Format?.Trim(), "epub", StringComparison.OrdinalIgnoreCase));

    private static int GetPriority(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "epub" => 0,
        "pdf" => 1,
        "azw3" => 2,
        "mobi" => 3,
        _ => 4
    };
}
