using System.IO.Compression;
using System.Text;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>
/// Reads and validates a <c>.kkplugin</c> file: a zip archive with a
/// <c>manifest.json</c> at its root plus the pages and assets it refers to.
/// Nothing in the archive is executed or extracted here.
/// </summary>
public static class PluginPackage
{
    public const string Extension = ".kkplugin";
    public const long MaximumPackageBytes = 32L * 1024 * 1024;
    public const long MaximumExtractedBytes = 64L * 1024 * 1024;
    public const int MaximumEntries = 1000;
    private const long MaximumManifestBytes = 256 * 1024;

    public static PluginManifest ReadManifest(string packagePath)
    {
        using var archive = Open(packagePath);
        return Validate(archive);
    }

    internal static ZipArchive Open(string packagePath)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists)
            throw new PluginPackageException("找不到插件文件。");
        if (info.Length > MaximumPackageBytes)
            throw new PluginPackageException("插件文件超过 32 MB。");
        try
        {
            return ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException)
        {
            throw new PluginPackageException("插件文件已损坏，或不是有效的 .kkplugin 文件。");
        }
    }

    /// <summary>Validates every entry and returns the parsed manifest.</summary>
    internal static PluginManifest Validate(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntries)
            throw new PluginPackageException("插件包含的文件过多。");

        long total = 0;
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (IsDirectory(entry)) continue;
            var path = NormalizeEntryPath(entry.FullName);
            if (!files.Add(path))
                throw new PluginPackageException($"插件包含重复的文件“{path}”。");
            total += entry.Length;
            if (entry.Length < 0 || total > MaximumExtractedBytes)
                throw new PluginPackageException("插件解压后超过 64 MB。");
        }

        var manifestEntry = archive.Entries.FirstOrDefault(entry =>
            !IsDirectory(entry) && NormalizeEntryPath(entry.FullName) == PluginManifest.FileName)
            ?? throw new PluginPackageException("插件缺少 manifest.json。");
        if (manifestEntry.Length > MaximumManifestBytes)
            throw new PluginPackageException("manifest.json 过大。");
        string json;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            json = reader.ReadToEnd();
        var manifest = PluginManifest.Parse(json);

        foreach (var page in manifest.PagePaths)
        {
            if (!files.Contains(page))
                throw new PluginPackageException($"插件缺少页面文件“{page}”。");
        }
        if (manifest.Icon is { } icon && !files.Contains(icon))
            throw new PluginPackageException($"插件缺少图标文件“{icon}”。");
        return manifest;
    }

    /// <summary>
    /// Extracts a validated archive into an empty directory. Paths were
    /// normalized during validation, so they cannot escape the target.
    /// </summary>
    internal static void ExtractTo(ZipArchive archive, string directory)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            if (IsDirectory(entry)) continue;
            var relative = NormalizeEntryPath(entry.FullName);
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PluginPackageException($"插件文件路径“{relative}”无效。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    /// <summary>
    /// Converts an archive or manifest path into a canonical relative path
    /// with forward slashes, rejecting anything that could leave the plugin
    /// directory.
    /// </summary>
    public static string NormalizeEntryPath(string path)
    {
        var value = path.Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        var segments = value.Split('/');
        if (value.Length == 0
            || value.StartsWith('/')
            || value.Contains(':')
            || value.Length > 240
            || segments.Any(segment => segment is "" or "." or ".."
                || segment.Any(character => char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*')))
            throw new PluginPackageException($"插件文件路径“{path}”无效。");
        return value;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
}
