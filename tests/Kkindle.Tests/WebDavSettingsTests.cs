using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class WebDavSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/dav/books")]
    [InlineData("ftp://example.test/books")]
    [InlineData("https://user:password@example.test/dav")]
    [InlineData("https://example.test/dav?token=secret")]
    [InlineData("https://example.test/dav#fragment")]
    [InlineData("https://example.test/dav\\books")]
    [InlineData("https://example.test/da\tv")]
    public void InvalidWebDavAddressesAreRejected(string endpoint)
    {
        var settings = S3SyncSettings.Normalize(new S3SyncSettings { Provider = SyncProvider.WebDav, WebDavEndpoint = endpoint });
        Assert.False(settings.IsConfigured);
        Assert.Contains("WebDAV", settings.Validate());
    }

    [Fact]
    public void ValidationUsesOnlySelectedProviderAndSupportsAnonymousWebDav()
    {
        var settings = S3SyncSettings.Normalize(new S3SyncSettings
        {
            Provider = SyncProvider.WebDav, WebDavEndpoint = " https://DAV.example.test:443/remote.php/dav/// ",
            WebDavUsername = " user ", WebDavPassword = " app password ", Endpoint = "invalid inactive S3 URL"
        });
        Assert.Equal("https://dav.example.test/remote.php/dav", settings.WebDavEndpoint);
        Assert.Equal("user", settings.WebDavUsername);
        Assert.Equal(" app password ", settings.WebDavPassword);
        Assert.True(settings.IsConfigured);
        Assert.True((settings with { WebDavUsername = "", WebDavPassword = "" }).IsConfigured);
        Assert.False((settings with { WebDavUsername = "" }).IsConfigured);
        Assert.False((settings with { WebDavUsername = "user:name" }).IsConfigured);
        Assert.False((settings with { Prefix = "books\0invalid" }).IsConfigured);
        Assert.False((settings with { Provider = SyncProvider.S3 }).IsConfigured);
        Assert.False((settings with { Provider = (SyncProvider)42 }).IsConfigured);
    }

    [Fact]
    public async Task LegacySettingsUpgradeKeepsDeviceAndBothSetsOfProtectedCredentials()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            var store = new S3SyncSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            var id = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(store.SettingsPath, JsonSerializer.Serialize(new
            {
                DeviceId = id, Enabled = true, Bucket = "legacy-books", Prefix = "sync",
                ProtectedAccessKey = Convert.ToBase64String("legacy-access"u8),
                ProtectedSecretKey = Convert.ToBase64String("legacy-secret"u8),
                ProtectedEncryptionKey = Convert.ToBase64String("encryption-secret"u8)
            }));
            var legacy = await store.LoadAsync();
            Assert.Equal(id, legacy.DeviceId);
            Assert.Equal(SyncProvider.S3, legacy.Settings.Provider);
            Assert.True(legacy.Settings.IsConfigured);

            var dav = legacy.Settings with
            {
                Provider = SyncProvider.WebDav, WebDavEndpoint = "https://dav.example.test/dav",
                WebDavUsername = "protected-webdav-user", WebDavPassword = " protected-webdav-password "
            };
            await store.SaveAsync(id, dav);
            var reloaded = await store.LoadAsync();
            Assert.Equal(id, reloaded.DeviceId);
            Assert.Equal(dav, reloaded.Settings);
            var json = await File.ReadAllTextAsync(store.SettingsPath);
            foreach (var secret in new[] { dav.AccessKey, dav.SecretKey, dav.WebDavUsername, dav.WebDavPassword, dav.EncryptionKey })
                Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            await store.SaveAsync(id, reloaded.Settings with { Provider = SyncProvider.S3 });
            var back = await store.LoadAsync();
            Assert.Equal(dav with { Provider = SyncProvider.S3 }, back.Settings);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("ProtectedWebDavUsername")]
    [InlineData("ProtectedWebDavPassword")]
    public async Task UnreadableCredentialsDisableSyncInsteadOfDowngradingToAnonymous(string field)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var store = new S3SyncSettingsStore(new AppPaths(root), new TestHelpers.PlaintextSecretProtector());
            var id = Guid.NewGuid().ToString("N");
            await store.SaveAsync(id, new WebDavTestServer().Settings());
            var json = JsonNode.Parse(await File.ReadAllTextAsync(store.SettingsPath))!;
            json[field] = "invalid protected credential";
            await File.WriteAllTextAsync(store.SettingsPath, json.ToJsonString());
            var loaded = await store.LoadAsync();
            Assert.Equal(id, loaded.DeviceId);
            Assert.Equal(SyncProvider.WebDav, loaded.Settings.Provider);
            Assert.False(loaded.Settings.Enabled);
            Assert.NotEmpty(loaded.Settings.WebDavEndpoint);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(SyncProvider.S3)]
    [InlineData(SyncProvider.WebDav)]
    public async Task ProfilesExportOnlySelectedCredentialsAndPreserveOtherConnection(SyncProvider provider)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = new WebDavTestServer().Settings() with
            {
                Provider = provider, Endpoint = "https://s3.example.test", Bucket = "books",
                AccessKey = "source-access", SecretKey = "source-secret", EncryptionKey = "never-export-key"
            };
            var path = Path.Combine(root, "profile" + SyncConnectionProfileService.FileExtension(provider));
            await SyncConnectionProfileService.ExportAsync(path, source);
            var json = await File.ReadAllTextAsync(path);
            using var exported = JsonDocument.Parse(json);
            Assert.DoesNotContain(source.EncryptionKey, json, StringComparison.Ordinal);
            if (provider == SyncProvider.WebDav)
            {
                Assert.DoesNotContain("source-access", json, StringComparison.Ordinal);
                Assert.DoesNotContain("source-secret", json, StringComparison.Ordinal);
                Assert.Equal(source.WebDavPassword, exported.RootElement.GetProperty("password").GetString());
            }
            else
            {
                Assert.DoesNotContain("webDav", json, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(source.SecretKey, exported.RootElement.GetProperty("secretKey").GetString());
            }
            var local = source with
            {
                Provider = provider == SyncProvider.S3 ? SyncProvider.WebDav : SyncProvider.S3,
                Enabled = false, IntervalMinutes = 90, EncryptionKey = "keep-local-key",
                AccessKey = "local-access", SecretKey = "local-secret", WebDavPassword = "local-dav-password"
            };
            var imported = await SyncConnectionProfileService.ImportAsync(path, local);
            Assert.Equal(provider, imported.Provider);
            Assert.False(imported.Enabled);
            Assert.Equal(90, imported.IntervalMinutes);
            Assert.Equal("keep-local-key", imported.EncryptionKey);
            Assert.Equal(provider == SyncProvider.S3 ? source.SecretKey : local.SecretKey, imported.SecretKey);
            Assert.Equal(provider == SyncProvider.WebDav ? source.WebDavPassword : local.WebDavPassword, imported.WebDavPassword);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"profileFormat\":\"KkindleWebDavConnectionProfile\",\"version\":99}")]
    public async Task UnknownProfileFormatsAreNotTreatedAsS3(string json)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "profile.json");
            await File.WriteAllTextAsync(path, json);
            await Assert.ThrowsAsync<InvalidDataException>(() => SyncConnectionProfileService.ImportAsync(path, new S3SyncSettings()));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BackupIncludesWebDavChoiceButExcludesCredentialsAndDisablesNewRemote()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var protector = new TestHelpers.PlaintextSecretProtector();
            var source = new AppPaths(Path.Combine(root, "source"));
            var target = new AppPaths(Path.Combine(root, "target"));
            foreach (var paths in new[] { source, target })
            {
                await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
                await new ReaderDataService(paths).InitializeAsync();
            }
            var settings = new WebDavTestServer().Settings() with { EncryptionKey = "backup-excludes-key" };
            var store = new S3SyncSettingsStore(source, protector);
            await store.SaveAsync(Guid.NewGuid().ToString("N"), settings);
            var backup = Path.Combine(root, "dav.kkindle");
            await new AppBackupService(source, protector).ExportAsync(backup);
            using (var zip = ZipFile.OpenRead(backup))
            using (var reader = new StreamReader(zip.GetEntry("settings/settings.json")!.Open()))
            {
                var json = await reader.ReadToEndAsync();
                Assert.DoesNotContain(settings.WebDavUsername, json, StringComparison.Ordinal);
                Assert.DoesNotContain("app:password", json, StringComparison.Ordinal);
                Assert.DoesNotContain(settings.EncryptionKey, json, StringComparison.Ordinal);
                Assert.Contains("dav.example.test", json, StringComparison.Ordinal);
            }
            var restored = await new AppBackupService(target, protector).ImportAsync(backup);
            Assert.Equal(SyncProvider.WebDav, restored.S3Settings!.Provider);
            Assert.Equal(settings.WebDavEndpoint, restored.S3Settings.WebDavEndpoint);
            Assert.False(restored.S3Settings.Enabled);
            Assert.Empty(restored.S3Settings.WebDavPassword);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
