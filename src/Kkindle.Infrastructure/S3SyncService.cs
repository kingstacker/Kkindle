using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// S3-compatible synchronisation for Kkindle's logical data.
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
    private const int SnapshotVersion = 2;
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private const int EncryptionSaltBytes = 16;
    private const int EncryptionNonceBytes = 12;
    private const int EncryptionTagBytes = 16;
    private const int EncryptionKeyBytes = 32;
    private const int EncryptionIterations = 120_000;
    private const int BlobEncryptionChunkBytes = 1024 * 1024;
    private const double LargeDeletionRatio = 0.50;
    private const int LargeDeletionMinimumEntities = 10;
    private static readonly byte[] EncryptionMagic = Encoding.ASCII.GetBytes("KKINDLE-SYNC1");
    private static readonly byte[] StreamingEncryptionMagic = Encoding.ASCII.GetBytes("KKINDLE-SYNC2");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly AppPaths _paths;
    private readonly S3SyncSettingsStore _settingsStore;
    private readonly Func<S3SyncSettings, IAmazonS3> _clientFactory;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly ZLibrarySettingsStore _zLibrarySettingsStore;
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
        : this(paths, protector, CreateClient)
    {
    }

    internal S3SyncService(AppPaths paths, ISecretProtector protector, Func<S3SyncSettings, IAmazonS3> clientFactory)
    {
        _paths = paths;
        _clientFactory = clientFactory;
        _settingsStore = new S3SyncSettingsStore(paths, protector);
        _appSettingsStore = new AppSettingsStore(paths);
        _aiSettingsStore = new AiSettingsStore(paths, protector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, protector);
        _zLibrarySettingsStore = new ZLibrarySettingsStore(paths, protector);
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
                && current.Settings.Endpoint == normalized.Endpoint
                && current.Settings.Bucket == normalized.Bucket
                && current.Settings.Prefix == normalized.Prefix)
            {
                var state = await LoadStateAsync(NormalizeDeviceId(deviceId), BuildStorageIdentity(current.Settings), cancellationToken);
                if (state.LastUploadedSnapshot is not null)
                    throw new InvalidOperationException(UiText.Get("当前同步目录已使用原加密配置。更换密钥或启停加密时，请改用新的对象前缀，并在其他设备上配置相同的前缀和密钥。"));
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
            await SaveStateAsync(new S3SyncState
            {
                DeviceId = deviceId,
                StorageIdentity = string.Empty,
                LastUploadedSnapshot = null,
                Tombstones = []
            }, cancellationToken);
            await ClearRecordedDeletionTimesAsync(cancellationToken);
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
            var storageIdentity = BuildStorageIdentity(normalized);
            var state = await LoadStateAsync(deviceId, storageIdentity, cancellationToken);
            var recordedDeletionTimes = await ReadRecordedDeletionTimesAsync(cancellationToken);
            var local = await CaptureSnapshotAsync(deviceId, state.Tombstones, cancellationToken);
            var detectedDeletions = DetectDeletedEntitiesWithRecordedTimes(
                state.LastUploadedSnapshot,
                local,
                recordedDeletionTimes);
            EnsureDeletionVolumeIsSafe(state.LastUploadedSnapshot, detectedDeletions, options?.ConfirmedDeletionFingerprint);
            local.Tombstones = MergeTombstones(
                state.Tombstones,
                detectedDeletions.Concat(GetRecordedTombstones(recordedDeletionTimes, local, state.LastUploadedSnapshot)));

            progress?.Report(UiText.Get("正在连接 S3…"));
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

            var remoteTombstones = remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones);
            local.Tombstones = MergeTombstones(local.Tombstones, remoteTombstones);

            progress?.Report(UiText.Get("正在合并书籍和阅读数据…"));
            var databaseResult = await ApplyRemoteSnapshotsAsync(
                client,
                normalized,
                local,
                remoteSnapshots,
                progress,
                cancellationToken);

            progress?.Report(UiText.Get("正在合并同步设置…"));
            var settingsChanged = await ApplyRemoteSettingsAsync(
                local.Settings,
                remoteSnapshots,
                cancellationToken);

            // Capture again after the merge. This makes this device's snapshot
            // a complete converged view, so a third device can catch up from it
            // without having to contact every previous device forever.
            var finalSnapshot = await CaptureSnapshotAsync(deviceId, local.Tombstones, cancellationToken);
            var finalDeletions = await ReadRecordedDeletionTimesAsync(cancellationToken);
            var recordedTombstones = GetRecordedTombstones(finalDeletions, finalSnapshot, state.LastUploadedSnapshot);
            EnsureDeletionVolumeIsSafe(state.LastUploadedSnapshot,
                MergeTombstones(detectedDeletions, recordedTombstones), options?.ConfirmedDeletionFingerprint);
            finalSnapshot.Tombstones = MergeTombstones(local.Tombstones, recordedTombstones);

            // Publish exactly this captured view after all its objects exist.
            // Writes after capture remain outside the saved local baseline.
            progress?.Report(UiText.Get("正在上传本地书籍文件…"));
            var uploadWarning = await UploadLocalObjectsAsync(
                client, normalized, finalSnapshot, progress, cancellationToken);

            progress?.Report(UiText.Get("正在保存同步快照…"));
            await UploadSnapshotAsync(client, normalized, finalSnapshot, cancellationToken);

            state.DeviceId = deviceId;
            state.StorageIdentity = storageIdentity;
            state.LastSyncAt = DateTimeOffset.UtcNow;
            state.LastUploadedSnapshot = finalSnapshot;
            state.Tombstones = finalSnapshot.Tombstones;
            await SaveStateAsync(state, cancellationToken);

            var warning = string.Join(
                " ",
                new[] { uploadWarning, databaseResult.Warning }
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

    private static string BuildStorageIdentity(S3SyncSettings settings) =>
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

    private static AmazonS3Client CreateClient(S3SyncSettings settings)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.PathStyle,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            AuthenticationRegion = settings.Region,
            // Alibaba Cloud OSS accepts the S3 API but does not accept the
            // optional trailer checksum that the AWS SDK 4.x sends by
            // default. Only relax optional checksum generation for a custom
            // S3-compatible endpoint; native AWS S3 keeps its normal defaults.
            RequestChecksumCalculation = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? RequestChecksumCalculation.WHEN_SUPPORTED
                : RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? ResponseChecksumValidation.WHEN_SUPPORTED
                : ResponseChecksumValidation.WHEN_REQUIRED
        };

        // AWS SDK treats a custom ServiceURL and RegionEndpoint as mutually
        // exclusive. AuthenticationRegion still controls SigV4 for MinIO,
        // Cloudflare R2, Wasabi and other S3-compatible endpoints.
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        else
            config.ServiceURL = settings.Endpoint;

        if (settings.SkipTlsVerify)
            config.HttpClientFactory = new InsecureHttpClientFactory();

        return new AmazonS3Client(settings.AccessKey, settings.SecretKey, config);
    }

    private sealed class InsecureHttpClientFactory : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            return new HttpClient(handler, disposeHandler: true);
        }

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }

    private static async Task<List<string>> ListSnapshotKeysAsync(
        IAmazonS3 client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        string? continuation = null;
        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = settings.Bucket,
                Prefix = $"{settings.Prefix}/devices/",
                ContinuationToken = continuation,
                MaxKeys = 1000
            };
            var response = await client.ListObjectsV2Async(request, cancellationToken);
            // Some S3-compatible services omit <Contents> completely for an
            // empty prefix. The AWS SDK then exposes a null collection.
            result.AddRange((response.S3Objects ?? [])
                .Select(item => item.Key)
                .Where(key => key.EndsWith("/snapshot.bin", StringComparison.OrdinalIgnoreCase)));
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrWhiteSpace(continuation));

        return result;
    }

    private async Task<string?> UploadLocalObjectsAsync(
        IAmazonS3 client,
        S3SyncSettings settings,
        S3SyncSnapshot snapshot,
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
        IAmazonS3 client,
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
        else if (await ObjectExistsAsync(client, settings.Bucket, key, cancellationToken))
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
            PutObjectRequest request;
            if (settings.EncryptionKey.Length == 0)
            {
                request = new PutObjectRequest
                {
                    BucketName = settings.Bucket,
                    Key = key,
                    InputStream = input,
                    ContentType = item.ContentType,
                    AutoCloseStream = false
                };
                ConfigureCompatibleUpload(request, settings, input.Length);
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
                request = new PutObjectRequest
                {
                    BucketName = settings.Bucket,
                    Key = key,
                    FilePath = temporaryEncryptedPath,
                    ContentType = item.ContentType,
                    AutoCloseStream = true
                };
                ConfigureCompatibleUpload(request, settings, new FileInfo(temporaryEncryptedPath).Length);
            }
            await client.PutObjectAsync(request, cancellationToken);
            existingKeys?.TryAdd(key, 0);
        }
        finally
        {
            if (temporaryEncryptedPath is not null)
                TryDeleteTemporaryFile(temporaryEncryptedPath);
        }
    }

    private static async Task<ConcurrentDictionary<string, byte>?> TryListBlobKeysAsync(
        IAmazonS3 client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var keys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var prefix = $"{settings.Prefix}/objects/"
            + (settings.EncryptionKey.Length > 0
                ? $"encrypted/{EncryptionKeyFingerprint(settings.EncryptionKey)}"
                : "plain")
            + "/";
        string? continuation = null;
        try
        {
            do
            {
                var response = await client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = settings.Bucket,
                        Prefix = prefix,
                        ContinuationToken = continuation,
                        MaxKeys = 1000
                    },
                    cancellationToken);
                foreach (var item in response.S3Objects ?? [])
                    if (!string.IsNullOrWhiteSpace(item.Key)) keys.TryAdd(item.Key, 0);
                continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (!string.IsNullOrWhiteSpace(continuation));
            return keys;
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Forbidden
            || exception.StatusCode is System.Net.HttpStatusCode.BadRequest
            || exception.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed
            || exception.StatusCode is System.Net.HttpStatusCode.NotImplemented
            || string.Equals(exception.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.ErrorCode, "Forbidden", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.ErrorCode, "InvalidRequest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.ErrorCode, "InvalidArgument", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.ErrorCode, "MethodNotAllowed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.ErrorCode, "NotImplemented", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    private static async Task<bool> ObjectExistsAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = key },
                cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return false;
        }
    }

    private async Task UploadSnapshotAsync(
        IAmazonS3 client,
        S3SyncSettings settings,
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var compressed = await CompressSnapshotAsync(snapshot, cancellationToken);
        var payload = settings.EncryptionKey.Length == 0
            ? compressed
            : ProtectPayloadForSync(compressed, settings.EncryptionKey);
        using var stream = new MemoryStream(payload, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = SnapshotKey(settings, snapshot.DeviceId),
            InputStream = stream,
            ContentType = "application/octet-stream",
            AutoCloseStream = false
        };
        ConfigureCompatibleUpload(request, settings, payload.LongLength);
        await client.PutObjectAsync(request, cancellationToken);
    }

    private static void ConfigureCompatibleUpload(
        PutObjectRequest request,
        S3SyncSettings settings,
        long contentLength)
    {
        // OSS requires a regular HTTP request with Content-Length. Its S3
        // compatibility layer rejects AWS's streaming chunk signature and
        // trailing checksum format (STREAMING-AWS4-*-PAYLOAD-TRAILER).
        request.Headers.ContentLength = contentLength;
        if (string.IsNullOrWhiteSpace(settings.Endpoint)) return;

        request.UseChunkEncoding = false;
        request.DisableDefaultChecksumValidation = true;
    }

    private async Task<List<S3SyncSnapshot>> DownloadRemoteSnapshotsAsync(
        IAmazonS3 client,
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
            var payload = await DownloadObjectBytesAsync(client, settings.Bucket, key, cancellationToken);
            var snapshot = await DecodeSnapshotAsync(payload, settings.EncryptionKey, cancellationToken);
            if (snapshot.Version > SnapshotVersion)
                throw new InvalidDataException(UiText.Get("同步快照版本 {0} 高于当前版本。请先升级 Kkindle。", snapshot.Version));
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
        IAmazonS3 client,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(
            new GetObjectRequest { BucketName = bucket, Key = key },
            cancellationToken);
        if (response.ContentLength > MaxSnapshotBytes)
            throw new InvalidDataException("S3 同步快照过大，已拒绝读取。");

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
                throw new InvalidDataException("S3 同步快照过大，已拒绝读取。");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return memory.ToArray();
    }

    private static async Task<byte[]> CompressSnapshotAsync(
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            await gzip.WriteAsync(json, cancellationToken);
        return output.ToArray();
    }

    private async Task<S3SyncSnapshot> DecodeSnapshotAsync(
        byte[] payload,
        string encryptionKey,
        CancellationToken cancellationToken)
    {
        if (HasMagic(payload) && encryptionKey.Length == 0)
            throw new InvalidDataException("S3 同步对象已加密，请配置相同的加密密钥。");
        if (!HasMagic(payload) && encryptionKey.Length > 0)
            throw new InvalidDataException(UiText.Get("此同步目录尚未加密。请保持原加密配置，或改用新的对象前缀创建加密同步目录。"));

        var compressed = HasMagic(payload)
            ? UnprotectPayloadWithCache(payload, encryptionKey)
            : payload;
        await using var input = new MemoryStream(compressed, writable: false);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var json = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await gzip.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (json.Length > MaxSnapshotBytes - read)
                throw new InvalidDataException("S3 同步快照过大，已拒绝读取。");
            await json.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return JsonSerializer.Deserialize<S3SyncSnapshot>(json.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("S3 同步快照为空。");
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
            throw new InvalidDataException("S3 同步对象的加密头无效。");

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
            throw new InvalidDataException("S3 同步加密密钥不匹配，或同步对象已损坏。", exception);
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
            throw new InvalidDataException("S3 同步对象的分块大小无效。");

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
                throw new InvalidDataException("S3 同步对象的分块长度无效。");

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
                throw new InvalidDataException("S3 同步加密密钥不匹配，或同步对象已损坏。", exception);
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
                throw new InvalidDataException("S3 同步对象提前结束。");
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

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode is System.Net.HttpStatusCode.NotFound
        || exception.StatusCode is System.Net.HttpStatusCode.Forbidden
        || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "NoSuchObject", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "Forbidden", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingObjectForRead(AmazonS3Exception exception) =>
        exception.StatusCode is System.Net.HttpStatusCode.NotFound
        || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "NoSuchObject", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exception.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

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
