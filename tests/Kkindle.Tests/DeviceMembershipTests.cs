using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class DeviceMembershipTests
{
    [Theory]
    [InlineData("deepseek", "https://api.deepseek.com", false)]
    [InlineData("deepseek", "https://api.deepseek.com/v1/", false)]
    [InlineData("openai", "https://api.openai.com/v1", false)]
    [InlineData("OPENAI", "https://api.openai.com:443/", false)]
    [InlineData("custom", "https://api.openai.com/v1", true)]
    [InlineData("openai", "https://example.invalid/v1", true)]
    [InlineData("deepseek", "https://api.openai.com/v1", true)]
    [InlineData("openai", "http://api.openai.com/v1", true)]
    [InlineData("openai", "https://api.openai.com:444/v1", true)]
    [InlineData("openai", "https://api.openai.com/proxy/v1", true)]
    [InlineData("deepseek", "https://api.deepseek.com/?redirect=example.invalid", true)]
    [InlineData("openai", "https://api.openai.com/v1#custom", true)]
    [InlineData("openai", "https://fictional-user@api.openai.com/v1", true)]
    [InlineData("unknown", "https://api.deepseek.com", true)]
    public void OnlyOfficialPresetEndpointsAreFree(string provider, string endpoint, bool requiresMember) =>
        Assert.Equal(requiresMember, new AiConnectionSettings { Provider = provider, BaseUrl = endpoint }.RequiresDeviceMembership);

    [Theory]
    [InlineData("models")]
    [InlineData("completion")]
    [InlineData("stream")]
    [InlineData("images")]
    [InlineData("vision")]
    [InlineData("test")]
    public async Task UnlicensedCustomAiIsRejectedBeforeAnyHttpRequest(string operation)
    {
        var requests = 0;
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ =>
        {
            requests++;
            throw new InvalidOperationException("AI request must not be sent");
        }));
        var settings = new AiConnectionSettings { Provider = "custom", BaseUrl = "https://example.invalid/v1",
            Model = "fictional-model", ApiKey = "<FICTIONAL_API_KEY>" };
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(async () =>
        {
            switch (operation)
            {
                case "models": await client.ListModelsAsync(settings); break;
                case "completion": await client.CompleteAsync(settings, "system", "question", []); break;
                case "stream": await foreach (var _ in client.StreamAsync(settings, "system", "question", [])) { } break;
                case "images": await foreach (var _ in client.StreamAsync(settings, "system", "question", [], images: [new("image/png", [1])])) { } break;
                case "vision": await foreach (var _ in client.StreamVisionAsync(settings, "system", "question", [], new("image/png", [1]))) { } break;
                case "test": await client.TestConnectionAsync(settings); break;
            }
        });
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("deepseek", "https://api.deepseek.com")]
    [InlineData("openai", "https://api.openai.com/v1")]
    public async Task FreeOfficialProviderStillMakesRequestsWithoutAnAccount(string provider, string url)
    {
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ => new()
        { Content = new StringContent("{\"data\":[{\"id\":\"fictional-model\"}]}", Encoding.UTF8, "application/json") }));
        Assert.Equal("fictional-model", Assert.Single(await client.ListModelsAsync(new()
            { Provider = provider, BaseUrl = url, ApiKey = "<FICTIONAL_API_KEY>" })));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("sync")]
    [InlineData("download")]
    public async Task UnlicensedCustomS3IsRejectedBeforeCreatingTheObjectStore(string operation)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await new ReaderDataService(paths).InitializeAsync();
            var creations = 0;
            Func<S3SyncSettings, ISyncObjectStore> factory = _ =>
            {
                creations++;
                throw new InvalidOperationException("S3 client must not be created");
            };
            var sync = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector(), factory);
            var settings = new S3SyncSettings { Enabled = true, Provider = SyncProvider.S3, Bucket = "fictional-bucket",
                Endpoint = "https://example.invalid", AccessKey = "<FICTIONAL_ACCESS_KEY>", SecretKey = "<FICTIONAL_SECRET_KEY>",
                EncryptionKey = "fictional-sync-key", Prefix = "fictional" };
            await Assert.ThrowsAsync<DeviceFeatureRequiredException>(async () =>
            {
                switch (operation)
                {
                    case "test": await sync.TestConnectionAsync(settings); break;
                    case "sync": await sync.SyncAsync(Guid.NewGuid().ToString(), settings); break;
                    case "download": await sync.EnsureBookFileDownloadedAsync(Guid.NewGuid().ToString(), settings,
                        new BookFile { Sha256 = new string('a', 64), RelativePath = "books/fictional.epub" }); break;
                }
            });
            Assert.Equal(0, creations);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task AccountFeaturesAreCheckedAgainBeforeEveryCustomOperation()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomAi);
        Assert.Equal(2, scope.Handler.FeatureChecks);
        scope.Handler.Revoked = true;
        var notices = 0; scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task FreeAccountHasDeviceManagementButCannotUsePaidFeatures()
    {
        using var scope = await AccountScope.CreateAsync(); scope.Handler.LifetimeFeatures = false;
        var status = await scope.Membership.RefreshAsync();
        Assert.Equal("free", status.Membership.Tier); Assert.Equal(2, status.Membership.LoggedInDeviceCount);
        Assert.False(status.CanUseFeatures);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomAi));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("device")]
    [InlineData("limit")]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("inactive")]
    public async Task FeatureAccessRejectsWrongOrMalformedServerScope(string failure)
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.TransformStatus = status => failure switch
        {
            "account" => status with { Membership = status.Membership with { AccountId = Guid.NewGuid() } },
            "region" => status with { Membership = status.Membership with { Region = AccountRegion.International } },
            "device" => status with { CurrentDeviceId = Guid.NewGuid() },
            "limit" => status with { Membership = status.Membership with { DeviceLimit = 6 } },
            "count" => status with { Membership = status.Membership with { LoggedInDeviceCount = 5 } },
            "duplicate" => status with { Devices = [status.Devices[0], status.Devices[0]] },
            "inactive" => status with { Devices = status.Devices.Select(device => device with { State = "signed_out" }).ToArray(),
                Membership = status.Membership with { LoggedInDeviceCount = 0, ActivatedDeviceCount = 0 } },
            _ => status
        };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
    }

    [Fact]
    public async Task ALongDeviceHistoryRemainsWithinTheBoundedClientResponse()
    {
        using var scope = await AccountScope.CreateAsync();
        for (var i = 0; i < 98; i++) scope.Handler.Devices.Add(new(Guid.NewGuid(), new string('测', 64), "fictional-platform", "signed_out", scope.Clock.UtcNow, scope.Clock.UtcNow));
        Assert.Equal(100, (await scope.Membership.RefreshAsync()).Devices.Length);
    }

    [Fact]
    public async Task KickingAnotherDevicePreservesCurrentLoginAndKickingCurrentDeviceNotifiesOnce()
    {
        using var scope = await AccountScope.CreateAsync(); var notices = 0;
        scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        await scope.Membership.KickAsync(scope.Handler.OtherDeviceId);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.Equal(1, (await scope.Membership.RefreshAsync()).Membership.LoggedInDeviceCount);
        await scope.Membership.KickAsync(scope.Handler.CurrentDeviceId);
        Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn); Assert.Equal(1, notices);
        await scope.Accounts.GetStatusAsync(true); Assert.Equal(1, notices);
    }

    [Fact]
    public async Task SuccessfulDailyCheckSurvivesClientAndMonitorRestartsAndRunsAt24Hours()
    {
        using var scope = await AccountScope.CreateAsync();
        using (var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            var checks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitor.CheckIfDueAsync()));
            Assert.Single(checks, checkedServer => checkedServer); Assert.Equal(1, scope.Handler.SessionChecks);
        }
        using var restarted = new AccountService(scope.Paths, scope.Protector, scope.Http, scope.Clock);
        using var next = new AccountDeviceMonitor(restarted, scope.Paths, scope.Clock);
        scope.Clock.Advance(TimeSpan.FromHours(23)); Assert.False(await next.CheckIfDueAsync());
        Assert.Equal(1, scope.Handler.SessionChecks);
        scope.Clock.Advance(TimeSpan.FromHours(1)); Assert.True(await next.CheckIfDueAsync());
        Assert.False(await next.CheckIfDueAsync()); Assert.Equal(2, scope.Handler.SessionChecks);
        var checkpoint = await File.ReadAllTextAsync(Path.Combine(scope.Paths.Data, "device-check.json"));
        Assert.DoesNotContain(scope.Handler.User.Id.ToString(), checkpoint);
        Assert.DoesNotContain(scope.Handler.CurrentDeviceId.ToString(), checkpoint);
    }

    [Fact]
    public async Task KickingAnAlreadyBlockedOtherDeviceKeepsTheCurrentSession()
    {
        using var scope = await AccountScope.CreateAsync(); var notices = 0;
        scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        var index = scope.Handler.Devices.FindIndex(device => device.DeviceId == scope.Handler.OtherDeviceId);
        scope.Handler.Devices[index] = scope.Handler.Devices[index] with { State = "blocked" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.KickAsync(scope.Handler.OtherDeviceId));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.True((await scope.Membership.RefreshAsync()).IsCurrentDeviceActive);
        Assert.Equal(0, notices);
    }

    [Fact]
    public async Task OfflineDailyCheckKeepsTheSessionAndRetriesWithoutRecordingSuccess()
    {
        using var scope = await AccountScope.CreateAsync();
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        scope.Handler.Offline = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.CheckIfDueAsync());
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(scope.Paths.Data, "device-check.json")));
        scope.Handler.Offline = false;
        Assert.True(await monitor.CheckIfDueAsync()); Assert.Equal(1, scope.Handler.SessionChecks);
    }

    [Fact]
    public async Task DailyCheckReportsInvalidationOnceAndClearsThePersistedSession()
    {
        using var scope = await AccountScope.CreateAsync();
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        var notices = 0; scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        scope.Handler.Revoked = true;
        Assert.True(await monitor.CheckIfDueAsync()); Assert.False(await monitor.CheckIfDueAsync());
        Assert.Equal(1, notices); Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        using var restarted = new AccountService(scope.Paths, scope.Protector, scope.Http, scope.Clock);
        Assert.False((await restarted.GetStatusAsync()).IsLoggedIn);
    }

    [Fact]
    public async Task CorruptedCheckpointIsReplacedAfterASuccessfulCheck()
    {
        using var scope = await AccountScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Paths.Data, "device-check.json"), "broken checkpoint");
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        Assert.True(await monitor.CheckIfDueAsync()); Assert.False(await monitor.CheckIfDueAsync());
    }

    [Fact]
    public async Task DeviceIdentityPersistsAndKeystoreFailureNeverReplacesIt()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "uninitialized")); var protector = new EncryptedProtector();
            using var account = new AccountService(paths, protector);
            var identities = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => account.GetDeviceAsync()));
            var id = Assert.Single(identities.Select(item => item.DeviceId).Distinct()); Assert.NotEqual(Guid.Empty, id);
            var file = Path.Combine(paths.Data, "device-identity.bin"); var protectedBytes = await File.ReadAllBytesAsync(file);
            Assert.False(protectedBytes.AsSpan().SequenceEqual(id.ToByteArray()));
            using var restored = new AccountService(paths, protector); Assert.Equal(id, (await restored.GetDeviceAsync()).DeviceId);
            protector.Unavailable = true;
            using var unavailable = new AccountService(paths, protector);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.GetDeviceAsync());
            Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(file));
            protector.Unavailable = false; Assert.Equal(id, (await unavailable.GetDeviceAsync()).DeviceId);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task EncryptedBackupNeverTransfersTheDeviceIdentitySessionOrMembershipCheckpoint()
    {
        using var source = await AccountScope.CreateAsync();
        using var monitor = new AccountDeviceMonitor(source.Accounts, source.Paths, source.Clock); await monitor.CheckIfDueAsync();
        await BackupTests.SetBackupKeyAsync(source.Paths);
        await new SqliteBookLibraryService(source.Paths, new BookMetadataService()).InitializeAsync();
        var archive = Path.Combine(source.Root, "fictional.kkindle");
        await new AppBackupService(source.Paths, new TestHelpers.PlaintextSecretProtector()).ExportAsync(archive);
        using (var zip = await BackupTests.OpenBackupArchiveAsync(archive))
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("device-identity") || entry.FullName.Contains("account-session") || entry.FullName.Contains("device-check"));
        var targetPaths = new AppPaths(Path.Combine(source.Root, "target"));
        await BackupTests.SetBackupKeyAsync(targetPaths);
        await new SqliteBookLibraryService(targetPaths, new BookMetadataService()).InitializeAsync();
        using var target = new AccountService(targetPaths, source.Protector, source.Http, source.Clock);
        var targetId = (await target.GetDeviceAsync()).DeviceId;
        await new AppBackupService(targetPaths, new TestHelpers.PlaintextSecretProtector()).ImportAsync(archive);
        Assert.NotEqual(source.Handler.CurrentDeviceId, targetId); Assert.Equal(targetId, (await target.GetDeviceAsync()).DeviceId);
        Assert.False((await target.GetStatusAsync()).IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(targetPaths.Data, "device-check.json")));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan delta) => UtcNow += delta;
    }

    private sealed class EncryptedProtector : ISecretProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool Unavailable { get; set; }
        public byte[] Protect(byte[] value)
        {
            var output = new byte[value.Length + 28]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(output.AsSpan(0, 12), value, output.AsSpan(28), output.AsSpan(12, 16)); return output;
        }
        public byte[] Unprotect(byte[] value)
        {
            if (Unavailable) throw new CryptographicException("fictional locked keystore");
            var output = new byte[value.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), output); return output;
        }
    }

    private sealed class AccountScope : IDisposable
    {
        public string Root { get; } = TestHelpers.CreateTempDirectory();
        public AppPaths Paths { get; }
        public MembershipHttpHandler Handler { get; } = new();
        public EncryptedProtector Protector { get; } = new();
        public Clock Clock { get; }
        public HttpClient Http { get; }
        public AccountService Accounts { get; }
        public DeviceMembershipService Membership { get; }
        private AccountScope()
        {
            Paths = new(Path.Combine(Root, "source")); Clock = new(Handler.Now); Http = new(Handler);
            Accounts = new(Paths, Protector, Http, Clock); Membership = new(Accounts);
        }
        public static async Task<AccountScope> CreateAsync()
        {
            var scope = new AccountScope();
            try { await scope.Accounts.SignInAsync(AccountRegions.DefaultServerUrl(AccountRegion.China), scope.Handler.User.Username, MembershipHttpHandler.Password, true); return scope; }
            catch { scope.Dispose(); throw; }
        }
        public void Dispose() { Accounts.Dispose(); Http.Dispose(); SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(Root); }
    }
}
