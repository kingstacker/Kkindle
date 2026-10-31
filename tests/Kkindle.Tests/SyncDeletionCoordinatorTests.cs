using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class SyncDeletionCoordinatorTests
{
    private static readonly S3SyncResult Result = new(1, 0, 0, 0, 0, false);

    [Fact]
    public async Task ChangedDeletionSetRequiresAnotherConfirmation()
    {
        var attempts = new List<string?>();
        var confirmations = new List<string>();
        var result = await SyncDeletionCoordinator.RunAsync((options, _) =>
        {
            attempts.Add(options?.ConfirmedDeletionFingerprint);
            if (attempts.Count < 3)
                throw new S3SyncDeletionConfirmationRequiredException("confirm", attempts.Count == 1 ? "first" : "changed");
            return Task.FromResult(Result);
        }, (error, _) =>
        {
            confirmations.Add(error.DeletionFingerprint);
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Same(Result, result);
        Assert.Equal(new string?[] { null, "first", "changed" }, attempts);
        Assert.Equal(new[] { "first", "changed" }, confirmations);
    }

    [Fact]
    public async Task DeclinedDeletionDoesNotRetry()
    {
        var attempts = 0;
        var result = await SyncDeletionCoordinator.RunAsync((_, _) =>
        {
            attempts++;
            throw new S3SyncDeletionConfirmationRequiredException("confirm", "fingerprint");
        }, (_, _) => Task.FromResult(false), CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationAfterApprovalDoesNotStartAnotherSync()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SyncDeletionCoordinator.RunAsync((_, _) =>
        {
            attempts++;
            throw new S3SyncDeletionConfirmationRequiredException("confirm", "fingerprint");
        }, (_, _) => { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token));
        Assert.Equal(1, attempts);
    }
}
