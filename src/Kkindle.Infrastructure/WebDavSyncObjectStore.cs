using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// WebDAV object storage using Depth: 1 listings (no Depth: infinity), MKCOL,
/// streamed transfers and MOVE publication. A failed upload never replaces a
/// previously published snapshot. Credentials are never sent to redirects or
/// to URLs supplied by an untrusted directory listing.
/// </summary>
internal sealed class WebDavSyncObjectStore : ISyncObjectStore
{
    private static readonly XNamespace Dav = "DAV:";
    private const string PropertiesXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>";
    private readonly S3SyncSettings _settings;
    private readonly Uri _root;
    private readonly string _rootPath;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _directoryGate = new(1, 1);
    private readonly HashSet<string> _knownCollections = new(StringComparer.Ordinal);
    private bool _endpointChecked;

    public WebDavSyncObjectStore(S3SyncSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = S3SyncSettings.Normalize(settings);
        var validation = (_settings with { Provider = SyncProvider.WebDav }).Validate();
        if (validation is not null) throw new InvalidOperationException(UiText.Localize(validation));
        _root = new Uri(_settings.WebDavEndpoint + "/", UriKind.Absolute);
        _rootPath = Uri.UnescapeDataString(_root.AbsolutePath);
        _client = new HttpClient(handler ?? CreateHandler(_settings), disposeHandler: true)
        {
            // Each operation owns a timeout covering headers AND body reads.
            Timeout = Timeout.InfiniteTimeSpan
        };
        if (_settings.WebDavUsername.Length > 0)
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(_settings.WebDavUsername + ":" + _settings.WebDavPassword)));
    }

    private static HttpClientHandler CreateHandler(S3SyncSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = settings.ConcurrentRequests
        };
        if (settings.SkipTlsVerify)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return handler;
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        await EnsureEndpointAsync(cancellationToken);
        try
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(prefix.TrimEnd('/'));
            while (queue.TryDequeue(out var collection))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(collection)) continue;
                if (seen.Count > 10_000) throw InvalidListing();
                var entries = await ReadCollectionAsync(collection, depthOne: true, allowMissing: true, cancellationToken);
                if (entries is null) continue; // The sync subdirectory may not exist yet.
                foreach (var entry in entries)
                {
                    if (entry.Key == collection) continue;
                    if (entry.IsCollection) queue.Enqueue(entry.Key);
                    else result.Add(entry.Key);
                    if (result.Count > 200_000) throw InvalidListing();
                }
            }
            return result.ToArray();
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            // Blob enumeration is an optimization; the engine can use HEAD
            // when a server grants object access but denies a directory list.
            throw new SyncListingUnavailableException(exception.Message, exception);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => WithTimeoutAsync(async token =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, ObjectUri(key));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            using var fallback = new HttpRequestMessage(HttpMethod.Get, ObjectUri(key));
            using var read = await _client.SendAsync(fallback, HttpCompletionOption.ResponseHeadersRead, token);
            if (read.StatusCode == HttpStatusCode.NotFound) return false;
            EnsureSuccess(read, "GET");
            return true;
        }
        EnsureSuccess(response, "HEAD");
        return true;
    }, cancellationToken);

    public async Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var timeout = CreateTimeout(cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(key));
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SyncObjectNotFoundException(UiText.Get("远端同步对象不存在。"));
            EnsureSuccess(response, "GET");
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return new SyncObjectRead(new TimedReadStream(stream, timeout.Token, cancellationToken),
                response.Content.Headers.ContentLength ?? -1, new DownloadLease(response, timeout));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            timeout.Dispose();
            throw RequestTimeout(exception);
        }
        catch
        {
            response?.Dispose();
            timeout.Dispose();
            throw;
        }
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken)
    {
        await EnsureParentAsync(key, cancellationToken);
        var slash = key.LastIndexOf('/');
        var temporaryKey = (slash < 0 ? string.Empty : key[..(slash + 1)]) + $".kkindle-upload-{Guid.NewGuid():N}.tmp";
        var published = false;
        try
        {
            await WithTimeoutAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(temporaryKey))
                {
                    Content = new UploadContent(input, contentType)
                };
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                EnsureSuccess(response, "PUT");
                return true;
            }, cancellationToken);

            await WithTimeoutAsync(async token =>
            {
                using var request = new HttpRequestMessage(new HttpMethod("MOVE"), ObjectUri(temporaryKey));
                request.Headers.TryAddWithoutValidation("Destination", ObjectUri(key).AbsoluteUri);
                request.Headers.TryAddWithoutValidation("Overwrite", "T");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                EnsureSuccess(response, "MOVE");
                return true;
            }, cancellationToken);
            published = true;
        }
        finally
        {
            if (!published) await TryDeleteTemporaryAsync(temporaryKey);
        }
    }

    public async Task TestWriteAsync(string prefix, CancellationToken cancellationToken)
    {
        var key = $"{prefix}/.kkindle-connection-{Guid.NewGuid():N}.bin";
        var bytes = RandomNumberGenerator.GetBytes(32);
        var cleaned = false;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            await PutAsync(key, input, "application/octet-stream", cancellationToken);
            using (var response = await OpenReadAsync(key, cancellationToken))
            {
                var downloaded = new byte[bytes.Length];
                await response.ResponseStream.ReadExactlyAsync(downloaded, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(bytes, downloaded)
                    || await response.ResponseStream.ReadAsync(new byte[1], cancellationToken) != 0)
                    throw new InvalidDataException(UiText.Get("WebDAV 测试文件校验失败。"));
            }
            await DeleteAsync(key, cancellationToken);
            cleaned = true;
        }
        finally
        {
            if (!cleaned) await TryDeleteTemporaryAsync(key);
        }
    }

    private async Task EnsureEndpointAsync(CancellationToken cancellationToken)
    {
        await _directoryGate.WaitAsync(cancellationToken);
        try
        {
            if (_endpointChecked) return;
            await ReadCollectionAsync(string.Empty, depthOne: false, allowMissing: false, cancellationToken);
            _endpointChecked = true;
            _knownCollections.Add(string.Empty);
        }
        finally { _directoryGate.Release(); }
    }

    private async Task EnsureParentAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await EnsureEndpointAsync(cancellationToken);
        await _directoryGate.WaitAsync(cancellationToken);
        try
        {
            var parts = key.Split('/');
            var collection = string.Empty;
            foreach (var part in parts[..^1])
            {
                collection = collection.Length == 0 ? part : collection + "/" + part;
                if (_knownCollections.Contains(collection)) continue;
                var status = await WithTimeoutAsync(async token =>
                {
                    using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), ObjectUri(collection + "/"));
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict))
                        EnsureSuccess(response, "MKCOL");
                    return response.StatusCode;
                }, cancellationToken);
                if (status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
                {
                    // 405 can mean either "already exists" or "MKCOL is
                    // disabled". Verify a collection instead of assuming it.
                    if (await ReadCollectionAsync(collection, false, true, cancellationToken) is null)
                        throw RequestFailure("MKCOL", status);
                }
                _knownCollections.Add(collection);
            }
        }
        finally { _directoryGate.Release(); }
    }

    private Task<IReadOnlyList<DavEntry>?> ReadCollectionAsync(
        string key, bool depthOne, bool allowMissing, CancellationToken cancellationToken) =>
        WithTimeoutAsync<IReadOnlyList<DavEntry>?>(async token =>
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), ObjectUri(key.Length == 0 ? key : key + "/"))
            {
                Content = new StringContent(PropertiesXml, Encoding.UTF8, "application/xml")
            };
            request.Headers.TryAddWithoutValidation("Depth", depthOne ? "1" : "0");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode != HttpStatusCode.MultiStatus)
            {
                EnsureSuccess(response, "PROPFIND");
                throw InvalidListing();
            }
            using var reader = XmlReader.Create(new TimedReadStream(
                await response.Content.ReadAsStreamAsync(token), token, cancellationToken), new XmlReaderSettings
            {
                Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024
            });
            XDocument document;
            try { document = await XDocument.LoadAsync(reader, LoadOptions.None, token); }
            catch (XmlException exception) { throw new InvalidDataException(UiText.Get("WebDAV 文件列表无效，请检查服务地址。"), exception); }
            if (document.Root?.Name != Dav + "multistatus") throw InvalidListing();

            var result = new List<DavEntry>();
            var foundSelf = false;
            var prefix = key.Length == 0 ? string.Empty : key + "/";
            foreach (var item in document.Root.Elements(Dav + "response"))
            {
                var href = item.Element(Dav + "href")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(href)) throw InvalidListing();
                var entryKey = KeyFromHref(request.RequestUri!, href);
                // Depth: 1 must contain only this collection and its direct
                // children. Reject links outside it before issuing requests.
                if (entryKey != key && (!depthOne || !entryKey.StartsWith(prefix, StringComparison.Ordinal)
                    || entryKey[prefix.Length..].Contains('/')))
                    throw InvalidListing();
                if (item.Element(Dav + "status") is { } status)
                {
                    var code = StatusCode(status.Value);
                    if (code is < 200 or >= 300) throw RequestFailure("PROPFIND", (HttpStatusCode)code);
                }
                var type = item.Elements(Dav + "propstat")
                    .Where(prop => StatusCode(prop.Element(Dav + "status")?.Value) is >= 200 and < 300)
                    .Select(prop => prop.Element(Dav + "prop")?.Element(Dav + "resourcetype"))
                    .FirstOrDefault(prop => prop is not null);
                if (type is null) throw InvalidListing();
                var isCollection = type.Element(Dav + "collection") is not null;
                if (entryKey == key)
                {
                    if (!isCollection) throw InvalidListing();
                    foundSelf = true;
                }
                result.Add(new DavEntry(entryKey, isCollection));
            }
            if (!foundSelf) throw InvalidListing();
            return result;
        }, cancellationToken);

    private string KeyFromHref(Uri collectionUri, string href)
    {
        if (!Uri.TryCreate(collectionUri, href, out var uri)
            || !string.Equals(uri.Scheme, _root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.IdnHost, _root.IdnHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _root.Port || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw InvalidListing();
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.TrimEnd('/') == _rootPath.TrimEnd('/')) return string.Empty;
        if (!path.StartsWith(_rootPath, StringComparison.Ordinal)) throw InvalidListing();
        var key = path[_rootPath.Length..].TrimEnd('/');
        ValidateKey(key);
        return key;
    }

    private Uri ObjectUri(string key)
    {
        ValidateKey(key);
        return new Uri(_root.AbsoluteUri + string.Join('/', key.Split('/').Select(Uri.EscapeDataString)), UriKind.Absolute);
    }

    private static void ValidateKey(string key)
    {
        if (key.StartsWith('/') || key.Contains('\\') || key.Any(char.IsControl)
            || key.Split('/').Any(part => part is "." or "..") || key.Contains("//", StringComparison.Ordinal))
            throw InvalidListing();
    }

    private static int StatusCode(string? status)
    {
        var parts = status?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && int.TryParse(parts[1], out var code) && code is >= 100 and <= 599
            ? code : throw InvalidListing();
    }

    private Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => WithTimeoutAsync(async token =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUri(key));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.NotFound) EnsureSuccess(response, "DELETE");
        return true;
    }, cancellationToken);

    private async Task TryDeleteTemporaryAsync(string key)
    {
        // Cleanup only the unique file created by this operation, even when
        // the user has cancelled. A cleanup failure must not hide the cause.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await DeleteAsync(key, cleanup.Token); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or TimeoutException) { }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        return timeout;
    }

    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        try { return await action(timeout.Token); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw RequestTimeout(exception); }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string method)
    {
        // 202 is only queued work; 207 can contain per-resource failures.
        // Neither guarantees that a snapshot was published in full.
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent))
            throw RequestFailure(method, response.StatusCode);
    }

    private static HttpRequestException RequestFailure(string method, HttpStatusCode status)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => UiText.Get("请检查 WebDAV 用户名和密码，部分服务需要应用密码。"),
            HttpStatusCode.Forbidden => UiText.Get("请检查 WebDAV 目录的读写权限。"),
            HttpStatusCode.NotFound => UiText.Get("请确认 WebDAV 服务地址指向已存在的目录。"),
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => UiText.Get("服务端需要支持 PROPFIND、MKCOL、GET、PUT、MOVE 和 DELETE。"),
            HttpStatusCode.InsufficientStorage => UiText.Get("WebDAV 存储空间不足。"),
            _ when (int)status is >= 300 and < 400 => UiText.Get("请填写重定向后的最终 WebDAV 服务地址。"),
            _ => UiText.Get("请检查 WebDAV 服务状态和目录权限。")
        };
        return new HttpRequestException(UiText.Get("WebDAV {0} 失败（HTTP {1}）。{2}", method, (int)status, hint), null, status);
    }

    private static InvalidDataException InvalidListing() => new(UiText.Get("WebDAV 文件列表无效，请检查服务地址。"));
    private static TimeoutException RequestTimeout(Exception inner) =>
        new(UiText.Get("WebDAV 请求超时，请检查网络或增大超时设置。"), inner);

    public void Dispose()
    {
        _client.Dispose();
        _directoryGate.Dispose();
    }

    private sealed record DavEntry(string Key, bool IsCollection);

    private sealed class DownloadLease(HttpResponseMessage response, CancellationTokenSource timeout) : IDisposable
    {
        public void Dispose() { response.Dispose(); timeout.Dispose(); }
    }

    private sealed class UploadContent : HttpContent
    {
        private readonly Stream _input;

        public UploadContent(Stream input, string contentType)
        {
            _input = input;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _input.CanSeek ? _input.Length - _input.Position : 0;
            return _input.CanSeek;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _input.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _input.CopyToAsync(stream, cancellationToken);
    }

    private sealed class TimedReadStream(Stream inner, CancellationToken timeoutToken, CancellationToken requestToken) : Stream
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
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken, cancellationToken);
            try { return await inner.ReadAsync(buffer, linked.Token); }
            catch (OperationCanceledException exception) when (!requestToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { throw RequestTimeout(exception); }
        }
    }
}
