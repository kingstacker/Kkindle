using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class KindleDeviceAuxiliaryCacheSnapshot
{
    public List<KindleDeviceResource> Fonts { get; set; } = [];
    public List<KindleDeviceResource> Dictionaries { get; set; } = [];
    public List<KindleClipping> Clippings { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Persists non-book Kindle data per device serial/identity.</summary>
public sealed class KindleDeviceAuxiliaryCacheStore
{
    private const int CurrentVersion = 1;
    private const int FileOperationRetryAttempts = 30;
    private const int FileOperationRetryDelayMilliseconds = 100;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheDocument? _document;

    public KindleDeviceAuxiliaryCacheStore(AppPaths paths)
    {
        _path = Path.Combine(paths.Data, "kindle-device-cache.json");
    }

    public async Task<KindleDeviceAuxiliaryCacheSnapshot?> GetAsync(
        string deviceIdentity,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _document!.Devices
                .FirstOrDefault(item => item.Key.Equals(deviceIdentity, StringComparison.OrdinalIgnoreCase))
                ?.Snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(
        string deviceIdentity,
        KindleDeviceAuxiliaryCacheSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var entry = _document!.Devices.FirstOrDefault(item =>
                item.Key.Equals(deviceIdentity, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                _document.Devices.Add(new CacheEntry { Key = deviceIdentity, Snapshot = snapshot });
            else
                entry.Snapshot = snapshot;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, _document, cancellationToken: cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                await MoveFileAsync(temporary, _path, cancellationToken);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task MoveFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                // Windows reports an open destination as access denied for
                // some filesystem providers instead of ERROR_SHARING_VIOLATION.
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_document is not null) return;
        if (!File.Exists(_path))
        {
            _document = new CacheDocument();
            return;
        }
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var loaded = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, cancellationToken: cancellationToken);
            _document = loaded is { Version: CurrentVersion } ? loaded : new CacheDocument();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _document = new CacheDocument();
        }
    }

    private sealed class CacheDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public List<CacheEntry> Devices { get; set; } = [];
    }

    private sealed class CacheEntry
    {
        public string Key { get; set; } = string.Empty;
        public KindleDeviceAuxiliaryCacheSnapshot Snapshot { get; set; } = new();
    }
}
