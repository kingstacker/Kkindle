using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal sealed class S3SyncObjectStore(S3SyncSettings settings, IAmazonS3 client) : ISyncObjectStore
{
    private static readonly HashSet<string> MissingCodes = new(StringComparer.OrdinalIgnoreCase)
        { "NoSuchKey", "NoSuchObject", "NotFound" };
    private static readonly HashSet<string> ListingUnavailableCodes = new(StringComparer.OrdinalIgnoreCase)
        { "AccessDenied", "Forbidden", "InvalidRequest", "InvalidArgument", "MethodNotAllowed", "NotImplemented" };
    public S3SyncObjectStore(S3SyncSettings settings) : this(settings, CreateClient(settings)) { }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        string? continuation = null;
        try
        {
            do
            {
                var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = settings.Bucket, Prefix = prefix,
                    ContinuationToken = continuation, MaxKeys = 1000
                }, cancellationToken);
                // Empty prefixes can omit Contents on S3-compatible services.
                result.AddRange((response.S3Objects ?? []).Select(item => item.Key)
                    .Where(key => !string.IsNullOrWhiteSpace(key)));
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
        try
        {
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = settings.Bucket, Key = key
            }, cancellationToken);
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

    public async Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = settings.Bucket, Key = key
            }, cancellationToken);
            return new SyncObjectRead(response.ResponseStream, response.ContentLength, response);
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            throw new SyncObjectNotFoundException(exception.Message, exception);
        }
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken)
    {
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
        await client.PutObjectAsync(request, cancellationToken);
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
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
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
