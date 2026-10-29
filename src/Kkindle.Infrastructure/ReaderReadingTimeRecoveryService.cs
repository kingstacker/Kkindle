using System.Text.Json;

namespace Kkindle.Infrastructure;

internal sealed record ReadingTimeCheckpoint(Guid Id, Guid BookId, Guid BookFileId, long Seconds,
    double ProgressPercent, int CompletedChapters, int TotalChapters, DateTimeOffset RecordedAt,
    ReaderRecoveryContext? Context);

public sealed class ReaderReadingTimeRecoveryService(AppPaths paths, ReaderDataService reader)
{
    internal const string FileName = "reader-reading-time-recovery.json";
    private const string SpoolDirectoryName = "reader-reading-time-pending";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ReadingTimeCheckpoint> _pending = [];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private bool _loaded;

    public async Task SaveAsync(Guid bookId, Guid fileId, long seconds, double progress, int chapters, int total,
        CancellationToken token = default)
    {
        if (seconds <= 0) return;
        var context = reader.CachedRecoveryContext;
        try { context = await reader.GetRecoveryContextAsync(CancellationToken.None); }
        catch (Exception exception) { Log(exception); }
        var checkpoint = new ReadingTimeCheckpoint(Guid.NewGuid(), bookId, fileId, seconds, progress, chapters, total,
            DateTimeOffset.UtcNow, context);
        await _gate.WaitAsync();
        try
        {
            await LoadAsync(CancellationToken.None);
            _pending[checkpoint.Id] = checkpoint;
            // Independent files remain writable even when the legacy queue is
            // locked. The SQLite applied-ID ledger makes replay idempotent.
            await PersistSpoolAsync();
            await PersistAsync();
            await ApplyPendingAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { await LoadAsync(token); await PersistSpoolAsync(); await ApplyPendingAsync(token); }
        finally { _gate.Release(); }
    }

    private async Task ApplyPendingAsync(CancellationToken token)
    {
        foreach (var item in _pending.Values.OrderBy(item => item.RecordedAt).ToArray())
        {
            try
            {
                await reader.AddRecoveredReadingTimeAsync(item, token);
                _pending.Remove(item.Id);
                try { File.Delete(SpoolPath(item.Id)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Log(exception); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) { Log(exception); }
        }
        await PersistAsync();
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (_loaded) return;
        var spoolDirectory = Path.Combine(paths.Data, SpoolDirectoryName);
        if (Directory.Exists(spoolDirectory))
        {
            foreach (var entry in Directory.EnumerateFiles(spoolDirectory, "*.json"))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<ReadingTimeCheckpoint>(await File.ReadAllTextAsync(entry, token), Options)
                        ?? throw new JsonException("Invalid reading time checkpoint.");
                    if (item.Id == Guid.Empty || item.BookId == Guid.Empty || item.BookFileId == Guid.Empty || item.Seconds <= 0
                        || !string.Equals(Path.GetFileNameWithoutExtension(entry), item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                        throw new JsonException("Invalid reading time checkpoint.");
                    _pending.TryAdd(item.Id, item);
                }
                catch (JsonException exception)
                {
                    Log(exception);
                    File.Move(entry, entry + ".corrupt-" + Guid.NewGuid().ToString("N"));
                }
            }
        }
        var file = Path.Combine(paths.Data, FileName);
        try
        {
            if (File.Exists(file))
            {
                var items = JsonSerializer.Deserialize<ReadingTimeCheckpoint[]>(await File.ReadAllTextAsync(file, token), Options)
                    ?? throw new JsonException("Invalid reading time recovery queue.");
                foreach (var item in items)
                {
                    if (item is null || item.Id == Guid.Empty || item.BookId == Guid.Empty || item.BookFileId == Guid.Empty || item.Seconds <= 0)
                        throw new JsonException("Invalid reading time recovery checkpoint.");
                }
                foreach (var item in items) _pending.TryAdd(item.Id, item);
            }
            _loaded = true;
        }
        catch (JsonException exception)
        {
            try { File.Move(file, file + ".corrupt-" + Guid.NewGuid().ToString("N")); _loaded = true; }
            catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException) { Log(moveException); }
            Log(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Log(exception); }
    }

    private string SpoolPath(Guid id) => Path.Combine(paths.Data, SpoolDirectoryName, id.ToString("N") + ".json");

    private async Task PersistSpoolAsync()
    {
        if (_pending.Count == 0) return;
        Directory.CreateDirectory(Path.Combine(paths.Data, SpoolDirectoryName));
        foreach (var item in _pending.Values)
        {
            var file = SpoolPath(item.Id);
            if (File.Exists(file)) continue;
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(output, item, Options);
                    await output.FlushAsync();
                    output.Flush(flushToDisk: true);
                }
                SettingsFile.Publish(temporary, file);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Log(exception); }
            }
        }
    }

    private async Task PersistAsync()
    {
        if (!_loaded) return;
        var file = Path.Combine(paths.Data, FileName);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (_pending.Count == 0) { File.Delete(file); return; }
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, _pending.Values.ToArray(), Options);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);
            }
            SettingsFile.Publish(temporary, file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Log(exception); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Log(exception); }
        }
    }

    private void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(paths.Logs);
            File.AppendAllText(Path.Combine(paths.Logs, "reader-reading-time.log"), $"[{DateTimeOffset.UtcNow:O}] {exception}{Environment.NewLine}");
        }
        catch { }
    }
}
