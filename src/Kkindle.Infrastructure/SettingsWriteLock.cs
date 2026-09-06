using System.Collections.Concurrent;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

// All settings writers for a data directory share this lock. Sync can compare
// and update the four portable settings files without racing an ordinary save.
internal static class SettingsWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async Task<IDisposable> AcquireAsync(AppPaths paths, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(paths.Data), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
