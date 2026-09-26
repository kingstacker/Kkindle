using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal sealed class S3SyncObjectStore(S3SyncSettings settings, IAmazonS3 client) : ISyncObjectStore
{
    private const long MultipartThresholdBytes = 32L * 1024 * 1024;
    private const long MultipartPartSizeBytes = 16L * 1024 * 1024;
    private static readonly HashSet<string> MissingCodes = new(StringComparer.OrdinalIgnoreCase)
        { "NoSuchKey", "NoSuchObject", "NotFound" };
    private static readonly HashSet<string> ListingUnavailableCodes = new(StringComparer.OrdinalIgnoreCase)
        { "AccessDenied", "Forbidden", "InvalidRequest", "InvalidArgument", "MethodNotAllowed", "NotImplemented" };
    public S3SyncObjectStore(S3SyncSettings settings) : this(settings, CreateClient(settings)) { }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken) =>
        (await ListObjectsAsync(prefix, cancellationToken))
            .Select(item => item.Key)
            .ToArray();

    public async Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        var result = new List<SyncObjectMetadata>();
        string? continuation = null;
        try
        {
            do
            {
                using var timeout = CreateTimeout(cancellationToken);
                var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = settings.Bucket, Prefix = prefix,
                    ContinuationToken = continuation, MaxKeys = 1000
                }, timeout.Token);
                // Empty prefixes can omit Contents on S3-compatible services.
                result.AddRange((response.S3Objects ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                    .Select(item => new SyncObjectMetadata(
                        item.Key,
                        item.LastModified is { } modified
                            ? new DateTimeOffset(modified.ToUniversalTime())
                            : null)));
                var next = response.IsTruncated == true ? response.NextContinuationToken : null;
                if (response.IsTruncated == true && (string.IsNullOrWhiteSpace(next) || next == continuation))
                    throw new InvalidDataException(UiText.Get("S3 文件列表分页无效，请检查服务端配置。"));
                continuation = next;
            } while (!string.IsNullOrWhiteSpace(continuation));
            return result;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
            || ListingUnavailableCodes.Contains(exception.ErrorCode ?? string.Empty))
        {
            throw new SyncListingUnavailableException(exception.Message, exception);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = settings.Bucket, Key = key
            }, timeout.Token);
            return true;
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception)
            || exception.StatusCode == HttpStatusCode.Forbidden
            || string.Equals(exception.ErrorCode, "Forbidden", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            // S3 may return 403 for an absent key when ListBucket is denied.
            return false;
        }
    }

    public async Task<SyncObjectRead> OpenReadAsync(
        string key,
        CancellationToken cancellationToken,
        long rangeStart = 0)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var request = new GetObjectRequest
            {
                BucketName = settings.Bucket, Key = key
            };
            if (rangeStart > 0)
                request.ByteRange = new ByteRange($"bytes={rangeStart}-");
            var response = await client.GetObjectAsync(request, timeout.Token);
            var stream = new IdleTimeoutReadStream(
                response.ResponseStream,
                cancellationToken,
                TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var isPartialResponse = rangeStart > 0 && !string.IsNullOrWhiteSpace(response.ContentRange);
            long? totalLength = response.ContentLength;
            if (isPartialResponse
                && response.ContentRange is { } contentRange
                && contentRange.LastIndexOf('/') is var separator
                && separator >= 0
                && long.TryParse(contentRange[(separator + 1)..], out var parsedTotal))
                totalLength = parsedTotal;
            return new SyncObjectRead(
                stream,
                response.ContentLength,
                response,
                isPartialResponse,
                totalLength);
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            throw new SyncObjectNotFoundException(exception.Message, exception);
        }
        catch (AmazonS3Exception exception) when (
            rangeStart > 0 && exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return await OpenReadAsync(key, cancellationToken, rangeStart: 0);
        }
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken)
    {
        if (input.CanSeek && input.Length - input.Position >= MultipartThresholdBytes)
        {
            var startPosition = input.Position;
            var sourceHashBytes = await SHA256.HashDataAsync(input, cancellationToken);
            var sourceHash = Convert.ToHexString(sourceHashBytes);
            input.Position = startPosition;
            try
            {
                var sourceLength = input.Length - startPosition;
                try
                {
                    await PutMultipartAsync(
                        key,
                        input,
                        contentType,
                        startPosition,
                        sourceLength,
                        sourceHash,
                        cancellationToken);
                }
                catch (AmazonS3Exception exception) when (
                    string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.OrdinalIgnoreCase))
                {
                    DeleteMultipartStateFile(key);
                    await PutMultipartAsync(
                        key,
                        input,
                        contentType,
                        startPosition,
                        sourceLength,
                        sourceHash,
                        cancellationToken,
                        forceNew: true);
                }
                return;
            }
            catch (AmazonS3Exception exception) when (
                exception.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed
                || string.Equals(exception.ErrorCode, "NotImplemented", StringComparison.OrdinalIgnoreCase))
            {
                input.Position = startPosition;
                var stale = TryReadMultipartState(GetMultipartStatePath(key));
                if (stale is not null)
                    await TryAbortMultipartAsync(key, stale.UploadId, cancellationToken);
                DeleteMultipartStateFile(key);
            }
        }

        await PutSingleAsync(key, input, contentType, cancellationToken);
    }

    private async Task PutSingleAsync(
        string key,
        Stream input,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        var request = new PutObjectRequest
        {
            BucketName = settings.Bucket, Key = key, InputStream = input,
            ContentType = contentType, AutoCloseStream = false
        };
        request.Headers.ContentLength = input.Length - input.Position;
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            // OSS rejects AWS's streaming signatures and optional trailers.
            request.UseChunkEncoding = false;
            request.DisableDefaultChecksumValidation = true;
        }
        EventHandler<Amazon.Runtime.StreamTransferProgressArgs> onProgress = (_, _) =>
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        request.StreamTransferProgress += onProgress;
        try
        {
            await client.PutObjectAsync(request, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"S3 上传在 {settings.TimeoutSeconds} 秒内没有传输进度。",
                exception);
        }
        finally
        {
            request.StreamTransferProgress -= onProgress;
        }
    }

    private async Task PutMultipartAsync(
        string key,
        Stream input,
        string contentType,
        long sourceStart,
        long sourceLength,
        string sourceHash,
        CancellationToken cancellationToken,
        bool forceNew = false)
    {
        var statePath = GetMultipartStatePath(key);
        var state = forceNew ? null : TryReadMultipartState(statePath);
        if (state is not null
            && (string.IsNullOrWhiteSpace(state.UploadId)
                || !string.Equals(state.Key, key, StringComparison.Ordinal)
                || state.Length != sourceLength
                || !state.SourceSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase)))
        {
            await TryAbortMultipartAsync(key, state.UploadId, cancellationToken);
            DeleteMultipartStateFile(key);
            state = null;
        }
        if (state is not null) state.Parts ??= [];

        state ??= new MultipartUploadState
        {
            Key = key,
            UploadId = (await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                ContentType = contentType
            }, cancellationToken)).UploadId,
            Length = sourceLength,
            SourceSha256 = sourceHash
        };
        await SaveMultipartStateAsync(statePath, state, cancellationToken);

        var partSize = Math.Max(MultipartPartSizeBytes, (sourceLength + 9_999) / 10_000);
        var partCount = (int)((sourceLength + partSize - 1) / partSize);
        for (var partNumber = 1; partNumber <= partCount; partNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partOffset = (long)(partNumber - 1) * partSize;
            var length = Math.Min(partSize, sourceLength - partOffset);
            if (state.Parts.TryGetValue(partNumber, out var previous)
                && previous.Length == length
                && !string.IsNullOrWhiteSpace(previous.ETag))
                continue;

            input.Position = sourceStart + partOffset;
            using var partStream = new SegmentReadStream(input, length);
            using var timeout = CreateTimeout(cancellationToken);
            var request = new UploadPartRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                UploadId = state.UploadId,
                PartNumber = partNumber,
                PartSize = length,
                InputStream = partStream
            };
            EventHandler<Amazon.Runtime.StreamTransferProgressArgs> onProgress = (_, _) =>
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            request.StreamTransferProgress += onProgress;
            try
            {
                var response = await client.UploadPartAsync(request, timeout.Token);
                state.Parts[partNumber] = new MultipartPartState(response.ETag ?? string.Empty, length);
                await SaveMultipartStateAsync(statePath, state, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"S3 第 {partNumber}/{partCount} 个分片在 {settings.TimeoutSeconds} 秒内没有传输进度。",
                    exception);
            }
            finally
            {
                request.StreamTransferProgress -= onProgress;
            }
        }

        var completedParts = Enumerable.Range(1, partCount)
            .Select(number => new PartETag(number, state.Parts[number].ETag))
            .ToList();
        using var completionTimeout = CreateTimeout(cancellationToken);
        try
        {
            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                UploadId = state.UploadId,
                PartETags = completedParts
            }, completionTimeout.Token);
            DeleteMultipartStateFile(key);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"S3 完成分片上传在 {settings.TimeoutSeconds} 秒内无响应。", exception);
        }
    }

    private static MultipartUploadState? TryReadMultipartState(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MultipartUploadState>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            TryDeleteFile(path);
            return null;
        }
    }

    private static async Task SaveMultipartStateAsync(
        string path,
        MultipartUploadState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private async Task TryAbortMultipartAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) return;
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                UploadId = uploadId
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is AmazonS3Exception or OperationCanceledException or HttpRequestException)
        {
            // A new upload can still proceed if the stale multipart session expired.
        }
    }

    private string GetMultipartStatePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(GetMultipartStateDirectory(), hash + ".json");
    }

    public async Task<int> CleanupIncompleteMultipartUploadsAsync(
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        var directory = GetMultipartStateDirectory();
        if (!Directory.Exists(directory)) return 0;
        var staleBefore = DateTime.UtcNow - retention;
        var cleaned = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { continue; }
            if (lastWrite > staleBefore) continue;
            var state = TryReadMultipartState(path);
            if (state is null)
            {
                TryDeleteFile(path);
                continue;
            }
            if (string.IsNullOrWhiteSpace(state.Key)
                || string.IsNullOrWhiteSpace(state.UploadId)
                || state.Key.StartsWith("/", StringComparison.Ordinal)
                || state.Key.Split('/').Any(segment => segment is "." or ".."))
                continue;
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = settings.Bucket,
                    Key = state.Key,
                    UploadId = state.UploadId
                }, cancellationToken);
                TryDeleteFile(path);
                cleaned++;
            }
            catch (AmazonS3Exception exception) when (
                string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(path);
                cleaned++;
            }
            catch (Exception exception) when (exception is AmazonS3Exception or IOException or HttpRequestException or TimeoutException)
            {
                // A completed/expired upload may already have disappeared;
                // keep its record so a later cleanup can retry safely.
            }
        }
        return cleaned;
    }

    private string GetMultipartStateDirectory()
    {
        var identity = $"{settings.Endpoint}|{settings.Region}|{settings.Bucket}|{settings.Prefix}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "Kkindle", "s3-multipart", hash);
    }

    private void DeleteMultipartStateFile(string key) => TryDeleteFile(GetMultipartStatePath(key));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class MultipartUploadState
    {
        public string Key { get; set; } = string.Empty;
        public string UploadId { get; set; } = string.Empty;
        public long Length { get; set; }
        public string SourceSha256 { get; set; } = string.Empty;
        public Dictionary<int, MultipartPartState> Parts { get; set; } = [];
    }

    private sealed record MultipartPartState(string ETag, long Length);

    private sealed class SegmentReadStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = settings.Bucket,
                Key = key
            }, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"S3 删除对象在 {settings.TimeoutSeconds} 秒内无响应。", exception);
        }
    }

    // S3 retains its existing read-only connection test; WebDAV additionally
    // probes its directory creation, publication and cleanup capabilities.
    public Task TestWriteAsync(string prefix, CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => client.Dispose();

    private static bool IsMissing(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
        || MissingCodes.Contains(exception.ErrorCode ?? string.Empty);

    private static AmazonS3Client CreateClient(S3SyncSettings settings)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.PathStyle,
            // SDK's timeout is a whole-request deadline. Individual object
            // operations use the idle-progress timer above/below, so large
            // books can transfer as long as bytes continue to move.
            Timeout = Timeout.InfiniteTimeSpan,
            AuthenticationRegion = settings.Region,
            RequestChecksumCalculation = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? RequestChecksumCalculation.WHEN_SUPPORTED : RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? ResponseChecksumValidation.WHEN_SUPPORTED : ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        else
            config.ServiceURL = settings.Endpoint;
        if (settings.SkipTlsVerify) config.HttpClientFactory = new InsecureHttpClientFactory();
        return new AmazonS3Client(settings.AccessKey, settings.SecretKey, config);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        return timeout;
    }

    private sealed class IdleTimeoutReadStream(
        Stream inner,
        CancellationToken requestToken,
        TimeSpan idleTimeout) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(requestToken, cancellationToken);
            idle.CancelAfter(idleTimeout);
            try
            {
                return await inner.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException exception)
                when (!requestToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"S3 下载在 {idleTimeout.TotalSeconds:0} 秒内没有接收到数据。", exception);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InsecureHttpClientFactory : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        }, disposeHandler: true);
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
