namespace Kkindle.Infrastructure;

/// <summary>
/// Removes files that are intentionally excluded from the installer payload
/// so upgrades can preserve the user's library, while a real uninstall can
/// still remove all Kkindle-owned data.
/// </summary>
public static class AppDataCleanup
{
    private static readonly string[] TemporaryDirectoryNames =
    [
        "Kkindle",
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

        // These are the only directories Kkindle creates below its data root.
        // Do not delete an arbitrary user-selected root recursively: a user may
        // have selected a folder that also contains unrelated files.
        TryDeleteDirectory(Path.Combine(dataRoot, "data"));
        TryDeleteDirectory(Path.Combine(dataRoot, "backups"));
        TryDeleteFile(AppRootConfiguration.MigrationBackupPath(dataRoot));

        // An externally selected root is safe to remove only when it is empty
        // after its Kkindle-owned children have been deleted.
        if (!PathsEqual(applicationRoot, dataRoot))
            TryDeleteEmptyDirectory(dataRoot);

        TryDeleteFile(Path.Combine(applicationRoot, "app-root.json"));
        TryDeleteFile(Path.Combine(applicationRoot, "app-root.json.tmp"));
        TryDeleteFile(Path.Combine(applicationRoot, "kkindle-crash.log"));

        var tempRoot = Path.GetFullPath(temporaryDirectory ?? Path.GetTempPath());
        foreach (var name in TemporaryDirectoryNames)
            TryDeleteDirectory(Path.Combine(tempRoot, name));
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
