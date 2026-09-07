using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed class SettingsTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Kkindle.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia();
}

public sealed class SettingsUiSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(SettingsTestApplication));
    public void Dispose() => Session.Dispose();
}

[CollectionDefinition("Settings UI", DisableParallelization = true)]
public sealed class SettingsUiCollection : ICollectionFixture<SettingsUiSession>;

[Collection("Settings UI")]
public sealed class SettingsTests(SettingsUiSession session)
{
    [Fact]
    public Task ClosingImmediatelyFlushesPreferencesAndLanguage() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<ToggleSwitch>("GridGalleryDisplayCheck").IsChecked = true;
        scope.Get<ComboBox>("PreferredOpenFormatBox").SelectedIndex = 1;
        scope.Get<ComboBox>("UiLanguageBox").SelectedIndex = 1;
        scope.Get<TextBox>("CalibrePathBox").Text = Path.Combine(scope.Paths.Data, "ebook-convert-test");
        scope.Window.Close();
        await Until(() => !scope.Window.IsVisible);
        var stored = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.True(stored.GridGalleryDisplay);
        Assert.Equal("pdf", stored.PreferredOpenFormat);
        Assert.Equal("en-US", stored.UiLanguage);
        Assert.Equal(Path.Combine(scope.Paths.Data, "ebook-convert-test"), stored.CalibrePath);
    });

    [Fact]
    public Task ClosingDuringStartupStillFlushesEditablePreferences() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Set("_stage3Ready", false);
        scope.Get<ToggleSwitch>("GridGalleryDisplayCheck").IsChecked = true;
        scope.Window.Close();
        await Until(() => !scope.Window.IsVisible);
        Assert.True((await new AppSettingsStore(scope.Paths).LoadAsync()).GridGalleryDisplay);
    });

    [Fact]
    public Task RepeatedCloseWaitsForInFlightSaveAndLatestEdits() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var lease = await AcquireSettingsWriteLock(scope.Paths);
        scope.Get<ToggleSwitch>("GridGalleryDisplayCheck").IsChecked = true;
        var saving = scope.Call<Task<bool>>("SaveAppSettingsCoreAsync");
        Assert.False(saving.IsCompleted);
        scope.Get<ToggleSwitch>("GridGalleryDisplayCheck").IsChecked = false;
        scope.Get<ComboBox>("PreferredOpenFormatBox").SelectedIndex = 2;
        scope.Window.Close();
        scope.Window.Close();
        await Task.Delay(50);
        Assert.True(scope.Window.IsVisible);
        lease.Dispose();
        Assert.True(await saving);
        await Until(() => !scope.Window.IsVisible);
        var stored = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.False(stored.GridGalleryDisplay);
        Assert.Equal("azw3", stored.PreferredOpenFormat);
    });

    [Fact]
    public Task FailedPreferenceSaveKeepsWindowOpenAndCanBeRetried() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        Directory.CreateDirectory(scope.Paths.Settings + ".tmp");
        scope.Get<ToggleSwitch>("GridGalleryDisplayCheck").IsChecked = true;
        scope.Window.Close();
        await Until(() => !scope.Field<bool>("_s3SyncExitInProgress"));
        Assert.True(scope.Window.IsVisible);
        Assert.Contains("保存失败", scope.Get<TextBlock>("SettingsStatusText").Text);
        Directory.Delete(scope.Paths.Settings + ".tmp");
        scope.Window.Close();
        await Until(() => !scope.Window.IsVisible);
        Assert.True((await new AppSettingsStore(scope.Paths).LoadAsync()).GridGalleryDisplay);
    });

    [Fact]
    public Task S3ChangesAreDraftsUntilExplicitSaveAndSurviveNavigationAndRefresh() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var original = await scope.Sync.LoadSettingsAsync();
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        scope.Get<ToggleSwitch>("S3SyncEnabledCheck").IsChecked = true;
        scope.Get<ToggleSwitch>("S3AutomaticSyncCheck").IsChecked = false;
        scope.Get<TextBox>("S3AccessKeyBox").Text = "test-access";
        scope.Get<TextBox>("S3SecretKeyBox").Text = "test-secret";
        scope.Get<TextBox>("S3BucketBox").Text = "test-bucket";
        Assert.Equal(original.Settings, (await scope.Sync.LoadSettingsAsync()).Settings);
        Assert.True(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        scope.Call("ShowSettingsSection", "Library");
        await scope.Call<Task>("RefreshSettingsAfterS3SyncAsync", CancellationToken.None);
        scope.Call("ShowSettingsSection", "Data");
        Assert.Equal("test-bucket", scope.Get<TextBox>("S3BucketBox").Text);
        Assert.True(scope.Get<ToggleSwitch>("S3SyncEnabledCheck").IsChecked);
        Assert.False(await scope.Call<Task<bool>>("RunS3SyncAsync", false, CancellationToken.None));
        Assert.Equal(original.Settings, (await scope.Sync.LoadSettingsAsync()).Settings);
        Assert.True(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));
        var saved = await scope.Sync.LoadSettingsAsync();
        Assert.True(saved.Settings.Enabled);
        Assert.Equal("test-bucket", saved.Settings.Bucket);
        scope.Get<TextBox>("S3BucketBox").Text = "discard-me";
        scope.Call("S3DiscardSettingsButton_Click", null, new RoutedEventArgs());
        Assert.Equal("test-bucket", scope.Get<TextBox>("S3BucketBox").Text);
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
    });

    [Fact]
    public Task S3SaveDoesNotOverwriteAnEditMadeDuringDiskWrite() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var lease = await AcquireSettingsWriteLock(scope.Paths);
        scope.Get<TextBox>("S3BucketBox").Text = "first-draft";
        var saving = scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None);
        scope.Get<TextBox>("S3BucketBox").Text = "next-draft";
        lease.Dispose();
        Assert.True(await saving);
        Assert.Equal("first-draft", (await scope.Sync.LoadSettingsAsync()).Settings.Bucket);
        Assert.Equal("next-draft", scope.Get<TextBox>("S3BucketBox").Text);
        Assert.True(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
    });

    [Fact]
    public Task ClosingWithS3DraftOffersReturnOrDiscardWithoutSavingIt() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<TextBox>("S3BucketBox").Text = "unsaved-bucket";
        scope.Window.Close();
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Window.Close();
        Assert.True(scope.Window.IsVisible);
        scope.Call("ConfirmationCancelButton_Click", null, new RoutedEventArgs());
        await Until(() => !scope.Field<bool>("_s3SyncExitInProgress"));
        Assert.Equal("unsaved-bucket", scope.Get<TextBox>("S3BucketBox").Text);
        scope.Window.Close();
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Call("ConfirmationOkButton_Click", null, new RoutedEventArgs());
        await Until(() => !scope.Window.IsVisible);
        Assert.NotEqual("unsaved-bucket", (await scope.Sync.LoadSettingsAsync()).Settings.Bucket);
    });

    [Fact]
    public Task TabSkipsInactiveCategoriesAndCollapsedForms() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        // Exercise a form whose template has already been created, as well as
        // the other forms that have never been expanded.
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = true;
        await Render();
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = false;
        await Render();
        scope.Get<Button>("SettingsDataButton").Focus();
        for (var index = 0; index < 60; index++)
        {
            scope.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            scope.Window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.IsAssignableFrom<Control>(scope.Window.FocusManager!.GetFocusedElement());
            var focused = (Control)scope.Window.FocusManager.GetFocusedElement()!;
            Assert.True(focused.IsEffectivelyVisible, $"Hidden focus: {focused.Name}");
            Assert.DoesNotContain(focused.GetVisualAncestors().OfType<Control>(), c => c.Name is
                "SettingsLibrarySection" or "SettingsReadingSection" or "SettingsKindleSection" or "SettingsAboutSection"
                or "SettingsBackupSection");
        }
        Assert.False(scope.Get<Control>("S3EncryptionKeyBox").IsEffectivelyVisible);
        Assert.False(scope.Get<Control>("S3RegionBox").IsEffectivelyVisible);
        scope.Get<Expander>("SettingsS3Expander").IsExpanded = false;
        await Render();
        Assert.False(scope.Get<Control>("S3EndpointBox").IsEffectivelyVisible);
        Assert.False(scope.Get<Control>("S3SaveSettingsButton").IsEffectivelyVisible);
    });

    [Fact]
    public Task ServiceShortcutsOpenTheirCategoryAndPreserveDrafts() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = 1024;
        scope.Window.Height = 664;
        scope.Call("KindleEmailSettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.True(scope.Get<Control>("SettingsKindleSection").IsEffectivelyVisible);
        Assert.Same(scope.Get<Control>("KindleEmailRecipientBox"), scope.Window.FocusManager!.GetFocusedElement());
        scope.Get<TextBox>("KindleEmailRecipientBox").Text = "draft@kindle.com";
        Capture(scope.Window, "zh-CN-1024-email");
        await scope.Call<Task>("ShowZLibraryAccountAsync", (object?)null);
        await Render();
        Assert.Same(scope.Get<Control>("ZLibraryEmailBox"), scope.Window.FocusManager!.GetFocusedElement());
        Capture(scope.Window, "zh-CN-1024-account");
        scope.Call("ReaderAiSettingsButton_Click", null, new RoutedEventArgs());
        await Until(() => scope.Field<bool>("_mainReaderAiSettingsLoaded"));
        await Render();
        Assert.True(scope.Get<Control>("SettingsReadingSection").IsEffectivelyVisible);
        scope.Get<TextBox>("MainReaderAiBaseUrlBox").Text = "https://draft.example.test";
        Capture(scope.Window, "zh-CN-1024-ai");
        scope.Call("KindleEmailSettingsButton_Click", null, new RoutedEventArgs());
        Assert.Equal("draft@kindle.com", scope.Get<TextBox>("KindleEmailRecipientBox").Text);
        scope.Call("ReaderAiSettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.Equal("https://draft.example.test", scope.Get<TextBox>("MainReaderAiBaseUrlBox").Text);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();
        scope.Get<Button>("S3TestConnectionButton").Focus();
        scope.Call("ShowSettingsSection", "Reading");
        Assert.Same(scope.Get<Button>("SettingsReadingButton"), scope.Window.FocusManager.GetFocusedElement());
    });

    [Fact]
    public Task SwitchLabelIsClickableAndInputsHaveAccessibleNames() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        var toggle = scope.Get<ToggleSwitch>("GridGalleryDisplayCheck");
        var labelPoint = toggle.TranslatePoint(new Point(24, toggle.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(labelPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(labelPoint, MouseButton.Left, RawInputModifiers.None);
        Assert.True(toggle.IsChecked);
        Assert.True(toggle.Bounds.Height >= 44);
        foreach (var name in new[] { "NetworkEnabledCheck", "GridGalleryDisplayCheck", "S3SecretKeyBox",
                     "MainReaderAiApiKeyBox", "KindleEmailPasswordBox", "PreferredOpenFormatBox" })
            Assert.False(string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(scope.Get<Control>(name))!.GetName()), name);
    });

    [Theory]
    [InlineData(1024, 664, "zh-CN")]
    [InlineData(1294, 818, "zh-CN")]
    [InlineData(1920, 1080, "zh-CN")]
    [InlineData(1024, 664, "en-US")]
    public Task LayoutKeepsCategoriesAndS3ActionsWithinWindow(int width, int height, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = width;
        scope.Window.Height = height;
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        Assert.Null(scope.Window.FindControl<Control>("SystemChildren"));
        Assert.Equal(5, scope.Get<WrapPanel>("SettingsCategoryButtons").Children.Count);
        foreach (var category in new[] { "Library", "Reading", "Kindle", "Data", "About" })
        {
            scope.Call("ShowSettingsSection", category);
            await Render();
            var section = scope.Get<Control>($"Settings{category}Section");
            Assert.True(section.IsEffectivelyVisible);
            Assert.True(section.Bounds.Width <= 820);
            foreach (var tab in scope.Get<WrapPanel>("SettingsCategoryButtons").Children)
                AssertWithinWindow(tab, scope.Window);
            Capture(scope.Window, $"{language}-{width}-{category.ToLowerInvariant()}");
        }
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();
        var button = scope.Get<Button>("S3SaveSettingsButton");
        Assert.True(button.IsEffectivelyVisible);
        AssertWithinWindow(button, scope.Window);
        var initialPosition = button.TranslatePoint(default, scope.Window)!.Value;
        Capture(scope.Window, $"{language}-{width}-s3");
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = true;
        scope.Get<ScrollViewer>("SettingsScrollViewer").Offset = new Vector(0, 10000);
        await Render();
        AssertWithinWindow(button, scope.Window);
        Assert.Equal(initialPosition, button.TranslatePoint(default, scope.Window)!.Value);
        Capture(scope.Window, $"{language}-{width}-s3-bottom");
    });

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);

    private static async Task Render()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Task.Delay(150);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void AssertWithinWindow(Control control, Window window)
    {
        var position = control.TranslatePoint(default, window)!.Value;
        Assert.True(position.X >= 0 && position.Y >= 0, control.Name);
        Assert.True(position.X + control.Bounds.Width <= window.Bounds.Width + 1, control.Name);
        Assert.True(position.Y + control.Bounds.Height <= window.Bounds.Height + 1, control.Name);
    }

    private static void Capture(MainWindow window, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var image = window.CaptureRenderedFrame();
        image!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static async Task<IDisposable> AcquireSettingsWriteLock(AppPaths paths)
    {
        var lease = await (Task<IDisposable>)typeof(AppSettingsStore).Assembly.GetType("Kkindle.Infrastructure.SettingsWriteLock")!
            .GetMethod("AcquireAsync")!.Invoke(null, [paths, CancellationToken.None])!;
        return new TestLease(lease);
    }

    private sealed class TestLease(IDisposable lease) : IDisposable
    {
        private IDisposable? _lease = lease;
        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    private sealed class TestWindow(MainWindow window, AppPaths paths, string directory) : IAsyncDisposable
    {
        public MainWindow Window => window;
        public AppPaths Paths => paths;
        public S3SyncService Sync => Field<S3SyncService>("_s3SyncService");
        public T Get<T>(string name) where T : Control => window.FindControl<T>(name)!;
        public T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        public void Set(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        public object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        public T Call<T>(string name, params object?[] args) => (T)Call(name, args)!;

        public static async Task<TestWindow> Create()
        {
            ((Kkindle.App)Application.Current!).ApplyLanguage("zh-CN");
            var directory = Path.Combine(Path.GetTempPath(), "kkindle-settings-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(directory);
            paths.EnsureDirectories();
            var initial = new AppSettings
            {
                UiLanguage = "zh-CN", OnboardingCompleted = true, NetworkEnabled = false,
                AutoUpdateCheckEnabled = false, AutoConnectDevice = false, GridGalleryDisplay = false
            };
            await new AppSettingsStore(paths).SaveAsync(initial);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            var window = new MainWindow(paths, library, startupSettings: initial) { Width = 1294, Height = 818 };
            var scope = new TestWindow(window, paths, directory);
            scope.Set("_s3SyncStoredSettings", await scope.Sync.LoadSettingsAsync());
            await scope.Field<ReaderDataService>("_readerData").InitializeAsync();
            scope.Call("PopulateSettingsControls");
            window.Show();
            await Render();
            scope.Set("_stage3Ready", true);
            scope.Set("_appSettingsStartupSettled", true);
            scope.Call("ConfigureAppSettingsAutoSave");
            return scope;
        }

        public async ValueTask DisposeAsync()
        {
            if (window.IsVisible)
            {
                await Call<Task<bool>>("FlushAppSettingsAsync");
                Set("_allowWindowCloseForS3Sync", true);
                window.Close();
            }
            await Task.Delay(30);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
