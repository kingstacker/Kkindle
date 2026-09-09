namespace Kkindle.Core;

public static class KindleEmailSelectionPolicy
{
    public const long MaximumAttachmentBytes = 50L * 1024 * 1024;

    public static bool IsSupportedFormat(string? format) => GetPriority(format) < 2;

    public static bool IsWithinAttachmentLimit(long fileSizeBytes) =>
        fileSizeBytes >= 0 && fileSizeBytes <= MaximumAttachmentBytes;

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files) =>
        GetCandidates(files).FirstOrDefault();

    // The batch sender still uses the first candidate. The context-menu sender
    // uses this complete list when a book has multiple EPUB/PDF editions.
    public static IReadOnlyList<BookFile> GetCandidates(IEnumerable<BookFile>? files)
    {
        if (files is null) return [];

        return files
            .Where(file => IsSupportedFormat(file.Format))
            .OrderByDescending(PinyinBookPolicy.IsGeneratedPinyinVersion)
            .ThenBy(file => GetPriority(file.Format))
            .ToArray();
    }

    private static int GetPriority(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "epub" => 0,
        "pdf" => 1,
        _ => 2
    };
}
