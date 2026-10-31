namespace Kkindle.ApplicationServices;

/// <summary>Tracks local changes and retry backoff independently of UI timers.</summary>
public sealed class LocalSyncChanges
{
    private long _version;
    private long _syncedVersion;
    private int _retryAttempt;

    public bool HasPendingChanges => _version != _syncedVersion;
    public long CaptureVersion() => _version;
    public void MarkChanged() => _version++;
    public void MarkSynced(long version) => _syncedVersion = Math.Max(_syncedVersion, version);
    public void ResetRetry() => _retryAttempt = 0;

    public TimeSpan NextRetryDelay()
    {
        var delay = _retryAttempt switch
        {
            0 => TimeSpan.FromSeconds(15),
            1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(1),
            3 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };
        _retryAttempt = Math.Min(_retryAttempt + 1, 4);
        return delay;
    }
}
