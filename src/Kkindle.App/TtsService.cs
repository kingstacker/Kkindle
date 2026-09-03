using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed class TtsStateChangedEventArgs : EventArgs
{
    public TtsStateChangedEventArgs(
        TtsPlaybackState state,
        string? message,
        int segmentIndex,
        int segmentCount)
    {
        State = state;
        Message = message;
        SegmentIndex = segmentIndex;
        SegmentCount = segmentCount;
    }

    public TtsPlaybackState State { get; }
    public string? Message { get; }
    public int SegmentIndex { get; }
    public int SegmentCount { get; }
}

/// <summary>
/// Coordinates sentence segmentation, cached generation, playback, sentence
/// highlighting and automatic chapter navigation. UI code only supplies the
/// current reader document and receives state changes.
/// </summary>
public sealed class TtsService : IDisposable
{
    public const string PreviewText = "你好，这是声音试听。";

    private readonly ITtsEngine? _engine;
    private readonly TtsCacheManager _cache;
    private readonly TtsPlaybackQueue? _queue;
    private readonly ITtsAudioPlayer? _player;
    private readonly ITtsEnvironmentSetup? _environmentSetup;
    private readonly Func<ReaderTtsDocument?> _documentAccessor;
    private readonly Func<CancellationToken, Task<bool>>? _advanceChapterAsync;
    private readonly Func<CancellationToken, Task<ReaderTtsDocument?>>?
        _prefetchDocumentAccessor;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly object _gate = new();

    private TtsSettings _settings = new();
    private TtsAvailability? _availability;
    private Task? _loopTask;
    private CancellationToken _sessionToken;
    private int _generation;
    private bool _disposed;
    private TtsPlaybackState _state = TtsPlaybackState.Stopped;
    private string? _message;
    private string? _environmentMessage;
    private bool _environmentSetupInProgress;
    private int _segmentIndex;
    private int _segmentCount;
    private IReadOnlyList<TtsTextSegment>? _currentSegments;
    private string? _currentDocumentKey;
    private int _currentSegmentIndex = -1;
    private int _requestedSegmentIndex = -1;
    private bool _previewInProgress;

    public TtsService(
        ITtsEngine? engine,
        TtsCacheManager cache,
        ITtsAudioPlayer? player,
        Func<ReaderTtsDocument?> documentAccessor,
        Func<CancellationToken, Task<bool>>? advanceChapterAsync = null,
        ITtsEnvironmentSetup? environmentSetup = null,
        Func<CancellationToken, Task<ReaderTtsDocument?>>? prefetchDocumentAccessor = null)
    {
        _engine = engine;
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _queue = engine is null ? null : new TtsPlaybackQueue(engine, cache);
        _player = player;
        _environmentSetup = environmentSetup;
        _documentAccessor = documentAccessor
            ?? throw new ArgumentNullException(nameof(documentAccessor));
        _advanceChapterAsync = advanceChapterAsync;
        _prefetchDocumentAccessor = prefetchDocumentAccessor;
    }

    public event EventHandler<TtsStateChangedEventArgs>? StateChanged;
    public event EventHandler? EnvironmentChanged;

    /// <summary>True only when both edge-tts and audio output are ready.</summary>
    public bool IsAvailable =>
        !_disposed
        && _engine?.IsAvailable == true
        && _player?.IsAvailable == true;

    /// <summary>
    /// Allows the reader to open the settings popup even when edge-tts is
    /// missing, so the installation instructions remain visible.
    /// </summary>
    public bool CanOpen => !_disposed && _player is not null;

    public TtsPlaybackState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public string? Message
    {
        get
        {
            lock (_gate) return _message;
        }
    }

    public TtsAvailability? Availability
    {
        get
        {
            lock (_gate) return _availability;
        }
    }

    public bool EnvironmentSetupInProgress
    {
        get
        {
            lock (_gate) return _environmentSetupInProgress;
        }
    }

    public bool PreviewInProgress
    {
        get
        {
            lock (_gate) return _previewInProgress;
        }
    }

    public string? EnvironmentMessage
    {
        get
        {
            lock (_gate) return _environmentMessage;
        }
    }

    public TtsSettings Settings
    {
        get
        {
            lock (_gate) return _settings.Clone();
        }
    }

    public int SegmentIndex
    {
        get
        {
            lock (_gate) return _segmentIndex;
        }
    }

    public int SegmentCount
    {
        get
        {
            lock (_gate) return _segmentCount;
        }
    }

    public bool CanSkipPrevious
    {
        get
        {
            lock (_gate)
            {
                return _currentSegmentIndex > 0
                    && _currentSegments is { Count: > 0 };
            }
        }
    }

    public bool CanSkipNext
    {
        get
        {
            lock (_gate)
            {
                return _currentSegments is { Count: > 0 } segments
                    && _currentSegmentIndex >= 0
                    && _currentSegmentIndex + 1 < segments.Count;
            }
        }
    }

    public async Task<TtsAvailability> CheckEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        if (_engine is null)
        {
            var unavailable = new TtsAvailability(
                false,
                "当前平台没有配置 TTS 引擎。");
            lock (_gate) _availability = unavailable;
            return unavailable;
        }

        var availability = await _engine.CheckAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        availability = IncludePlayerAvailability(availability);

        lock (_gate) _availability = availability;
        return availability;
    }

    /// <summary>
    /// Ensures the platform dependencies exist before playback starts. The
    /// setup service is optional so tests and future providers can omit it.
    /// </summary>
    public async Task<TtsAvailability> EnsureEnvironmentReadyAsync(
        IProgress<TtsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is null)
        {
            var unavailable = new TtsAvailability(
                false,
                "当前平台没有配置 TTS 引擎。 ");
            lock (_gate)
            {
                _availability = unavailable;
                _environmentMessage = unavailable.Message;
                _environmentSetupInProgress = false;
            }
            return unavailable;
        }

        if (_environmentSetup is not null)
        {
            SetEnvironmentStatus("正在自动准备 TTS 环境…", inProgress: true);
            var setupProgress = new Progress<TtsSetupProgress>(update =>
            {
                SetEnvironmentStatus(update.Message, inProgress: true);
                progress?.Report(update);
            });
            var setup = await _environmentSetup.EnsureReadyAsync(
                    setupProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!setup.IsSuccess)
            {
                SetEnvironmentStatus(setup.Message, inProgress: false);
                var unavailable = new TtsAvailability(false, setup.Message);
                lock (_gate) _availability = unavailable;
                return unavailable;
            }
        }

        var availability = await CheckEnvironmentAsync(cancellationToken)
            .ConfigureAwait(false);
        SetEnvironmentStatus(
            availability.IsAvailable ? "TTS 环境已就绪。" : availability.Message,
            inProgress: false);
        return availability;
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_engine is null) return [];
        // The voice popup can be opened while the startup bootstrap is still
        // running. Reuse it here so a fast click does not race the installer.
        if (_environmentSetup is not null)
        {
            var ready = await EnsureEnvironmentReadyAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!ready.IsAvailable) return [];
        }

        var engineAvailability = await _engine.CheckAvailabilityAsync(
                cancellationToken)
            .ConfigureAwait(false);
        var availability = IncludePlayerAvailability(engineAvailability);
        lock (_gate) _availability = availability;
        if (!engineAvailability.IsAvailable) return [];
        return await _engine.GetVoicesAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private TtsAvailability IncludePlayerAvailability(
        TtsAvailability availability)
    {
        if (_player is null || _player.IsAvailable) return availability;

        var playerMessage = string.IsNullOrWhiteSpace(_player.UnavailableReason)
            ? "当前平台没有可用的音频输出。"
            : _player.UnavailableReason;
        return availability with
        {
            IsAvailable = false,
            Message = string.IsNullOrWhiteSpace(availability.Message)
                ? playerMessage
                : availability.Message + "\n\n" + playerMessage
        };
    }

    public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        string engineId,
        CancellationToken cancellationToken = default)
        => string.Equals(
            engineId,
            _engine?.Id,
            StringComparison.OrdinalIgnoreCase)
            ? GetVoicesAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<TtsVoiceInfo>>([]);

    /// <summary>
    /// Generates and plays one fixed sample using the supplied settings. The
    /// sample is deliberately not written to the book cache; its temporary MP3
    /// is removed after playback or cancellation.
    /// </summary>
    public async Task PreviewAsync(
        TtsSettings settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = TtsSettings.Normalize(settings);
        if (_engine is null || _player is null)
        {
            throw new InvalidOperationException(
                "当前平台没有配置 TTS 引擎或音频播放器。");
        }

        await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var markedInProgress = false;
        string? temporaryPath = null;
        try
        {
            // StartAsync holds this gate while it prepares a session. Taking it
            // here prevents a preview from racing an in-flight start, while the
            // short-lived flag below prevents a new start during playback.
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_loopTask is { IsCompleted: false }
                        || _state is TtsPlaybackState.Generating
                            or TtsPlaybackState.Playing
                            or TtsPlaybackState.Paused
                            or TtsPlaybackState.AdvancingChapter)
                    {
                        throw new InvalidOperationException(
                            "请先停止听书，再试听声音。");
                    }

                    _previewInProgress = true;
                    markedInProgress = true;
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            var availability = await EnsureEnvironmentReadyAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                throw new InvalidOperationException(availability.Message);
            }

            if (!_player.IsAvailable)
            {
                throw new InvalidOperationException(
                    _player.UnavailableReason ?? "当前平台没有可用的音频输出。");
            }

            var generated = await _engine.SynthesizeAsync(
                    PreviewText,
                    normalized.ToOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            temporaryPath = generated.AudioPath;
            if (!generated.IsSuccess || string.IsNullOrWhiteSpace(generated.AudioPath))
            {
                throw new InvalidOperationException(
                    generated.ErrorMessage ?? "声音试听失败。");
            }

            await _player.PlayAsync(
                    generated.AudioPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _cache.DeleteTemporaryFile(temporaryPath);
            if (markedInProgress)
            {
                lock (_gate) _previewInProgress = false;
            }

            try { _previewGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task StartAsync(
        TtsSettings settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = TtsSettings.Normalize(settings);

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_previewInProgress) return;
                if (_loopTask is { IsCompleted: false }) return;
                _settings = normalized;
                _message = null;
                _segmentIndex = 0;
                _segmentCount = 0;
                _currentSegments = null;
                _currentDocumentKey = null;
                _currentSegmentIndex = -1;
                _requestedSegmentIndex = -1;
            }

            if (_engine is null || _queue is null || _player is null)
            {
                SetState(
                    TtsPlaybackState.Error,
                    "当前平台没有配置 TTS 引擎或音频播放器。",
                    0,
                    0);
                return;
            }

            var availability = await EnsureEnvironmentReadyAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                SetState(
                    TtsPlaybackState.Error,
                    availability.Message,
                    0,
                    0);
                return;
            }

            if (!_player.IsAvailable)
            {
                SetState(
                    TtsPlaybackState.Error,
                    _player.UnavailableReason ?? "当前平台没有可用的音频输出。",
                    0,
                    0);
                return;
            }

            _sessionToken = await _queue.BeginSessionAsync().ConfigureAwait(false);
            var generation = ++_generation;
            lock (_gate)
            {
                if (_disposed) return;
                _loopTask = RunAsync(generation, normalized, _sessionToken);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void PauseOrResume()
    {
        if (_disposed || _player is null) return;

        TtsPlaybackState state;
        lock (_gate) state = _state;
        if (state == TtsPlaybackState.Playing)
        {
            _player.Pause();
            SetState(
                TtsPlaybackState.Paused,
                null,
                SegmentIndex,
                SegmentCount);
        }
        else if (state == TtsPlaybackState.Paused)
        {
            _player.Resume();
            SetState(
                TtsPlaybackState.Playing,
                null,
                SegmentIndex,
                SegmentCount);
        }
    }

    public Task SkipSegmentAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _disposed || _player is null) return Task.CompletedTask;

        lock (_gate)
        {
            if (_currentSegments is not { Count: > 0 }
                || _currentSegmentIndex < 0
                || _state is TtsPlaybackState.Stopped or TtsPlaybackState.Error)
            {
                return Task.CompletedTask;
            }

            var target = Math.Clamp(
                _currentSegmentIndex + direction,
                0,
                _currentSegments.Count - 1);
            if (target == _currentSegmentIndex) return Task.CompletedTask;
            _requestedSegmentIndex = target;
        }

        // Stop only the audio. The session token stays alive so the loop can
        // continue with the requested segment and prefetching is preserved.
        _player.Stop();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? loop;
            lock (_gate)
            {
                loop = _loopTask;
                _requestedSegmentIndex = -1;
            }

            if (_queue is not null)
                await _queue.StopAsync().ConfigureAwait(false);
            try { _player?.Stop(); }
            catch { }

            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }

            ClearCurrentHighlight();
            SetState(TtsPlaybackState.Stopped, null, 0, 0);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task RunAsync(
        int generation,
        TtsSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var availability = await CheckEnvironmentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!availability.IsAvailable)
                throw new InvalidOperationException(availability.Message);

            var firstDocument = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = _documentAccessor();
                if (document is null || string.IsNullOrWhiteSpace(document.Text))
                    throw new InvalidOperationException("当前章节没有可朗读的正文。");

                var segments = TtsTextSegmenter.SplitSentencesAtPageBreaks(
                    document.Text,
                    document.PageBreakOffsets,
                    settings.MaxCharactersPerRequest);
                if (segments.Count == 0)
                    throw new InvalidOperationException("当前章节没有可朗读的正文。");

                var startIndex = firstDocument
                    ? FindStartingSegment(segments, document.StartOffset)
                    : 0;
                firstDocument = false;
                SetCurrentChapter(document, segments, startIndex);

                var documentChanged = false;
                var nextChapterPrefetchStarted = false;
                for (var index = startIndex; index < segments.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = _documentAccessor();
                    if (current is null)
                        throw new InvalidOperationException("阅读器章节已关闭。");
                    if (!string.Equals(current.Key, document.Key, StringComparison.Ordinal))
                    {
                        documentChanged = true;
                        break;
                    }

                    SetCurrentSegment(index, segments.Count);
                    var segment = segments[index];
                    var options = settings.ToOptions();
                    var item = new TtsQueueItem(
                        document.BookKey ?? document.Key,
                        document.ChapterKey ?? document.Key,
                        segment,
                        options);

                    SetState(
                        TtsPlaybackState.Generating,
                        null,
                        index + 1,
                        segments.Count);
                    var audioTask = _queue!.GetOrCreateAsync(
                        item,
                        cancellationToken);
                    _queue.Prefetch(
                        segments
                            .Skip(index + 1)
                            .Take(settings.PrefetchCount)
                            .Select(next => new TtsQueueItem(
                                document.BookKey ?? document.Key,
                                document.ChapterKey ?? document.Key,
                                next,
                                options)),
                        cancellationToken);

                    // Start the next-chapter request after the current item
                    // and its local look-ahead have entered the queue. This
                    // preserves first-sentence latency while allowing the
                    // next chapter to be ready when the transition arrives.
                    if (!nextChapterPrefetchStarted
                        && settings.PrefetchCount > 0
                        && _prefetchDocumentAccessor is not null)
                    {
                        nextChapterPrefetchStarted = true;
                        _ = ObserveNextChapterPrefetchAsync(
                            document,
                            settings,
                            cancellationToken);
                    }

                    var result = await audioTask.ConfigureAwait(false);
                    if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.AudioPath))
                    {
                        throw new InvalidOperationException(
                            result.ErrorMessage ?? "TTS 没有生成音频。");
                    }

                    if (TryTakeRequestedSegment(out var requestedIndex))
                    {
                        index = requestedIndex - 1;
                        continue;
                    }

                    current = _documentAccessor();
                    if (current is null)
                        throw new InvalidOperationException("阅读器章节已关闭。");
                    if (!string.Equals(current.Key, document.Key, StringComparison.Ordinal))
                    {
                        documentChanged = true;
                        break;
                    }

                    var highlight = current.MapHighlight?.Invoke(
                        segment.Start,
                        segment.Length)
                        ?? (segment.Start, segment.Length);
                    await current.Highlight(
                            highlight.Start,
                            highlight.Length)
                        .ConfigureAwait(false);

                    SetState(
                        TtsPlaybackState.Playing,
                        null,
                        index + 1,
                        segments.Count);
                    try
                    {
                        await _player!.PlayAsync(
                                result.AudioPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested
                            && HasPendingSegmentRequest())
                    {
                        // SkipSegmentAsync intentionally stops only the
                        // current player, leaving the service session alive.
                    }

                    if (TryTakeRequestedSegment(out requestedIndex))
                    {
                        index = requestedIndex - 1;
                    }
                }

                if (documentChanged) continue;
                if (!settings.AutoAdvance || _advanceChapterAsync is null) break;

                SetState(
                    TtsPlaybackState.AdvancingChapter,
                    null,
                    segments.Count,
                    segments.Count);
                if (!await _advanceChapterAsync(cancellationToken)
                        .ConfigureAwait(false))
                {
                    break;
                }
            }

            SetState(TtsPlaybackState.Stopped, null, 0, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(TtsPlaybackState.Stopped, null, 0, 0);
        }
        catch (Exception exception)
        {
            try { _player?.Stop(); }
            catch { }
            SetState(TtsPlaybackState.Error, exception.Message, 0, 0);
        }
        finally
        {
            ClearCurrentHighlight();
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _loopTask = null;
                    _currentSegments = null;
                    _currentDocumentKey = null;
                    _currentSegmentIndex = -1;
                    _requestedSegmentIndex = -1;
                }
            }
        }
    }

    private async Task ObserveNextChapterPrefetchAsync(
        ReaderTtsDocument currentDocument,
        TtsSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextDocument = await _prefetchDocumentAccessor!(cancellationToken)
                .ConfigureAwait(false);
            if (nextDocument is null
                || string.Equals(
                    nextDocument.Key,
                    currentDocument.Key,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(nextDocument.Text))
            {
                return;
            }

            var nextSegments = TtsTextSegmenter.SplitSentencesAtPageBreaks(
                nextDocument.Text,
                nextDocument.PageBreakOffsets,
                settings.MaxCharactersPerRequest);
            var options = settings.ToOptions();
            _queue?.Prefetch(
                nextSegments
                    .Take(settings.PrefetchCount)
                    .Select(segment => new TtsQueueItem(
                        nextDocument.BookKey ?? nextDocument.Key,
                        nextDocument.ChapterKey ?? nextDocument.Key,
                        segment,
                        options)),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Cross-chapter prefetch is an optimization. Navigation and the
            // normal current-chapter request remain authoritative if it fails.
        }
    }

    private void SetCurrentChapter(
        ReaderTtsDocument document,
        IReadOnlyList<TtsTextSegment> segments,
        int startIndex)
    {
        lock (_gate)
        {
            _currentDocumentKey = document.Key;
            _currentSegments = segments;
            _currentSegmentIndex = startIndex;
            _segmentIndex = Math.Max(0, startIndex);
            _segmentCount = segments.Count;
        }
    }

    private void SetCurrentSegment(int index, int count)
    {
        lock (_gate)
        {
            _currentSegmentIndex = index;
            _segmentIndex = index + 1;
            _segmentCount = count;
        }
    }

    private bool HasPendingSegmentRequest()
    {
        lock (_gate) return _requestedSegmentIndex >= 0;
    }

    private bool TryTakeRequestedSegment(out int index)
    {
        lock (_gate)
        {
            index = _requestedSegmentIndex;
            _requestedSegmentIndex = -1;
            return index >= 0;
        }
    }

    private static int FindStartingSegment(
        IReadOnlyList<TtsTextSegment> segments,
        int startOffset)
    {
        startOffset = Math.Max(0, startOffset);
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].End > startOffset) return index;
        }

        return Math.Max(0, segments.Count - 1);
    }

    private void SetState(
        TtsPlaybackState state,
        string? message,
        int segmentIndex,
        int segmentCount)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _state = state;
            _message = message;
            _segmentIndex = segmentIndex;
            _segmentCount = segmentCount;
        }

        try
        {
            StateChanged?.Invoke(
                this,
                new TtsStateChangedEventArgs(
                    state,
                    message,
                    segmentIndex,
                    segmentCount));
        }
        catch
        {
            // A UI observer must never terminate the audio loop.
        }
    }

    private void SetEnvironmentStatus(string? message, bool inProgress)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _environmentMessage = message;
            _environmentSetupInProgress = inProgress;
        }

        try { EnvironmentChanged?.Invoke(this, EventArgs.Empty); }
        catch
        {
            // Environment diagnostics must never terminate startup or playback.
        }
    }

    private void ClearCurrentHighlight()
    {
        try { _documentAccessor()?.ClearHighlight(); }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _player?.Stop(); }
        catch { }
        _queue?.Dispose();
        _player?.Dispose();
        _engine?.Dispose();
        _lifecycle.Dispose();
        _previewGate.Dispose();
    }
}
