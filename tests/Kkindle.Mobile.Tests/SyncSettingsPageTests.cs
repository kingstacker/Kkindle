using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile;
using Kkindle.Mobile.Controls;
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

    [Fact]
    public Task SheetsDimNativeSegmentsWithoutChangingStyle() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-mobile-sheet-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new TestPicker("unused"));
        var previousFactory = NativeSegmentGroup.CreateNativeControl;
        NativeSegmentGroup.CreateNativeControl = _ => new TestNativeSegment();
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            var library = (LibraryPage)shell.CurrentPage!;
            await Until(() => library.GetVisualDescendants().OfType<TestNativeSegment>().Any());
            var librarySegment = library.GetVisualDescendants().OfType<TestNativeSegment>().Single();
            await Until(() => librarySegment.IsVisible);
            Click(library.FindControl<Button>("SortButton")!);
            Assert.NotNull(shell.CurrentSheet);
            // The system control stays on screen; only a matching native scrim
            // dims it, so its glass style never changes during the animation.
            Assert.True(librarySegment.IsVisible);
            Assert.True(librarySegment.Blocked);
            await Task.Delay(300);
            Assert.True(librarySegment.IsVisible);
            Assert.NotNull(librarySegment.Scrim);

            // Reopening while the previous close animation is running must keep it visible.
            shell.HideSheet();
            shell.ShowSheet("排序", new ChoiceSheet([], _ => { }));
            await Task.Delay(300);
            Assert.True(librarySegment.IsVisible);
            shell.HideSheet();
            await Until(() => shell.CurrentSheet is null && librarySegment.IsVisible);
            Assert.False(librarySegment.Blocked);
            Assert.Null(librarySegment.Scrim);

            shell.ShowProfile();
            var profile = (ProfilePage)shell.CurrentPage!;
            await Until(() => profile.GetVisualDescendants().OfType<TestNativeSegment>().Any());
            var appearance = profile.GetVisualDescendants().OfType<TestNativeSegment>().Single();
            await Until(() => appearance.IsVisible);
            shell.ShowSheet("外观", new ChoiceSheet([], _ => { }));
            Assert.IsType<ChoiceSheet>(shell.CurrentSheet);
            Assert.True(appearance.IsVisible);
            await Task.Delay(300);
            Assert.True(appearance.IsVisible);
            Assert.NotNull(appearance.Scrim);
            shell.HideSheet();
            await Until(() => shell.CurrentSheet is null && appearance.IsVisible);
            Assert.False(appearance.Blocked);
        }
        finally
        {
            NativeSegmentGroup.CreateNativeControl = previousFactory;
            window.Close();
            await Task.Delay(100);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task BookDetailNativeActionsKeepTheirHandlersAndRespectSheets() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-mobile-book-actions", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new TestPicker("unused"));
        var previousMenuFactory = NativeMenuButton.CreateNativeControl;
        var previousActionFactory = NativeActionButton.CreateNativeControl;
        IReadOnlyList<MenuItem>? items = null;
        NativeMenuButton.CreateNativeControl = menuItems =>
        {
            items = menuItems;
            return new Border();
        };
        NativeActionButton.CreateNativeControl = _ => new Border();
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            shell.OpenBook(new Book { Title = "测试书籍", Description = new string('文', 150) });
            var page = Assert.IsType<BookDetailPage>(shell.CurrentPage);
            await Until(() => items is not null &&
                page.GetVisualDescendants().OfType<NativeActionButton>().Count() == 5 &&
                page.GetVisualDescendants().OfType<NativeActionButton>().All(group => group.Content is Grid));
            Assert.Equal(5, items!.Count);
            var expand = page.FindControl<Button>("ExpandButton")!;
            Click(expand);
            Assert.Equal("收起", expand.Content);
            var menu = page.GetVisualDescendants().OfType<NativeMenuButton>().Single();
            var nativeMenu = ((Grid)menu.Content!).Children.OfType<Border>().Single();
            var action = page.GetVisualDescendants().OfType<NativeActionButton>().First();
            var nativeAction = ((Grid)action.Content!).Children.OfType<Border>().Single();
            Assert.True(nativeMenu.IsVisible);
            Assert.True(nativeAction.IsVisible);
            items.Single(item => Equals(item.Header, "删除"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsType<PromptSheet>(shell.CurrentSheet);
            Assert.False(nativeMenu.IsVisible);
            Assert.False(nativeAction.IsVisible);
            shell.HideSheet();
            await Until(() => shell.CurrentSheet is null && nativeMenu.IsVisible && nativeAction.IsVisible);
        }
        finally
        {
            NativeMenuButton.CreateNativeControl = previousMenuFactory;
            NativeActionButton.CreateNativeControl = previousActionFactory;
            window.Close();
            await Task.Delay(100);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SearchFieldsOpenInlineAndClearFilters() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-mobile-search-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new TestPicker("unused"));
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            var library = (LibraryPage)shell.CurrentPage!;
            var search = library.FindControl<TextBox>("SearchBox")!;
            var searchButton = library.FindControl<Button>("SearchButton")!;
            var titles = library.FindControl<StackPanel>("MastheadTabs")!;
            Assert.False(search.IsVisible);
            Click(searchButton);
            Assert.True(search.IsVisible);
            Assert.False(titles.IsVisible);
            search.Text = "some book";
            Click(searchButton);
            Assert.False(search.IsVisible);
            Assert.True(titles.IsVisible);
            Assert.Equal(string.Empty, search.Text);

            var picker = new BookPickSheet(
                [new Book { Title = "Alpha" }, new Book { Title = "Beta" }], shell, _ => Task.CompletedTask);
            shell.ShowSheet("选书", picker);
            var filter = picker.FindControl<TextBox>("FilterBox")!;
            var filterButton = picker.FindControl<Button>("FilterSearchButton")!;
            var rows = picker.FindControl<ItemsControl>("Rows")!;
            Assert.False(filter.IsVisible);
            Click(filterButton);
            filter.Text = "Alpha";
            await Until(() => rows.Items.Count == 1);
            Click(filterButton);
            await Until(() => rows.Items.Count == 2);
            Assert.False(filter.IsVisible);
            Assert.Equal(string.Empty, filter.Text);
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

    [Fact]
    public Task SheetTracksTabBarOcclusionUntilDismissed() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-mobile-tab-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var tabBar = new TestOccludingTabBar();
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new TestPicker("unused"), TabBar: tabBar);
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            shell.ShowSheet("排序", new ChoiceSheet([new ChoiceItem("书名")], _ => { }));
            await Until(() => tabBar.SheetBounds is not null);
            Assert.True(tabBar.Visible);
            await Until(() => tabBar.SheetTops.Distinct().Count() > 1);
            shell.HideSheet();
            await Until(() => shell.CurrentSheet is null);
            Assert.True(tabBar.Visible);
            Assert.Null(tabBar.SheetBounds);
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

    [Fact]
    public Task ReaderSearchRunsTheSubmittedQuery() => session.Session.Dispatch(async () =>
    {
        string? searched = null;
        var sheet = new SearchSheet((query, _, _) =>
        {
            searched = query;
            return Task.FromResult<IReadOnlyList<SearchHit>>([new SearchHit("章节", "结果", query, new object())]);
        }, _ => { }, "关键词", searchImmediately: true);
        var window = new Window { Width = 411, Height = 880, Content = sheet };
        try
        {
            window.Show();
            await Until(() => sheet.FindControl<ItemsControl>("Results")!.Items.Count == 1);
            Assert.Equal("关键词", searched);
            Assert.Equal("共 1 条", sheet.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    private sealed class TestOccludingTabBar : IMobileTabBar, ISheetOccludingTabBar
    {
        public event Action<int>? TabSelected { add { } remove { } }
        public bool Visible { get; private set; }
        public Rect? SheetBounds { get; private set; }
        public List<double> SheetTops { get; } = [];
        public void Update(bool visible, int selectedIndex) => Visible = visible;
        public void SetDarkAppearance(bool dark) { }
        public void SetSheetOcclusion(Rect? sheetBounds)
        {
            SheetBounds = sheetBounds;
            if (sheetBounds is { } bounds) SheetTops.Add(bounds.Top);
        }
    }

    private sealed class TestNativeSegment : Control, INativeSegmentInteraction
    {
        public bool Blocked { get; private set; }
        public Color? Scrim { get; private set; }
        public void SetSheetInteractionBlocked(bool blocked) => Blocked = blocked;
        public void SetSheetScrim(Color? color, Color backdrop) => Scrim = color;
    }

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
