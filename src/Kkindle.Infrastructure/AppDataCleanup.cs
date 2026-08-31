namespace Kkindle.Infrastructure;

/// <summary>
/// Removes files that are intentionally excluded from the installer payload
/// so upgrades can preserve the user's library, while a real uninstall can
/// still remove all Kkindle-owned data.
/// </summary>
public static class AppDataCleanup
{
    private static readonly string[] TemporaryDirectoryRelativePaths =
    [
        Path.Combine("Kkindle", "updates"),
        "KkindleConversions",
        "KkindleKreaderValidation",
        "KkindleAnimationProbe"
    ];

    public static void RemoveForUninstall(string applicationDirectory, string? temporaryDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var applicationRoot = Path.GetFullPath(applicationDirectory);
        var configuredRoot = AppRootConfiguration.TryReadRoot(applicationRoot);
        var dataRoot = configuredRoot ?? applicationRoot;

        // The normal install root is removed by Inno's native
        // [UninstallDelete] entries. Do not make an uninstaller wrapper wait
        // for a second recursive delete of a potentially large library.
        // Only an externally selected root needs cleanup here because Inno
        // cannot express that runtime-selected path in the static script.
        if (configuredRoot is not null && !PathsEqual(applicationRoot, dataRoot))
        {
            TryDeleteDirectory(Path.Combine(dataRoot, "data"));
            TryDeleteDirectory(Path.Combine(dataRoot, "backups"));
            TryDeleteFile(AppRootConfiguration.MigrationBackupPath(dataRoot));
            TryDeleteEmptyDirectory(dataRoot);
        }

        TryDeleteFile(Path.Combine(applicationRoot, "app-root.json"));
        TryDeleteFile(Path.Combine(applicationRoot, "app-root.json.tmp"));
        TryDeleteFile(Path.Combine(applicationRoot, "kkindle-crash.log"));

        var tempRoot = Path.GetFullPath(temporaryDirectory ?? Path.GetTempPath());
        foreach (var relativePath in TemporaryDirectoryRelativePaths)
            TryDeleteDirectory(Path.Combine(tempRoot, relativePath));
        TryDeleteEmptyDirectory(Path.Combine(tempRoot, "Kkindle"));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        // Most directories can be removed in one native operation. Only pay
        // for a full attribute walk when a read-only item actually blocked it;
        // this matters when an uninstaller wrapper waits for this process.
        try
        {
            if (!Directory.Exists(path)) return;
            ClearReadOnlyAttributes(path);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(child, FileAttributes.Normal);
            File.SetAttributes(directory, FileAttributes.Normal);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
