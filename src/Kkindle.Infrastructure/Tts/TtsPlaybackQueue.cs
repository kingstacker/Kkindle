using System.Collections.Concurrent;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Generates at most two audio files at once and de-duplicates requests for
/// the same chapter/text/options tuple. Prefetch failures are deliberately
/// isolated; the item is retried if it later becomes the current item.
/// </summary>
public sealed class TtsPlaybackQueue : IDisposable
{
    private readonly ITtsEngine _engine;
    private readonly TtsCacheManager _cache;
    private readonly SemaphoreSlim _generationSlots;
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<TtsResult>>> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource _sessionCancellation = new();
    private bool _disposed;

    public TtsPlaybackQueue(
        ITtsEngine engine,
        TtsCacheManager cache,
        int maxConcurrentGenerations = 2)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _generationSlots = new SemaphoreSlim(
            Math.Clamp(maxConcurrentGenerations, 1, 2));
    }

    public CancellationToken SessionToken
    {
        get
        {
            lock (_gate) return _sessionCancellation.Token;
        }
    }

    /// <summary>
    /// Cancels all pending work from the previous run and starts a fresh
    /// session. Completed cache files are never removed.
    /// </summary>
    public async Task<CancellationToken> BeginSessionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            return _sessionCancellation.Token;
        }
    }

    public Task<TtsResult> GetOrCreateAsync(
        TtsQueueItem item,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        var options = TtsOptions.Normalize(item.Options);
        var cachePath = _cache.GetCachePath(
            item.BookKey,
            item.ChapterKey,
            item.Segment.Text,
            options);
        var token = cancellationToken == default
            ? SessionToken
            : cancellationToken;
        var lazy = new Lazy<Task<TtsResult>>(
            () => GenerateAsync(item with { Options = options }, token),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = _inflight.GetOrAdd(cachePath, lazy);
        var task = actual.Value;
        _ = RemoveWhenCompleteAsync(cachePath, actual, task);
        return task;
    }

    /// <summary>
    /// Starts later requests without making the caller wait. Exceptions and
    /// cancellations are observed so prefetch never becomes an unhandled task.
    /// </summary>
    public void Prefetch(
        IEnumerable<TtsQueueItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            try
            {
                _ = ObserveAsync(GetOrCreateAsync(item, cancellationToken));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task<TtsResult> GenerateAsync(
        TtsQueueItem item,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        string? temporaryPath = null;
        try
        {
            await _generationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var cached = await _cache.FindAsync(
                    item.BookKey,
                    item.ChapterKey,
                    item.Segment.Text,
                    item.Options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return TtsResult.Success(
                    cached,
                    TtsOptions.Normalize(item.Options).AudioFormat,
                    fromCache: true);

            var generated = await _engine.SynthesizeAsync(
                    item.Segment.Text,
                    item.Options,
                    cancellationToken)
                .ConfigureAwait(false);
            temporaryPath = generated.AudioPath;
            if (!generated.IsSuccess)
            {
                return generated;
            }

            if (string.IsNullOrWhiteSpace(generated.AudioPath))
                return TtsResult.Failure("TTS 引擎没有返回音频文件。");

            var cachedPath = await _cache.WriteAsync(
                    item.BookKey,
                    item.ChapterKey,
                    item.Segment.Text,
                    item.Options,
                    generated.AudioPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return TtsResult.Success(
                cachedPath,
                string.IsNullOrWhiteSpace(generated.Format)
                    ? "mp3"
                    : generated.Format,
                fromCache: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return TtsResult.Failure($"TTS 音频生成失败：{exception.Message}");
        }
        finally
        {
            _cache.DeleteTemporaryFile(temporaryPath);
            if (acquired)
            {
                try { _generationSlots.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource cancellation;
        KeyValuePair<string, Lazy<Task<TtsResult>>>[] pending;
        lock (_gate)
        {
            cancellation = _sessionCancellation;
            cancellation.Cancel();
            pending = _inflight
                .ToArray();
        }

        var tasks = pending
            .Select(pair => pair.Value.Value)
            .ToArray();

        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch
            {
                // Individual generation errors are returned as TtsResult. This
                // catch only covers a provider/task that violated that contract.
            }
        }

        // Remove canceled entries synchronously before a new session can ask
        // for the same cache key. The conditional remove prevents an older
        // completion from deleting a request that a new session inserted.
        foreach (var pair in pending)
        {
            if (pair.Value.IsValueCreated && pair.Value.Value.IsCompleted)
                RemoveIfCurrent(pair.Key, pair.Value);
        }
    }

    private async Task RemoveWhenCompleteAsync(
        string key,
        Lazy<Task<TtsResult>> lazy,
        Task<TtsResult> task)
    {
        await ObserveAsync(task).ConfigureAwait(false);
        RemoveIfCurrent(key, lazy);
    }

    private bool RemoveIfCurrent(string key, Lazy<Task<TtsResult>> lazy)
        => ((ICollection<KeyValuePair<string, Lazy<Task<TtsResult>>>>)_inflight)
            .Remove(new KeyValuePair<string, Lazy<Task<TtsResult>>>(key, lazy));

    private static async Task ObserveAsync(Task<TtsResult> task)
    {
        try { await task.ConfigureAwait(false); }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sessionCancellation.Cancel();
        }

        _generationSlots.Dispose();
        _sessionCancellation.Dispose();
    }
}
