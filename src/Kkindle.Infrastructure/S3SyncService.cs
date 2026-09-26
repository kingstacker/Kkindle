using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>
/// S3/WebDAV synchronisation for Kkindle's logical data.
///
/// The live SQLite database is never placed in the bucket. Each device owns a
/// compressed snapshot and book bytes are stored as content-addressed objects:
/// <c>objects/{sha256}</c>. Snapshots are merged by stable IDs/content hashes,
/// and timestamped tombstones carry deletions between devices. This follows
/// the important part of SiYuan/dejavu's design while keeping Kkindle's
/// existing SQLite schema and reader services intact.
/// </summary>
public sealed partial class S3SyncService
{
    private const int SnapshotVersion = 3;
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private const int EncryptionSaltBytes = 16;
    private const int EncryptionNonceBytes = 12;
    private const int EncryptionTagBytes = 16;
    private const int EncryptionKeyBytes = 32;
    private const int EncryptionIterations = 120_000;
    private const int BlobEncryptionChunkBytes = 1024 * 1024;
    private const double LargeDeletionRatio = 0.50;
    private const int LargeDeletionMinimumEntities = 10;
    private static readonly TimeSpan RetiredDeviceSnapshotRetention = TimeSpan.FromDays(365);
    private static readonly TimeSpan UnreferencedBlobRetention = TimeSpan.FromDays(30);
    private static readonly byte[] EncryptionMagic = Encoding.ASCII.GetBytes("KKINDLE-SYNC1");
    private static readonly byte[] StreamingEncryptionMagic = Encoding.ASCII.GetBytes("KKINDLE-SYNC2");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly AppPaths _paths;
    private readonly S3SyncSettingsStore _settingsStore;
    private readonly Func<S3SyncSettings, ISyncObjectStore> _clientFactory;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedFileHash> _fileHashCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _derivedEncryptionKeys =
        new(StringComparer.Ordinal);
    private EncryptionSession? _encryptionSession;

    private sealed record CachedFileHash(long Length, DateTime LastWriteTimeUtc, string Sha256);

    private sealed class EncryptionSession
    {
        public EncryptionSession(string passphrase, byte[] salt, byte[] key)
        {
            Passphrase = passphrase;
            Salt = salt;
            Key = key;
        }

        public string Passphrase { get; }
        public byte[] Salt { get; }
        public byte[] Key { get; }
    }

    public S3SyncService(AppPaths paths, ISecretProtector protector)
        : this(paths, protector, CreateObjectStore)
    {
    }

    internal S3SyncService(AppPaths paths, ISecretProtector protector, Func<S3SyncSettings, IAmazonS3> clientFactory)
        : this(paths, protector, settings => new S3SyncObjectStore(settings, clientFactory(settings)))
    {
    }

    internal S3SyncService(AppPaths paths, ISecretProtector protector, Func<S3SyncSettings, ISyncObjectStore> clientFactory)
    {
        _paths = paths;
        _clientFactory = clientFactory;
        _settingsStore = new S3SyncSettingsStore(paths, protector);
        _appSettingsStore = new AppSettingsStore(paths);
        _aiSettingsStore = new AiSettingsStore(paths, protector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, protector);
    }

    public Task<S3SyncStoredSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        _settingsStore.LoadAsync(cancellationToken);

    // A later upload can fail after settings have already been committed.
    public event EventHandler? RemoteSettingsApplied;

    public async Task SaveSettingsAsync(
        string deviceId,
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var current = await _settingsStore.LoadAsync(cancellationToken);
            if (current.Settings.EncryptionKey != normalized.EncryptionKey
                && SameRemoteDirectory(current.Settings, normalized))
            {
                var state = await LoadStateAsync(NormalizeDeviceId(deviceId), BuildStorageIdentity(current.Settings), cancellationToken);
                if (state.LastUploadedSnapshot is not null)
                    throw new InvalidOperationException(UiText.Get("当前同步目录已使用原加密配置。更换密钥或启停加密时，请改用新的同步子目录，并在其他设备上配置相同的前缀和密钥。"));
            }
            await _settingsStore.SaveAsync(deviceId, normalized, cancellationToken);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Clears the local change-detection baseline after a portable backup has
    /// replaced the database and library. The next sync treats the imported
    /// package as the current local state instead of turning package omissions
    /// into deletion tombstones.
    /// </summary>
    public async Task ResetLocalBaselineAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        deviceId = NormalizeDeviceId(deviceId);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
            await ResetLocalBaselineCoreAsync(deviceId, cancellationToken);
        }
        finally
        {
            _encryptionSession = null;
            _derivedEncryptionKeys.Clear();
            _syncGate.Release();
        }
    }

    public async Task TestConnectionAsync(
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        ThrowIfInvalid(normalized);

        using var client = _clientFactory(normalized);
        var keys = await ListSnapshotKeysAsync(client, normalized, cancellationToken);
        await DownloadRemoteSnapshotsAsync(client, normalized, keys, string.Empty, false, null, cancellationToken);
        await client.TestWriteAsync(normalized.Prefix, cancellationToken);
    }

    /// <summary>
    /// Downloads one book file on demand. A normal sync only writes the file's
    /// metadata to SQLite; opening the book is the explicit user action that
    /// fetches the content-addressed object.
    /// </summary>
    public async Task<string> EnsureBookFileDownloadedAsync(
        string deviceId,
        S3SyncSettings settings,
        BookFile file,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var normalized = S3SyncSettings.Normalize(settings);
        ThrowIfInvalid(normalized);
        if (!IsSha256(file.Sha256))
            throw new InvalidDataException("同步文件缺少有效的 SHA-256 校验值。");

        var targetPath = ResolveDataPath(file.RelativePath);
        if (targetPath is null)
            throw new InvalidDataException("同步文件目标路径无效。");

        deviceId = NormalizeDeviceId(deviceId);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            _derivedEncryptionKeys.Clear();
            _encryptionSession = normalized.EncryptionKey.Length == 0
                ? null
                : CreateEncryptionSession(normalized.EncryptionKey);
            _paths.EnsureDirectories();

            var storageIdentity = BuildStorageIdentity(normalized);
            var state = await LoadStateAsync(deviceId, storageIdentity, cancellationToken);
            using var client = _clientFactory(normalized);
            progress?.Report(UiText.Get("正在下载《{0}》…", Path.GetFileName(file.RelativePath)));
            await EnsureLocalBlobAsync(
                client,
                normalized,
                file.Sha256,
                targetPath,
                progress,
                cancellationToken);

            state.DeviceId = deviceId;
            state.StorageIdentity = storageIdentity;
            state.RemoteOnlyFileHashes = (state.RemoteOnlyFileHashes ?? [])
                .Where(hash => !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await SaveStateAsync(state, cancellationToken);
            return targetPath;
        }
        finally
        {
            _encryptionSession = null;
            _derivedEncryptionKeys.Clear();
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Returns the four-state badge value for the supplied local library view.
    /// The comparison uses only the local metadata baseline and file existence;
    /// it never contacts the remote object store.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BookSyncStatus>> GetBookSyncStatusesAsync(
        string deviceId,
        S3SyncSettings settings,
        IReadOnlyCollection<Book> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        var statuses = books.ToDictionary(book => book.Id, _ => BookSyncStatus.NotSynced);
        var normalized = S3SyncSettings.Normalize(settings);
        if (!normalized.IsConfigured) return statuses;

        // The local state file is replaced during SyncAsync. Serialize this
        // read with the same gate, otherwise a badge refresh can observe the
        // transient/empty state and incorrectly mark every book NotSynced.
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(
                NormalizeDeviceId(deviceId),
                BuildStorageIdentity(normalized),
                cancellationToken);
            var baseline = state.LastUploadedSnapshot;
            if (baseline is null) return statuses;

            var remoteBookIds = (state.RemoteBookIds ?? [])
                .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToHashSet();
            var remoteOnlyFileHashes = (state.RemoteOnlyFileHashes ?? [])
                .Where(IsSha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var book in books)
            {
                var baselineBook = baseline.Books.FirstOrDefault(item => item.Id == book.Id);
                var baselineFiles = baseline.Files.Where(item => item.BookId == book.Id).ToArray();
                if (baselineBook is null
                    || book.UpdatedAt != baselineBook.UpdatedAt
                    || !BookFilesMatchBaseline(book.Files, baselineFiles))
                {
                    statuses[book.Id] = BookSyncStatus.NotSynced;
                    continue;
                }

                var hasRemoteOnlyFile = book.Files.Any(file => remoteOnlyFileHashes.Contains(file.Sha256));
                var hasLocalFile = book.Files.Any(file =>
                {
                    if (remoteOnlyFileHashes.Contains(file.Sha256)) return false;
                    var path = ResolveDataPath(file.RelativePath);
                    return path is not null && File.Exists(path);
                });
                if (!remoteBookIds.Contains(book.Id) && !hasRemoteOnlyFile && hasLocalFile)
                {
                    statuses[book.Id] = BookSyncStatus.Synced;
                    continue;
                }

                statuses[book.Id] = hasLocalFile
                    ? BookSyncStatus.Downloaded
                    : BookSyncStatus.NotDownloaded;
            }

            return statuses;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static bool BookFilesMatchBaseline(
        IEnumerable<BookFile> currentFiles,
        IEnumerable<S3SyncBookFile> baselineFiles)
    {
        var current = currentFiles.ToArray();
        var baseline = baselineFiles.ToArray();
        return current.Length == baseline.Length
            && current.All(file => baseline.Any(remote =>
                remote.Id == file.Id
                && string.Equals(remote.Format, file.Format, StringComparison.OrdinalIgnoreCase)
                && remote.Size == file.Size
                && string.Equals(remote.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<S3SyncResult> SyncAsync(
        string deviceId,
        S3SyncSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        S3SyncOptions? options = null)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        ThrowIfInvalid(normalized);
        deviceId = NormalizeDeviceId(deviceId);

        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            _derivedEncryptionKeys.Clear();
            _encryptionSession = normalized.EncryptionKey.Length == 0
                ? null
                : CreateEncryptionSession(normalized.EncryptionKey);
            _paths.EnsureDirectories();
            await InitializeDeletionTrackingAsync(cancellationToken, deviceId);
            if (File.Exists(GetPendingBaselineResetPath()))
            {
                using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
                await ResetLocalBaselineCoreAsync(deviceId, cancellationToken);
            }
            var storageIdentity = BuildStorageIdentity(normalized);
            var state = await LoadStateAsync(deviceId, storageIdentity, cancellationToken);
            var local = await CaptureSnapshotAsync(deviceId, state.Tombstones, cancellationToken);
            local.TombstonesPrunedBefore = state.LastUploadedSnapshot?.TombstonesPrunedBefore;
            PreserveSettingsTimestampsForUnchangedValues(
                local.Settings,
                state.LastUploadedSnapshot?.Settings);
            var recordedDeletionTimes = local.LocalDeletionTimes;
            var detectedDeletions = DetectDeletedEntitiesWithRecordedTimes(
                state.LastUploadedSnapshot,
                local,
                recordedDeletionTimes);
            EnsureDeletionVolumeIsSafe(state.LastUploadedSnapshot, detectedDeletions, options?.ConfirmedDeletionFingerprint);
            local.Tombstones = MergeTombstones(
                local.Tombstones,
                detectedDeletions.Concat(GetRecordedTombstones(recordedDeletionTimes, local, state.LastUploadedSnapshot)));

            progress?.Report(UiText.Get("正在连接 {0}…", normalized.ProviderName));
            using var client = _clientFactory(normalized);
            var snapshotKeys = await ListSnapshotKeysAsync(client, normalized, cancellationToken);

            // Validate remote encryption before uploading any local bytes.
            progress?.Report(UiText.Get("正在读取其他设备的同步快照…"));
            var remoteSnapshots = await DownloadRemoteSnapshotsAsync(
                client,
                normalized,
                snapshotKeys,
                deviceId,
                state.LastUploadedSnapshot is not null,
                progress,
                cancellationToken);

            var tombstoneWatermark = Max(
                local.TombstonesPrunedBefore ?? DateTimeOffset.MinValue,
                remoteSnapshots.Select(snapshot => snapshot.TombstonesPrunedBefore ?? DateTimeOffset.MinValue)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max());
            if (tombstoneWatermark > DateTimeOffset.MinValue)
            {
                local.TombstonesPrunedBefore = tombstoneWatermark;
                local.Tombstones = local.Tombstones
                    .Where(tombstone => tombstone.DeletedAt > tombstoneWatermark)
                    .ToList();
                local.LocalDeletionTimes = local.LocalDeletionTimes
                    .Where(entry => entry.Value > tombstoneWatermark)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                recordedDeletionTimes = local.LocalDeletionTimes;
                state.Tombstones = local.Tombstones;
                detectedDeletions = detectedDeletions
                    .Where(tombstone => tombstone.DeletedAt > tombstoneWatermark)
                    .ToList();
                foreach (var remote in remoteSnapshots)
                {
                    remote.TombstonesPrunedBefore = Max(
                        remote.TombstonesPrunedBefore ?? DateTimeOffset.MinValue,
                        tombstoneWatermark);
                    remote.Tombstones = (remote.Tombstones ?? [])
                        .Where(tombstone => tombstone.DeletedAt > tombstoneWatermark)
                        .ToList();
                }
            }

            local.Tombstones = MergeTombstones(
                local.Tombstones,
                remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones ?? []));
            var restoredEntityKeys = await ResolveLocalRestorationsAsync(
                local,
                state.LastUploadedSnapshot,
                remoteSnapshots,
                cancellationToken);
            if (restoredEntityKeys.Count > 0)
            {
                local.Tombstones = local.Tombstones
                    .Where(tombstone => !restoredEntityKeys.Contains(VersionKey(tombstone.EntityType, tombstone.Key)))
                    .ToList();
                local.LocalDeletionTimes = local.LocalDeletionTimes
                    .Where(entry => !restoredEntityKeys.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                recordedDeletionTimes = local.LocalDeletionTimes;
                detectedDeletions = detectedDeletions
                    .Where(tombstone => !restoredEntityKeys.Contains(VersionKey(tombstone.EntityType, tombstone.Key)))
                    .ToList();
                state.Tombstones = local.Tombstones;
                foreach (var remote in remoteSnapshots)
                    remote.Tombstones = (remote.Tombstones ?? [])
                        .Where(tombstone => !restoredEntityKeys.Contains(VersionKey(tombstone.EntityType, tombstone.Key)))
                        .ToList();
            }
            LiftRemoteDeletionVersionsOverUnchangedLocalRows(
                local,
                state.LastUploadedSnapshot,
                remoteSnapshots);
            LiftLocalDeletionVersionsOverObservedRemoteRows(
                local,
                remoteSnapshots,
                detectedDeletions,
                tombstoneWatermark);

            progress?.Report(UiText.Get("正在合并书籍和阅读数据…"));
            var databaseResult = await ApplyRemoteSnapshotsAsync(
                client,
                normalized,
                local,
                remoteSnapshots,
                progress,
                cancellationToken);

            // The database transaction commits before the remaining network
            // work. Persist the remote provenance immediately so a later
            // object/snapshot upload failure cannot make an incoming book look
            // like a locally-created, already-synced book on the next retry.
            var remoteOnlyFileHashes = (state.RemoteOnlyFileHashes ?? [])
                .Where(IsSha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            remoteOnlyFileHashes.UnionWith(databaseResult.RemoteOnlyFileHashes);
            state.RemoteBookIds = (state.RemoteBookIds ?? [])
                .Concat(databaseResult.RemoteBookIds.Select(id => id.ToString("N")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (databaseResult.RemoteBookIds.Count > 0 || databaseResult.RemoteOnlyFileHashes.Count > 0)
                await SaveStateAsync(state, cancellationToken);

            progress?.Report(UiText.Get("正在合并同步设置…"));
            var settingsChanged = await ApplyRemoteSettingsAsync(
                local.Settings,
                remoteSnapshots,
                cancellationToken);

            // Capture again after the merge. This makes this device's snapshot
            // a complete converged view, so a third device can catch up from it
            // without having to contact every previous device forever.
            var finalSnapshot = await CaptureSnapshotAsync(deviceId, local.Tombstones, cancellationToken);
            finalSnapshot.TombstonesPrunedBefore = local.TombstonesPrunedBefore;
            var finalDeletions = finalSnapshot.LocalDeletionTimes
                .Where(entry => finalSnapshot.TombstonesPrunedBefore is not { } watermark
                    || entry.Value > watermark)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var recordedTombstones = GetRecordedTombstones(finalDeletions, finalSnapshot, state.LastUploadedSnapshot);
            EnsureDeletionVolumeIsSafe(state.LastUploadedSnapshot,
                MergeTombstones(FilterReadingTombstones(detectedDeletions, finalSnapshot.ReadingDataReset), recordedTombstones),
                options?.ConfirmedDeletionFingerprint);
            finalSnapshot.Tombstones = MergeTombstones(finalSnapshot.Tombstones, recordedTombstones);

            // Publish exactly this captured view after all its objects exist.
            // Writes after capture remain outside the saved local baseline.
            progress?.Report(UiText.Get("正在上传本地书籍文件…"));
            var uploadWarning = await UploadLocalObjectsAsync(
                client, normalized, finalSnapshot, remoteOnlyFileHashes, progress, cancellationToken);

            progress?.Report(UiText.Get("正在保存同步快照…"));
            await UploadSnapshotAsync(client, normalized, finalSnapshot, cancellationToken);

            state.DeviceId = deviceId;
            state.StorageIdentity = storageIdentity;
            state.LastSyncAt = DateTimeOffset.UtcNow;
            state.LastUploadedSnapshot = finalSnapshot;
            state.Tombstones = finalSnapshot.Tombstones;
            var finalBookIds = finalSnapshot.Books.Select(book => book.Id).ToHashSet();
            state.RemoteBookIds = state.RemoteBookIds
                .Where(id => Guid.TryParse(id, out var parsed) && finalBookIds.Contains(parsed))
                .ToList();
            state.RemoteOnlyFileHashes = remoteOnlyFileHashes
                .Where(hash => finalSnapshot.Files.Any(file =>
                        string.Equals(file.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                    && !finalSnapshot.Files.Any(file =>
                        string.Equals(file.Sha256, hash, StringComparison.OrdinalIgnoreCase)
                        && finalSnapshot.LocalFilePaths.TryGetValue(file.Id, out var relativePath)
                        && ResolveDataPath(relativePath) is { } path
                        && File.Exists(path)))
                .ToList();
            await SaveStateAsync(state, cancellationToken);

            string? cleanupWarning = null;
            try
            {
                cleanupWarning = await CleanupRemoteStorageAsync(
                    client,
                    normalized,
                    deviceId,
                    finalSnapshot,
                    state,
                    remoteSnapshots,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The converged snapshot and local sync state are already
                // durable. A cancelled cleanup can resume on the next sync.
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupWarning = UiText.Get("远端清理未完成：{0}", UiText.Localize(exception.Message));
            }
            state.LastUploadedSnapshot = finalSnapshot;
            state.Tombstones = finalSnapshot.Tombstones;
            await SaveStateAsync(state, cancellationToken);

            var warning = string.Join(
                " ",
                new[] { uploadWarning, databaseResult.Warning, cleanupWarning }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
            return new S3SyncResult(
                remoteSnapshots
                    .Select(snapshot => NormalizeKnownDeviceId(snapshot.DeviceId))
                    .Where(remoteDeviceId => !string.Equals(remoteDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                databaseResult.BooksAdded,
                databaseResult.FilesDownloaded,
                databaseResult.AnnotationsApplied,
                settingsChanged ? 1 : 0,
                databaseResult.Changed || settingsChanged,
                warning.Length == 0 ? null : warning)
            {
                IsPartial = uploadWarning is not null || databaseResult.IsPartial
            };
        }
        finally
        {
            _encryptionSession = null;
            _derivedEncryptionKeys.Clear();
            _syncGate.Release();
        }
    }

    private static void PreserveSettingsTimestampsForUnchangedValues(
        S3SyncSettingsSnapshot? current,
        S3SyncSettingsSnapshot? previous)
    {
        if (current is null || previous is null) return;
        if (AppSyncSettingsEqual(current.App, previous.App))
            current.AppUpdatedAt = previous.AppUpdatedAt;
        if (AiSyncSettingsEqual(current.Ai, previous.Ai))
            current.AiUpdatedAt = previous.AiUpdatedAt;
        if (EmailSyncSettingsEqual(current.KindleEmail, previous.KindleEmail))
            current.KindleEmailUpdatedAt = previous.KindleEmailUpdatedAt;

        current.UpdatedAt = new[]
        {
            current.AppUpdatedAt ?? DateTimeOffset.MinValue,
            current.AiUpdatedAt ?? DateTimeOffset.MinValue,
            current.KindleEmailUpdatedAt ?? DateTimeOffset.MinValue
        }.Max();
    }

    private static void LiftLocalDeletionVersionsOverObservedRemoteRows(
        S3SyncSnapshot local,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        IReadOnlyCollection<S3SyncTombstone> localDeletions,
        DateTimeOffset existingWatermark)
    {
        if (localDeletions.Count == 0 || local.Tombstones.Count == 0) return;
        var localDeletionKeys = localDeletions
            .Select(tombstone => VersionKey(tombstone.EntityType, tombstone.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, version) in GetSnapshotEntityVersions(local))
            remoteVersions[key] = version;
        foreach (var snapshot in remoteSnapshots)
        {
            foreach (var (key, version) in GetSnapshotEntityVersions(snapshot))
                if (!remoteVersions.TryGetValue(key, out var existing) || version > existing)
                    remoteVersions[key] = version;
        }

        foreach (var tombstone in local.Tombstones)
        {
            var key = VersionKey(tombstone.EntityType, tombstone.Key);
            if (localDeletionKeys.Contains(key)
                && remoteVersions.TryGetValue(key, out var remoteVersion)
                && remoteVersion >= tombstone.DeletedAt)
            {
                // Wall clocks on two devices are not a reliable ordering
                // source. The delete was recorded locally after the last local
                // baseline, so make it newer than the stale remote row it is
                // intended to suppress.
                tombstone.DeletedAt = Max(remoteVersion, existingWatermark).AddTicks(1);
            }
        }
    }

    private static void LiftRemoteDeletionVersionsOverUnchangedLocalRows(
        S3SyncSnapshot local,
        S3SyncSnapshot? previousLocal,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots)
    {
        if (previousLocal is null || remoteSnapshots.Count == 0) return;
        var unchangedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddUnchangedKeys(local.Books, previousLocal.Books, item => item.Id.ToString("N"), "book", unchangedKeys);
        AddUnchangedKeys(local.Files, previousLocal.Files, item => item.Id.ToString("N"), "file", unchangedKeys);
        AddUnchangedKeys(local.Collections, previousLocal.Collections, item => item.Id.ToString("N"), "collection", unchangedKeys);
        AddUnchangedKeys(local.CollectionItems, previousLocal.CollectionItems,
            item => CompositeKey(item.CollectionId, item.BookId), "collection-item", unchangedKeys);
        AddUnchangedKeys(local.Annotations, previousLocal.Annotations, item => item.Id.ToString("N"), "annotation", unchangedKeys);
        AddUnchangedKeys(local.BookReflections, previousLocal.BookReflections, item => item.BookId.ToString("N"), "reflection", unchangedKeys);
        AddUnchangedKeys(local.Progress, previousLocal.Progress, item => item.BookFileId.ToString("N"), "progress", unchangedKeys);
        AddUnchangedKeys(local.Bookmarks, previousLocal.Bookmarks, item => item.Id.ToString("N"), "bookmark", unchangedKeys);
        AddUnchangedKeys(local.Layouts, previousLocal.Layouts, item => item.BookFileId.ToString("N"), "layout", unchangedKeys);
        AddUnchangedKeys(local.ReadingStats, previousLocal.ReadingStats, item => item.BookFileId.ToString("N"), "stats", unchangedKeys);
        if (unchangedKeys.Count == 0) return;

        var remoteVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, version) in GetSnapshotEntityVersions(local))
            remoteVersions[key] = version;
        foreach (var snapshot in remoteSnapshots)
        {
            foreach (var (key, version) in GetSnapshotEntityVersions(snapshot))
                if (!remoteVersions.TryGetValue(key, out var existing) || version > existing)
                    remoteVersions[key] = version;
        }

        var tombstones = local.Tombstones.ToDictionary(
            tombstone => VersionKey(tombstone.EntityType, tombstone.Key),
            StringComparer.OrdinalIgnoreCase);
        foreach (var remoteTombstone in remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones ?? []))
        {
            var key = VersionKey(remoteTombstone.EntityType, remoteTombstone.Key);
            if (!unchangedKeys.Contains(key)) continue;
            var observedVersion = remoteVersions.GetValueOrDefault(key, DateTimeOffset.MinValue);
            var deletionVersion = Max(remoteTombstone.DeletedAt, observedVersion).AddTicks(1);
            if (tombstones.TryGetValue(key, out var localTombstone))
            {
                if (deletionVersion > localTombstone.DeletedAt)
                    localTombstone.DeletedAt = deletionVersion;
            }
            else
            {
                localTombstone = new S3SyncTombstone
                {
                    EntityType = remoteTombstone.EntityType,
                    Key = remoteTombstone.Key,
                    DeletedAt = deletionVersion,
                    ReadingDataResetId = remoteTombstone.ReadingDataResetId
                };
                local.Tombstones.Add(localTombstone);
                tombstones[key] = localTombstone;
            }
        }
    }

    private static void AddUnchangedKeys<T>(
        IEnumerable<T> currentRows,
        IEnumerable<T> previousRows,
        Func<T, string> id,
        string entityType,
        ISet<string> output)
    {
        var previous = previousRows.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        foreach (var current in currentRows)
        {
            var entityId = id(current);
            if (previous.TryGetValue(entityId, out var old)
                && string.Equals(
                    JsonSerializer.Serialize(current, JsonOptions),
                    JsonSerializer.Serialize(old, JsonOptions),
                    StringComparison.Ordinal))
                output.Add(VersionKey(entityType, entityId));
        }
    }

    private async Task<HashSet<string>> ResolveLocalRestorationsAsync(
        S3SyncSnapshot local,
        S3SyncSnapshot? previousLocal,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        CancellationToken cancellationToken)
    {
        if (local.Tombstones.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousBooks = (previousLocal?.Books ?? [])
            .ToDictionary(item => item.Id);
        var previousFiles = (previousLocal?.Files ?? [])
            .ToDictionary(item => item.Id);
        var deletionVersions = MergeTombstones(
                local.Tombstones,
                remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones ?? []))
            .GroupBy(tombstone => VersionKey(tombstone.EntityType, tombstone.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.DeletedAt), StringComparer.OrdinalIgnoreCase);
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updatedBookVersions = new Dictionary<Guid, DateTimeOffset>();

        foreach (var book in local.Books)
        {
            var bookKey = VersionKey("book", book.Id);
            var changedSinceBaseline = !previousBooks.TryGetValue(book.Id, out var previous)
                || !JsonSerializer.Serialize(book, JsonOptions)
                    .Equals(JsonSerializer.Serialize(previous, JsonOptions), StringComparison.Ordinal);
            if (!changedSinceBaseline || !deletionVersions.TryGetValue(bookKey, out var bookDeletedAt))
                continue;

            resolved.Add(bookKey);
            var restoreVersion = bookDeletedAt;
            foreach (var file in local.Files.Where(item => item.BookId == book.Id))
            {
                var fileKey = VersionKey("file", file.Id);
                if (deletionVersions.TryGetValue(fileKey, out var fileDeletedAt))
                {
                    resolved.Add(fileKey);
                    if (fileDeletedAt > restoreVersion) restoreVersion = fileDeletedAt;
                }
            }
            restoreVersion = GetVersionAfter(book.UpdatedAt, restoreVersion);
            book.UpdatedAt = restoreVersion;
            updatedBookVersions[book.Id] = restoreVersion;
        }

        foreach (var file in local.Files)
        {
            var fileKey = VersionKey("file", file.Id);
            var changedSinceBaseline = !previousFiles.TryGetValue(file.Id, out var previous)
                || !JsonSerializer.Serialize(file, JsonOptions)
                    .Equals(JsonSerializer.Serialize(previous, JsonOptions), StringComparison.Ordinal);
            if (!changedSinceBaseline || !deletionVersions.TryGetValue(fileKey, out var deletedAt))
                continue;

            resolved.Add(fileKey);
            if (updatedBookVersions.ContainsKey(file.BookId)) continue;
            var book = local.Books.FirstOrDefault(item => item.Id == file.BookId);
            if (book is null) continue;
            var restoreVersion = GetVersionAfter(book.UpdatedAt, deletedAt);
            book.UpdatedAt = restoreVersion;
            updatedBookVersions[book.Id] = restoreVersion;
        }

        if (resolved.Count == 0) return resolved;
        foreach (var file in local.Files)
            if (updatedBookVersions.TryGetValue(file.BookId, out var version))
                file.ModifiedAt = version;

        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (bookId, updatedAt) in updatedBookVersions)
        {
            using var update = CreateCommand(connection, transaction, """
                UPDATE Books SET UpdatedAt = $updatedAt
                WHERE Id = $bookId AND julianday(UpdatedAt) < julianday($updatedAt);
                """);
            AddParameter(update, "$updatedAt", updatedAt.ToString("O"));
            AddParameter(update, "$bookId", bookId.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var hasDeletionLog = CreateCommand(connection, transaction,
                   "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'S3SyncDeletionLog');"))
        {
            if (Convert.ToInt32(await hasDeletionLog.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                foreach (var versionKey in resolved)
                {
                    var separator = versionKey.IndexOf(':');
                    if (separator <= 0) continue;
                    var entityType = versionKey[..separator];
                    var entityId = versionKey[(separator + 1)..];
                    if (entityType is not ("book" or "file")) continue;
                    using var delete = CreateCommand(connection, transaction, """
                        DELETE FROM S3SyncDeletionLog
                        WHERE EntityType = $entityType
                          AND lower(replace(EntityKey, '-', '')) = $entityId;
                        """);
                    AddParameter(delete, "$entityType", entityType);
                    AddParameter(delete, "$entityId", entityId.ToLowerInvariant());
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return resolved;
    }

    private static DateTimeOffset GetVersionAfter(DateTimeOffset current, DateTimeOffset deletion) =>
        current > deletion ? current : deletion.AddTicks(1);

    private async Task ResetLocalBaselineCoreAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var markerPath = GetPendingBaselineResetPath();
        var markerTemporaryPath = markerPath + ".tmp";
        await File.WriteAllTextAsync(
            markerTemporaryPath,
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken);
        SettingsFile.Publish(markerTemporaryPath, markerPath);

        await ClearRecordedDeletionTimesAsync(cancellationToken);
        await SaveStateAsync(new S3SyncState
        {
            DeviceId = NormalizeDeviceId(deviceId),
            StorageIdentity = string.Empty,
            LastUploadedSnapshot = null,
            Tombstones = [],
            RemoteBookIds = [],
            RemoteOnlyFileHashes = []
        }, cancellationToken);
        try { File.Delete(markerPath); }
        catch (FileNotFoundException) { }
    }

    private string GetPendingBaselineResetPath() =>
        Path.Combine(_paths.Data, AppBackupService.PendingSyncBaselineResetFileName);

    private static bool AppSyncSettingsEqual(S3SyncAppSettings? left, S3SyncAppSettings? right)
    {
        if (left is null || right is null) return left is null && right is null;
        var leftValues = JsonSerializer.Serialize(new
        {
            left.UiLanguage,
            left.PreferredOpenFormat,
            left.AutoBackupEnabled,
            left.AutoGenerateEpubAndAzw3OnImport,
            left.CollectionsMutuallyExclusive,
            left.AutoBackupRetention,
            left.AiEnabled,
            left.AutoUpdateCheckEnabled,
            left.DevelopmentUpdateCheckEnabled,
            left.AutoDoubanMatchOnImport,
            left.CompareKindleLibraryEnabled,
            left.GridGalleryDisplay,
            left.ShowSyncStatusIcon,
            left.ShowLibraryPresenceIcon,
            left.ReadingMaterialsCollapsedByDefault,
            left.PinyinContextMenuEnabled,
            left.PinyinLocalOnly,
            left.PinyinEngineId,
            left.DefaultReaderLayout
        });
        var rightValues = JsonSerializer.Serialize(new
        {
            right.UiLanguage,
            right.PreferredOpenFormat,
            right.AutoBackupEnabled,
            right.AutoGenerateEpubAndAzw3OnImport,
            right.CollectionsMutuallyExclusive,
            right.AutoBackupRetention,
            right.AiEnabled,
            right.AutoUpdateCheckEnabled,
            right.DevelopmentUpdateCheckEnabled,
            right.AutoDoubanMatchOnImport,
            right.CompareKindleLibraryEnabled,
            right.GridGalleryDisplay,
            right.ShowSyncStatusIcon,
            right.ShowLibraryPresenceIcon,
            right.ReadingMaterialsCollapsedByDefault,
            right.PinyinContextMenuEnabled,
            right.PinyinLocalOnly,
            right.PinyinEngineId,
            right.DefaultReaderLayout
        });
        return string.Equals(leftValues, rightValues, StringComparison.Ordinal);
    }

    private static bool AiSyncSettingsEqual(S3SyncAiSettings? left, S3SyncAiSettings? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
              && string.Equals(left.BaseUrl, right.BaseUrl, StringComparison.Ordinal)
              && string.Equals(left.Model, right.Model, StringComparison.Ordinal);

    private static bool EmailSyncSettingsEqual(
        S3SyncKindleEmailSettings? left,
        S3SyncKindleEmailSettings? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.KindleEmailAddress, right.KindleEmailAddress, StringComparison.OrdinalIgnoreCase)
              && string.Equals(left.SenderEmailAddress, right.SenderEmailAddress, StringComparison.OrdinalIgnoreCase)
              && string.Equals(left.SmtpHost, right.SmtpHost, StringComparison.OrdinalIgnoreCase)
              && left.SmtpPort == right.SmtpPort
              && string.Equals(left.SmtpUsername, right.SmtpUsername, StringComparison.Ordinal)
              && left.EnableSsl == right.EnableSsl;

    private static void ThrowIfInvalid(S3SyncSettings settings)
    {
        var validation = settings.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
    }

    private static string NormalizeDeviceId(string deviceId) =>
        Guid.TryParse(deviceId, out var parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");

    private static string NormalizeKnownDeviceId(string? deviceId) =>
        Guid.TryParse(deviceId, out var parsed)
            ? parsed.ToString("N")
            : (deviceId ?? string.Empty).Trim();

    private static bool SameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
        left.Provider == right.Provider && left.Prefix == right.Prefix
        && (left.Provider == SyncProvider.WebDav
            ? left.WebDavEndpoint == right.WebDavEndpoint && left.WebDavUsername == right.WebDavUsername
            : left.Endpoint == right.Endpoint && left.Bucket == right.Bucket);

    private static string BuildStorageIdentity(S3SyncSettings settings) => settings.Provider == SyncProvider.WebDav
        ? string.Join("|", "WebDAV", settings.WebDavEndpoint, EncryptionKeyFingerprint(settings.WebDavUsername), settings.Prefix,
            settings.EncryptionKey.Length > 0 ? $"encrypted:{EncryptionKeyFingerprint(settings.EncryptionKey)}" : "plain")
        // Keep the legacy S3 identity unchanged so existing baselines survive
        // an upgrade. A provider, account or remote-root change starts fresh.
        :
        string.Join(
            "|",
            settings.Endpoint,
            settings.Bucket,
            settings.Region,
            settings.Prefix,
            settings.EncryptionKey.Length > 0
                ? $"encrypted:{EncryptionKeyFingerprint(settings.EncryptionKey)}"
                : "plain");

    private string StatePath => Path.Combine(_paths.Data, "s3-sync-state.json");

    private async Task<S3SyncState> LoadStateAsync(
        string deviceId,
        string storageIdentity,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
            return new S3SyncState { DeviceId = deviceId, StorageIdentity = storageIdentity };

        try
        {
            await using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var state = await JsonSerializer.DeserializeAsync<S3SyncState>(stream, JsonOptions, cancellationToken)
                ?? new S3SyncState();
            if (!string.Equals(state.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.StorageIdentity, storageIdentity, StringComparison.Ordinal))
            {
                return new S3SyncState { DeviceId = deviceId, StorageIdentity = storageIdentity };
            }

            state.Tombstones ??= [];
            state.RemoteBookIds ??= [];
            state.RemoteOnlyFileHashes ??= [];
            return state;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new S3SyncState { DeviceId = deviceId, StorageIdentity = storageIdentity };
        }
    }

    private async Task SaveStateAsync(S3SyncState state, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var temporaryPath = StatePath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, StatePath, overwrite: true);
    }

    private static string SnapshotKey(S3SyncSettings settings, string deviceId) =>
        $"{settings.Prefix}/devices/{deviceId}/snapshot.bin";

    private static string BlobKey(S3SyncSettings settings, string hash) =>
        $"{settings.Prefix}/objects/"
        + (settings.EncryptionKey.Length > 0
            ? $"encrypted/{EncryptionKeyFingerprint(settings.EncryptionKey)}"
            : "plain")
        + $"/{hash.ToLowerInvariant()}";

    private static string EncryptionKeyFingerprint(string encryptionKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey))).ToLowerInvariant();

    private static ISyncObjectStore CreateObjectStore(S3SyncSettings settings) => settings.Provider switch
    {
        SyncProvider.S3 => new S3SyncObjectStore(settings),
        SyncProvider.WebDav => new WebDavSyncObjectStore(settings),
        _ => throw new InvalidOperationException(UiText.Get("请选择受支持的同步方式。"))
    };

    private static async Task<List<string>> ListSnapshotKeysAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var keys = await client.ListKeysAsync($"{settings.Prefix}/devices/", cancellationToken);
        return keys.Where(key => key.EndsWith("/snapshot.bin", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<string?> UploadLocalObjectsAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        S3SyncSnapshot snapshot,
        IReadOnlySet<string> remoteOnlyFileHashes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var objects = new List<LocalSyncObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new ConcurrentBag<string>();
        var invalid = new ConcurrentBag<string>();

        foreach (var file in snapshot.Files)
        {
            if (!IsSha256(file.Sha256))
            {
                invalid.Add(UiText.Get("本地文件“{0}”缺少有效的 SHA-256 校验值，已跳过上传。", file.FileName));
                continue;
            }
            var relativePath = snapshot.LocalFilePaths.GetValueOrDefault(file.Id)
                ?? Path.Combine("library", file.BookId.ToString("N"), file.FileName);
            var path = ResolveDataPath(relativePath);
            if (path is null || !File.Exists(path))
            {
                // A remote-only file is deliberately represented in the local
                // database before its bytes arrive. Do not turn that expected
                // absence into a failed upload warning.
                if (remoteOnlyFileHashes.Contains(file.Sha256)) continue;
                missing.Add(path ?? relativePath);
                continue;
            }
            if (seen.Add(file.Sha256))
                objects.Add(new LocalSyncObject(file.Sha256, path, "application/octet-stream"));
        }

        foreach (var book in snapshot.Books)
        {
            if (!IsSha256(book.CoverHash) || string.IsNullOrWhiteSpace(book.CoverFileName)) continue;
            var relativePath = book.LocalCoverPath
                ?? snapshot.LocalCoverPaths.GetValueOrDefault(book.Id)
                ?? Path.Combine("covers", book.CoverFileName);
            var path = ResolveDataPath(relativePath);
            if (path is null || !File.Exists(path))
            {
                missing.Add(path ?? relativePath);
                continue;
            }
            if (seen.Add(book.CoverHash!))
                objects.Add(new LocalSyncObject(book.CoverHash!, path, "image/*"));
        }

        // Objects are content-addressed and immutable. Listing the object
        // prefix once avoids one HEAD round-trip for every file. If a backend
        // does not allow listing this prefix, fall back to the existing HEAD
        // check so synchronization remains compatible.
        var existingKeys = await TryListBlobKeysAsync(client, settings, cancellationToken);

        var uploaded = 0;
        await Parallel.ForEachAsync(
            objects,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = settings.ConcurrentRequests,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                try
                {
                    await UploadObjectIfMissingAsync(client, settings, item, existingKeys, token);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    missing.Add(item.Path);
                }
                catch (InvalidDataException exception)
                {
                    invalid.Add(UiText.Get("本地文件“{0}”校验失败：{1}", Path.GetFileName(item.Path), UiText.Localize(exception.Message)));
                }
                var count = Interlocked.Increment(ref uploaded);
                if (count == objects.Count || count % 10 == 0)
                    progress?.Report(UiText.Get("已处理 {0}/{1} 个同步对象…", count, objects.Count));
            });

        if (!missing.IsEmpty)
            invalid.Add(UiText.Get("有 {0} 个本地文件缺失，已跳过上传。", missing.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        return invalid.IsEmpty ? null : string.Join(" ", invalid.Distinct());
    }

    private async Task UploadObjectIfMissingAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        LocalSyncObject item,
        ConcurrentDictionary<string, byte>? existingKeys,
        CancellationToken cancellationToken)
    {
        var key = BlobKey(settings, item.Hash);
        if (existingKeys is not null)
        {
            if (existingKeys.ContainsKey(key)) return;
        }
        else if (await client.ExistsAsync(key, cancellationToken))
        {
            return;
        }

        // Hold the same input stream through validation and upload. On Windows
        // the sharing mode also prevents a concurrent edit or removal.
        await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, useAsync: true);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!string.Equals(actualHash, item.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.Get("本地文件内容与 SHA-256 校验值不匹配，已跳过上传。"));
        input.Position = 0;

        string? temporaryEncryptedPath = null;
        try
        {
            if (settings.EncryptionKey.Length == 0)
            {
                await client.PutAsync(key, input, item.ContentType, cancellationToken);
            }
            else
            {
                temporaryEncryptedPath = Path.Combine(
                    Path.GetTempPath(),
                    $"kkindle-sync-{Guid.NewGuid():N}.bin");
                await EncryptStreamToPathAsync(
                    input,
                    temporaryEncryptedPath,
                    settings.EncryptionKey,
                    cancellationToken);
                await using var encrypted = new FileStream(temporaryEncryptedPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
                await client.PutAsync(key, encrypted, item.ContentType, cancellationToken);
            }
            existingKeys?.TryAdd(key, 0);
        }
        finally
        {
            if (temporaryEncryptedPath is not null)
                TryDeleteTemporaryFile(temporaryEncryptedPath);
        }
    }

    private static async Task<ConcurrentDictionary<string, byte>?> TryListBlobKeysAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var keys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var prefix = $"{settings.Prefix}/objects/"
            + (settings.EncryptionKey.Length > 0
                ? $"encrypted/{EncryptionKeyFingerprint(settings.EncryptionKey)}"
                : "plain")
            + "/";
        try
        {
            foreach (var key in await client.ListKeysAsync(prefix, cancellationToken)) keys.TryAdd(key, 0);
            return keys;
        }
        catch (SyncListingUnavailableException)
        {
            return null;
        }
    }

    private async Task UploadSnapshotAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var compressed = await CompressSnapshotAsync(snapshot, cancellationToken);
        var payload = settings.EncryptionKey.Length == 0
            ? compressed
            : ProtectPayloadForSync(compressed, settings.EncryptionKey);
        using var stream = new MemoryStream(payload, writable: false);
        await client.PutAsync(SnapshotKey(settings, snapshot.DeviceId), stream,
            "application/octet-stream", cancellationToken);
    }

    private async Task<string?> CleanupRemoteStorageAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        string localDeviceId,
        S3SyncSnapshot localSnapshot,
        S3SyncState state,
        IReadOnlyList<S3SyncSnapshot> downloadedRemoteSnapshots,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SyncObjectMetadata> deviceObjects;
        try
        {
            deviceObjects = await client.ListObjectsAsync(
                $"{settings.Prefix}/devices/",
                cancellationToken);
        }
        catch (SyncListingUnavailableException)
        {
            return null;
        }

        var snapshotsByDevice = downloadedRemoteSnapshots
            .Where(snapshot => !string.Equals(
                NormalizeKnownDeviceId(snapshot.DeviceId),
                NormalizeDeviceId(localDeviceId),
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(snapshot => NormalizeKnownDeviceId(snapshot.DeviceId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);
        var localId = NormalizeDeviceId(localDeviceId);
        var deletedSnapshots = new HashSet<string>(StringComparer.Ordinal);
        var cleanupErrors = new List<string>();
        var retiredBefore = DateTimeOffset.UtcNow - RetiredDeviceSnapshotRetention;

        foreach (var remoteObject in deviceObjects
                     .Where(item => item.Key.EndsWith("/snapshot.bin", StringComparison.OrdinalIgnoreCase)
                         && item.LastModified is { } modified
                         && modified <= retiredBefore)
                     .OrderBy(item => item.LastModified))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSnapshotDeviceId(settings, remoteObject.Key, out var snapshotDeviceId)
                || string.Equals(snapshotDeviceId, localId, StringComparison.OrdinalIgnoreCase)
                || !snapshotsByDevice.TryGetValue(snapshotDeviceId, out var candidate))
                continue;

            var targets = snapshotsByDevice
                .Where(pair => !string.Equals(pair.Key, snapshotDeviceId, StringComparison.OrdinalIgnoreCase)
                    && !deletedSnapshots.Contains(SnapshotKey(settings, pair.Key)))
                .Select(pair => pair.Value)
                .Append(localSnapshot)
                .ToArray();
            if (!SnapshotIsSubsumed(candidate, targets)) continue;

            try
            {
                await client.DeleteAsync(remoteObject.Key, cancellationToken);
                deletedSnapshots.Add(remoteObject.Key);
                snapshotsByDevice.Remove(snapshotDeviceId);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupErrors.Add(exception.Message);
            }
        }

        var retainedSnapshots = snapshotsByDevice.Values.Append(localSnapshot).ToArray();
        var compactedWatermark = GetTombstoneCompactionWatermark(retainedSnapshots);
        var existingWatermark = retainedSnapshots
            .Select(snapshot => snapshot.TombstonesPrunedBefore ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (compactedWatermark > existingWatermark)
        {
            localSnapshot.Tombstones = (localSnapshot.Tombstones ?? [])
                .Where(tombstone => tombstone.DeletedAt > compactedWatermark)
                .ToList();
            localSnapshot.TombstonesPrunedBefore = compactedWatermark;
            state.LastUploadedSnapshot = localSnapshot;
            state.Tombstones = localSnapshot.Tombstones;
            await SaveStateAsync(state, cancellationToken);
            using (await AppDataProcessLock.AcquireAsync(_paths, cancellationToken))
                await ClearRecordedDeletionTimesThroughAsync(compactedWatermark, cancellationToken);
            await UploadSnapshotAsync(client, settings, localSnapshot, cancellationToken);
        }

        retainedSnapshots = snapshotsByDevice.Values.Append(localSnapshot).ToArray();
        var referencedBlobKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in retainedSnapshots)
        {
            foreach (var file in snapshot.Files)
                if (IsSha256(file.Sha256)) referencedBlobKeys.Add(BlobKey(settings, file.Sha256));
            foreach (var book in snapshot.Books)
                if (IsSha256(book.CoverHash)) referencedBlobKeys.Add(BlobKey(settings, book.CoverHash!));
        }

        IReadOnlyList<SyncObjectMetadata> blobObjects;
        try
        {
            blobObjects = await client.ListObjectsAsync(
                BlobKey(settings, string.Empty),
                cancellationToken);
        }
        catch (SyncListingUnavailableException)
        {
            return cleanupErrors.Count == 0
                ? null
                : UiText.Get("已清理部分远端快照，但对象清理失败：{0}", string.Join("; ", cleanupErrors.Distinct()));
        }

        var orphanBefore = DateTimeOffset.UtcNow - UnreferencedBlobRetention;
        foreach (var blob in blobObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referencedBlobKeys.Contains(blob.Key)
                || blob.LastModified is not { } modified
                || modified > orphanBefore)
                continue;
            try
            {
                await client.DeleteAsync(blob.Key, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupErrors.Add(exception.Message);
            }
        }

        if (client is S3SyncObjectStore s3Store)
        {
            try
            {
                await s3Store.CleanupIncompleteMultipartUploadsAsync(
                    TimeSpan.FromDays(180),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupErrors.Add(exception.Message);
            }
        }

        return cleanupErrors.Count == 0
            ? null
            : UiText.Get("远端清理未完成：{0}", string.Join("; ", cleanupErrors.Distinct()));
    }

    private static bool TryReadSnapshotDeviceId(
        S3SyncSettings settings,
        string key,
        out string deviceId)
    {
        deviceId = string.Empty;
        var marker = $"{settings.Prefix}/devices/";
        const string suffix = "/snapshot.bin";
        if (!key.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
            || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = key[marker.Length..^suffix.Length];
        if (!Guid.TryParse(candidate, out var parsed)) return false;
        deviceId = parsed.ToString("N");
        return true;
    }

    private static bool SnapshotIsSubsumed(
        S3SyncSnapshot candidate,
        IReadOnlyList<S3SyncSnapshot> targets)
    {
        if (targets.Count == 0) return false;
        var targetLiveRows = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var tombstoneVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            foreach (var (key, version) in GetSnapshotEntityVersions(target))
                if (!targetLiveRows.TryGetValue(key, out var existing) || version > existing)
                    targetLiveRows[key] = version;
            foreach (var tombstone in target.Tombstones ?? [])
            {
                var key = VersionKey(tombstone.EntityType, tombstone.Key);
                if (!tombstoneVersions.TryGetValue(key, out var existing) || tombstone.DeletedAt > existing)
                    tombstoneVersions[key] = tombstone.DeletedAt;
            }
        }

        var targetSnapshots = targets.ToArray();
        bool RowsCovered<T>(
            IEnumerable<T> sourceRows,
            IEnumerable<T> targetRows,
            Func<T, string> key,
            Func<T, DateTimeOffset> version,
            Func<T, IEnumerable<string>> fallbackTombstones)
        {
            var byKey = targetRows
                .GroupBy(key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var source in sourceRows)
            {
                var sourceKey = key(source);
                var sourceVersion = version(source);
                if (byKey.TryGetValue(sourceKey, out var alternatives)
                    && alternatives.Any(target => version(target) >= sourceVersion
                        && JsonSerializer.Serialize(target, JsonOptions)
                            .Equals(JsonSerializer.Serialize(source, JsonOptions), StringComparison.Ordinal)))
                    continue;

                var covered = fallbackTombstones(source).Any(tombstoneKey =>
                    tombstoneVersions.TryGetValue(tombstoneKey, out var deletedAt)
                    && deletedAt >= sourceVersion);
                if (!covered) return false;
            }
            return true;
        }

        var targetBooks = targetSnapshots.SelectMany(snapshot => snapshot.Books);
        var targetFiles = targetSnapshots.SelectMany(snapshot => snapshot.Files);
        var targetCollections = targetSnapshots.SelectMany(snapshot => snapshot.Collections);
        var targetItems = targetSnapshots.SelectMany(snapshot => snapshot.CollectionItems);
        var targetAnnotations = targetSnapshots.SelectMany(snapshot => snapshot.Annotations);
        var targetReflections = targetSnapshots.SelectMany(snapshot => snapshot.BookReflections);
        var targetProgress = targetSnapshots.SelectMany(snapshot => snapshot.Progress);
        var targetBookmarks = targetSnapshots.SelectMany(snapshot => snapshot.Bookmarks);
        var targetLayouts = targetSnapshots.SelectMany(snapshot => snapshot.Layouts);
        var targetStats = targetSnapshots.SelectMany(snapshot => snapshot.ReadingStats);

        if (!RowsCovered(candidate.Books, targetBooks,
                item => item.Id.ToString("N"), item => item.UpdatedAt,
                item => [VersionKey("book", item.Id)])) return false;
        if (!RowsCovered(candidate.Files, targetFiles,
                item => item.Id.ToString("N"), item => item.ModifiedAt,
                item => [VersionKey("file", item.Id), VersionKey("book", item.BookId)])) return false;
        if (!RowsCovered(candidate.Collections, targetCollections,
                item => item.Id.ToString("N"), CollectionUpdatedAt,
                item => [VersionKey("collection", item.Id)])) return false;
        if (!RowsCovered(candidate.CollectionItems, targetItems,
                item => CompositeKey(item.CollectionId, item.BookId), item => item.AddedAt,
                item => [
                    VersionKey("collection-item", CompositeKey(item.CollectionId, item.BookId)),
                    VersionKey("collection", item.CollectionId),
                    VersionKey("book", item.BookId)
                ])) return false;
        if (!RowsCovered(candidate.Annotations, targetAnnotations,
                item => item.Id.ToString("N"), item => item.UpdatedAt,
                item => [VersionKey("annotation", item.Id), VersionKey("book", item.BookId), VersionKey("file", item.BookFileId)])) return false;
        if (!RowsCovered(candidate.BookReflections, targetReflections,
                item => item.BookId.ToString("N"), item => item.UpdatedAt,
                item => [VersionKey("reflection", item.BookId), VersionKey("book", item.BookId)])) return false;
        if (!RowsCovered(candidate.Progress, targetProgress,
                item => item.BookFileId.ToString("N"), item => item.UpdatedAt,
                item => [VersionKey("progress", item.BookFileId), VersionKey("book", item.BookId), VersionKey("file", item.BookFileId)])) return false;
        if (!RowsCovered(candidate.Bookmarks, targetBookmarks,
                item => item.Id.ToString("N"), item => item.CreatedAt,
                item => [VersionKey("bookmark", item.Id), VersionKey("book", item.BookId), VersionKey("file", item.BookFileId)])) return false;
        if (!RowsCovered(candidate.Layouts, targetLayouts,
                item => item.BookFileId.ToString("N"), item => item.UpdatedAt,
                item => [VersionKey("layout", item.BookFileId), VersionKey("book", item.BookId), VersionKey("file", item.BookFileId)])) return false;
        if (!RowsCovered(candidate.ReadingStats, targetStats,
                item => item.BookFileId.ToString("N"), item => item.UpdatedAt,
                item => [VersionKey("stats", item.BookFileId), VersionKey("book", item.BookId), VersionKey("file", item.BookFileId)])) return false;

        foreach (var tombstone in candidate.Tombstones ?? [])
        {
            var key = VersionKey(tombstone.EntityType, tombstone.Key);
            if (tombstoneVersions.TryGetValue(key, out var deletedAt) && deletedAt >= tombstone.DeletedAt) continue;
            if (targetLiveRows.TryGetValue(key, out var liveVersion) && liveVersion > tombstone.DeletedAt) continue;
            return false;
        }
        return true;
    }

    private static DateTimeOffset GetTombstoneCompactionWatermark(
        IReadOnlyList<S3SyncSnapshot> snapshots)
    {
        var existingWatermark = snapshots
            .Select(snapshot => snapshot.TombstonesPrunedBefore ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        var retentionCutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(365);
        var versions = snapshots
            .SelectMany(snapshot => (snapshot.Tombstones ?? []).Select(tombstone => (
                Key: VersionKey(tombstone.EntityType, tombstone.Key),
                DeviceId: NormalizeKnownDeviceId(snapshot.DeviceId),
                tombstone.DeletedAt)))
            .Where(item => item.DeletedAt > existingWatermark)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versions.Length == 0) return existingWatermark;

        var commonKeys = versions
            .Where(group => group.Select(item => item.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == snapshots.Count
                && snapshots.All(snapshot => !ContainsEntityAtOrBefore(
                    snapshot,
                    group.Key,
                    group.Max(item => item.DeletedAt))))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldestUnreplicated = versions
            .Where(group => !commonKeys.Contains(group.Key))
            .Select(group => group.Min(item => item.DeletedAt))
            .Where(date => date <= retentionCutoff)
            .DefaultIfEmpty(retentionCutoff.AddTicks(1))
            .Min();
        var upperBound = oldestUnreplicated <= retentionCutoff
            ? oldestUnreplicated.AddTicks(-1)
            : retentionCutoff;
        var safeCommonVersions = versions
            .Where(group => commonKeys.Contains(group.Key))
            .Select(group => group.Max(item => item.DeletedAt))
            .Where(date => date <= upperBound)
            .ToArray();
        if (safeCommonVersions.Length == 0) return existingWatermark;
        return Max(existingWatermark, safeCommonVersions.Max());
    }

    private static bool ContainsEntityAtOrBefore(
        S3SyncSnapshot snapshot,
        string versionKey,
        DateTimeOffset deletedAt)
    {
        var separator = versionKey.IndexOf(':');
        if (separator <= 0) return false;
        var type = versionKey[..separator];
        var rawKey = versionKey[(separator + 1)..];
        if (type == "book" && Guid.TryParse(rawKey, out var bookId))
        {
            return snapshot.Books.Any(row => row.Id == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.Files.Any(row => row.BookId == bookId && row.ModifiedAt <= deletedAt)
                || snapshot.Annotations.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.BookReflections.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.Progress.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.Bookmarks.Any(row => row.BookId == bookId && row.CreatedAt <= deletedAt)
                || snapshot.Layouts.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.ReadingStats.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.CollectionItems.Any(row => row.BookId == bookId && row.AddedAt <= deletedAt);
        }
        if (type == "file" && Guid.TryParse(rawKey, out var fileId))
        {
            return snapshot.Files.Any(row => row.Id == fileId && row.ModifiedAt <= deletedAt)
                || snapshot.Annotations.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt)
                || snapshot.Progress.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt)
                || snapshot.Bookmarks.Any(row => row.BookFileId == fileId && row.CreatedAt <= deletedAt)
                || snapshot.Layouts.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt)
                || snapshot.ReadingStats.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt);
        }

        return GetSnapshotEntityVersions(snapshot).TryGetValue(versionKey, out var liveVersion)
            && liveVersion <= deletedAt;
    }

    private async Task<List<S3SyncSnapshot>> DownloadRemoteSnapshotsAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        IReadOnlyList<string> snapshotKeys,
        string localDeviceId,
        bool skipOwnSnapshot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<S3SyncSnapshot>();
        var processed = 0;
        var keysToRead = snapshotKeys
            .Where(key => !skipOwnSnapshot || !IsSnapshotKeyForDevice(key, settings, localDeviceId))
            .ToArray();
        foreach (var key in keysToRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await DownloadObjectBytesAsync(client, key, cancellationToken);
            var snapshot = await DecodeSnapshotAsync(payload, settings.EncryptionKey, cancellationToken);
            if (snapshot.Version > SnapshotVersion)
                throw new InvalidDataException(UiText.Get("同步快照版本 {0} 高于当前版本。请先升级 Kkindle。", snapshot.Version));
            ReaderReadingDataReset.Validate(snapshot.ReadingDataReset);
            if (!Guid.TryParse(snapshot.DeviceId, out var parsedDeviceId))
                throw new InvalidDataException("同步快照缺少有效的设备 ID。");
            snapshot.DeviceId = parsedDeviceId.ToString("N");
            snapshot.Tombstones ??= [];
            snapshots.Add(snapshot);
            progress?.Report(UiText.Get("已读取 {0}/{1} 台设备的快照…", ++processed, keysToRead.Length));
        }
        return snapshots;
    }

    private static bool IsSnapshotKeyForDevice(
        string key,
        S3SyncSettings settings,
        string deviceId)
    {
        var suffix = "/snapshot.bin";
        if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var marker = $"{settings.Prefix}/devices/";
        if (!key.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;
        var keyDeviceId = key[marker.Length..^suffix.Length];
        return string.Equals(
            NormalizeKnownDeviceId(keyDeviceId),
            NormalizeDeviceId(deviceId),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> DownloadObjectBytesAsync(
        ISyncObjectStore client,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await client.OpenReadAsync(key, cancellationToken);
        if (response.ContentLength > MaxSnapshotBytes)
            throw new InvalidDataException("云端同步快照过大，已拒绝读取。");

        using var memory = new MemoryStream(
            response.ContentLength is > 0 and <= int.MaxValue
                ? (int)response.ContentLength
                : 64 * 1024);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await response.ResponseStream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (memory.Length > MaxSnapshotBytes - read)
                throw new InvalidDataException("云端同步快照过大，已拒绝读取。");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return memory.ToArray();
    }

    private static async Task<byte[]> CompressSnapshotAsync(
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken);
        return output.ToArray();
    }

    private async Task<S3SyncSnapshot> DecodeSnapshotAsync(
        byte[] payload,
        string encryptionKey,
        CancellationToken cancellationToken)
    {
        if (HasMagic(payload) && encryptionKey.Length == 0)
            throw new InvalidDataException("云端同步对象已加密，请配置相同的加密密钥。");
        if (!HasMagic(payload) && encryptionKey.Length > 0)
            throw new InvalidDataException(UiText.Get("此同步目录尚未加密。请保持原加密配置，或改用新的同步子目录创建加密同步目录。"));

        var compressed = HasMagic(payload)
            ? UnprotectPayloadWithCache(payload, encryptionKey)
            : payload;
        await using var input = new MemoryStream(compressed, writable: false);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var limited = new SnapshotReadLimitStream(gzip, MaxSnapshotBytes);
        return await JsonSerializer.DeserializeAsync<S3SyncSnapshot>(limited, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("云端同步快照为空。");
    }

    private sealed class SnapshotReadLimitStream(Stream inner, long maximumBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytesRead;
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Track(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer)
            => Track(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => Track(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Track(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value < 0) return -1;
            Track(1);
            return value;
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Track(int read)
        {
            if (read <= 0) return read;
            if (_bytesRead > maximumBytes - read)
                throw new InvalidDataException("云端同步快照过大，已拒绝读取。");
            _bytesRead += read;
            return read;
        }
    }

    private byte[] ProtectPayloadForSync(byte[] value, string passphrase)
    {
        if (_encryptionSession is { } session
            && string.Equals(session.Passphrase, passphrase, StringComparison.Ordinal))
            return ProtectPayload(value, passphrase, session.Salt, session.Key);
        return ProtectPayload(value, passphrase);
    }

    private static byte[] ProtectPayload(byte[] value, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        var key = DeriveEncryptionKey(passphrase, salt);
        return ProtectPayload(value, passphrase, salt, key);
    }

    private static byte[] ProtectPayload(
        byte[] value,
        string _,
        byte[] salt,
        byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceBytes);
        var ciphertext = new byte[value.Length];
        var tag = new byte[EncryptionTagBytes];
        using var aes = new AesGcm(key, EncryptionTagBytes);
        aes.Encrypt(nonce, value, ciphertext, tag, EncryptionMagic);

        var result = new byte[
            EncryptionMagic.Length
            + EncryptionSaltBytes
            + EncryptionNonceBytes
            + EncryptionTagBytes
            + ciphertext.Length];
        var offset = 0;
        EncryptionMagic.CopyTo(result, offset);
        offset += EncryptionMagic.Length;
        salt.CopyTo(result, offset);
        offset += EncryptionSaltBytes;
        nonce.CopyTo(result, offset);
        offset += EncryptionNonceBytes;
        tag.CopyTo(result, offset);
        offset += EncryptionTagBytes;
        ciphertext.CopyTo(result, offset);
        return result;
    }

    private static byte[] UnprotectPayload(byte[] value, string passphrase) =>
        UnprotectPayload(value, salt => DeriveEncryptionKey(passphrase, salt));

    private byte[] UnprotectPayloadWithCache(byte[] value, string passphrase) =>
        UnprotectPayload(value, salt => GetOrDeriveEncryptionKey(passphrase, salt));

    private static byte[] UnprotectPayload(
        byte[] value,
        Func<byte[], byte[]> keyFactory)
    {
        var minimum = EncryptionMagic.Length
            + EncryptionSaltBytes
            + EncryptionNonceBytes
            + EncryptionTagBytes;
        if (value.Length < minimum || !HasMagic(value))
            throw new InvalidDataException("云端同步对象的加密头无效。");

        var offset = EncryptionMagic.Length;
        var salt = value.AsSpan(offset, EncryptionSaltBytes).ToArray();
        offset += EncryptionSaltBytes;
        var nonce = value.AsSpan(offset, EncryptionNonceBytes).ToArray();
        offset += EncryptionNonceBytes;
        var tag = value.AsSpan(offset, EncryptionTagBytes).ToArray();
        offset += EncryptionTagBytes;
        var ciphertext = value.AsSpan(offset).ToArray();
        var key = keyFactory(salt);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, EncryptionTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, EncryptionMagic);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("云端同步加密密钥不匹配，或同步对象已损坏。", exception);
        }
    }

    private async Task EncryptFileToPathAsync(
        string sourcePath,
        string targetPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BlobEncryptionChunkBytes,
            useAsync: true);
        await EncryptStreamToPathAsync(input, targetPath, passphrase, cancellationToken);
    }

    private async Task EncryptStreamToPathAsync(
        Stream input, string targetPath, string passphrase, CancellationToken cancellationToken)
    {
        var material = GetEncryptionMaterial(passphrase);
        await using var output = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BlobEncryptionChunkBytes,
            useAsync: true);

        await output.WriteAsync(StreamingEncryptionMagic, cancellationToken);
        await output.WriteAsync(material.Salt, cancellationToken);
        await WriteInt32Async(output, BlobEncryptionChunkBytes, cancellationToken);

        var buffer = new byte[BlobEncryptionChunkBytes];
        var chunkIndex = 0;
        using var aes = new AesGcm(material.Key, EncryptionTagBytes);
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;

            var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceBytes);
            var ciphertext = new byte[read];
            var tag = new byte[EncryptionTagBytes];
            var associatedData = BuildChunkAssociatedData(chunkIndex++);
            aes.Encrypt(
                nonce,
                buffer.AsSpan(0, read),
                ciphertext,
                tag,
                associatedData);
            await WriteInt32Async(output, read, cancellationToken);
            await output.WriteAsync(nonce, cancellationToken);
            await output.WriteAsync(tag, cancellationToken);
            await output.WriteAsync(ciphertext, cancellationToken);
        }

        // A zero-length record terminates the stream. Empty files therefore
        // remain representable without a special case.
        await WriteInt32Async(output, 0, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private async Task DecryptBlobToPathAsync(
        Stream encryptedStream,
        string targetPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[StreamingEncryptionMagic.Length];
        await ReadExactlyAsync(encryptedStream, prefix, cancellationToken);
        if (!prefix.AsSpan().SequenceEqual(StreamingEncryptionMagic))
        {
            // Objects written by older versions use the one-shot format. Keep
            // them readable while using the streaming format for new uploads.
            await using var legacyPayload = new MemoryStream();
            await legacyPayload.WriteAsync(prefix, cancellationToken);
            await encryptedStream.CopyToAsync(legacyPayload, cancellationToken);
            var plaintext = UnprotectPayloadWithCache(legacyPayload.ToArray(), passphrase);
            await using var legacyOutput = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                useAsync: true);
            await legacyOutput.WriteAsync(plaintext, cancellationToken);
            return;
        }

        var salt = new byte[EncryptionSaltBytes];
        await ReadExactlyAsync(encryptedStream, salt, cancellationToken);
        var chunkSize = await ReadInt32Async(encryptedStream, cancellationToken);
        if (chunkSize is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("云端同步对象的分块大小无效。");

        var key = GetOrDeriveEncryptionKey(passphrase, salt);
        using var aes = new AesGcm(key, EncryptionTagBytes);
        await using var output = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            Math.Min(chunkSize, 1024 * 128),
            useAsync: true);
        var chunkIndex = 0;
        while (true)
        {
            var length = await ReadInt32Async(encryptedStream, cancellationToken);
            if (length == 0) break;
            if (length < 0 || length > chunkSize)
                throw new InvalidDataException("云端同步对象的分块长度无效。");

            var nonce = new byte[EncryptionNonceBytes];
            var tag = new byte[EncryptionTagBytes];
            var ciphertext = new byte[length];
            var plaintext = new byte[length];
            await ReadExactlyAsync(encryptedStream, nonce, cancellationToken);
            await ReadExactlyAsync(encryptedStream, tag, cancellationToken);
            await ReadExactlyAsync(encryptedStream, ciphertext, cancellationToken);
            try
            {
                aes.Decrypt(
                    nonce,
                    ciphertext,
                    tag,
                    plaintext,
                    BuildChunkAssociatedData(chunkIndex++));
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("云端同步加密密钥不匹配，或同步对象已损坏。", exception);
            }
            await output.WriteAsync(plaintext, cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
    }

    private (byte[] Salt, byte[] Key) GetEncryptionMaterial(string passphrase)
    {
        if (_encryptionSession is { } session
            && string.Equals(session.Passphrase, passphrase, StringComparison.Ordinal))
            return (session.Salt, session.Key);
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        return (salt, GetOrDeriveEncryptionKey(passphrase, salt));
    }

    private EncryptionSession CreateEncryptionSession(string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        return new EncryptionSession(
            passphrase,
            salt,
            GetOrDeriveEncryptionKey(passphrase, salt));
    }

    private static byte[] BuildChunkAssociatedData(int chunkIndex)
    {
        var result = new byte[StreamingEncryptionMagic.Length + sizeof(int)];
        StreamingEncryptionMagic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(StreamingEncryptionMagic.Length),
            chunkIndex);
        return result;
    }

    private static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, bytes, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new InvalidDataException("云端同步对象提前结束。");
            offset += read;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private byte[] GetOrDeriveEncryptionKey(string passphrase, byte[] salt)
    {
        var passphraseFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)));
        var cacheKey = Convert.ToBase64String(salt) + "\u001f" + passphraseFingerprint;
        if (_derivedEncryptionKeys.TryGetValue(cacheKey, out var cached))
            return cached;
        var derived = DeriveEncryptionKey(passphrase, salt);
        _derivedEncryptionKeys[cacheKey] = derived;
        if (_derivedEncryptionKeys.Count > 128)
            _derivedEncryptionKeys.Clear();
        return derived;
    }

    private static byte[] DeriveEncryptionKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            EncryptionIterations,
            HashAlgorithmName.SHA256,
            EncryptionKeyBytes);

    private static bool HasMagic(byte[] value) =>
        value.Length >= EncryptionMagic.Length
        && value.AsSpan(0, EncryptionMagic.Length).SequenceEqual(EncryptionMagic);

    private async Task<string> GetCachedFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("同步文件不存在。", fullPath);

        if (_fileHashCache.TryGetValue(fullPath, out var cached)
            && cached.Length == info.Length
            && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            return cached.Sha256;

        var hash = await Hashing.Sha256Async(fullPath, cancellationToken);
        _fileHashCache[fullPath] = new CachedFileHash(
            info.Length,
            info.LastWriteTimeUtc,
            hash);
        // Do not allow a long-running process to retain an unbounded path
        // cache. A fresh capture will simply repopulate evicted entries.
        if (_fileHashCache.Count > 4096)
            _fileHashCache.Clear();
        return hash;
    }

    private string? ResolveDataPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var dataRoot = Path.GetFullPath(_paths.Data)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(_paths.Data, relativePath));
        return fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record LocalSyncObject(string Hash, string Path, string ContentType);
}
