using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-dev.10", "1.0.0-dev.2", 1)]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("v1.2.3", "1.2.3+build.4", 0)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    public void ComparesSemanticVersions(string left, string right, int expectedSign)
    {
        var result = UpdateService.CompareVersions(left, right);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public async Task FindsNewStableReleaseAndSelectsWindowsInstaller()
    {
        const string responseJson = """
            {
              "tag_name": "v1.2.0",
              "html_url": "https://github.com/kingstacker/Kkindle/releases/tag/v1.2.0",
              "body": "Release notes",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "Kkindle-1.2.0-win-x64-setup.exe",
                  "browser_download_url": "https://example.test/Kkindle-1.2.0-win-x64-setup.exe",
                  "size": 1234
                },
                {
                  "name": "SHA256SUMS.txt",
                  "browser_download_url": "https://example.test/SHA256SUMS.txt",
                  "size": 128
                }
              ]
            }
            """;
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(responseJson)));
        using var service = new UpdateService(
            new TestInstaller(),
            client,
            Path.GetTempPath());

        var update = await service.CheckForUpdateAsync("1.1.9");

        Assert.NotNull(update);
        Assert.Equal("1.2.0", update.Version);
        Assert.Equal("Kkindle-1.2.0-win-x64-setup.exe", update.Package.Name);
        Assert.Equal("Release notes", update.ReleaseNotes);
    }

    [Fact]
    public async Task ReturnsNullWhenCurrentDevelopmentVersionIsNewer()
    {
        const string responseJson = """
            {
              "tag_name": "v1.1.9",
              "html_url": "https://github.com/kingstacker/Kkindle/releases/tag/v1.1.9",
              "body": "Older release",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """;
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(responseJson)));
        using var service = new UpdateService(
            new TestInstaller(),
            client,
            Path.GetTempPath());

        Assert.Null(await service.CheckForUpdateAsync("1.2.0-dev.3"));
    }

    [Fact]
    public async Task UsesLatestReleaseRedirectWhenManifestIsMissing()
    {
        const string responseJson = """
            {
              "tag_name": "v1.2.0",
              "html_url": "https://github.com/kingstacker/Kkindle/releases/tag/v1.2.0",
              "body": "Release notes",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "Kkindle-1.2.0-win-x64-setup.exe",
                  "browser_download_url": "https://example.test/Kkindle-1.2.0-win-x64-setup.exe",
                  "size": 1234
                },
                {
                  "name": "SHA256SUMS.txt",
                  "browser_download_url": "https://example.test/SHA256SUMS.txt",
                  "size": 128
                }
              ]
            }
            """;
        var requestedUris = new List<Uri>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestedUris.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("update-manifest.json", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.RequestUri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(
                        HttpMethod.Get,
                        "https://github.com/kingstacker/Kkindle/releases/tag/v1.2.0")
                };
            }
            return JsonResponse(responseJson);
        }));
        using var service = new UpdateService(
            new TestInstaller(),
            client,
            Path.GetTempPath());

        var update = await service.CheckForUpdateAsync("1.1.0");

        Assert.NotNull(update);
        Assert.Equal(2, requestedUris.Count);
        Assert.Equal("github.com", requestedUris[0].Host);
        Assert.Equal("github.com", requestedUris[1].Host);
        Assert.Equal("1.2.0", update.Version);
        Assert.Equal("Kkindle-1.2.0-win-x64-setup.exe", update.Package.Name);
    }

    [Fact]
    public async Task DownloadsPackageOnlyWhenSha256Matches()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var packageBytes = Encoding.UTF8.GetBytes("verified installer payload");
            var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            var installer = new TestInstaller();
            using var client = new HttpClient(new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                    return TextResponse($"{packageHash}  Kkindle-1.2.0-win-x64-setup.exe\n");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            }));
            using var service = new UpdateService(installer, client, root);
            var update = CreateUpdate(packageBytes.Length);
            var progressUpdates = new List<AppUpdateDownloadProgress>();

            var packagePath = await service.DownloadAsync(
                update,
                new TestHelpers.InlineProgress<AppUpdateDownloadProgress>(progressUpdates.Add));

            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(packagePath));
            Assert.Equal(100, progressUpdates.Last().Percentage);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsPackageWhenSha256DoesNotMatch()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var packageBytes = Encoding.UTF8.GetBytes("modified installer payload");
            using var client = new HttpClient(new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                    return TextResponse($"{new string('0', 64)}  Kkindle-1.2.0-win-x64-setup.exe\n");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            }));
            using var service = new UpdateService(new TestInstaller(), client, root);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAsync(CreateUpdate(packageBytes.Length)));

            Assert.Contains("SHA256", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static AppUpdateInfo CreateUpdate(long packageSize) => new(
        "1.2.0",
        "v1.2.0",
        "Release notes",
        new Uri("https://github.com/kingstacker/Kkindle/releases/tag/v1.2.0"),
        new AppUpdateAsset(
            "Kkindle-1.2.0-win-x64-setup.exe",
            new Uri("https://example.test/Kkindle-1.2.0-win-x64-setup.exe"),
            packageSize),
        new AppUpdateAsset(
            "SHA256SUMS.txt",
            new Uri("https://example.test/SHA256SUMS.txt"),
            128));

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TextResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/plain")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class TestInstaller : IAppUpdateInstaller
    {
        public bool CanInstall => true;
        public string UnavailableReason => string.Empty;
        public string GetPackageAssetName(string version) => $"Kkindle-{version}-win-x64-setup.exe";
        public void LaunchInstaller(string packagePath)
        {
        }
    }
}
