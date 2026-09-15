using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class SettingsConcurrencyTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("ai")]
    [InlineData("email")]
    [InlineData("zlibrary")]
    [InlineData("tts")]
    [InlineData("sync")]
    public async Task SavingSettingsPreservesAnOpenSnapshot(string kind)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var deviceId = Guid.NewGuid().ToString("N");
            (string Path, Func<string, Task> Save) store = kind switch
            {
                "app" => (paths.Settings,
                    value => new AppSettingsStore(paths).SaveAsync(new AppSettings { CalibrePath = value })),
                "ai" => (Path.Combine(paths.Data, "ai-settings.json"),
                    value => new AiSettingsStore(paths, protector).SaveAsync(new AiConnectionSettings { Model = value })),
                "email" => (Path.Combine(paths.Data, "kindle-email-settings.json"),
                    value => new KindleEmailSettingsStore(paths, protector).SaveAsync(new KindleEmailSettings { SmtpHost = value })),
                "zlibrary" => (Path.Combine(paths.Data, "zlibrary-settings.json"),
                    value => new ZLibrarySettingsStore(paths, protector).SaveAsync(new ZLibrarySettings { Email = value })),
                "tts" => (Path.Combine(paths.Data, "tts-settings.json"),
                    value => new TtsSettingsStore(paths).SaveAsync(new TtsSettings { Model = value })),
                "sync" => (Path.Combine(paths.Data, "s3-sync-settings.json"),
                    value => new S3SyncSettingsStore(paths, protector).SaveAsync(deviceId, new S3SyncSettings { Bucket = value })),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var (path, save) = store;
            await save("old-value");
            var previousJson = await File.ReadAllTextAsync(path);
            await using var snapshot = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            await save("new-value");

            using var reader = new StreamReader(snapshot);
            Assert.Equal(previousJson, await reader.ReadToEndAsync());
            var currentJson = await File.ReadAllTextAsync(path);
            Assert.NotEqual(previousJson, currentJson);
            using var parsed = JsonDocument.Parse(currentJson);
            Assert.Contains("new-value", currentJson);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReadingAnOldSettingsSnapshotAllowsAnAtomicSave()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var plaintext = new TestHelpers.PlaintextSecretProtector();
            var writer = new AiSettingsStore(paths, plaintext);
            await writer.SaveAsync(new AiConnectionSettings { ApiKey = "old-key" });
            var protector = new CallbackProtector(() =>
                Task.Run(() => writer.SaveAsync(new AiConnectionSettings { ApiKey = "new-key" })).GetAwaiter().GetResult());

            // Unprotect runs after deserialization, while LoadAsync still owns
            // its file handle. Saving concurrently must not invalidate either view.
            var previous = await new AiSettingsStore(paths, protector).LoadAsync();

            Assert.Equal("old-key", previous.ApiKey);
            Assert.Equal("new-key", (await writer.LoadAsync()).ApiKey);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RepairingAnInvalidSyncDeviceIdPreservesSettingsAndPersistsTheNewId()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            var store = new S3SyncSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            await File.WriteAllTextAsync(store.SettingsPath,
                """{"DeviceId":"invalid-id","Bucket":"my-books","Endpoint":"https://s3.example.com"}""");

            var first = await store.LoadAsync();
            var second = await store.LoadAsync();

            Assert.Equal("my-books", first.Settings.Bucket);
            Assert.Equal("https://s3.example.com", first.Settings.Endpoint);
            Assert.True(Guid.TryParseExact(first.DeviceId, "N", out _));
            Assert.Equal(first.DeviceId, second.DeviceId);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
            Assert.Equal(first.DeviceId, persisted.RootElement.GetProperty("DeviceId").GetString());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private sealed class CallbackProtector(Action whileReading) : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) { whileReading(); return value.ToArray(); }
    }
}
