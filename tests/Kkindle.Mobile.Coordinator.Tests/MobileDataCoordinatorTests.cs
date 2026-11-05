using Kkindle.Mobile;
using Xunit;

namespace Kkindle.Mobile.Coordinator.Tests;

public class MobileDataCoordinatorTests
{
    // The final assertion permanently closes the process-wide barrier, just like a
    // successful restore in the app. Keep the lifecycle in one isolated test process.
    [Fact]
    public async Task RestoreDrainsOperationsAndReopensOnFailureOrCancellation()
    {
        var first = Assert.IsType<MobileDataCoordinator.OperationLease>(MobileDataCoordinator.TryBeginOperation());
        var second = Assert.IsType<MobileDataCoordinator.OperationLease>(MobileDataCoordinator.TryBeginOperation());
        var pending = MobileDataCoordinator.BeginRestoreAsync();
        Assert.False(pending.IsCompleted);
        Assert.Null(MobileDataCoordinator.TryBeginOperation());
        Assert.Null(await MobileDataCoordinator.BeginRestoreAsync());

        first.Dispose();
        first.Dispose(); // Releasing a lease twice must not let restoration start early.
        Assert.False(pending.IsCompleted);
        second.Dispose();
        using (var restore = Assert.IsType<MobileDataCoordinator.RestoreLease>(await pending.WaitAsync(TimeSpan.FromSeconds(5))))
        {
            Assert.Null(MobileDataCoordinator.TryBeginOperation());
            Assert.False(MobileDataCoordinator.IsRestartRequired);
        }
        using (Assert.IsType<MobileDataCoordinator.OperationLease>(MobileDataCoordinator.TryBeginOperation())) { }

        using var cancellation = new CancellationTokenSource();
        var active = Assert.IsType<MobileDataCoordinator.OperationLease>(MobileDataCoordinator.TryBeginOperation());
        var canceledRestore = MobileDataCoordinator.BeginRestoreAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRestore);
        using (Assert.IsType<MobileDataCoordinator.OperationLease>(MobileDataCoordinator.TryBeginOperation())) { }
        active.Dispose();

        using var successfulRestore = Assert.IsType<MobileDataCoordinator.RestoreLease>(await MobileDataCoordinator.BeginRestoreAsync());
        successfulRestore.Complete();
        successfulRestore.Dispose();
        Assert.True(MobileDataCoordinator.IsRestartRequired);
        Assert.Null(MobileDataCoordinator.TryBeginOperation());
        Assert.Null(await MobileDataCoordinator.BeginRestoreAsync());
    }
}
