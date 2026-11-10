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
    [InlineData("custom", "https://example.invalid/v1")]
    [InlineData("openai", "https://api.openai.com/proxy/v1")]
    [InlineData("deepseek", "https://api.openai.com/v1")]
    public async Task CustomAiEndpointsAreFreeWithoutAnAccount(string provider, string url)
    {
        // The membership plan makes custom AI interfaces free for every tier.
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ => new()
        { Content = new StringContent("{\"data\":[{\"id\":\"fictional-model\"}]}", Encoding.UTF8, "application/json") }));
        Assert.Equal("fictional-model", Assert.Single(await client.ListModelsAsync(new()
            { Provider = provider, BaseUrl = url, ApiKey = "<FICTIONAL_API_KEY>" })));
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
    [InlineData(SyncProvider.S3, "test")]
    [InlineData(SyncProvider.S3, "sync")]
    [InlineData(SyncProvider.S3, "download")]
    [InlineData(SyncProvider.WebDav, "test")]
    [InlineData(SyncProvider.WebDav, "sync")]
    [InlineData(SyncProvider.WebDav, "download")]
    [InlineData(SyncProvider.AccountServer, "test")]
    [InlineData(SyncProvider.AccountServer, "sync")]
    [InlineData(SyncProvider.AccountServer, "download")]
    public async Task UnlicensedSyncIsRejectedBeforeCreatingTheObjectStore(SyncProvider provider, string operation)
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
            var settings = new S3SyncSettings { Enabled = true, Provider = provider, Bucket = "fictional-bucket",
                Endpoint = "https://example.invalid", AccessKey = "<FICTIONAL_ACCESS_KEY>", SecretKey = "<FICTIONAL_SECRET_KEY>",
                WebDavEndpoint = "https://example.invalid/dav", AccountServerUrl = "https://example.invalid", AccountUserId = Guid.NewGuid(),
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
    public async Task CustomS3IsCheckedAgainstTheServerBeforeEveryOperation()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        Assert.Equal(1, scope.Handler.FeatureChecks);
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
        Assert.False(status.CanUseAccountCloudSync);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
    }

    [Fact]
    public async Task SixActiveDevicesAreAcceptedAndSeventhIsRejected()
    {
        using var scope = await AccountScope.CreateAsync();
        for (var index = scope.Handler.Devices.Count; index < 6; index++)
            scope.Handler.Devices.Add(new(Guid.NewGuid(), "Fictional additional device", "Test", "active", scope.Clock.UtcNow, scope.Clock.UtcNow));
        var status = await scope.Membership.RefreshAsync();
        Assert.Equal(6, status.Membership.DeviceLimit);
        Assert.Equal(6, status.Membership.LoggedInDeviceCount);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        scope.Handler.Devices.Add(new(Guid.NewGuid(), "Fictional seventh device", "Test", "active", scope.Clock.UtcNow, scope.Clock.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RefreshAsync());
    }

    [Fact]
    public async Task FeatureUnlockUsesOwnStorageAndOnlyActivePlusUsesOfficialCloud()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Handler.Now.AddDays(1);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        scope.Handler.Now = scope.Handler.ProExpiresAt.Value;
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        scope.Handler.LifetimeFeatures = true;
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
    }

    [Fact]
    public async Task PlusExpiryIsAnnouncedOnceAcrossRestartsWithoutSigningTheAccountOut()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Clock.UtcNow.AddDays(-1);
        var notices = new List<AccountMembershipExpiredEventArgs>();
        using (var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            monitor.MembershipExpired += (_, notice) => notices.Add(notice);
            Assert.True(await monitor.CheckIfDueAsync());
        }
        var notice = Assert.Single(notices);
        Assert.Equal(scope.Handler.ProExpiresAt.Value.AddMonths(1), notice.CloudRetentionEndsAt);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        scope.Clock.Advance(TimeSpan.FromDays(1));
        using (var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            monitor.MembershipExpired += (_, next) => notices.Add(next);
            await monitor.CheckIfDueAsync();
        }
        Assert.Single(notices);
    }

    [Fact]
    public async Task KnownPlusExpiryIsCheckedBeforeTheNextDailyHeartbeat()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Clock.UtcNow.AddHours(2);
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        var notices = 0; monitor.MembershipExpired += (_, _) => notices++;
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(0, notices);
        scope.Clock.Advance(TimeSpan.FromHours(2)); scope.Handler.Now = scope.Clock.UtcNow;
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(1, notices);
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
            "limit" => status with { Membership = status.Membership with { DeviceLimit = MemberEntitlements.DeviceLimit + 1 } },
            "count" => status with { Membership = status.Membership with { LoggedInDeviceCount = MemberEntitlements.DeviceLimit + 1 } },
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

    [Fact]
    public async Task ActivationRequiresLoginAndDoesNotSendTheCodeWhileSignedOut()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Accounts.SignOutAsync();
        var error = await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", Guid.NewGuid()));
        Assert.Contains("登录", error.Message); Assert.Equal(0, scope.Handler.ActivationAttempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RedeemActivationCodeAsync(" ", Guid.NewGuid()));
        Assert.Equal(0, scope.Handler.ActivationAttempts);
    }

    [Fact]
    public async Task ActivationReturnsValidatedRightsAndLostResponsesCanBeRetriedWithoutExtendingAgain()
    {
        using var scope = await AccountScope.CreateAsync(); scope.Handler.LifetimeFeatures = false;
        scope.Handler.LoseNextActivationResponse = true;
        var request = Guid.NewGuid(); var now = scope.Handler.Now;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", request));
        var result = await scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", request);
        Assert.Equal("pro", result.Kind); Assert.Equal(1, result.Years); Assert.True(result.Status.CanUseAccountCloudSync);
        Assert.True(result.AlreadyRedeemed);
        Assert.Equal(now.AddDays(365), result.Status.Membership.ProExpiresAt);
        Assert.Equal(scope.Handler.CurrentDeviceId, result.Status.CurrentDeviceId); Assert.Equal(scope.Handler.User.Id, result.Status.Membership.AccountId);
        Assert.Equal(2, scope.Handler.ActivationAttempts);
        var used = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", Guid.NewGuid()));
        Assert.Contains("已使用", used.Message);
        scope.Handler.ProExpiresAt = now;
        Assert.Equal("free", (await scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", request)).Status.Membership.Tier);
    }

    [Theory]
    [InlineData("account")] [InlineData("region")] [InlineData("device")] [InlineData("years")] [InlineData("id")] [InlineData("pending")]
    public async Task ActivationRejectsMalformedOrMismatchedServerRights(string field)
    {
        using var scope = await AccountScope.CreateAsync(); scope.Handler.LifetimeFeatures = false;
        scope.Handler.TransformRedemption = value => field switch
        {
            "account" => value with { Status = value.Status with { Membership = value.Status.Membership with { AccountId = Guid.NewGuid() } } },
            "region" => value with { Status = value.Status with { Membership = value.Status.Membership with { Region = AccountRegion.International } } },
            "device" => value with { Status = value.Status with { CurrentDeviceId = Guid.NewGuid() } },
            "years" => value with { Years = 5 }, "id" => value with { CodeId = Guid.Empty },
            _ => value with { Status = value.Status with { Membership = value.Status.Membership with { PendingPlusDays = -1 } } }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", Guid.NewGuid()));
    }

    [Fact]
    public async Task FiveYearRenewalUiRuleIncludesPendingOrdersAndPreservesExpiredMembershipHistory()
    {
        using var scope = await AccountScope.CreateAsync(); var now = scope.Handler.Now;
        var member = (await scope.Membership.RefreshAsync()).Membership;
        Assert.True(MemberEntitlements.CanExtendPlus(member, 1825, now));
        Assert.True(MemberEntitlements.CanExtendPlus(member with { ProExpiresAt = now.AddDays(-10) }, 365, now));
        Assert.False(MemberEntitlements.CanExtendPlus(member with { ProExpiresAt = now.AddDays(1825) }, 365, now));
        Assert.False(MemberEntitlements.CanExtendPlus(member with { ProExpiresAt = now.AddDays(1460), PendingPlusDays = 365 }, 365, now));
        Assert.True(MemberEntitlements.CanExtendPlus(member with { ProExpiresAt = now.AddDays(1460) }, 365, now));
        Assert.False(MemberEntitlements.CanExtendPlus(member, 1826, now));
        scope.Handler.ActivationFailureCode = "membership_limit";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RedeemActivationCodeAsync("fictional-plus-code", Guid.NewGuid()));
        Assert.Contains("5 年", error.Message); Assert.Null(scope.Handler.ProExpiresAt);
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
            try { await scope.Accounts.SignInAsync(AccountRegions.DefaultServerUrl(AccountRegion.China), scope.Handler.User.Username, MembershipHttpHandler.Password, true, email: scope.Handler.User.Email, confirmPassword: MembershipHttpHandler.Password); return scope; }
            catch { scope.Dispose(); throw; }
        }
        public void Dispose() { Accounts.Dispose(); Http.Dispose(); SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(Root); }
    }
}
