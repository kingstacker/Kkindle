using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Kkindle.Core;

namespace Kkindle.Tests;

/// <summary>An independent, small WebDAV server for handler and real HTTP tests.</summary>
internal sealed class WebDavTestServer
{
    public const string Username = "reader@example.test";
    public const string Password = " app:password 中文 ";
    public const string RootPath = "/dav/书库 space#%";
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, byte> Collections { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<DavRequest> Requests { get; } = new();
    public Func<DavRequest, CancellationToken, Task<HttpResponseMessage?>>? BeforeRequest { get; set; }
    public bool AbsoluteHrefs { get; set; }
    public bool RelativeHrefs { get; set; }

    public WebDavTestServer() => Collections[RootPath] = 0;

    public S3SyncSettings Settings(string origin = "https://dav.example.test") => new()
    {
        Provider = SyncProvider.WebDav, Enabled = true, AutomaticSyncEnabled = false,
        WebDavEndpoint = origin + EncodePath(RootPath),
        WebDavUsername = Username, WebDavPassword = Password,
        Prefix = "阅读 同步/#%", ConcurrentRequests = 4, TimeoutSeconds = 10
    };

    public string PathFor(string key) => RootPath + "/" + key.TrimEnd('/');
    public HttpMessageHandler Handler() => new Adapter(this);
    public Loopback Listen() => new(this);
    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var declaredLength = request.Content?.Headers.ContentLength;
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var captured = new DavRequest(request.Method.Method, request.RequestUri!,
            request.Headers.Authorization?.ToString(), Header(request, "Depth"), Header(request, "Destination"),
            Header(request, "Overwrite"), bytes, declaredLength, request.Content?.Headers.ContentType?.MediaType);
        Requests.Enqueue(captured);
        cancellationToken.ThrowIfCancellationRequested();
        if (captured.Authorization != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password)))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (BeforeRequest is { } before && await before(captured, cancellationToken) is { } intercepted)
            return intercepted;
        var path = Uri.UnescapeDataString(captured.Uri.AbsolutePath).TrimEnd('/');
        if (path != RootPath && !path.StartsWith(RootPath + "/", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        switch (captured.Method)
        {
            case "PROPFIND":
                if (captured.Depth is not ("0" or "1")) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (!Collections.ContainsKey(path) && !Files.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                XNamespace dav = "DAV:";
                var children = captured.Depth == "1"
                    ? Collections.Keys.Concat(Files.Keys).Where(child => Parent(child) == path).ToArray() : [];
                var listing = new XDocument(new XElement(dav + "multistatus",
                    new XAttribute(XNamespace.Xmlns + "D", dav.NamespaceName),
                    children.Prepend(path).Distinct().Select(child =>
                    {
                        var directory = Collections.ContainsKey(child);
                        var href = EncodePath(child) + (directory ? "/" : string.Empty);
                        if (AbsoluteHrefs) href = captured.Uri.GetLeftPart(UriPartial.Authority) + href;
                        else if (RelativeHrefs) href = child == path ? "./" : Uri.EscapeDataString(child[(path.Length + 1)..]) + (directory ? "/" : string.Empty);
                        return new XElement(dav + "response",
                            new XElement(dav + "href", href),
                            new XElement(dav + "propstat",
                                new XElement(dav + "prop", new XElement(dav + "resourcetype", directory ? new XElement(dav + "collection") : null)),
                                new XElement(dav + "status", "HTTP/1.1 200 OK")));
                    })));
                return XmlResponse(listing.ToString());
            case "MKCOL":
                if (Collections.ContainsKey(path) || Files.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                if (!Collections.ContainsKey(Parent(path))) return new HttpResponseMessage(HttpStatusCode.Conflict);
                Collections[path] = 0;
                return new HttpResponseMessage(HttpStatusCode.Created);
            case "PUT":
                if (!Collections.ContainsKey(Parent(path))) return new HttpResponseMessage(HttpStatusCode.Conflict);
                if (declaredLength != bytes.LongLength || string.IsNullOrWhiteSpace(captured.ContentType))
                    return new HttpResponseMessage(HttpStatusCode.LengthRequired);
                Files[path] = bytes;
                return new HttpResponseMessage(HttpStatusCode.Created);
            case "MOVE":
                var destination = new Uri(captured.Destination!);
                if (destination.GetLeftPart(UriPartial.Authority) != captured.Uri.GetLeftPart(UriPartial.Authority))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var target = Uri.UnescapeDataString(destination.AbsolutePath);
                if (!Collections.ContainsKey(Parent(target))) return new HttpResponseMessage(HttpStatusCode.Conflict);
                if (Files.ContainsKey(target) && captured.Overwrite != "T") return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                if (!Files.TryRemove(path, out var moved)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                Files[target] = moved;
                return new HttpResponseMessage(HttpStatusCode.Created);
            case "GET":
            case "HEAD":
                if (!Files.TryGetValue(path, out var content)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(captured.Method == "HEAD" ? [] : content) };
                response.Content.Headers.ContentLength = content.Length;
                return response;
            case "DELETE":
                return new HttpResponseMessage(Files.TryRemove(path, out _) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private static string Parent(string path) => path[..path.LastIndexOf('/')];
    private static string? Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    public static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    internal sealed record DavRequest(string Method, Uri Uri, string? Authorization, string? Depth,
        string? Destination, string? Overwrite, byte[] Body, long? ContentLength, string? ContentType)
    {
        public string Path => Uri.UnescapeDataString(Uri.AbsolutePath).TrimEnd('/');
    }

    private sealed class Adapter(WebDavTestServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            server.RespondAsync(request, cancellationToken);
    }

    /// <summary>HTTP/1.1 loopback bridge, without URL ACLs or external services.</summary>
    internal sealed class Loopback : IAsyncDisposable
    {
        private readonly WebDavTestServer _server;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _accept;
        public string Origin { get; }

        public Loopback(WebDavTestServer server)
        {
            _server = server;
            _listener.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var headerBytes = new MemoryStream();
                    var one = new byte[1];
                    var ending = 0;
                    while (ending != 0x0d0a0d0a)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        headerBytes.WriteByte(one[0]);
                        ending = (ending << 8) | one[0];
                        if (headerBytes.Length > 64 * 1024) throw new InvalidDataException("HTTP headers too large.");
                    }
                    var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var first = lines[0].Split(' ');
                    using var request = new HttpRequestMessage(new HttpMethod(first[0]), new Uri(Origin + first[1]));
                    var headers = lines.Skip(1).Select(line => line.Split(':', 2))
                        .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    var length = headers.TryGetValue("Content-Length", out var size) ? int.Parse(size) : 0;
                    if (length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Invalid HTTP body size.");
                    if (headers.GetValueOrDefault("Expect") == "100-continue")
                        await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), _stop.Token);
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    request.Content = new ByteArrayContent(body);
                    foreach (var (name, value) in headers)
                        if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content.Headers.TryAddWithoutValidation(name, value);
                    using var response = await _server.RespondAsync(request, _stop.Token);
                    var bytes = response.Content is null ? [] : await response.Content.ReadAsByteArrayAsync(_stop.Token);
                    var actualLength = response.Content?.Headers.ContentLength ?? bytes.Length;
                    var extraHeaders = string.Concat(response.Headers.Select(header => header.Key + ": " + string.Join(", ", header.Value) + "\r\n"));
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\nContent-Length: {actualLength}\r\nConnection: close\r\nContent-Type: {response.Content?.Headers.ContentType?.ToString() ?? "application/octet-stream"}\r\n{extraHeaders}\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    if (first[0] != "HEAD") await stream.WriteAsync(bytes, _stop.Token);
                }
                catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or IOException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accept;
            _listener.Stop();
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
