using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class CalibreSetupServiceTests
{
    [Theory]
    [InlineData("success", 0)]
    [InlineData("success", 1)]
    [InlineData("success", 2)]
    [InlineData("hash", 0)]
    [InlineData("size", 0)]
    [InlineData("manifest404", 0)]
    [InlineData("file404", 0)]
    [InlineData("json", 0)]
    [InlineData("timeout", 0)]
    [InlineData("host", 0)]
    [InlineData("invalidHash", 0)]
    [InlineData("http", 0)]
    [InlineData("maximum", 0)]
    [InlineData("hash", 1)]
    [InlineData("hash", 2)]
    [InlineData("windowsFallbackName", 0)]
    public async Task DownloadsMirrorOrFallsBackToOriginalOfficialUri(string scenario, int targetValue)
    {
        var target = (CalibreMirrorTarget)targetValue;
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "artifact");
            var mirror = Encoding.UTF8.GetBytes("mirror payload");
            var official = Encoding.UTF8.GetBytes("official payload");
            var officialUri = target switch
            {
                CalibreMirrorTarget.WindowsMsi => CalibreSetupService.WindowsDownloadUri,
                CalibreMirrorTarget.MacOsDmg => CalibreSetupService.MacOSDownloadUri,
                _ => CalibreSetupService.KfxInputPluginUri
            };
            var name = target switch
            {
                CalibreMirrorTarget.WindowsMsi => scenario == "windowsFallbackName" ? "calibre-64bit-9.14.0.msi" : "calibre-64bit-9.15.0.msi",
                CalibreMirrorTarget.MacOsDmg => "calibre-9.15.0.dmg",
                _ => "kfx-input-291290.zip"
            };
            var manifest = JsonSerializer.Serialize(new
            {
                version = "9.15.0",
                files = new[] { new { name, size = scenario == "maximum" ? 1025 : mirror.Length + (scenario == "size" ? 1 : 0),
                    sha256 = scenario == "hash" ? new string('0', 64) : scenario == "invalidHash" ? "bad" : Convert.ToHexString(SHA256.HashData(mirror)).ToLowerInvariant(),
                    url = scenario == "host" ? "https://unknown.test/file" : "/dl/calibre/" + name,
                    official_url = "https://wrong.test/ignored" } }
            });
            var requests = new List<Uri>();
            using var service = new CalibreSetupService(new StubHandler(request =>
            {
                var uri = request.RequestUri!;
                requests.Add(uri);
                if (uri == officialUri)
                {
                    Assert.False(File.Exists(destination));
                    return Bytes(official);
                }
                if (uri == CalibreSetupService.MirrorManifestUri)
                    return scenario switch
                    {
                        "manifest404" => new HttpResponseMessage(HttpStatusCode.NotFound),
                        "timeout" => throw new TaskCanceledException("timeout"),
                        "http" => throw new HttpRequestException("offline"),
                        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scenario == "json" ? "{broken" : manifest) }
                    };
                Assert.Equal("kkindle.stacker.beauty", uri.Host);
                return scenario == "file404" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Bytes(mirror);
            }));
            await service.DownloadCalibreArtifactAsync(target, officialUri, destination, 1024, null, CancellationToken.None);
            var success = scenario is "success" or "windowsFallbackName";
            Assert.Equal(success ? mirror : official, await File.ReadAllBytesAsync(destination));
            Assert.Equal(CalibreSetupService.MirrorManifestUri, requests[0]);
            if (success)
            {
                Assert.Equal(2, requests.Count);
                Assert.All(requests, uri => Assert.Equal("kkindle.stacker.beauty", uri.Host));
            }
            else Assert.Equal(officialUri, requests.Last());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("https://kkindle.stacker.beauty/dl/file")]
    [InlineData("https://calibre-ebook.com/dist/win64")]
    [InlineData("https://download.calibre-ebook.com/file")]
    [InlineData("https://github.com/file")]
    [InlineData("https://raw.githubusercontent.com/file")]
    public void AllowsTrustedHosts(string uri) => CalibreSetupService.EnsureTrustedDownloadUri(new Uri(uri));

    [Theory]
    [InlineData("https://unknown.test/file")]
    [InlineData("http://kkindle.stacker.beauty/file")]
    public void RejectsUntrustedUri(string uri) => Assert.Throws<InvalidDataException>(() => CalibreSetupService.EnsureTrustedDownloadUri(new Uri(uri)));

    [Fact]
    public async Task CancellationCleansMirrorFileAndDoesNotRequestOfficial()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "artifact");
            using var cancellation = new CancellationTokenSource();
            var requests = 0;
            var payload = Encoding.UTF8.GetBytes("mirror payload");
            var manifest = JsonSerializer.Serialize(new
            {
                version = "9.15.0",
                files = new[] { new { name = "calibre-64bit-9.15.0.msi", size = payload.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(payload)), url = "/dl/calibre/file.msi" } }
            });
            using var service = new CalibreSetupService(new StubHandler(request =>
            {
                requests++;
                Assert.Equal("kkindle.stacker.beauty", request.RequestUri!.Host);
                return request.RequestUri == CalibreSetupService.MirrorManifestUri
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
                    : Bytes(payload);
            }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadCalibreArtifactAsync(
                CalibreMirrorTarget.WindowsMsi, CalibreSetupService.WindowsDownloadUri, destination, 1024,
                new TestHelpers.InlineProgress<CalibreSetupProgress>(_ => cancellation.Cancel()), cancellation.Token));
            Assert.Equal(2, requests);
            Assert.False(File.Exists(destination));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(factory(request));
    }
}
