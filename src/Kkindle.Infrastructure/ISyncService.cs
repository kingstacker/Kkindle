using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Portable synchronization operations required by an application shell.</summary>
public interface ISyncService
{
    event EventHandler? RemoteSettingsApplied;
    Task<S3SyncStoredSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(string deviceId, S3SyncSettings settings, CancellationToken cancellationToken = default);
    Task ResetLocalBaselineAsync(string deviceId, CancellationToken cancellationToken = default);
    Task TestConnectionAsync(S3SyncSettings settings, CancellationToken cancellationToken = default);
    Task<S3SyncStatusSummary> GetStatusAsync(string deviceId, S3SyncSettings settings, CancellationToken cancellationToken = default);
    Task<string> EnsureBookFileDownloadedAsync(string deviceId, S3SyncSettings settings, BookFile file,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, BookSyncStatus>> GetBookSyncStatusesAsync(string deviceId, S3SyncSettings settings,
        IReadOnlyCollection<Book> books, CancellationToken cancellationToken = default);
    Task<S3SyncResult> SyncAsync(string deviceId, S3SyncSettings settings, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, S3SyncOptions? options = null);
    Task InitializeDeletionTrackingAsync(CancellationToken cancellationToken = default, string? deviceId = null);
}
