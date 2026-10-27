using System.Collections.Concurrent;
using System.Diagnostics;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Generates at most two audio files at once and de-duplicates requests for
/// the same chapter/text/options tuple. Prefetch failures are deliberately
/// isolated; the item is retried if it later becomes the current item.
/// </summary>
public sealed class TtsPlaybackQueue : IDisposable
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);
    private readonly ITtsEngine _engine;
    private readonly TtsCacheManager _cache;
    private readonly int _maxConcurrentGenerations;
    private SemaphoreSlim _generationSlots;
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<TtsResult>>> _inflight = new(StringComparer.Ordinal);
    private readonly HashSet<Task<TtsResult>> _activeTasks = [];
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
        _maxConcurrentGenerations = Math.Clamp(maxConcurrentGenerations, 1, 2);
        _generationSlots = new SemaphoreSlim(_maxConcurrentGenerations);
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
    public Task<CancellationToken> BeginSessionAsync(
        CancellationToken cancellationToken = default)
        => BeginSessionAsync(DefaultStopTimeout, cancellationToken);

    public async Task<CancellationToken> BeginSessionAsync(
        TimeSpan stopTimeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync(stopTimeout).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionCancellation = new CancellationTokenSource();
            return _sessionCancellation.Token;
        }
    }

    public Task<TtsResult> GetOrCreateAsync(
        TtsQueueItem item,
        CancellationToken cancellationToken = default)
    {
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
        Lazy<Task<TtsResult>> actual;
        Task<TtsResult> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            actual = _inflight.GetOrAdd(cachePath, lazy);
            task = actual.Value;
            _activeTasks.Add(task);
        }
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
        var generationSlots = Volatile.Read(ref _generationSlots);
        var acquired = false;
        string? temporaryPath = null;
        try
        {
            await generationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                try { generationSlots.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public Task StopAsync() => StopAsync(DefaultStopTimeout);

    public async Task StopAsync(TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        var startedAt = Stopwatch.GetTimestamp();
        CancellationTokenSource cancellation;
        KeyValuePair<string, Lazy<Task<TtsResult>>>[] pending;
        lock (_gate)
        {
            cancellation = _sessionCancellation;
            pending = _inflight
                .ToArray();
        }
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        var tasks = pending
            .Select(pair => pair.Value.Value)
            .ToArray();

        var tasksCompleted = true;
        if (tasks.Length > 0)
        {
            try
            {
                var remaining = RemainingTimeout(timeout, startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    tasksCompleted = false;
                }
                else
                {
                    await Task.WhenAll(tasks)
                        .WaitAsync(remaining)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                tasksCompleted = false;
            }
            catch (OperationCanceledException) { }
            catch
            {
                // Individual generation errors are returned as TtsResult. This
                // catch only covers a provider/task that violated that contract.
            }
        }

        // Detach this session even if a provider ignores cancellation. New
        // sessions must not inherit a permanently pending cache entry.
        foreach (var pair in pending)
            RemoveIfCurrent(pair.Key, pair.Value);

        if (!tasksCompleted)
        {
            // A provider that ignores cancellation may keep its old generation
            // slot forever. Give the next session a fresh pair of slots while
            // the abandoned task is allowed to unwind on its own.
            lock (_gate)
            {
                if (!_disposed)
                {
                    Volatile.Write(
                        ref _generationSlots,
                        new SemaphoreSlim(_maxConcurrentGenerations));
                }
            }
            // In-flight providers still hold this semaphore until they return.
            // Let it become collectible with those tasks instead of disposing
            // a synchronization primitive they may release later.
        }
    }

    private async Task RemoveWhenCompleteAsync(
        string key,
        Lazy<Task<TtsResult>> lazy,
        Task<TtsResult> task)
    {
        try
        {
            await ObserveAsync(task).ConfigureAwait(false);
        }
        finally
        {
            RemoveIfCurrent(key, lazy);
            lock (_gate) _activeTasks.Remove(task);
        }
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

    /// <summary>
    /// Observes every generation that was active when the queue was disposed.
    /// A provider that ignores cancellation may delay this indefinitely; in
    /// that case its owner must remain alive rather than being disposed under it.
    /// </summary>
    public async Task WaitForIdleAsync()
    {
        Task<TtsResult>[] tasks;
        lock (_gate) tasks = _activeTasks.ToArray();
        if (tasks.Length == 0) return;

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static TimeSpan RemainingTimeout(
        TimeSpan timeout,
        long startedAt)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cancellation = _sessionCancellation;
        }

        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        // Semaphores and cancellation tokens remain alive until active
        // generations have observed the cancellation. They are managed objects
        // and can be reclaimed with the queue if a provider never returns.
    }
}
