namespace Kkindle;

/// <summary>
/// Holds files that arrived before the main window was ready. macOS can
/// deliver Finder document activations while Avalonia is still starting, and
/// the second-instance socket can theoretically beat window construction too.
/// </summary>
public static class ExternalFileOpenQueue
{
    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];

    public static void Enqueue(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (Gate)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!File.Exists(path)) continue;
                if (!Pending.Contains(path, StringComparer.OrdinalIgnoreCase))
                    Pending.Add(path);
            }
        }
    }

    public static string[] Drain()
    {
        lock (Gate)
        {
            var paths = Pending.ToArray();
            Pending.Clear();
            return paths;
        }
    }
}
