using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class AutomaticSyncCoordinatorTests
{
    [Fact]
    public void PendingChangesOverrideIntervalButNotReadiness()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(AutomaticSyncCoordinator.IsDue(false, now, now, 30));
        Assert.True(AutomaticSyncCoordinator.IsDue(true, now, now, 30));
        Assert.True(AutomaticSyncCoordinator.IsDue(false, now.AddMinutes(-30), now, 30));
        Assert.False(AutomaticSyncCoordinator.IsReady(new S3SyncSettings(), true, true, false, false, false));
        var configured = new S3SyncSettings { Provider = SyncProvider.WebDav, WebDavEndpoint = "https://example.test", Enabled = true };
        Assert.True(AutomaticSyncCoordinator.IsReady(configured, true, true, false, false, false));
        Assert.False(AutomaticSyncCoordinator.IsReady(configured, true, true, true, false, false));
        Assert.False(AutomaticSyncCoordinator.IsReady(configured, true, true, false, true, false));
        Assert.False(AutomaticSyncCoordinator.IsReady(configured, true, true, false, false, true));
        Assert.False(AutomaticSyncCoordinator.IsReady(configured, true, false, false, false, false));
    }

    [Fact]
    public async Task CheckpointsContinueForConcurrentWritesButStopAfterThree()
    {
        var runs = 0;
        await AutomaticSyncCoordinator.RunCheckpointsAsync(_ =>
        {
            runs++;
            return Task.FromResult(true);
        }, () => true, CancellationToken.None);
        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task FailedCheckpointDoesNotRetry()
    {
        var runs = 0;
        await AutomaticSyncCoordinator.RunCheckpointsAsync(_ =>
        {
            runs++;
            return Task.FromResult(false);
        }, () => true, CancellationToken.None);
        Assert.Equal(1, runs);
    }
}
