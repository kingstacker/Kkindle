namespace Kkindle.Tests;

public sealed class ReaderSessionAndSyncTests
{
    [Fact]
    public void ReplacingAndEndingSessionCancelsOnlyTheOldSession()
    {
        using var lifetime = new CancellationTokenSource();
        using var session = new ReaderSession();
        Assert.Equal(lifetime.Token, session.GetTokenOr(lifetime.Token));
        var first = session.Begin(lifetime.Token);
        var second = session.Begin(lifetime.Token);
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        session.End();
        Assert.True(second.IsCancellationRequested);
        Assert.Equal(lifetime.Token, session.GetTokenOr(lifetime.Token));
        Assert.False(lifetime.IsCancellationRequested);
    }

    [Fact]
    public void LifetimeCancellationReachesCurrentSession()
    {
        using var lifetime = new CancellationTokenSource();
        using var session = new ReaderSession();
        var token = session.Begin(lifetime.Token);
        lifetime.Cancel();
        Assert.True(token.IsCancellationRequested);
        Assert.True(session.IsCancellationRequested);
    }

    [Fact]
    public void SyncOnlyAcknowledgesChangesCapturedBeforeTheRun()
    {
        var changes = new LocalSyncChanges();
        changes.MarkChanged();
        var atStart = changes.CaptureVersion();
        changes.MarkChanged();
        changes.MarkSynced(atStart);
        Assert.True(changes.HasPendingChanges);
        changes.MarkSynced(changes.CaptureVersion());
        changes.MarkSynced(atStart);
        Assert.False(changes.HasPendingChanges);
    }

    [Fact]
    public void SyncRetryBackoffCapsAndResets()
    {
        var changes = new LocalSyncChanges();
        Assert.Equal(new[] { 15, 30, 60, 120, 300, 300 },
            Enumerable.Range(0, 6).Select(_ => (int)changes.NextRetryDelay().TotalSeconds));
        changes.ResetRetry();
        Assert.Equal(TimeSpan.FromSeconds(15), changes.NextRetryDelay());
    }
}
