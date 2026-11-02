namespace Kkindle.Mobile;

/// <summary>Counts short foreground reading intervals and excludes background time.</summary>
internal sealed class ReadingActivityClock(DateTimeOffset startedAt)
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(120);
    private DateTimeOffset _last = startedAt;
    private bool _paused;

    public long CountUntil(DateTimeOffset now)
    {
        if (_paused) return 0;
        var elapsed = now - _last;
        if (elapsed.TotalSeconds < 1) return 0;
        _last = now;
        return elapsed <= IdleLimit ? (long)elapsed.TotalSeconds : 0;
    }

    public long Pause(DateTimeOffset now)
    {
        var seconds = CountUntil(now);
        _paused = true;
        return seconds;
    }

    public void Resume(DateTimeOffset now)
    {
        if (!_paused) return;
        _last = now;
        _paused = false;
    }
}
