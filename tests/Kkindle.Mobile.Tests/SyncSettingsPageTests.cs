using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile;
using Kkindle.Mobile.Views;
using Kkindle.Mobile.Views.Sheets;
using Xunit;

namespace Kkindle.Mobile.Tests;

// Load the real mobile resources without starting the platform's application shell.
public sealed class TestMobileApp : MobileApp
{
    public override void OnFrameworkInitializationCompleted() { }
}

public sealed class MobileTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestMobileApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia();
}

public sealed class MobileUiSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(MobileTestApplication));
    public void Dispose() => Session.Dispose();
}

[CollectionDefinition("Mobile UI", DisableParallelization = true)]
public sealed class MobileUiCollection : ICollectionFixture<MobileUiSession>;

[Collection("Mobile UI")]
public sealed class SyncSettingsPageTests(MobileUiSession session)
{
    [Theory]
    [InlineData(SyncProvider.S3)]
    [InlineData(SyncProvider.WebDav)]
    public Task ImportEditsDraftAndSavePersistsIt(SyncProvider provider) => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-mobile-sync-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        var store = new S3SyncSettingsStore(paths, protector);
        var initial = new S3SyncSettings
        {
            EncryptionKey = "local-key", IntervalMinutes = 47, TimeoutSeconds = 70,
            ConcurrentRequests = 3, AutomaticSyncEnabled = false, DownloadBookFilesOnSync = true,
            WebDavUsername = "old-dav-user", WebDavPassword = "old-dav-password"
        };
        await store.SaveAsync("test-device", initial);
        var originalBytes = await File.ReadAllBytesAsync(store.SettingsPath);
        var profilePath = Path.Combine(root, "import.json");
        var json = provider == SyncProvider.S3
            ? """{"profileFormat":"KkindleS3ConnectionProfile","version":1,"endpoint":"https://s3.example.test","accessKey":"import-access","secretKey":"import-secret","bucket":"import-bucket","region":"eu-west-1","prefix":"import/s3","pathStyle":true,"skipTlsVerify":true,"timeoutSeconds":123,"concurrentRequests":9}"""
            : """{"profileFormat":"KkindleWebDavConnectionProfile","version":1,"endpoint":"https://dav.example.test/files","username":"import-user","password":"import-password","prefix":"import/dav","skipTlsVerify":true,"timeoutSeconds":123,"concurrentRequests":9}""";
        await File.WriteAllTextAsync(profilePath, json);
        var picker = new TestPicker(profilePath);
        var share = new TestShare();
        var services = new MobileServices(paths, protector, new BookMetadataService(),
            new UnavailableBookFormatConverter(), picker, FileShare: share);
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            shell.OpenSyncSettings();
            await Until(() => shell.GetVisualDescendants().OfType<SyncSettingsPage>().Any());
            var page = shell.GetVisualDescendants().OfType<SyncSettingsPage>().Single();
            T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
            await Until(() => Get<TextBox>("EncryptionKeyBox").Text == "local-key");

            // The value edited before import must win over the last saved key.
            Get<TextBox>("EncryptionKeyBox").Text = "draft-key";
            Click(Get<Button>("ImportProfileButton"));
            await Until(() => picker.Released);
            Assert.Contains("参数已导入", Get<TextBlock>("StatusText").Text);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(store.SettingsPath));
            Assert.Equal(initial, (await store.LoadAsync()).Settings);
            Assert.Equal("draft-key", Get<TextBox>("EncryptionKeyBox").Text);
            Assert.False(Get<ToggleSwitch>("AutomaticSwitch").IsChecked);
            Assert.True(Get<ToggleSwitch>("DownloadFilesSwitch").IsChecked);
            Assert.True(Get<ToggleSwitch>("SkipTlsSwitch").IsChecked);
            Assert.Equal(provider == SyncProvider.S3, Get<StackPanel>("S3Fields").IsVisible);
            Assert.Equal(provider == SyncProvider.WebDav, Get<StackPanel>("WebDavFields").IsVisible);
            if (provider == SyncProvider.S3)
            {
                Assert.Equal("https://s3.example.test", Get<TextBox>("EndpointBox").Text);
                Assert.Equal("import-bucket", Get<TextBox>("BucketBox").Text);
                Assert.Equal("eu-west-1", Get<TextBox>("RegionBox").Text);
                Assert.Equal("import-access", Get<TextBox>("AccessKeyBox").Text);
                Assert.Equal("import-secret", Get<TextBox>("SecretKeyBox").Text);
                Assert.True(Get<ToggleSwitch>("PathStyleSwitch").IsChecked);
            }
            else
            {
                Assert.Equal("https://dav.example.test/files", Get<TextBox>("WebDavEndpointBox").Text);
                Assert.Equal("import-user", Get<TextBox>("WebDavUserBox").Text);
                Assert.Equal("import-password", Get<TextBox>("WebDavPasswordBox").Text);
            }
            Assert.Equal(provider == SyncProvider.S3 ? "import/s3" : "import/dav", Get<TextBox>("PrefixBox").Text);

            // Export from the unsaved form, including another edit after import.
            Get<TextBox>("PrefixBox").Text = "edited/draft";
            Click(Get<Button>("ExportProfileButton"));
            await Until(() => shell.GetVisualDescendants().OfType<PromptSheet>().Any());
            var prompt = shell.GetVisualDescendants().OfType<PromptSheet>().Single();
            Click(prompt.FindControl<Button>("ConfirmButton")!);
            await Until(() => share.Content is not null);
            using (var exported = JsonDocument.Parse(share.Content!))
            {
                Assert.Equal("edited/draft", exported.RootElement.GetProperty("prefix").GetString());
                Assert.Equal(123, exported.RootElement.GetProperty("timeoutSeconds").GetInt32());
                Assert.Equal(9, exported.RootElement.GetProperty("concurrentRequests").GetInt32());
                Assert.False(exported.RootElement.TryGetProperty("encryptionKey", out _));
                Assert.DoesNotContain("draft-key", share.Content!);
                Assert.Equal(provider == SyncProvider.S3 ? "import-access" : "import-user",
                    exported.RootElement.GetProperty(provider == SyncProvider.S3 ? "accessKey" : "username").GetString());
            }
            await Until(() => Get<Button>("SaveButton").IsEnabled);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(store.SettingsPath));
            Click(Get<Button>("SaveButton"));
            await Until(() => Get<Button>("SaveButton").IsEnabled);
            var saved = (await store.LoadAsync()).Settings;
            Assert.Equal(provider, saved.Provider);
            Assert.Equal("edited/draft", saved.Prefix);
            Assert.Equal("draft-key", saved.EncryptionKey);
            Assert.Equal(47, saved.IntervalMinutes);
            Assert.Equal(123, saved.TimeoutSeconds);
            Assert.Equal(9, saved.ConcurrentRequests);
            Assert.False(saved.AutomaticSyncEnabled);
            Assert.True(saved.DownloadBookFilesOnSync);
            Assert.True(saved.SkipTlsVerify);
            if (provider == SyncProvider.S3)
            {
                Assert.Equal("import-bucket", saved.Bucket);
                Assert.Equal("import-access", saved.AccessKey);
                Assert.Equal("import-secret", saved.SecretKey);
                Assert.True(saved.PathStyle);
                Assert.Equal("old-dav-user", saved.WebDavUsername);
                Assert.Equal("old-dav-password", saved.WebDavPassword);
            }
            else
            {
                Assert.Equal("https://dav.example.test/files", saved.WebDavEndpoint);
                Assert.Equal("import-user", saved.WebDavUsername);
                Assert.Equal("import-password", saved.WebDavPassword);
            }
        }
        finally
        {
            window.Close();
            await Task.Delay(100);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return true;
    }, CancellationToken.None);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private sealed class TestPicker(string path) : IFileImportSource
    {
        public bool Released { get; private set; }
        public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([path]);
        public void ReleaseFiles(IReadOnlyList<string> files)
        {
            Assert.Equal(new[] { path }, files);
            Released = true;
        }
    }

    private sealed class TestShare : IMobileFileShare
    {
        public string? Content { get; private set; }
        public Task<bool> ShareAsync(string fileName, byte[] content) => throw new NotSupportedException();
        public async Task<bool> ShareFileAsync(string path)
        {
            Content = await File.ReadAllTextAsync(path);
            return true;
        }
    }
}
