using Android.Media;
using Kkindle.Core;

namespace Kkindle.AndroidHead;

/// <summary>Plays one rendered sentence at a time; stopping cancels the wait.</summary>
internal sealed class AndroidTtsAudioPlayer : ITtsAudioPlayer
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private TaskCompletionSource? _finished;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public bool IsPaused { get; private set; }

    public async Task PlayAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        Stop();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new MediaPlayer();
        player.Completion += (_, _) => finished.TrySetResult();
        player.Error += (_, e) =>
        {
            e.Handled = true;
            finished.TrySetException(new InvalidOperationException("音频播放失败。"));
        };
        player.SetDataSource(audioPath);
        player.Prepare();
        lock (_gate)
        {
            _player = player;
            _finished = finished;
            IsPaused = false;
        }
        player.Start();

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
            if (_player is not { IsPlaying: true } player) return;
            player.Pause();
            IsPaused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_player is not { } player || !IsPaused) return;
            player.Start();
            IsPaused = false;
        }
    }

    public void Stop()
    {
        MediaPlayer? player;
        lock (_gate)
        {
            player = _player;
            _finished?.TrySetCanceled();
        }
        if (player is not null) Release(player);
    }

    private void Release(MediaPlayer player)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_player, player)) return;
            _player = null;
            _finished = null;
            IsPaused = false;
        }
        try
        {
            player.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Already stopped by completion.
        }
        player.Release();
        player.Dispose();
    }

    public void Dispose() => Stop();
}
