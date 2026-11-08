using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Authenticated object operations for the regional account server; no token is stored in sync settings.</summary>
internal sealed class AccountSyncObjectStore : ISyncObjectStore
{
    private readonly AccountService _accounts;
    private readonly S3SyncSettings _settings;
    private readonly Uri _root;
    private readonly HttpClient _http;

    public AccountSyncObjectStore(S3SyncSettings settings, AccountService accounts, HttpMessageHandler? handler = null)
    {
        _settings = S3SyncSettings.Normalize(settings);
        if (_settings.Validate() is { } invalid) throw new InvalidOperationException(invalid);
        _accounts = accounts;
        _root = new Uri(AccountValidation.ServerUri(_settings.AccountServerUrl), "api/sync/objects");
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = _settings.ConcurrentRequests
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

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
        var timeout = Deadline(cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(key));
            if (rangeStart > 0) request.Headers.Range = new RangeHeaderValue(rangeStart, null);
            response = await SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SyncObjectNotFoundException("远端同步对象不存在。");
            if (rangeStart > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose(); response = null; timeout.Dispose();
                return await OpenReadAsync(key, cancellationToken);
            }
            if (response.StatusCode != HttpStatusCode.PartialContent) EnsureSuccess(response);
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return new SyncObjectRead(new TimedReadStream(stream, timeout, TimeSpan.FromSeconds(_settings.TimeoutSeconds)),
                response.Content.Headers.ContentLength ?? -1, new DownloadLease(response, timeout),
                response.StatusCode == HttpStatusCode.PartialContent,
                response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength);
        }
        catch { response?.Dispose(); timeout.Dispose(); throw; }
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken, string? knownSha256 = null)
    {
        using var timeout = Deadline(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(key))
        { Content = new UploadContent(input, timeout, TimeSpan.FromSeconds(_settings.TimeoutSeconds)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await SendAsync(request, timeout.Token);
        EnsureSuccess(response);
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

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent) return;
        var error = response.Headers.TryGetValues("X-Kkindle-Error", out var values) ? values.FirstOrDefault() : null;
        if (error is "account_banned" or "traffic_quota_exceeded" or "storage_quota_exceeded" or "encryption_required" or "data_unavailable")
            throw new InvalidOperationException(error switch
            {
                "account_banned" => "此账号已被封禁，请联系管理员。",
                "traffic_quota_exceeded" => "本月流量额度不足，请联系管理员。",
                "storage_quota_exceeded" => "云存储空间额度不足，请联系管理员。",
                "encryption_required" => "账号云同步必须设置同步密钥，请先打开同步设置。",
                _ => "此账号的云数据正在处理，请联系管理员。"
            });
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "账号会话已失效，请重新登录当前区域。",
            HttpStatusCode.Forbidden => "账号服务区域不匹配，请检查所选区域和服务地址。",
            HttpStatusCode.RequestEntityTooLarge => "同步文件超过服务器大小限制。",
            _ when (int)response.StatusCode is >= 300 and < 400 => "请填写最终的账号服务地址，账号同步不接受重定向。",
            _ => "账号同步服务暂时不可用，请稍后重试。"
        });
    }

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

    public void Dispose() => _http.Dispose();

    private sealed class DownloadLease(HttpResponseMessage response, CancellationTokenSource timeout) : IDisposable
    {
        public void Dispose() { response.Dispose(); timeout.Dispose(); }
    }

    private sealed class UploadContent(Stream input, CancellationTokenSource timeout, TimeSpan idleTimeout) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        { length = input.CanSeek ? input.Length - input.Position : 0; return input.CanSeek; }
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
