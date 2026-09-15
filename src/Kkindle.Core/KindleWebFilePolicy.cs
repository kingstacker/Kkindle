namespace Kkindle.Core;

public sealed record KindleWebFile(string FullPath, string Name, long Length);

public static class KindleWebFilePolicy
{
    public const long MaximumFileBytes = 200L * 1024 * 1024;

    public static bool IsSupportedFormat(string? format) =>
        format?.Trim().TrimStart('.').ToLowerInvariant() is
            "epub" or "pdf" or "doc" or "docx" or "txt" or "rtf" or "htm" or "html"
            or "png" or "gif" or "jpg" or "jpeg" or "bmp";

    public static bool IsWithinLimit(long length) => length > 0 && length <= MaximumFileBytes;

    public static IReadOnlyList<BookFile> GetCandidates(IEnumerable<BookFile> files) => files
        .Where(file => IsSupportedFormat(file.Format))
        .OrderByDescending(PinyinBookPolicy.IsGeneratedPinyinVersion)
        .ThenBy(file => file.Format.ToLowerInvariant() switch { "epub" => 0, "pdf" => 1, _ => 2 })
        .ToArray();

    public static KindleWebFile Inspect(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new IOException(UiText.Get("文件不存在或无法读取。"));
        if (!IsSupportedFormat(file.Extension))
            throw new InvalidOperationException(UiText.Get("此格式不支持网页发送，请先转换为 EPUB 或 PDF。"));
        if (!IsWithinLimit(file.Length))
            throw new InvalidOperationException(UiText.Get("文件不能为空，且单个文件不能超过 200 MB。"));
        return new KindleWebFile(file.FullName, file.Name, file.Length);
    }
}
