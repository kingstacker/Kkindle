using System.Collections.Concurrent;

namespace Kkindle.Infrastructure;

/// <summary>
/// Serializes multi-step library file and database mutations across the
/// desktop app and the standalone MCP process. SQLite already coordinates its
/// own transactions; this lease also protects filesystem moves that bracket
/// those transactions.
/// </summary>
internal sealed class AppDataProcessLock : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly SemaphoreSlim _processGate;
    private readonly FileStream _lockStream;
    private bool _disposed;

    private AppDataProcessLock(SemaphoreSlim processGate, FileStream lockStream)
    {
        _processGate = processGate;
        _lockStream = lockStream;
    }

    public static async Task<AppDataProcessLock> AcquireAsync(
        AppPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        var dataDirectory = Path.GetFullPath(paths.Data)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var processGate = ProcessGates.GetOrAdd(dataDirectory, static _ => new SemaphoreSlim(1, 1));
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lockPath = Path.Combine(dataDirectory, ".kkindle-data.lock");
            var giveUpAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                    return new AppDataProcessLock(processGate, stream);
                }
                catch (IOException exception)
                {
                    if (DateTimeOffset.UtcNow >= giveUpAt)
                        throw new TimeoutException("Kkindle 数据目录仍被另一个进程占用。", exception);
                    await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            processGate.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lockStream.Dispose(); }
        finally { _processGate.Release(); }
    }
}
