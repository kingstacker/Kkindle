namespace Kkindle.Infrastructure;

internal enum DesktopOperatingSystem
{
    Windows,
    Linux,
    MacOS
}

internal static class CalibreExecutableLocator
{
    public static string? Locate(
        string applicationDirectory,
        string? overridePath,
        string? pathVariable,
        DesktopOperatingSystem operatingSystem,
        string? programFiles = null,
        string? programFilesX86 = null,
        string? userProfile = null)
    {
        var executableName = ToolName("ebook-convert", operatingSystem);
        var resolvedOverride = ResolveOverride(overridePath, executableName, operatingSystem);
        if (resolvedOverride is not null) return resolvedOverride;
        var candidates = new List<string>
        {
            Path.Combine(applicationDirectory, "Calibre", executableName),
            Path.Combine(applicationDirectory, "Calibre2", executableName)
        };
        switch (operatingSystem)
        {
            case DesktopOperatingSystem.Windows:
                AddWindowsInstall(candidates, programFiles, executableName);
                AddWindowsInstall(candidates, programFilesX86, executableName);
                break;
            case DesktopOperatingSystem.Linux:
                candidates.Add("/usr/bin/ebook-convert");
                candidates.Add("/usr/local/bin/ebook-convert");
                candidates.Add("/opt/calibre/ebook-convert");
                if (!string.IsNullOrWhiteSpace(userProfile))
                    candidates.Add(Path.Combine(userProfile, "calibre-bin", executableName));
                break;
            case DesktopOperatingSystem.MacOS:
                candidates.Add("/Applications/calibre.app/Contents/MacOS/ebook-convert");
                if (!string.IsNullOrWhiteSpace(userProfile))
                    candidates.Add(Path.Combine(userProfile, "Applications", "calibre.app", "Contents", "MacOS", executableName));
                candidates.Add("/opt/homebrew/bin/ebook-convert");
                candidates.Add("/usr/local/bin/ebook-convert");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operatingSystem));
        }
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = directory.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(trimmed)) candidates.Add(Path.Combine(trimmed, executableName));
            }
        }
        return candidates.Where(File.Exists).Select(Path.GetFullPath).FirstOrDefault();
    }

    public static DesktopOperatingSystem CurrentOperatingSystem()
    {
        if (OperatingSystem.IsWindows()) return DesktopOperatingSystem.Windows;
        if (OperatingSystem.IsLinux()) return DesktopOperatingSystem.Linux;
        if (OperatingSystem.IsMacOS()) return DesktopOperatingSystem.MacOS;
        throw new PlatformNotSupportedException("Kkindle supports Calibre discovery on Windows, Linux, and macOS.");
    }

    public static string ToolName(string baseName, DesktopOperatingSystem operatingSystem) =>
        operatingSystem == DesktopOperatingSystem.Windows ? baseName + ".exe" : baseName;

    private static string? ResolveOverride(
        string? overridePath,
        string executableName,
        DesktopOperatingSystem operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return null;
        var path = Path.GetFullPath(overridePath.Trim().Trim('"'));
        if (File.Exists(path))
        {
            // The settings field is meant for ebook-convert, but users often
            // select calibre/calibre.exe from the same installation. Resolve
            // that launcher to its sibling converter instead of starting the
            // GUI, which rejects conversion-only options such as
            // --output-profile.
            if (IsEbookConvertFile(path, executableName)) return path;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var siblingCandidates = new[]
                {
                    Path.Combine(directory, executableName),
                    Path.Combine(directory, "ebook-convert"),
                    Path.Combine(directory, "ebook-convert.exe")
                };
                return siblingCandidates
                    .Where(File.Exists)
                    .Select(Path.GetFullPath)
                    .FirstOrDefault();
            }

            return null;
        }
        if (!Directory.Exists(path)) return null;

        var candidates = new List<string> { Path.Combine(path, executableName) };
        if (operatingSystem == DesktopOperatingSystem.MacOS && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(path, "Contents", "MacOS", executableName));
        candidates.Add(Path.Combine(path, "Calibre2", executableName));
        candidates.Add(Path.Combine(path, "Calibre", executableName));
        return candidates.Where(File.Exists).Select(Path.GetFullPath).FirstOrDefault();
    }

    private static bool IsEbookConvertFile(string path, string executableName)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals(executableName, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ebook-convert", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ebook-convert.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddWindowsInstall(List<string> candidates, string? root, string executableName)
    {
        if (!string.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(root, "Calibre2", executableName));
    }
}
