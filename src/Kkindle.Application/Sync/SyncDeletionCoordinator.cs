using Kkindle.Core;

namespace Kkindle.ApplicationServices;

/// <summary>Retries a sync only with the fingerprint the user approved.</summary>
public static class SyncDeletionCoordinator
{
    public static async Task<S3SyncResult?> RunAsync(
        Func<S3SyncOptions?, CancellationToken, Task<S3SyncResult>> sync,
        Func<S3SyncDeletionConfirmationRequiredException, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(confirm);
        S3SyncOptions? options = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await sync(options, cancellationToken); }
            catch (S3SyncDeletionConfirmationRequiredException exception)
            {
                if (!await confirm(exception, cancellationToken)) return null;
                options = new S3SyncOptions { ConfirmedDeletionFingerprint = exception.DeletionFingerprint };
            }
        }
    }
}
