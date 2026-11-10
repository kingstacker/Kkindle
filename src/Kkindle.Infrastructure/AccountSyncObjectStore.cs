using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Object operations for the regional account server; no token is stored in sync settings. The server only
/// authorises and indexes: object bytes go directly to and from its storage service (for example Qiniu Kodo)
/// through short-lived signed URLs, which never receive the account session.
/// </summary>
internal sealed class AccountSyncObjectStore : ISyncObjectStore
{
    private readonly AccountService _accounts;
    private readonly S3SyncSettings _settings;
    private readonly Uri _server;
    private readonly Uri _root;
    private readonly HttpClient _http;
    private readonly HttpClient _direct;

    public AccountSyncObjectStore(S3SyncSettings settings, AccountService accounts, HttpMessageHandler? handler = null)
    {
        _settings = S3SyncSettings.Normalize(settings);
        if (_settings.Validate() is { } invalid) throw new InvalidOperationException(invalid);
        _accounts = accounts;
        _server = AccountValidation.ServerUri(_settings.AccountServerUrl);
        _root = new Uri(_server, "api/sync/objects");
        _http = new HttpClient(handler ?? NewHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
        // Signed URLs carry their own authorisation; this client never sees the session or cookies.
        _direct = new HttpClient(handler ?? NewHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private HttpClientHandler NewHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = _settings.ConcurrentRequests
    };

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken) =>
        (await ListObjectsAsync(prefix, cancellationToken)).Select(item => item.Key).ToArray();

    public async Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        ValidateKey(prefix);
        using var timeout = Deadline(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, _root.AbsoluteUri + "?prefix=" + Uri.EscapeDataString(prefix));
        using var response = await SendAsync(request, timeout.Token);
        EnsureSuccess(response);
        try
        {
            await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024).WaitAsync(timeout.Token);
            var values = await response.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSyncObjectMetadataArray, timeout.Token)
                ?? throw new InvalidDataException("账号同步服务返回了无效的对象列表。");
            if (values.Length > 200_000) throw new InvalidDataException("同步对象列表过大。");
            foreach (var item in values)
            {
                ValidateKey(item.Key);
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("账号同步服务返回了目录外的对象。");
            }
            return values.Select(item => new SyncObjectMetadata(item.Key, item.LastModified)).ToArray();
        }
        catch (System.Text.Json.JsonException)
        { throw new InvalidDataException("账号同步服务返回了无效的对象列表。"); }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = Deadline(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Head, ObjectUri(key));
        using var response = await SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        EnsureSuccess(response);
        return true;
    }

    public async Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken, long rangeStart = 0)
    {
        var grant = await AuthorizeDownloadAsync(key, rangeStart, cancellationToken);
        // The object shrank since the partial copy was written; start the download again.
        if (grant is null) return await OpenReadAsync(key, cancellationToken);
        var timeout = Deadline(cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, grant.Url);
            if (grant.Range is { } range) request.Headers.TryAddWithoutValidation("Range", range);
            response = await SendDirectAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SyncObjectNotFoundException("远端同步对象不存在。");
            var partial = response.StatusCode == HttpStatusCode.PartialContent;
            if (grant.Range is null ? response.StatusCode != HttpStatusCode.OK : !partial) throw DirectFailure(response);
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return new SyncObjectRead(new TimedReadStream(stream, timeout, TimeSpan.FromSeconds(_settings.TimeoutSeconds)),
                response.Content.Headers.ContentLength ?? grant.Length, new DownloadLease(response, timeout), partial,
                grant.TotalLength);
        }
        catch { response?.Dispose(); timeout.Dispose(); throw; }
    }

    private async Task<SyncDownloadGrant?> AuthorizeDownloadAsync(string key, long rangeStart, CancellationToken cancellationToken)
    {
        using var timeout = Deadline(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, Api("downloads"))
        {
            Content = JsonContent.Create(new SyncDownloadRequest { Key = Checked(key), Offset = Math.Max(0, rangeStart) },
                AccountJsonContext.Default.SyncDownloadRequest)
        };
        using var response = await SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new SyncObjectNotFoundException("远端同步对象不存在。");
        if (rangeStart > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return null;
        EnsureSuccess(response);
        var grant = await ReadAsync(response, AccountJsonContext.Default.SyncDownloadGrant, timeout.Token);
        if (grant.Offset != Math.Max(0, rangeStart) || grant.Length < 0 || grant.TotalLength < grant.Length)
            throw new InvalidDataException("账号同步服务返回了无效的下载授权。");
        return grant with { Url = DirectUri(grant.Url) };
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken, string? knownSha256 = null)
    {
        if (!input.CanSeek)
        {
            // Signed uploads must declare their exact length, so spool unknown-length input first.
            var spool = Path.Combine(Path.GetTempPath(), "kkindle-upload-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                await using var file = new FileStream(spool, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await input.CopyToAsync(file, cancellationToken);
                file.Position = 0;
                await PutAsync(key, file, contentType, cancellationToken, knownSha256);
                return;
            }
            finally { try { File.Delete(spool); } catch (IOException) { } }
        }
        var length = input.Length - input.Position;
        SyncUploadGrant grant;
        using (var timeout = Deadline(cancellationToken))
        using (var request = new HttpRequestMessage(HttpMethod.Post, Api("uploads"))
        {
            Content = JsonContent.Create(new SyncUploadRequest { Key = Checked(key), Length = length }, AccountJsonContext.Default.SyncUploadRequest)
        })
        using (var response = await SendAsync(request, timeout.Token))
        {
            EnsureSuccess(response);
            grant = await ReadAsync(response, AccountJsonContext.Default.SyncUploadGrant, timeout.Token);
        }
        if (grant.UploadId == Guid.Empty) throw new InvalidDataException("账号同步服务返回了无效的上传授权。");
        var url = DirectUri(grant.Url);
        var completed = false;
        try
        {
            using (var timeout = Deadline(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Put, url)
            { Content = new UploadContent(input, timeout, TimeSpan.FromSeconds(_settings.TimeoutSeconds), length) })
            using (var response = await SendDirectAsync(request, timeout.Token))
                if (!response.IsSuccessStatusCode) throw DirectFailure(response);
            using (var timeout = Deadline(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Post, Api($"uploads/{grant.UploadId}/complete")))
            using (var response = await SendAsync(request, timeout.Token))
                EnsureSuccess(response);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                // Release the reservation so a retry is not refused for space it already holds.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, Api($"uploads/{grant.UploadId}"));
                    using var _ = await SendAsync(request, cleanup.Token);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException) { }
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = Deadline(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUri(key));
        using var response = await SendAsync(request, timeout.Token);
        if (response.StatusCode != HttpStatusCode.NotFound) EnsureSuccess(response);
    }

    public async Task TestWriteAsync(string prefix, CancellationToken cancellationToken)
    {
        var key = prefix.TrimEnd('/') + "/.kkindle-connection-" + Guid.NewGuid().ToString("N") + ".bin";
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            await PutAsync(key, input, "application/octet-stream", cancellationToken);
            using var response = await OpenReadAsync(key, cancellationToken);
            var actual = new byte[bytes.Length];
            await response.ResponseStream.ReadExactlyAsync(actual, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(bytes, actual)
                || await response.ResponseStream.ReadAsync(new byte[1], cancellationToken) != 0)
                throw new InvalidDataException("账号同步测试文件校验失败。");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await DeleteAsync(key, cleanup.Token); }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException) { }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        await _accounts.AuthorizeSyncRequestAsync(request, _settings, token);
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new IOException("无法连接账号同步服务，请检查服务地址和网络。"); }
    }

    private async Task<HttpResponseMessage> SendDirectAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await _direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new IOException("无法连接云存储，请检查网络后重试。"); }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken token)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(64 * 1024).WaitAsync(token);
            return await response.Content.ReadFromJsonAsync(type, token) ?? throw new InvalidDataException("账号同步服务返回了无效的授权。");
        }
        catch (System.Text.Json.JsonException) { throw new InvalidDataException("账号同步服务返回了无效的授权。"); }
    }

    /// <summary>Signed URLs must use HTTPS; plain HTTP is accepted only for loopback development servers.</summary>
    private static Uri DirectUri(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || url.UserInfo.Length != 0 || url.Fragment.Length != 0
            || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && (url.IsLoopback || url.Host == "10.0.2.2"))))
            throw new InvalidDataException("账号同步服务返回了无效的传输地址。");
        return url;
    }

    private static Exception DirectFailure(HttpResponseMessage response)
    {
        if (response.Headers.Contains("X-Kkindle-Error"))
        {
            try { EnsureSuccess(response); } catch (Exception mapped) { return mapped; }
        }
        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden => new IOException("云存储拒绝了同步链接，可能已过期，请重新同步。"),
            HttpStatusCode.NotFound => new SyncObjectNotFoundException("远端同步对象不存在。"),
            _ when (int)response.StatusCode >= 500 => new IOException("云存储暂时不可用，请稍后重试。"),
            _ => new InvalidOperationException("云存储拒绝了同步请求，请稍后重试。")
        };
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent) return;
        var error = response.Headers.TryGetValues("X-Kkindle-Error", out var values) ? values.FirstOrDefault() : null;
        if (error is "too_many_uploads" or "upload_incomplete" or "upload_not_found" or "upload_length_mismatch" or "upload_copy_mismatch"
            or "upload_rate_limited" or "transfer_expired")
            throw new IOException(error switch
            {
                "too_many_uploads" => "同时进行的上传过多，请稍后重试。",
                "upload_length_mismatch" => "上传数据不完整，请重新同步。",
                "upload_copy_mismatch" => "云存储发布失败，请重新同步。",
                "upload_rate_limited" => "上传请求过于频繁，请稍后重试。",
                _ => "同步链接已失效，请重新同步。"
            });
        if (error is "plus_required" or "pro_required") throw new DeviceFeatureRequiredException(MemberEntitlements.AccountCloudRequirement);
        if (error is "account_banned" or "traffic_quota_exceeded" or "storage_quota_exceeded" or "encryption_required" or "data_unavailable"
            or "client_update_required")
            throw new InvalidOperationException(error switch
            {
                "account_banned" => "此账号已被封禁，请联系管理员。",
                "traffic_quota_exceeded" => "本会员年度下载额度与额外下载余额已用尽。请等待下一会员年度开始；提前续费不会增加本年度额度。",
                "storage_quota_exceeded" => "Kkindle 云空间已满，请清理不再需要的云数据。",
                "encryption_required" => "Kkindle 云同步必须设置同步密钥，请先打开同步设置。",
                "client_update_required" => "账号服务已升级，请更新 Kkindle 后继续同步。",
                _ => "此账号的云数据正在处理，请联系管理员。"
            });
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "账号会话已失效，请重新登录当前区域。",
            HttpStatusCode.Forbidden => "账号服务区域不匹配，请检查所选区域和服务地址。",
            HttpStatusCode.RequestEntityTooLarge => "同步文件超过服务器大小限制。",
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "账号服务版本过旧，暂不支持当前同步协议。",
            _ when (int)response.StatusCode is >= 300 and < 400 => "请填写最终的账号服务地址，账号同步不接受重定向。",
            _ => "账号同步服务暂时不可用，请稍后重试。"
        });
    }

    private Uri Api(string path) => new(_server, "api/sync/" + path);

    private static string Checked(string key) { ValidateKey(key); return key; }

    private Uri ObjectUri(string key)
    {
        ValidateKey(key);
        return new Uri(_root.AbsoluteUri + "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString)));
    }

    private static void ValidateKey(string key)
    {
        if (key.Length > 1024 || key.StartsWith('/') || key.Contains('\\') || key.Any(char.IsControl)
            || key.Split('/').Any(part => part is "." or "..") || key.Contains("//", StringComparison.Ordinal))
            throw new InvalidDataException("无效的同步对象路径。");
    }

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        return deadline;
    }

    public void Dispose() { _http.Dispose(); _direct.Dispose(); }

    private sealed class DownloadLease(HttpResponseMessage response, CancellationTokenSource timeout) : IDisposable
    {
        public void Dispose() { response.Dispose(); timeout.Dispose(); }
    }

    private sealed class UploadContent(Stream input, CancellationTokenSource timeout, TimeSpan idleTimeout, long declared) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = declared; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, timeout.Token);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => CopyAsync(stream, token);
        private async Task CopyAsync(Stream output, CancellationToken token)
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                timeout.CancelAfter(idleTimeout);
            }
        }
    }

    private sealed class TimedReadStream(Stream inner, CancellationTokenSource timeout, TimeSpan idleTimeout) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            timeout.CancelAfter(idleTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            return await inner.ReadAsync(buffer, linked.Token);
        }
    }
}
