namespace Kkindle.Core;

public static class DeviceTransferPolicy
{
    public static IReadOnlyList<BookFile> GetCandidates(ReaderDeviceProfile profile, IEnumerable<BookFile>? files)
    {
        if (profile.Family == ReaderDeviceFamily.Kindle) return KindleTransferPolicy.GetCandidates(files);
        return (files ?? []).Where(file => profile.BookFormats.Contains(Normalize(file.Format), StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(PinyinBookPolicy.IsGeneratedPinyinVersion)
            .ThenBy(file => profile.BookFormats.ToList().FindIndex(format => format.Equals(Normalize(file.Format), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static bool RequiresKindleConversion(ReaderDeviceProfile profile, BookFile file) =>
        profile.Family == ReaderDeviceFamily.Kindle && KindleTransferPolicy.RequiresConversionToAzw3(file);

    private static string Normalize(string format) => format.Trim().TrimStart('.');
}
