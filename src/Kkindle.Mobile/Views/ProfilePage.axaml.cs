using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public partial class ProfilePage : UserControl
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private const int DeveloperModeClicks = 5;
    private static readonly TimeSpan DeveloperModeClickWindow = TimeSpan.FromSeconds(3);
    private int _versionClicks;
    private DateTimeOffset _firstVersionClick;

    /// <summary>Design-time only.</summary>
    public ProfilePage() => InitializeComponent();

    public ProfilePage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        VersionText.Text = ApplicationVersion.GetDisplayVersion(typeof(MobileApp).Assembly);
        PresentPreferences();
        DeveloperSection.IsVisible = shell.IsDeveloperMode;
        shell.DeveloperModeChanged += OnDeveloperModeChanged;
        DetachedFromVisualTree += (_, _) => shell.DeveloperModeChanged -= OnDeveloperModeChanged;
        Loaded += OnLoaded;
    }

    private void PresentPreferences()
    {
        var preferences = _shell!.Preferences.Current;
        ThemeSystem.Classes.Set("on", preferences.ThemeMode == MobileThemeMode.System);
        ThemeLight.Classes.Set("on", preferences.ThemeMode == MobileThemeMode.Light);
        ThemeDark.Classes.Set("on", preferences.ThemeMode == MobileThemeMode.Dark);
    }

    // Also runs when a pushed page is popped, so both summaries stay current.
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _modules!.Library.Books.InitializeAsync();
            var books = await _modules.Library.Books.SearchAsync();
            var sync = await _modules.Sync.Service.LoadSettingsAsync();

            SyncSummaryText.Text = sync.Settings is { Enabled: true, IsConfigured: true } settings
                ? $"{settings.ProviderName} · 已开启"
                : "未开启";
            var ai = await _modules.Ai.Settings.LoadAsync();
            AiSummaryText.Text = ai.IsConfigured ? ai.ProviderDisplayName : "未设置";
            var kindle = await _modules.Integrations.KindleEmailSettings.LoadAsync();
            KindleSummaryText.Text = kindle.IsConfigured ? "已设置" : "未设置";
            var zlibrary = await _modules.Integrations.ZLibrarySettings.LoadAsync();
            ZLibrarySummaryText.Text = zlibrary.IsConfigured ? "已设置" : "未设置";
            StorageText.Text = MobileFormat.Size(books.Sum(book => book.Files.Sum(file => file.Size)));
        }
        catch (Exception exception)
        {
            _shell?.ShowToast("读取数据失败：" + exception.Message);
        }
    }

    /// <summary>Five quick taps on the version toggle developer mode, as on desktop.</summary>
    private void OnVersionClick(object? sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_versionClicks == 0 || now - _firstVersionClick > DeveloperModeClickWindow)
        {
            _versionClicks = 0;
            _firstVersionClick = now;
        }
        if (++_versionClicks < DeveloperModeClicks) return;
        _versionClicks = 0;
        _shell?.ToggleDeveloperMode();
    }

    private void OnDeveloperModeChanged(object? sender, EventArgs e) =>
        DeveloperSection.IsVisible = _shell!.IsDeveloperMode;

    private void OnZLibraryClick(object? sender, RoutedEventArgs e) => _shell?.OpenZLibrary();

    private void OnZLibraryAccountClick(object? sender, RoutedEventArgs e) => _shell?.OpenZLibraryAccount();

    private void OnSyncClick(object? sender, RoutedEventArgs e) => _shell?.OpenSyncSettings();

    private void OnAiClick(object? sender, RoutedEventArgs e) => _shell?.OpenAiSettings();

    private void OnKindleClick(object? sender, RoutedEventArgs e) => _shell?.OpenKindleSettings();

    private async void OnDeveloperClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(new Uri("https://github.com/kingstacker")))
                _shell?.ShowToast("无法打开开发者主页");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell?.ShowToast("无法打开开发者主页");
        }
    }

    private async void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { _shell?.ShowToast("剪贴板不可用"); return; }
            await clipboard.SetTextAsync("1109898894");
            _shell?.ShowToast("群号已复制到剪贴板");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell?.ShowToast("复制群号失败");
        }
    }

    private void OnSupportClick(object? sender, RoutedEventArgs e) =>
        _shell?.ShowSheet("请我喝杯咖啡", new SupportSheet());

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        var mode = sender switch
        {
            Button { Name: nameof(ThemeLight) } => MobileThemeMode.Light,
            Button { Name: nameof(ThemeDark) } => MobileThemeMode.Dark,
            _ => MobileThemeMode.System
        };
        _shell!.Preferences.Update(value => value with { ThemeMode = mode });
        _shell.ApplyThemeMode();
        PresentPreferences();
    }

}
