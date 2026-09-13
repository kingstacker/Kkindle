namespace Kkindle.Core;

public enum ReaderDeviceFamily { Generic, Kindle, Kobo, IReader, Hanvon }

/// <summary>Detected device capabilities. A user-assigned model name never changes these rules.</summary>
public sealed record ReaderDeviceProfile
{
    public ReaderDeviceFamily Family { get; init; }
    public string DisplayName { get; init; } = "阅读设备";
    public string BooksDirectory { get; init; } = "Books";
    public string? FontsDirectory { get; init; }
    public string? DictionariesDirectory { get; init; }
    public IReadOnlyList<string> BookFormats { get; init; } = ["epub", "pdf", "txt"];
    public IReadOnlyList<string> DictionaryFormats { get; init; } = [];
    public bool SupportsClippings => Family == ReaderDeviceFamily.Kindle;
    public bool SupportsKoboNotes => Family == ReaderDeviceFamily.Kobo;
    public bool CanReadNotes => SupportsClippings || SupportsKoboNotes;
    public bool CanDeleteNotes => SupportsClippings;
    public bool UsesKindleThumbnails => Family == ReaderDeviceFamily.Kindle;

    public bool SupportsResource(KindleResourceKind kind) =>
        (kind == KindleResourceKind.Font ? FontsDirectory : DictionariesDirectory) is not null;

    public string ResourceDirectory(KindleResourceKind kind) =>
        (kind == KindleResourceKind.Font ? FontsDirectory : DictionariesDirectory)
        ?? throw new NotSupportedException(UiText.Get("当前设备暂不支持此资源管理功能。"));

    public IReadOnlyList<string> ResourceFormats(KindleResourceKind kind) =>
        !SupportsResource(kind) ? [] : kind == KindleResourceKind.Font ? ["ttf", "otf"] : DictionaryFormats;

    public bool SupportsResourceFile(KindleResourceKind kind, string? path) =>
        ResourceFormats(kind).Contains(Path.GetExtension(path ?? "").TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public bool SupportsBookFile(string path) =>
        BookFormats.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public bool IsBookPath(string relativePath)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && SupportsBookFile(relativePath)
            && !segments[^1].Equals("My Clippings.txt", StringComparison.OrdinalIgnoreCase)
            && !segments.Any(segment => segment.StartsWith('.') || segment.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase))
            && !segments[..^1].Any(segment => segment.Equals("system", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("fonts", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("dictionaries", StringComparison.OrdinalIgnoreCase));
    }

    public bool TryGetResourcePath(KindleResourceKind kind, string? relativePath, out string pathWithinRoot)
    {
        pathWithinRoot = string.Empty;
        if (!SupportsResource(kind) || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return false;
        var root = ResourceDirectory(kind).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= root.Length) return false;
        for (var index = 0; index < root.Length; index++)
            if (!segments[index].Equals(root[index], StringComparison.OrdinalIgnoreCase)) return false;
        pathWithinRoot = Path.Combine(segments[root.Length..]);
        return SupportsResourceFile(kind, pathWithinRoot);
    }
}

public static class ReaderDeviceProfiles
{
    public static ReaderDeviceProfile Kindle { get; } = new()
    {
        Family = ReaderDeviceFamily.Kindle, DisplayName = "Kindle", BooksDirectory = "documents",
        FontsDirectory = "fonts", DictionariesDirectory = "documents/dictionaries",
        BookFormats = ["azw3", "mobi", "epub", "pdf", "azw", "prc", "kfx", "txt"],
        DictionaryFormats = ["azw", "azw3", "mobi", "prc", "kfx"]
    };

    public static ReaderDeviceProfile Kobo { get; } = new()
    {
        Family = ReaderDeviceFamily.Kobo, DisplayName = "Kobo", BooksDirectory = "",
        FontsDirectory = "fonts", DictionariesDirectory = ".kobo/custom-dict",
        BookFormats = ["epub", "pdf", "txt", "cbz", "cbr", "html", "htm", "rtf", "mobi"],
        DictionaryFormats = ["zip"]
    };

    public static ReaderDeviceFamily FamilyFromName(string? name)
    {
        name ??= "";
        if (name.Contains("Kindle", StringComparison.OrdinalIgnoreCase)) return ReaderDeviceFamily.Kindle;
        if (name.Contains("Kobo", StringComparison.OrdinalIgnoreCase)) return ReaderDeviceFamily.Kobo;
        if (name.Contains("iReader", StringComparison.OrdinalIgnoreCase) || name.Contains("掌阅")) return ReaderDeviceFamily.IReader;
        if (name.Contains("Hanvon", StringComparison.OrdinalIgnoreCase) || name.Contains("汉王")) return ReaderDeviceFamily.Hanvon;
        return ReaderDeviceFamily.Generic;
    }

    public static ReaderDeviceProfile? Detect(string? name, Func<string, bool> directoryExists,
        string? hardwareId = null, Func<string, bool>? fileExists = null)
    {
        var family = FamilyFromName(name);
        if (directoryExists(".kobo")) family = ReaderDeviceFamily.Kobo;
        else if ((hardwareId?.Contains("vid_1949", StringComparison.OrdinalIgnoreCase) ?? false)
            || (fileExists?.Invoke("documents/My Clippings.txt") ?? false)
            || (directoryExists("documents") && directoryExists("system/thumbnails"))) family = ReaderDeviceFamily.Kindle;

        if (family == ReaderDeviceFamily.Kindle)
            return directoryExists("documents") ? Kindle : null;
        if (family == ReaderDeviceFamily.Kobo) return Kobo;

        var books = new[] { "Books", "books", "Documents", "documents" }.FirstOrDefault(directoryExists);
        if (family == ReaderDeviceFamily.Generic && books is null) return null;
        var fonts = new[] { "fonts", "Fonts" }.FirstOrDefault(directoryExists);
        return new ReaderDeviceProfile
        {
            Family = family, BooksDirectory = books ?? "", FontsDirectory = fonts,
            DisplayName = family switch
            {
                ReaderDeviceFamily.IReader => "掌阅",
                ReaderDeviceFamily.Hanvon => "汉王",
                _ => "阅读设备"
            }
        };
    }

    public static ReaderDeviceProfile? DetectMounted(string root, string? name = null) =>
        Detect(name ?? new DirectoryInfo(root).Name,
            path => Directory.Exists(Path.Combine(root, path)),
            fileExists: path => File.Exists(Path.Combine(root, path)));
}

public static class ReaderDevicePaths
{
    public static string ResolveDirectory(string deviceRoot, string relativePath)
    {
        var root = Path.GetFullPath(deviceRoot);
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(UiText.Get("设备路径不在允许的目录范围内。"));
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(UiText.Get("设备目录不能是链接或联接点。"));
        }
        return target;
    }
}
