using AVFoundation;
using Foundation;
using Kkindle.Core;

namespace Kkindle.iOSHead;

/// <summary>
/// Plays one rendered sentence at a time with AVAudioPlayer. The shared
/// read-aloud service owns queueing and prefetch; this adapter only mirrors
/// the Android player's contract (pause, resume, stop, cancellation).
/// </summary>
internal sealed class IOSTtsAudioPlayer : ITtsAudioPlayer
{
    private readonly object _gate = new();
    private AVAudioPlayer? _player;
    private TaskCompletionSource? _finished;
    private NSObject? _interruptionObserver;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public bool IsPaused { get; private set; }

    public async Task PlayAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        Stop();

        // Speech should stay audible even when the ring/silent switch is on,
        // and mixing keeps background music running at a lower level.
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionCategoryOptions.MixWithOthers);
        session.SetActive(true);
        _interruptionObserver ??= AVAudioSession.Notifications.ObserveInterruption(OnInterruption);

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
            if (_player is not { Playing: true } player)
            {
                return;
            }

            player.Pause();
            IsPaused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_player is not { } player || !IsPaused)
            {
                return;
            }

            if (player.Play())
            {
                IsPaused = false;
            }
        }
    }

    public void Stop()
    {
        AVAudioPlayer? player;
        lock (_gate)
        {
            player = _player;
            _finished?.TrySetCanceled();
        }

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
                if (_player is not null) IsPaused = true;
            }
            return;
        }

        if (e.InterruptionType == AVAudioSessionInterruptionType.Ended
            && e.Option == AVAudioSessionInterruptionOptions.ShouldResume)
        {
            AVAudioSession.SharedInstance().SetActive(true);
            Resume();
        }
    }

    public void Dispose()
    {
        Stop();
        _interruptionObserver?.Dispose();
        _interruptionObserver = null;
    }
}
