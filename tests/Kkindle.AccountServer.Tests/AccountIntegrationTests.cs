using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Kkindle.AccountServer;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    private const string TestPassword = "fictional-test-password";

    [Fact]
    public async Task RegisterAndLoginValidateCredentialsAndStoreOnlyHashes()
    {
        await using var server = await LocalServer.CreateAsync();
        var first = await RegisterAsync(server.Http, "test_reader");
        Assert.Equal("test_reader", first.User.Username);
        Assert.Equal(43, first.Token.Length);
        using var duplicate = await CredentialsAsync(server.Http, "register", "TEST_READER", TestPassword);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var wrong = await CredentialsAsync(server.Http, "login", "test_reader", "fictional-wrong-password");
        using var unknown = await CredentialsAsync(server.Http, "login", "unknown_reader", "fictional-wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        using var correct = await CredentialsAsync(server.Http, "login", "TEST_READER", TestPassword);
        var signedIn = await correct.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSessionResponse);
        Assert.Equal(first.User, signedIn!.User);
        Assert.NotEqual(first.Token, signedIn.Token);
        var second = await RegisterAsync(server.Http, "another_reader");

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(server.RegionDirectory, "accounts.db")}");
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT Salt, PasswordHash FROM Accounts ORDER BY Username;";
        await using var reader = await query.ExecuteReaderAsync();
        var salts = new List<byte[]>(); var hashes = new List<byte[]>();
        while (await reader.ReadAsync()) { salts.Add((byte[])reader[0]); hashes.Add((byte[])reader[1]); }
        Assert.All(salts, salt => Assert.Equal(16, salt.Length));
        Assert.All(hashes, hash => Assert.Equal(32, hash.Length));
        Assert.NotEqual(salts[0], salts[1]); Assert.NotEqual(hashes[0], hashes[1]);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(server.RegionDirectory, "accounts.db"));
        var diskText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(TestPassword, diskText);
        Assert.DoesNotContain(first.Token, diskText);
        Assert.DoesNotContain(second.Token, diskText);
    }

    [Fact]
    public async Task SessionExpiresAndLogoutRevokesOnlyTheCurrentSession()
    {
        var clock = new TestClock();
        await using var server = await LocalServer.CreateAsync(clock);
        var first = await RegisterAsync(server.Http, "test_reader");
        using var response = await CredentialsAsync(server.Http, "login", "test_reader", TestPassword);
        var second = (await response.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSessionResponse))!;
        using (var session = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", first.Token))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            var info = await session.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSessionInfo);
            Assert.Equal(first.User, info!.User);
        }
        using (var logout = await AuthorizedAsync(server.Http, HttpMethod.Post, "logout", first.Token))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using (var old = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", first.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var other = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", second.Token))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        clock.Advance(TimeSpan.FromDays(31));
        using (var expired = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", second.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var anonymous = await server.Http.GetAsync("api/accounts/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task ProtectedClientSessionRestoresAndIsClearedAfterServerRevocation()
    {
        await using var server = await LocalServer.CreateAsync();
        var paths = new AppPaths(Path.Combine(server.Directory, "client"));
        var protector = new TestProtector();
        using var client = new AccountService(paths, protector);
        var status = await client.SignInAsync(server.Url, "test_reader", TestPassword, register: true);
        Assert.True(status.IsLoggedIn);
        var persisted = await File.ReadAllTextAsync(Path.Combine(paths.Data, "account-session.json"));
        Assert.DoesNotContain(TestPassword, persisted);
        Assert.DoesNotContain("test_reader", persisted);
        using var document = JsonDocument.Parse(persisted);
        var clear = protector.Unprotect(Convert.FromBase64String(document.RootElement.GetProperty("connections")[0].GetProperty("protectedSession").GetString()!));
        var session = JsonSerializer.Deserialize(clear, AccountJsonContext.Default.AccountSessionResponse)!;
        Assert.DoesNotContain(session.Token, persisted);
        using var restored = new AccountService(paths, protector);
        Assert.Equal(status.User, (await restored.GetStatusAsync(validateSession: true)).User);
        using var revoke = await AuthorizedAsync(server.Http, HttpMethod.Post, "logout", session.Token);
        Assert.False((await restored.GetStatusAsync(validateSession: true)).IsLoggedIn);
        using var restarted = new AccountService(paths, protector);
        Assert.False((await restarted.GetStatusAsync()).IsLoggedIn);
        Assert.Equal(server.Url, (await restarted.GetStatusAsync()).ServerUrl);
    }

    [Fact]
    public async Task ClientLogoutRevokesServerTokenAndOfflineLogoutStillClearsTheDevice()
    {
        await using var server = await LocalServer.CreateAsync();
        var paths = new AppPaths(Path.Combine(server.Directory, "client"));
        var protector = new TestProtector();
        using var client = new AccountService(paths, protector);
        await client.SignInAsync(server.Url, "test_reader", TestPassword, register: true);
        var token = await ReadStoredTokenAsync(paths, protector);
        Assert.True(await client.SignOutAsync());
        using (var old = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", token))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        await client.SignInAsync(server.Url, "test_reader", TestPassword);
        await server.Application.StopAsync();
        Assert.False(await client.SignOutAsync());
        using var restarted = new AccountService(paths, protector);
        Assert.False((await restarted.GetStatusAsync()).IsLoggedIn);
    }

    [Fact]
    public async Task AccountsAndSessionsSurviveServerRestartAndClientDropsLocalExpiry()
    {
        var clock = new TestClock();
        await using var server = await LocalServer.CreateAsync(clock);
        var paths = new AppPaths(Path.Combine(server.Directory, "client"));
        var protector = new TestProtector();
        using var client = new AccountService(paths, protector, clock: clock);
        await client.SignInAsync(server.Url, "test_reader", TestPassword, register: true);
        var token = await ReadStoredTokenAsync(paths, protector);
        await server.Application.StopAsync();
        await using var restarted = await AccountServerHost.BuildAsync([
            "--urls", "http://127.0.0.1:0", "--AccountServer:DataDirectory", server.Directory,
            "--Logging:LogLevel:Default", "None"], clock);
        await restarted.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(restarted.Urls.Single()) };
        http.DefaultRequestHeaders.Add("X-Kkindle-Region", "china");
        using (var existing = await AuthorizedAsync(http, HttpMethod.Get, "session", token))
            Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        clock.Advance(TimeSpan.FromDays(31));
        Assert.False((await client.GetStatusAsync()).IsLoggedIn);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task ConcurrentRegistrationCreatesExactlyOneAccountAndRateLimitBlocksMoreAttempts()
    {
        await using var server = await LocalServer.CreateAsync();
        var responses = await Task.WhenAll(CredentialsAsync(server.Http, "register", "test_reader", TestPassword),
            CredentialsAsync(server.Http, "register", "TEST_READER", TestPassword));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        for (var i = 0; i < 8; i++)
        {
            using var wrong = await CredentialsAsync(server.Http, "login", "test_reader", "fictional-wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using var limited = await CredentialsAsync(server.Http, "login", "test_reader", TestPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());
    }

    [Theory]
    [InlineData("bad username", "fictional-test-password")]
    [InlineData("test_reader", "short")]
    [InlineData("test_reader", "        ")]
    public async Task ServerRejectsMalformedCredentials(string username, string password)
    {
        await using var server = await LocalServer.CreateAsync();
        using var response = await CredentialsAsync(server.Http, "register", username, password);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("https://example.invalid/?token=placeholder")]
    [InlineData("https://test-user:test-password@example.invalid")]
    [InlineData("file:///tmp/accounts")]
    public void ClientRejectsInsecureOrCredentialBearingServerUrls(string url) =>
        Assert.Throws<ArgumentException>(() => AccountValidation.ServerUri(url));

    private static Task<HttpResponseMessage> CredentialsAsync(HttpClient http, string action, string username, string password) =>
        http.PostAsJsonAsync("api/accounts/" + action, new AccountCredentials { Username = username, Password = password,
            Region = http.DefaultRequestHeaders.GetValues("X-Kkindle-Region").Single() == "international" ? AccountRegion.International : AccountRegion.China },
            AccountJsonContext.Default.AccountCredentials);

    private static async Task<AccountSessionResponse> RegisterAsync(HttpClient http, string username)
    {
        using var response = await CredentialsAsync(http, "register", username, TestPassword);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSessionResponse))!;
    }

    private static async Task<HttpResponseMessage> AuthorizedAsync(HttpClient http, HttpMethod method, string action, string token)
    {
        using var request = new HttpRequestMessage(method, "api/accounts/" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private static async Task<string> ReadStoredTokenAsync(AppPaths paths, TestProtector protector)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "account-session.json")));
        var active = document.RootElement.GetProperty("activeRegion").GetInt32();
        var connection = document.RootElement.GetProperty("connections").EnumerateArray().Single(item => item.GetProperty("region").GetInt32() == active);
        var clear = protector.Unprotect(Convert.FromBase64String(connection.GetProperty("protectedSession").GetString()!));
        return JsonSerializer.Deserialize(clear, AccountJsonContext.Default.AccountSessionResponse)!.Token;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestProtector : ISecretProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] value)
        {
            var result = new byte[12 + 16 + value.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(result.AsSpan(0, 12), value, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        public byte[] Unprotect(byte[] value)
        {
            var clear = new byte[value.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), clear);
            return clear;
        }
    }

    private sealed class LocalServer(WebApplication application, string directory, HttpClient http,
        AccountRegion region, bool ownsDirectory) : IAsyncDisposable
    {
        public WebApplication Application { get; } = application;
        public string Directory { get; } = directory;
        public HttpClient Http { get; } = http;
        public string Url => Http.BaseAddress!.AbsoluteUri.TrimEnd('/');
        public string RegionDirectory => Path.Combine(Directory, AccountRegions.Slug(region));

        public static async Task<LocalServer> CreateAsync(TimeProvider? clock = null, AccountRegion region = AccountRegion.China, string? sharedDirectory = null)
        {
            var directory = sharedDirectory ?? Path.Combine(Path.GetTempPath(), "kkindle-account-tests-" + Guid.NewGuid().ToString("N"));
            var app = await AccountServerHost.BuildAsync(["--urls", "http://127.0.0.1:0", "--AccountServer:DataDirectory", directory,
                "--AccountServer:Region", region.ToString(),
                "--Logging:LogLevel:Default", "None"], clock);
            await app.StartAsync();
            var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            http.DefaultRequestHeaders.Add("X-Kkindle-Region", AccountRegions.Slug(region));
            return new(app, directory, http, region, sharedDirectory is null);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Application.StopAsync();
            await Application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (ownsDirectory) System.IO.Directory.Delete(Directory, true);
        }
    }
}
