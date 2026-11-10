using System.Net;
using System.Text.Json;
using Kkindle.AccountServer;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    private static string FictionalDeploymentEndpoint(AccountRegion region) => region == AccountRegion.China
        ? "https://china.example.invalid" : "https://international.example.invalid";

    [Theory]
    [InlineData(AccountRegion.China, "http://127.0.0.1:5088", null, true)]
    [InlineData(AccountRegion.International, "http://127.0.0.1:5089", null, true)]
    [InlineData(AccountRegion.China, "http://localhost:5088", null, true)]
    [InlineData(AccountRegion.International, "http://10.0.2.2:5089", null, true)]
    [InlineData(AccountRegion.China, "https://old-deployment.example.invalid", true, true)]
    [InlineData(AccountRegion.China, "https://custom.example.invalid", null, false)]
    [InlineData(AccountRegion.International, "http://localhost:59423", null, false)]
    [InlineData(AccountRegion.China, "http://127.0.0.1:5088", false, false)]
    [InlineData(AccountRegion.China, "http://localhost:5089", null, false)]
    public async Task SignedOutConnectionsFollowOnlyMarkedOrLegacyDefaults(AccountRegion region, string oldEndpoint,
        bool? followsDefault, bool changes)
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        try
        {
            paths.EnsureDirectories();
            var stored = new Dictionary<string, object?>
                { ["region"] = region, ["serverUrl"] = oldEndpoint, ["protectedSession"] = "" };
            if (followsDefault is not null) stored["usesDefaultServerUrl"] = followsDefault;
            await File.WriteAllTextAsync(Path.Combine(paths.Data, "account-session.json"),
                JsonSerializer.Serialize(new { activeRegion = region, connections = new[] { stored } }));
            var protector = new TestProtector();
            using var client = new AccountService(paths, protector, defaultServerUrl: FictionalDeploymentEndpoint);
            var status = await client.GetStatusAsync();
            Assert.False(status.IsLoggedIn);
            Assert.Equal(region, status.Region);
            Assert.Equal(changes ? FictionalDeploymentEndpoint(region) : oldEndpoint, status.ServerUrl);
            // Selecting persists the provenance; a later release must keep the
            // same default/custom distinction even for HTTPS or loopback URLs.
            await client.SelectRegionAsync(region);
            using var restarted = new AccountService(paths, protector,
                defaultServerUrl: selected => FictionalDeploymentEndpoint(selected) + "/next");
            var afterRestart = await restarted.GetStatusAsync();
            Assert.Equal(changes ? FictionalDeploymentEndpoint(region) + "/next" : oldEndpoint, afterRestart.ServerUrl);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BothRegionsGetIndependentDefaultsAndPersistTheirProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var protector = new TestProtector();
            using var client = new AccountService(paths, protector, defaultServerUrl: FictionalDeploymentEndpoint);
            Assert.Equal(FictionalDeploymentEndpoint(AccountRegion.China), (await client.GetStatusAsync()).ServerUrl);
            Assert.Equal(FictionalDeploymentEndpoint(AccountRegion.International),
                (await client.SelectRegionAsync(AccountRegion.International)).ServerUrl);
            using var restarted = new AccountService(paths, protector,
                defaultServerUrl: region => FictionalDeploymentEndpoint(region) + "/next");
            var status = await restarted.GetStatusAsync();
            Assert.Equal(AccountRegion.International, status.Region);
            Assert.Equal(FictionalDeploymentEndpoint(AccountRegion.International) + "/next", status.ServerUrl);
            Assert.Equal(FictionalDeploymentEndpoint(AccountRegion.China) + "/next",
                (await restarted.SelectRegionAsync(AccountRegion.China)).ServerUrl);
            using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "account-session.json")));
            Assert.All(stored.RootElement.GetProperty("connections").EnumerateArray(),
                connection => Assert.True(connection.GetProperty("usesDefaultServerUrl").GetBoolean()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task ADefaultChangeNeverSendsAnExistingTokenToTheNewServer(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var paths = new AppPaths(Path.Combine(server.Directory, "endpoint-client"));
        var protector = new TestProtector();
        using (var original = new AccountService(paths, protector, defaultServerUrl: _ => server.Url))
        {
            await original.SelectRegionAsync(region);
            await original.SignInAsync(server.Url, "endpoint_reader", TestPassword, register: true,
                email: "endpoint_reader@example.invalid", confirmPassword: TestPassword);
        }
        using var handler = new EndpointGuardHandler(new Uri(server.Url));
        using var http = new HttpClient(handler);
        using var updated = new AccountService(paths, protector, http, defaultServerUrl: FictionalDeploymentEndpoint);
        var status = await updated.GetStatusAsync(validateSession: true);
        Assert.True(status.IsLoggedIn);
        Assert.Equal(server.Url, status.ServerUrl);
        var settings = await updated.CreateSyncSettingsAsync(new S3SyncSettings());
        Assert.Equal(server.Url, settings.AccountServerUrl);
        Assert.Equal(region, settings.AccountRegion);
        var other = region == AccountRegion.China ? AccountRegion.International : AccountRegion.China;
        var otherStatus = await updated.SelectRegionAsync(other);
        Assert.False(otherStatus.IsLoggedIn);
        Assert.Equal(FictionalDeploymentEndpoint(other), otherStatus.ServerUrl);
        Assert.Equal(server.Url, (await updated.SelectRegionAsync(region)).ServerUrl);
        Assert.True(await updated.SignOutAsync());
        Assert.False((await updated.GetStatusAsync()).IsLoggedIn);
        Assert.Equal(FictionalDeploymentEndpoint(region), (await updated.GetStatusAsync()).ServerUrl);
        Assert.True(handler.AuthenticatedRequests >= 3);
        Assert.Equal(0, handler.RejectedRequests);
        using var restarted = new AccountService(paths, protector, defaultServerUrl: FictionalDeploymentEndpoint);
        Assert.Equal(FictionalDeploymentEndpoint(region), (await restarted.GetStatusAsync()).ServerUrl);
    }

    [Fact]
    public async Task LocalExpiryClearsTheTokenBeforeFollowingTheNewDefault()
    {
        var clock = new TestClock();
        await using var server = await LocalServer.CreateAsync(clock);
        var paths = new AppPaths(Path.Combine(server.Directory, "endpoint-client"));
        var protector = new TestProtector();
        using (var original = new AccountService(paths, protector, clock: clock, defaultServerUrl: _ => server.Url))
            await original.SignInAsync(server.Url, "endpoint_reader", TestPassword, register: true,
                email: "endpoint_reader@example.invalid", confirmPassword: TestPassword);
        clock.Advance(TimeSpan.FromDays(31));
        using var handler = new EndpointGuardHandler(new Uri(server.Url));
        using var http = new HttpClient(handler);
        using var updated = new AccountService(paths, protector, http, clock, defaultServerUrl: FictionalDeploymentEndpoint);
        var status = await updated.GetStatusAsync(validateSession: true);
        Assert.False(status.IsLoggedIn);
        Assert.Equal(FictionalDeploymentEndpoint(AccountRegion.China), status.ServerUrl);
        Assert.Equal(0, handler.AuthenticatedRequests);
        Assert.Equal(0, handler.RejectedRequests);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "account-session.json")));
        Assert.Equal("", stored.RootElement.GetProperty("connections")[0].GetProperty("protectedSession").GetString());
    }

    [Fact]
    public async Task AnInvalidDeploymentOverrideCannotPreventLocalLogout()
    {
        await using var server = await LocalServer.CreateAsync();
        var paths = new AppPaths(Path.Combine(server.Directory, "endpoint-client"));
        var protector = new TestProtector();
        using (var original = new AccountService(paths, protector, defaultServerUrl: _ => server.Url))
            await original.SignInAsync(server.Url, "endpoint_reader", TestPassword, register: true,
                email: "endpoint_reader@example.invalid", confirmPassword: TestPassword);
        using var handler = new EndpointGuardHandler(new Uri(server.Url));
        using var http = new HttpClient(handler);
        using var updated = new AccountService(paths, protector, http,
            defaultServerUrl: region => AccountServerEndpoints.Resolve(region, "invalid-deployment-value"));
        Assert.True((await updated.GetStatusAsync(validateSession: true)).IsLoggedIn);
        Assert.True(await updated.SignOutAsync());
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "account-session.json")));
        Assert.Equal("", stored.RootElement.GetProperty("connections")[0].GetProperty("protectedSession").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => updated.GetStatusAsync());
        Assert.Equal(0, handler.RejectedRequests);
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task ProtocolTestHostAlwaysUsesItsLocalListeningDefault(AccountRegion region)
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = await AccountServerHost.BuildAsync([
                "--AccountServer:Region", region.ToString(), "--AccountServer:DataDirectory", root,
                "--Logging:LogLevel:Default", "None"]);
            Assert.Equal(AccountServerEndpoints.LocalDevelopmentServerUrl(region), app.Configuration["urls"]);
            // Do not start the listener: the live PostgreSQL services already
            // occupy these ports. All real HTTP fixtures use random ports.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class EndpointGuardHandler(Uri expected) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        public int AuthenticatedRequests { get; private set; }
        public int RejectedRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri is null || !expected.IsBaseOf(request.RequestUri))
            {
                RejectedRequests++;
                throw new InvalidOperationException("A fixture request targeted an unexpected endpoint.");
            }
            if (request.Headers.Authorization?.Scheme == "Bearer") AuthenticatedRequests++;
            return base.SendAsync(request, token);
        }
    }
}
