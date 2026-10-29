using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class ReaderProgressRecoveryService
{
    internal const string RecoveryFileName = "reader-progress-recovery.json";
    internal const string LogFileName = "reader-progress.log";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly ReaderDataService _readerData;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _logGate = new();
    private readonly Dictionary<Guid, ReaderProgressRow> _pending = [];
    private readonly Dictionary<Guid, ReadingDataReset?> _pendingResets = [];
    private readonly Dictionary<Guid, ReaderRecoveryContext?> _pendingContexts = [];
    private bool _pendingLoaded;

    public ReaderProgressRecoveryService(AppPaths paths, ReaderDataService readerData)
    {
        _paths = paths;
        _readerData = readerData;
    }

    public async Task SaveAsync(
        ReaderProgressRow progress,
        CancellationToken cancellationToken = default)
    {
        ReaderRecoveryContext? context = _readerData.CachedRecoveryContext;
        try { context = await _readerData.GetRecoveryContextAsync(CancellationToken.None); }
        catch (Exception exception) { Log("Unable to read the progress recovery context.", exception); }
        // Pin the originating database before waiting behind another save.
        await _gate.WaitAsync();
        try
        {
            await EnsurePendingLoadedAsync(CancellationToken.None);
            var reset = context?.Reset;
            try
            {
                await _readerData.SaveRecoveredProgressAsync(progress, context, cancellationToken);
                if (_pending.TryGetValue(progress.BookFileId, out var queued)
                    && (_pendingContexts.GetValueOrDefault(progress.BookFileId) != context
                        || queued.UpdatedAt <= progress.UpdatedAt))
                {
                    RemovePending(progress.BookFileId);
                    await PersistPendingAsync(CancellationToken.None);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log("阅读记录保存已取消，已转入本地恢复队列。");
            }
            catch (Exception exception)
            {
                Log("阅读记录保存失败，已转入本地恢复队列。", exception);
            }

            if (!_pending.TryGetValue(progress.BookFileId, out var existing)
                || _pendingContexts.GetValueOrDefault(progress.BookFileId) != context
                || progress.UpdatedAt >= existing.UpdatedAt)
            {
                _pending[progress.BookFileId] = progress;
                _pendingResets[progress.BookFileId] = reset;
                _pendingContexts[progress.BookFileId] = context;
            }
            await PersistPendingAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsurePendingLoadedAsync(cancellationToken);
            if (_pending.Count == 0) return;

            var changed = false;
            foreach (var progress in _pending.Values.OrderBy(item => item.UpdatedAt).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _readerData.SaveRecoveredProgressAsync(
                        progress, _pendingContexts.GetValueOrDefault(progress.BookFileId), cancellationToken);
                    RemovePending(progress.BookFileId);
                    changed = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Log($"阅读记录恢复失败，将在下次启动时重试：{progress.BookFileId:N}", exception);
                }
            }

            if (changed)
                await PersistPendingAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsurePendingLoadedAsync(CancellationToken cancellationToken)
    {
        if (_pendingLoaded) return;
        _pendingLoaded = true;

        var recoveryPath = Path.Combine(_paths.Data, RecoveryFileName);
        if (!File.Exists(recoveryPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(recoveryPath, cancellationToken);
            var document = JsonSerializer.Deserialize<ReaderProgressRecoveryDocument>(json, JsonOptions);
            if (document?.Version is not (1 or 2 or 3))
                throw new JsonException("恢复文件的版本无效。");

            var current = await _readerData.GetRecoveryContextAsync(cancellationToken);
            var entries = new List<(ReaderProgressRow Progress, ReaderRecoveryContext? Context)>();
            foreach (var progress in document.Items ?? [])
            {
                if (progress is null || progress.BookId == Guid.Empty || progress.BookFileId == Guid.Empty)
                    throw new JsonException("恢复文件包含无效的书籍标识。");
                var reset = document.Version >= 2
                    ? document.ReadingDataResets?.GetValueOrDefault(progress.BookFileId)
                    : null;
                ReaderReadingDataReset.Validate(reset);
                if (document.Version < 3 && current.Imported) continue;
                var epoch = document.Version == 3
                    ? document.RecoveryEpochs?.GetValueOrDefault(progress.BookFileId) ?? Guid.Empty
                    : current.Epoch;
                entries.Add((progress, epoch == Guid.Empty ? null : new(epoch, reset, document.Version == 3 ? document.ImportedContexts?.GetValueOrDefault(progress.BookFileId) ?? false : current.Imported)));
            }

            foreach (var (progress, context) in entries)
            {
                if (!_pending.TryGetValue(progress.BookFileId, out var existing)
                    || progress.UpdatedAt >= existing.UpdatedAt)
                {
                    _pending[progress.BookFileId] = progress;
                    _pendingResets[progress.BookFileId] = context?.Reset;
                    _pendingContexts[progress.BookFileId] = context;
                }
            }
            if (document.Items?.Count > 0 && entries.Count == 0 && _pending.Count == 0)
                await PersistPendingAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pendingLoaded = false;
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            IsolateCorruptFile(recoveryPath, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _pendingLoaded = false;
            Log($"阅读记录恢复文件读取失败，已保留原文件：{recoveryPath}", exception);
        }
    }

    private void RemovePending(Guid bookFileId)
    {
        _pending.Remove(bookFileId);
        _pendingResets.Remove(bookFileId);
        _pendingContexts.Remove(bookFileId);
    }

    private async Task PersistPendingAsync(CancellationToken cancellationToken)
    {
        if (!_pendingLoaded) return;
        var recoveryPath = Path.Combine(_paths.Data, RecoveryFileName);
        if (_pending.Count == 0)
        {
            try
            {
                if (File.Exists(recoveryPath))
                    File.Delete(recoveryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"阅读记录恢复文件清理失败：{recoveryPath}", exception);
            }
            return;
        }

        Directory.CreateDirectory(_paths.Data);
        var temporaryPath = recoveryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = new ReaderProgressRecoveryDocument
            {
                Items = _pending.Values
                    .OrderBy(item => item.UpdatedAt)
                    .ToList(),
                ReadingDataResets = new Dictionary<Guid, ReadingDataReset?>(_pendingResets),
                RecoveryEpochs = _pendingContexts.ToDictionary(pair => pair.Key, pair => pair.Value?.Epoch ?? Guid.Empty),
                ImportedContexts = _pendingContexts.ToDictionary(pair => pair.Key, pair => pair.Value?.Imported ?? false)
            };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(false),
                cancellationToken);
            SettingsFile.Publish(temporaryPath, recoveryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log($"阅读记录恢复文件写入失败：{recoveryPath}", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"阅读记录恢复临时文件清理失败：{temporaryPath}", exception);
            }
        }
    }

    private void IsolateCorruptFile(string recoveryPath, Exception exception)
    {
        var corruptPath = recoveryPath
            + ".corrupt-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")
            + "-"
            + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(recoveryPath, corruptPath);
            Log($"阅读记录恢复文件损坏，已隔离到：{corruptPath}", exception);
        }
        catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
        {
            Log($"阅读记录恢复文件损坏且无法隔离：{recoveryPath}", moveException);
        }
    }

    private void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            var line = $"[{DateTimeOffset.UtcNow:O}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            line += Environment.NewLine;
            lock (_logGate)
            {
                File.AppendAllText(
                    Path.Combine(_paths.Logs, LogFileName),
                    line,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Recovery logging must never affect normal reading.
        }
    }

    private sealed class ReaderProgressRecoveryDocument
    {
        public int Version { get; init; } = 3;
        public List<ReaderProgressRow>? Items { get; init; }
        public Dictionary<Guid, ReadingDataReset?>? ReadingDataResets { get; init; }
        public Dictionary<Guid, Guid>? RecoveryEpochs { get; init; }
        public Dictionary<Guid, bool>? ImportedContexts { get; init; }
    }
}
