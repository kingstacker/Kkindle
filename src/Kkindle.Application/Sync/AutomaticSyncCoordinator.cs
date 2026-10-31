using Kkindle.Core;

namespace Kkindle.ApplicationServices;

/// <summary>Cross-platform decisions for automatic and lifecycle sync.</summary>
public static class AutomaticSyncCoordinator
{
    public static bool IsReady(S3SyncSettings settings, bool initialized, bool networkEnabled,
        bool conflictingOperation, bool deletionConfirmationPending, bool cancelledByUser) =>
        initialized && networkEnabled && !conflictingOperation && !deletionConfirmationPending
        && !cancelledByUser && settings.Enabled && settings.AutomaticSyncEnabled && settings.IsConfigured;

    public static bool IsDue(bool hasPendingChanges, DateTimeOffset? lastSync,
        DateTimeOffset now, int intervalMinutes) =>
        hasPendingChanges || lastSync is null || now - lastSync.Value >= TimeSpan.FromMinutes(intervalMinutes);

    /// <summary>Allows writes during a checkpoint to trigger up to two more passes.</summary>
    public static async Task RunCheckpointsAsync(Func<CancellationToken, Task<bool>> sync,
        Func<bool> hasPendingChanges, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(hasPendingChanges);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await sync(cancellationToken) || !hasPendingChanges()) break;
        }
    }
}
