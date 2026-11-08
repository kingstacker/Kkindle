using AVFoundation;
using Foundation;
using Kkindle.Core;

namespace Kkindle.iOSHead;

/// <summary>
/// Plays one rendered sentence at a time with AVAudioPlayer. The shared
/// read-aloud service owns queueing and prefetch; this adapter only mirrors
/// the Android player's contract (pause, resume, stop, cancellation).
/// </summary>
internal sealed class IOSTtsAudioPlayer : ITtsAudioPlayer, ITtsPlaybackClock, ITtsLiveSpeechPlayer
{
    private readonly object _gate = new();
    private AVAudioPlayer? _player;
    private TaskCompletionSource? _finished;
    private NSObject? _interruptionObserver;
    private readonly AVSpeechSynthesizer _liveSpeech = new();
    private AVSpeechUtterance? _liveUtterance;
    private TaskCompletionSource? _liveFinished;
    private int _livePosition = -1;
    private bool _resumeAfterInterruption;
    private AVSpeechUtterance? _pendingUtterance;
    private bool _pauseLiveOnStart;

    public IOSTtsAudioPlayer()
    {
        _liveSpeech.WillSpeakRangeOfSpeechString += (_, e) =>
        {
            lock (_gate)
            {
                if (_liveUtterance is null || _liveUtterance.Handle != e.Utterance.Handle || IsPaused) return;
                var range = e.CharacterRange;
                if ((ulong)range.Location <= int.MaxValue && range.Length > 0
                    && (long)range.Location + (long)range.Length <= _liveUtterance.SpeechString.Length
                    && (int)range.Location >= _livePosition)
                    _livePosition = (int)range.Location;
            }
        };
        _liveSpeech.DidFinishSpeechUtterance += (_, e) => CompleteLive(e.Utterance, false);
        _liveSpeech.DidCancelSpeechUtterance += (_, e) => CompleteLive(e.Utterance, true);
    }

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public bool IsPaused { get; private set; }

    public bool TryGetPosition(out TimeSpan position)
    {
        lock (_gate)
        {
            if (_player is { Playing: true } player && !IsPaused)
            {
                position = TimeSpan.FromSeconds(player.CurrentTime);
                return true;
            }
        }
        position = default;
        return false;
    }

    public bool TryGetSpeechPosition(out int offset)
    {
        lock (_gate)
        {
            offset = _livePosition;
            return _liveUtterance is not null && !IsPaused && offset >= 0;
        }
    }

    public async Task SpeakAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        using var utterance = new AVSpeechUtterance(text)
        {
            Voice = IOSTtsEngine.SelectVoice(options.Voice),
            Rate = IOSTtsEngine.MapRate(options.RatePercent),
            PitchMultiplier = Math.Clamp(1f + options.PitchHz / 100f, 0.5f, 2f),
            Volume = Math.Clamp((options.VolumePercent + 100) / 100f, 0f, 1f),
        };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pendingUtterance = utterance;
            _pauseLiveOnStart = false;
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _liveSpeech.BeginInvokeOnMainThread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PrepareAudioSession();
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_pendingUtterance, utterance)) throw new OperationCanceledException();
                    _pendingUtterance = null;
                    _liveUtterance = utterance;
                    _liveFinished = finished;
                    _livePosition = -1;
                    try
                    {
                        _liveSpeech.SpeakUtterance(utterance);
                        if (_pauseLiveOnStart) _liveSpeech.PauseSpeaking(AVSpeechBoundary.Immediate);
                    }
                    catch
                    {
                        _liveUtterance = null;
                        _liveFinished = null;
                        throw;
                    }
                }
                started.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                    if (ReferenceEquals(_pendingUtterance, utterance)) _pendingUtterance = null;
                started.TrySetCanceled();
            }
            catch (Exception exception)
            {
                lock (_gate)
                    if (ReferenceEquals(_pendingUtterance, utterance)) _pendingUtterance = null;
                started.TrySetException(exception);
            }
        });
        await started.Task.ConfigureAwait(false);
        using var registration = cancellationToken.Register(() => StopLive(utterance));
        try { await finished.Task.ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_liveUtterance, utterance))
                {
                    _liveUtterance = null;
                    _liveFinished = null;
                    _livePosition = -1;
                    IsPaused = false;
                }
            }
        }
    }

    private void CompleteLive(AVSpeechUtterance utterance, bool cancelled)
    {
        lock (_gate)
        {
            if (_liveUtterance is null || _liveUtterance.Handle != utterance.Handle) return;
            if (cancelled) _liveFinished?.TrySetCanceled();
            else _liveFinished?.TrySetResult();
        }
    }

    private void StopLive(AVSpeechUtterance utterance)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_liveUtterance, utterance)) return;
            _liveUtterance = null;
            _livePosition = -1;
            _liveFinished?.TrySetCanceled();
            _liveFinished = null;
            IsPaused = false;
            _liveSpeech.StopSpeaking(AVSpeechBoundary.Immediate);
        }
    }

    private void PrepareAudioSession()
    {
        // Keep speech audible with the silent switch on while mixing other audio.
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionCategoryOptions.MixWithOthers);
        session.SetActive(true);
        _interruptionObserver ??= AVAudioSession.Notifications.ObserveInterruption(OnInterruption);
    }

    public async Task PlayAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        Stop();
        PrepareAudioSession();

        if (!File.Exists(audioPath))
        {
            throw new InvalidOperationException("朗读音频文件不存在。");
        }

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(audioPath))
            ?? throw new InvalidOperationException("无法打开朗读音频。");
        player.FinishedPlaying += (_, _) => finished.TrySetResult();
        player.PrepareToPlay();

        lock (_gate)
        {
            _player = player;
            _finished = finished;
            IsPaused = false;
        }

        if (!player.Play())
        {
            Release(player);
            throw new InvalidOperationException("音频播放失败。");
        }

        using var registration = cancellationToken.Register(Stop);
        try
        {
            await finished.Task;
        }
        finally
        {
            Release(player);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_pendingUtterance is not null)
            {
                _pauseLiveOnStart = true;
                IsPaused = true;
                return;
            }
            if (_liveUtterance is not null)
            {
                if (_liveSpeech.PauseSpeaking(AVSpeechBoundary.Immediate)) IsPaused = true;
                return;
            }
            if (_player is not { Playing: true } player) return;
            player.Pause();
            IsPaused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsPaused) return;
            if (_pendingUtterance is not null)
            {
                _pauseLiveOnStart = false;
                IsPaused = false;
                return;
            }
            if (_liveUtterance is not null)
            {
                if (_liveSpeech.ContinueSpeaking()) IsPaused = false;
                return;
            }
            if (_player is not { } player) return;
            if (player.Play())
            {
                IsPaused = false;
            }
        }
    }

    public void Stop()
    {
        AVAudioPlayer? player;
        AVSpeechUtterance? utterance;
        lock (_gate)
        {
            player = _player;
            utterance = _liveUtterance;
            if (_pendingUtterance is not null) IsPaused = false;
            _pendingUtterance = null;
            _pauseLiveOnStart = false;
            _finished?.TrySetCanceled();
        }

        if (utterance is not null) StopLive(utterance);
        if (player is not null)
        {
            Release(player);
        }
    }

    private void Release(AVAudioPlayer player)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_player, player))
            {
                return;
            }

            _player = null;
            _finished = null;
            IsPaused = false;
        }

        player.Stop();
        player.Dispose();
    }

    /// <summary>
    /// A call or alarm pauses the player without finishing it. Mark it paused so
    /// the speech bar's resume works, and continue when the system says to.
    /// </summary>
    private void OnInterruption(object? sender, AVAudioSessionInterruptionEventArgs e)
    {
        if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            lock (_gate)
            {
                _resumeAfterInterruption = !IsPaused && (_player is not null || _liveUtterance is not null);
                if (_resumeAfterInterruption)
                {
                    if (_liveUtterance is not null) _liveSpeech.PauseSpeaking(AVSpeechBoundary.Immediate);
                    IsPaused = true;
                }
            }
            return;
        }

        if (e.InterruptionType == AVAudioSessionInterruptionType.Ended)
        {
            bool resume;
            lock (_gate)
            {
                resume = _resumeAfterInterruption && e.Option == AVAudioSessionInterruptionOptions.ShouldResume;
                _resumeAfterInterruption = false;
            }
            if (resume)
            {
                AVAudioSession.SharedInstance().SetActive(true);
                Resume();
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _interruptionObserver?.Dispose();
        _interruptionObserver = null;
        _liveSpeech.Dispose();
    }
}
