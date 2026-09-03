namespace Kkindle.Infrastructure;

/// <summary>
/// Paths for dependencies installed by Kkindle itself. Keeping pipx's
/// virtual environments and entry points below the app data root means the
/// running process never has to depend on a user's shell profile or PATH
/// changes made after the app started.
/// </summary>
public static class TtsRuntimePaths
{
    public static string Root(AppPaths paths)
        => Path.Combine(paths.Data, "tts-runtime");

    public static string PipxHome(AppPaths paths)
        => Path.Combine(Root(paths), "pipx");

    public static string PipxBin(AppPaths paths)
        => Path.Combine(Root(paths), "bin");

    public static string PipxMan(AppPaths paths)
        => Path.Combine(Root(paths), "man");

    public static string PipxVenvCache(AppPaths paths)
        => Path.Combine(Root(paths), "venv-cache");

    public static IReadOnlyList<string> EdgeTtsCandidates(AppPaths paths)
    {
        var directory = PipxBin(paths);
        return OperatingSystem.IsWindows()
            ? [
                Path.Combine(directory, "edge-tts.exe"),
                Path.Combine(directory, "edge-tts.cmd"),
                Path.Combine(directory, "edge-tts.bat"),
            ]
            : [Path.Combine(directory, "edge-tts")];
    }

    public static string PreferredEdgeTtsPath(AppPaths paths)
        => EdgeTtsCandidates(paths)[0];
}
