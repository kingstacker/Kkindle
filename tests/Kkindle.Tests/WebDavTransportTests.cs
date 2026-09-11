using System.Net;
using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class WebDavTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CreatesDirectoriesAndListsNestedObjectsWithEscapedPaths(bool absoluteHrefs, bool relativeHrefs)
    {
        var server = new WebDavTestServer { AbsoluteHrefs = absoluteHrefs, RelativeHrefs = relativeHrefs };
        var settings = server.Settings();
        var key = settings.Prefix + "/devices/device/snapshot.bin";
        using var store = new WebDavSyncObjectStore(settings, server.Handler());
        Assert.Empty(await store.ListKeysAsync(settings.Prefix + "/devices/", default));
        using var input = new MemoryStream("test-content"u8.ToArray());
        await store.PutAsync(key, input, "application/octet-stream", default);
        Assert.True(input.CanRead);
        Assert.Equal(key, Assert.Single(await store.ListKeysAsync(settings.Prefix + "/devices/", default)));
        using var read = await store.OpenReadAsync(key, default);
        using var downloaded = new MemoryStream();
        await read.ResponseStream.CopyToAsync(downloaded);
        Assert.Equal("test-content", Encoding.UTF8.GetString(downloaded.ToArray()));
        Assert.All(server.Requests.Where(request => request.Method == "PROPFIND"), request => Assert.Contains(request.Depth, new[] { "0", "1" }));
        Assert.Contains(server.Requests, request => request.Uri.AbsoluteUri.Contains("%23%25", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Files.Keys, path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectionTestUsesRealHttpAndLeavesNoProbeFile()
    {
        var server = new WebDavTestServer();
        await using var listener = server.Listen();
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            // Public constructor verifies actual provider routing and the
            // production HttpClient, not just an injected storage adapter.
            var service = new S3SyncService(new AppPaths(root), new TestHelpers.PlaintextSecretProtector());
            await service.TestConnectionAsync(server.Settings(listener.Origin));
            Assert.Empty(server.Files);
            foreach (var method in new[] { "PROPFIND", "MKCOL", "PUT", "MOVE", "GET", "DELETE" })
                Assert.Contains(server.Requests, request => request.Method == method);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthenticationAndPermissionErrorsAreNotReportedAsMissing(HttpStatusCode status)
    {
        var server = new WebDavTestServer { BeforeRequest = (_, _) => Task.FromResult<HttpResponseMessage?>(new(status)) };
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => store.ExistsAsync("sync/object", default));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("WebDAV", error.Message);
    }

    [Fact]
    public async Task ProductionHandlerDoesNotSendCredentialsToRedirectTargets()
    {
        var server = new WebDavTestServer();
        var other = new WebDavTestServer();
        await using var listener = server.Listen();
        await using var target = other.Listen();
        server.BeforeRequest = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri(other.Settings(target.Origin).WebDavEndpoint + "/");
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        using var store = new WebDavSyncObjectStore(server.Settings(listener.Origin));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => store.ListKeysAsync("sync/devices/", default));
        Assert.Equal(HttpStatusCode.MovedPermanently, error.StatusCode);
        Assert.Single(server.Requests);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task HeadUnsupportedFallsBackToGetWithoutTreatingForbiddenAsAbsent()
    {
        var server = new WebDavTestServer();
        server.Files[server.PathFor("sync/object")] = [1, 2, 3];
        server.BeforeRequest = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Method == "HEAD" ? new(HttpStatusCode.MethodNotAllowed) : null);
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        Assert.True(await store.ExistsAsync("sync/object", default));
        Assert.False(await store.ExistsAsync("sync/missing", default));
        Assert.Equal(2, server.Requests.Count(request => request.Method == "GET"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.MultiStatus)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task FailedPublicationRetainsPreviousSnapshotAndCleansTemporaryFile(HttpStatusCode status)
    {
        var server = new WebDavTestServer();
        var key = server.Settings().Prefix + "/devices/test/snapshot.bin";
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        using var original = new MemoryStream("original-snapshot"u8.ToArray());
        await store.PutAsync(key, original, "application/octet-stream", default);
        server.BeforeRequest = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Method == "MOVE" ? new(status) : null);
        using var changed = new MemoryStream("new-snapshot"u8.ToArray());
        await Assert.ThrowsAsync<HttpRequestException>(() => store.PutAsync(key, changed, "application/octet-stream", default));
        Assert.Equal("original-snapshot", Encoding.UTF8.GetString(server.Files[server.PathFor(key)]));
        Assert.Single(server.Files);
        Assert.True(changed.CanRead);
    }

    [Fact]
    public async Task CancelledUploadDoesNotPublishPartialBytes()
    {
        var server = new WebDavTestServer();
        var key = server.Settings().Prefix + "/devices/test/snapshot.bin";
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        using var original = new MemoryStream("original"u8.ToArray());
        await store.PutAsync(key, original, "application/octet-stream", default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeRequest = async (request, token) =>
        {
            if (request.Method == "PUT")
            {
                server.Files[request.Path] = request.Body[..1];
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return null;
        };
        using var cancellation = new CancellationTokenSource();
        using var input = new MemoryStream("changed"u8.ToArray());
        var upload = store.PutAsync(key, input, "application/octet-stream", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.Equal("original", Encoding.UTF8.GetString(server.Files[server.PathFor(key)]));
        Assert.Single(server.Files);
    }

    [Theory]
    [InlineData("https://unrelated.example.test/dav/foreign/")]
    [InlineData("/outside-root/")]
    [InlineData("../../outside-root/")]
    public async Task DirectoryListingCannotEscapeRemoteRoot(string href)
    {
        var server = new WebDavTestServer();
        server.BeforeRequest = (_, _) => Task.FromResult<HttpResponseMessage?>(WebDavTestServer.XmlResponse($"""
            <D:multistatus xmlns:D="DAV:"><D:response><D:href>{href}</D:href>
            <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
            <D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
            """));
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListKeysAsync("sync/devices/", default));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task XmlDtdIsRejectedAndRedirectDoesNotFollowWithCredentials()
    {
        var server = new WebDavTestServer();
        server.BeforeRequest = (_, _) => Task.FromResult<HttpResponseMessage?>(WebDavTestServer.XmlResponse("""
            <!DOCTYPE multistatus [<!ENTITY data SYSTEM "https://unrelated.example.test/entity">]>
            <multistatus xmlns="DAV:">&data;</multistatus>
            """));
        using (var store = new WebDavSyncObjectStore(server.Settings(), server.Handler()))
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListKeysAsync("sync/devices/", default));
        server.BeforeRequest = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://unrelated.example.test/dav/");
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        using (var store = new WebDavSyncObjectStore(server.Settings(), server.Handler()))
            await Assert.ThrowsAsync<HttpRequestException>(() => store.ListKeysAsync("sync/devices/", default));
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PROPFIND")]
    public async Task CancellationInterruptsBodyReadsAfterHeaders(string method)
    {
        var server = new WebDavTestServer();
        server.BeforeRequest = (_, _) => Task.FromResult<HttpResponseMessage?>(new(method == "GET" ? HttpStatusCode.OK : HttpStatusCode.MultiStatus)
        { Content = new StreamContent(new PendingReadStream()) });
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (method == "PROPFIND") await store.ListKeysAsync("sync/devices/", cancellation.Token);
            else
            {
                using var read = await store.OpenReadAsync("sync/object", cancellation.Token);
                await read.ResponseStream.ReadExactlyAsync(new byte[8], cancellation.Token);
            }
        });
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ConfiguredTimeoutCoversDownloadBody()
    {
        var server = new WebDavTestServer();
        server.BeforeRequest = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
        { Content = new StreamContent(new PendingReadStream()) });
        using var store = new WebDavSyncObjectStore(server.Settings(), server.Handler());
        using var read = await store.OpenReadAsync("sync/object", default);
        var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await read.ResponseStream.ReadExactlyAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("WebDAV", error.Message);
    }

    private sealed class PendingReadStream : Stream
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
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
