using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Views;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile;

/// <summary>
/// Single-view mobile shell: a content slot plus three tab roots. Book detail
/// and the reader are pushed on top of the active tab and hide the tab bar.
/// Transient choices open in one shared bottom sheet. Android's back gesture is
/// routed through <see cref="TopLevel.BackRequested"/>.
/// </summary>
public partial class MobileShell : UserControl
{
    private enum ShellTab
    {
        Library,
        Materials,
        Profile
    }

    private const string PageBackgroundKey = "PageBackgroundBrush";

    /// <summary>Quiet periods that keep routine app events from syncing constantly.</summary>
    public static class AutoSyncGap
    {
        public static readonly TimeSpan OnStart = TimeSpan.Zero;
        public static readonly TimeSpan OnResume = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan AfterReading = TimeSpan.FromSeconds(30);
    }

    private static readonly TimeSpan SheetCloseDelay = TimeSpan.FromMilliseconds(230);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(2);

    private readonly ApplicationModules _modules;
    private readonly MobileServices _services;
    private readonly Stack<(Control Page, ShellTab Tab)> _history = new();
    private ShellTab _tab = ShellTab.Library;
    private Task? _readerDataReady;
    private int _sheetVersion;
    private int _toastVersion;
    private Action? _sheetDismissed;
    private bool _sheetFillsHeight;

    /// <summary>Design-time only: keeps the XAML previewer and runtime loader working.</summary>
    public MobileShell()
    {
        _modules = null!;
        _services = null!;
        Preferences = null!;
        Covers = null!;
        Sync = null!;
        InitializeComponent();
    }

    public MobileShell(ApplicationModules modules, MobileServices services, MobilePreferencesStore preferences)
    {
        _modules = modules;
        _services = services;
        Preferences = preferences;
        Covers = new CoverCache(modules.Paths);
        Sync = new MobileSync(modules);
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => SyncSystemBars();
        ApplyThemeMode();
        ShowLibrary();
    }

    public MobilePreferencesStore Preferences { get; }

    public MobileServices Services => _services;

    /// <summary>
    /// Developer mode holds features outside the normal product (Douban lookup,
    /// Z-Library). Like the desktop app it lasts for this run only.
    /// </summary>
    public bool IsDeveloperMode { get; private set; }

    public event EventHandler? DeveloperModeChanged;

    public void ToggleDeveloperMode()
    {
        IsDeveloperMode = !IsDeveloperMode;
        DeveloperModeChanged?.Invoke(this, EventArgs.Empty);
        ShowToast(IsDeveloperMode ? "已进入开发者模式" : "已退出开发者模式");
    }

    public CoverCache Covers { get; }

    public MobileSync Sync { get; }

    public Control? CurrentPage => _history.Count > 0 ? _history.Peek().Page : null;

    public bool CanGoBack => SheetScope.IsVisible || _history.Count > 1;

    /// <summary>
    /// Reader tables (progress, bookmarks, annotations) are created lazily the
    /// first time a page needs them; the desktop shell does this during startup.
    /// </summary>
    public Task EnsureReaderDataAsync() =>
        _readerDataReady ??= _modules.Reader.Data.InitializeAsync();

    public void ShowLibrary() => ShowRoot(ShellTab.Library, new LibraryPage(_modules, _services, this));

    public void ShowMaterials() => ShowRoot(ShellTab.Materials, new MaterialsPage(_modules, this));

    public void ShowProfile() => ShowRoot(ShellTab.Profile, new ProfilePage(_modules, this));

    public void OpenBook(Book book) => Push(new BookDetailPage(book, _modules, this));

    public void OpenCollection(BookCollection collection) => Push(new CollectionPage(collection, _modules, this));

    public void OpenReader(Book book, ReaderProgressRow? location = null) =>
        Push(new ReaderPage(book, _modules, this, location));

    public void OpenZLibrary() => Push(new ZLibraryPage(_modules, this));

    public void OpenZLibraryAccount() => Push(new ZLibraryAccountPage(_modules, this));

    /// <summary>The control shown in the open sheet, for pages that update it later.</summary>
    public Control? CurrentSheet => SheetScope.IsVisible ? SheetBody.Content as Control : null;

    public void OpenBookEditor(Book book) => Push(new BookEditPage(book, _modules, this));

    public void OpenReadingData() => Push(new ReadingDataPage(_modules, this));

    public void OpenSyncSettings() => Push(new SyncSettingsPage(_modules, this));

    public void OpenAiSettings() => Push(new AiSettingsPage(_modules, this));

    public void OpenKindleSettings() => Push(new KindleSettingsPage(_modules, this));

    /// <summary>
    /// Runs a silent sync when it is due. Deletions always wait for the user on
    /// the sync page, because a background pass must never approve them.
    /// </summary>
    public async void RequestAutoSync(TimeSpan minimumGap)
    {
        if (DateTimeOffset.UtcNow - Sync.LastRunAt < minimumGap) return;
        try
        {
            var stored = await _modules.Sync.Service.LoadSettingsAsync();
            if (stored.Settings is not { Enabled: true, AutomaticSyncEnabled: true, IsConfigured: true }) return;
            await _modules.Library.Books.InitializeAsync();
            await EnsureReaderDataAsync();
            var result = await Sync.RunAsync(stored, null, (_, _) => Task.FromResult(false), CancellationToken.None);
            if (result is null) ShowToast("同步发现待确认的删除，请到 S3 同步页处理");
            else if (result.Changed && CurrentPage is LibraryPage library) await library.ReloadAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Offline phones are normal; the sync page reports errors on request.
        }
    }

    public void OnAppResumed() => RequestAutoSync(AutoSyncGap.OnResume);

    public void OnAppPaused() => (CurrentPage as ReaderPage)?.SaveNow();

    public void ApplyThemeMode()
    {
        if (Application.Current is not { } application) return;
        application.RequestedThemeVariant = Preferences.Current.ThemeMode switch
        {
            MobileThemeMode.Light => ThemeVariant.Light,
            MobileThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        Dispatcher.UIThread.Post(SyncSystemBars);
    }

    public void ShowSheet(
        string title,
        Control content,
        ThemeVariant? variant = null,
        Action? onDismissed = null)
    {
        NotifySheetDismissed();
        _sheetDismissed = onDismissed;
        _sheetVersion++;
        SheetTitle.Text = title;
        SheetBody.Content = content;
        SheetScope.RequestedThemeVariant = variant ?? ThemeVariant.Default;
        SheetPanel.MaxHeight = Math.Max(320, Bounds.Height * 0.82);
        _sheetFillsHeight = content is IFullHeightSheet { FillsHeight: true };
        SheetPanel.Height = _sheetFillsHeight ? SheetPanel.MaxHeight : double.NaN;
        // The shell is inset by the safe area; the scrim and sheet reach the screen edges.
        var safe = Padding;
        SheetScope.Margin = new Thickness(-safe.Left, -safe.Top, -safe.Right, -safe.Bottom);
        SheetPanel.Padding = new Thickness(0, 0, 0, safe.Bottom);
        SheetScope.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            SheetScrim.Classes.Add("open");
            SheetPanel.Classes.Add("open");
        }, DispatcherPriority.Render);
        RefreshVirtualizedListsAfterOpen(_sheetVersion);
    }

    /// <summary>
    /// A virtualized list measures what is visible while the sheet is still
    /// sliding up from off-screen, realizes only a row or two, and does not
    /// re-measure when the slide ends. Measure it again once the sheet is in place.
    /// </summary>
    private async void RefreshVirtualizedListsAfterOpen(int version)
    {
        await Task.Delay(SheetCloseDelay);
        if (version != _sheetVersion) return;
        foreach (var panel in SheetBody.GetVisualDescendants().OfType<VirtualizingStackPanel>())
            panel.InvalidateMeasure();
    }

    /// <summary>Resolves false when the sheet is closed any way other than confirming.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowSheet(title, new PromptSheet(message, confirmText, _ =>
        {
            decision.TrySetResult(true);
            HideSheet();
            return Task.CompletedTask;
        }), onDismissed: () => decision.TrySetResult(false));
        return decision.Task;
    }

    private void NotifySheetDismissed()
    {
        var dismissed = _sheetDismissed;
        _sheetDismissed = null;
        dismissed?.Invoke();
    }

    public async void HideSheet()
    {
        if (!SheetScope.IsVisible) return;
        NotifySheetDismissed();
        var version = ++_sheetVersion;
        SheetScrim.Classes.Remove("open");
        SheetPanel.Classes.Remove("open");
        await Task.Delay(SheetCloseDelay);
        if (version != _sheetVersion) return;
        SheetScope.IsVisible = false;
        SheetBody.Content = null;
    }

    public async void ShowToast(string message)
    {
        var version = ++_toastVersion;
        ToastText.Text = message;
        Toast.IsVisible = true;
        await Task.Delay(ToastDuration);
        if (version == _toastVersion) Toast.IsVisible = false;
    }

    /// <summary>
    /// Reader paper can differ from the app theme, so the reader tints the system
    /// bars itself. Hiding them instead would reflow the page on every chrome
    /// toggle and raise Android's full-screen confirmation.
    /// </summary>
    public void SetPaperSystemBars(Color paper, bool dark)
    {
        // A local override also repaints the safe area behind the status bar.
        Resources[PageBackgroundKey] = new SolidColorBrush(paper);
        ApplySystemBars(paper, dark);
    }

    public void ClearPaperSystemBars()
    {
        Resources.Remove(PageBackgroundKey);
        SyncSystemBars();
    }

    public bool GoBack()
    {
        if (SheetScope.IsVisible)
        {
            HideSheet();
            return true;
        }
        if (_history.Count <= 1) return false;
        _history.Pop();
        Apply();
        return true;
    }

    private void ShowRoot(ShellTab tab, Control page)
    {
        HideSheet();
        _tab = tab;
        _history.Clear();
        _history.Push((page, tab));
        Apply();
    }

    private void Push(Control page)
    {
        HideSheet();
        _history.Push((page, _tab));
        Apply();
    }

    private void Apply()
    {
        var (page, tab) = _history.Peek();
        PageHost.Content = page;
        // Pushed pages (book detail, reader) own the full screen.
        TabBar.IsVisible = _history.Count == 1;
        LibraryTab.Classes.Set("active", tab == ShellTab.Library);
        MaterialsTab.Classes.Set("active", tab == ShellTab.Materials);
        ProfileTab.Classes.Set("active", tab == ShellTab.Profile);
    }

    private void SyncSystemBars()
    {
        if (this.TryFindResource(PageBackgroundKey, ActualThemeVariant, out var value)
            && value is ISolidColorBrush brush)
        {
            ApplySystemBars(brush.Color, ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    private void ApplySystemBars(Color background, bool dark)
    {
        if (TopLevel.GetTopLevel(this)?.InsetsManager is { } insets)
            insets.SystemBarColor = background;
        _services.SystemBars?.SetIconsForBackground(dark);
    }

    private void OnLibraryTabClick(object? sender, RoutedEventArgs e) => ShowLibrary();

    private void OnMaterialsTabClick(object? sender, RoutedEventArgs e) => ShowMaterials();

    private void OnProfileTabClick(object? sender, RoutedEventArgs e) => ShowProfile();

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => HideSheet();

    private void OnCloseSheetClick(object? sender, RoutedEventArgs e) => HideSheet();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.BackRequested += OnBackRequested;
            if (topLevel.InputPane is { } inputPane) inputPane.StateChanged += OnInputPaneChanged;
        }
        SyncSystemBars();
        RequestAutoSync(AutoSyncGap.OnStart);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.BackRequested -= OnBackRequested;
            if (topLevel.InputPane is { } inputPane) inputPane.StateChanged -= OnInputPaneChanged;
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Keeps the sheet's text field and confirm button above the keyboard.</summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var open = e.NewState == InputPaneState.Open;
        var lift = open ? e.EndRect.Height + Padding.Bottom : 0;
        SheetPanel.Margin = new Thickness(0, 0, 0, lift);
        // A full-height sheet would otherwise be pushed off the top of the screen.
        if (_sheetFillsHeight) SheetPanel.Height = Math.Max(200, SheetPanel.MaxHeight - lift);
        SheetPanel.Padding = new Thickness(0, 0, 0, open ? 0 : Padding.Bottom);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (GoBack()) e.Handled = true;
    }
}
