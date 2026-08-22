namespace Kkindle.Core;

public static class KindleEmailSelectionPolicy
{
    public const long MaximumAttachmentBytes = 50L * 1024 * 1024;

    public static bool IsSupportedFormat(string? format) => GetPriority(format) < 2;

    public static bool IsWithinAttachmentLimit(long fileSizeBytes) =>
        fileSizeBytes >= 0 && fileSizeBytes <= MaximumAttachmentBytes;

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files)
    {
        if (files is null) return null;

        return files
            .Where(file => IsSupportedFormat(file.Format))
            .OrderBy(file => GetPriority(file.Format))
            .FirstOrDefault();
    }

    private static int GetPriority(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "epub" => 0,
        "pdf" => 1,
        _ => 2
    };
}
