using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal sealed class KindleScanCacheEntry
{
    public KindleScanCacheEntry() { }

    public string DeviceIdentity { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public string Format { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public bool IsDictionary { get; set; }

    public bool Matches(long size, DateTimeOffset? modifiedAt)
    {
        if (Size != size) return false;
        if (ModifiedAt is null || modifiedAt is null) return ModifiedAt is null && modifiedAt is null;
        return Math.Abs((ModifiedAt.Value - modifiedAt.Value).TotalSeconds) < 1;
    }
}

internal sealed class KindleScanCacheStore
{
    private const int CurrentVersion = 4;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheDocument? _document;

    public KindleScanCacheStore(AppPaths paths)
    {
        _path = Path.Combine(paths.Data, "kindle-scan-cache.json");
    }

    public async Task<IReadOnlyDictionary<string, KindleScanCacheEntry>> GetDeviceEntriesAsync(
        string deviceIdentity,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _document!.Entries
                .Where(entry => entry.DeviceIdentity.Equals(deviceIdentity, StringComparison.OrdinalIgnoreCase))
                .GroupBy(entry => NormalizePath(entry.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(string deviceIdentity, CancellationToken cancellationToken)
    {
        var entries = await GetDeviceEntriesAsync(deviceIdentity, cancellationToken);
        return entries.Values.Where(entry => !entry.IsDictionary && !string.IsNullOrWhiteSpace(entry.RelativePath))
            .Select(entry => new KindleBook
            {
                RelativePath = entry.RelativePath, Size = entry.Size, ModifiedAt = entry.ModifiedAt,
                Format = entry.Format, Sha256 = entry.Sha256, Title = entry.Title, Authors = entry.Authors,
                CoverPath = entry.CoverPath is not null && File.Exists(entry.CoverPath) ? entry.CoverPath : null
            }).OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task ReplaceDeviceEntriesAsync(
        string deviceIdentity,
        IEnumerable<KindleScanCacheEntry> entries,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _document!.Entries.RemoveAll(entry =>
                entry.DeviceIdentity.Equals(deviceIdentity, StringComparison.OrdinalIgnoreCase));
            _document.Entries.AddRange(entries);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporary,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 useAsync: true))
                    await JsonSerializer.SerializeAsync(stream, _document, cancellationToken: cancellationToken);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }
        finally { _gate.Release(); }
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
            if (loaded is { Version: 3 })
            {
                // MTP scans previously skipped PDF metadata entirely, then cached
                // the empty result. Retry those entries once without discarding
                // covers or metadata that were already extracted successfully.
                loaded.Entries.RemoveAll(entry =>
                    entry.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(entry.Sha256)
                    && string.IsNullOrEmpty(entry.CoverPath));
                loaded.Version = CurrentVersion;
            }
            _document = loaded is { Version: CurrentVersion } ? loaded : new CacheDocument();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _document = new CacheDocument();
        }
    }

    private static string NormalizePath(string path) => path.Replace('/', '\\').TrimStart('\\');

    private sealed class CacheDocument
    {
        public CacheDocument() { }

        public int Version { get; set; } = CurrentVersion;
        public List<KindleScanCacheEntry> Entries { get; set; } = [];
    }
}
