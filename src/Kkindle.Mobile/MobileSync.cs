using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

/// <summary>
/// The one path every sync takes on the phone. Manual and automatic runs share
/// a gate, because two passes over the same local database and bucket would race.
/// </summary>
public sealed class MobileSync(ApplicationModules modules)
{
    // Process-wide: the background job builds its own modules for the same files.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private bool _deletionTrackingReady;

    public DateTimeOffset LastRunAt { get; private set; } = DateTimeOffset.MinValue;

    /// <returns>Null when the user, or a background pass, declines a deletion.</returns>
    public async Task<S3SyncResult?> RunAsync(
        S3SyncStoredSettings stored,
        IProgress<string>? progress,
        Func<S3SyncDeletionConfirmationRequiredException, CancellationToken, Task<bool>> confirmDeletion,
        CancellationToken cancellationToken)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return null;
        return await RunAsync(stored, progress, confirmDeletion, cancellationToken, operation);
    }

    /// <summary>
    /// Runs under the caller's lease, acquired before reading settings or initializing data.
    /// The caller must keep it alive until the entire sync task has finished.
    /// </summary>
    public async Task<S3SyncResult?> RunAsync(
        S3SyncStoredSettings stored,
        IProgress<string>? progress,
        Func<S3SyncDeletionConfirmationRequiredException, CancellationToken, Task<bool>> confirmDeletion,
        CancellationToken cancellationToken,
        MobileDataCoordinator.OperationLease operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!operation.IsActive) throw new ObjectDisposedException(nameof(operation));
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var sync = modules.Sync.Service;
            if (!_deletionTrackingReady)
            {
                await sync.InitializeDeletionTrackingAsync(cancellationToken, stored.DeviceId);
                _deletionTrackingReady = true;
            }

            var result = await SyncDeletionCoordinator.RunAsync(
                (options, token) => sync.SyncAsync(stored.DeviceId, stored.Settings, progress, token, options),
                confirmDeletion,
                cancellationToken);
            LastRunAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A silent pass for a platform scheduler while no window exists.</summary>
    public static async Task RunInBackgroundAsync(MobileServices services, CancellationToken cancellationToken)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        var modules = MobileComposition.Create(services);
        // Initialization completes any interrupted import before settings are read.
        await modules.Library.Books.InitializeAsync(cancellationToken);
        await modules.Reader.Data.InitializeAsync(cancellationToken);
        var stored = await modules.Sync.Service.LoadSettingsAsync(cancellationToken);
        if (stored.Settings is not { Enabled: true, AutomaticSyncEnabled: true, IsConfigured: true }) return;
        await new MobileSync(modules).RunAsync(stored, null, (_, _) => Task.FromResult(false), cancellationToken, operation);
    }
}
