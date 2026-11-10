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
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views;
using Kkindle.Mobile.Views.Sheets;
using Kkindle.Mobile.Controls;

namespace Kkindle.Mobile;

/// <summary>
/// Single-view mobile shell: a content slot plus four tab roots. Book detail
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
        ReadingData,
        Profile
    }

    private const string PageBackgroundKey = "PageBackgroundBrush";

    /// <summary>Quiet periods that keep routine app events from syncing constantly.</summary>
    public static class AutoSyncGap
    {
        public static readonly TimeSpan OnStart = TimeSpan.Zero;
        public static readonly TimeSpan OnResume = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan AfterReading = TimeSpan.Zero;
    }

    private static readonly TimeSpan SheetCloseDelay = TimeSpan.FromMilliseconds(380);
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
    private bool _backupBlocked;
    private DispatcherTimer? _sheetControlTimer;

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
        _services = services with { Paths = modules.Paths };
        Preferences = preferences;
        Covers = new CoverCache(modules.Paths);
        Sync = new MobileSync(modules);
        InitializeComponent();
        SheetPanel.SizeChanged += (_, _) =>
        {
            if (SheetScope.IsVisible && SheetPanel.Classes.Contains("open"))
                Dispatcher.UIThread.Post(() =>
                {
                    if (SheetScope.IsVisible && SheetPanel.Classes.Contains("open"))
                    {
                        SetPageNativeControlsObscured(true, checkCoverage: true);
                        UpdateSheetNativeControlClipping();
                    }
                }, DispatcherPriority.Render);
        };
        Loaded += (_, _) => StartDeviceChecks();
        if (_services.TabBar is { } tabBar)
        {
            // The platform bar is the visible one. This one only keeps the grid
            // row that reserves its space.
            tabBar.TabSelected += OnNativeTabSelected;
            TabBarGlass.Opacity = 0;
            TabBarGlass.IsHitTestVisible = false;
        }
        ActualThemeVariantChanged += (_, _) => SyncSystemBars();
        ApplyThemeMode();
        if (MobileDataCoordinator.IsRestartRequired)
            ShowRestartRequired(MobileDataCoordinator.IsRecoveryIncomplete);
        else
            ShowLibrary();
    }

    public MobilePreferencesStore Preferences { get; }

    public MobileServices Services => _services;

    /// <summary>Shared services for platform frontends that own their own view hierarchy.</summary>
    public ApplicationModules Modules => _modules;

    public Func<string, string, string, Task<bool>>? NativeConfirmation { get; set; }
    public Action<string>? NativeToast { get; set; }
    public Action? NativeRestartRequired { get; set; }
    public Action? NativeLibraryChanged { get; set; }
    private bool _deviceChecksStarted;

    public void StartDeviceChecks()
    {
        if (_deviceChecksStarted) return;
        _deviceChecksStarted = true;
        _modules.Accounts.Service.DeviceInvalidated += (_, args) => Dispatcher.UIThread.Post(async () =>
        {
            if (_modules.IsDisposed) return;
            await ConfirmAsync("设备已失效", args.Message, "知道了");
        });
        _modules.Accounts.Monitor.MembershipExpired += (_, args) => Dispatcher.UIThread.Post(async () =>
        {
            if (_modules.IsDisposed) return;
            await ConfirmAsync("会员已过期", args.Message, "知道了");
        });
        _modules.Accounts.Monitor.Start();
    }

    public async Task CheckDevicesIfDueAsync()
    {
        try { await _modules.Accounts.Monitor.CheckIfDueAsync(); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { }
    }

    /// <summary>
    /// Developer mode holds features outside the normal product.
    /// Like the desktop app it lasts for this run only.
    /// </summary>
    public bool IsDeveloperMode { get; private set; }

    public event EventHandler? DeveloperModeChanged;

    public void ToggleDeveloperMode()
    {
        if (IsDeveloperMode)
        {
            SetDeveloperMode(false);
            return;
        }
        ShowSheet("开发者模式", new PromptSheet(null, "进入", password =>
        {
            if (password != BookDetailEditing.DeveloperPassword)
            {
                ShowToast("密码错误");
                return Task.CompletedTask;
            }
            HideSheet();
            SetDeveloperMode(true);
            return Task.CompletedTask;
        }, placeholder: "请输入密码", isPassword: true));
    }

    private void SetDeveloperMode(bool enabled)
    {
        IsDeveloperMode = enabled;
        DeveloperModeChanged?.Invoke(this, EventArgs.Empty);
        ShowToast(enabled ? "已进入开发者模式" : "已退出开发者模式");
    }

    public bool TryToggleDeveloperMode(string? password)
    {
        if (!IsDeveloperMode && password != BookDetailEditing.DeveloperPassword) return false;
        SetDeveloperMode(!IsDeveloperMode);
        return true;
    }

    public CoverCache Covers { get; }

    public MobileSync Sync { get; }

    public Task ExportBackupAsync(string path, CancellationToken token = default) =>
        RunBackupAsync(path, restore: false, token);

    public Task RestoreBackupAsync(string path, CancellationToken token = default) =>
        RunBackupAsync(path, restore: true, token);

    public Task EnsureBackupEncryptionAsync(CancellationToken token = default) =>
        new AppBackupService(_services.Paths, _services.SecretProtector).EnsureEncryptionConfiguredAsync(token);

    private async Task RunBackupAsync(string path, bool restore, CancellationToken token)
    {
        await EnsureBackupEncryptionAsync(token);
        if (_backupBlocked) throw new InvalidOperationException("备份或恢复正在进行，请稍候。");
        _backupBlocked = true;
        BackupBlockerTitle.Text = restore ? "正在恢复备份" : "正在导出备份";
        BackupBlockerText.Text = "正在等待进行中的数据操作完成，请保持应用打开。";
        BackupBlockerProgress.Value = 0;
        BackupBlockerProgress.IsVisible = true;
        BackupBlocker.IsVisible = true;
        SetNativeControlsBlocked(true);
        _services.TabBar?.Update(false, (int)_tab);
        // Reported from a background thread; Progress<T> hops back to the UI one.
        var progress = new Progress<AppBackupProgress>(update =>
        {
            BackupBlockerProgress.Value = Math.Clamp(update.Fraction, 0, 1);
            if (!string.IsNullOrWhiteSpace(update.Message)) BackupBlockerText.Text = update.Message;
        });
        try
        {
            using var lease = await MobileDataCoordinator.BeginRestoreAsync(token)
                ?? throw new InvalidOperationException("数据操作暂不可用，请重新启动应用。");
            var backup = new AppBackupService(_services.Paths, _services.SecretProtector);
            if (restore)
            {
                try
                {
                    await backup.ImportAsync(path, token, progress);
                }
                catch (AppBackupRollbackException)
                {
                    // A failed rollback leaves a recovery journal for startup. Do not
                    // allow this process to write into the partially restored library.
                    lease.Complete(recoveryIncomplete: true);
                    ShowRestartRequired(recoveryIncomplete: true);
                    throw;
                }
                lease.Complete();
                ShowRestartRequired();
            }
            else
                await backup.ExportAsync(path, token, progress);
        }
        finally
        {
            if (!MobileDataCoordinator.IsRestartRequired)
            {
                _backupBlocked = false;
                BackupBlocker.IsVisible = false;
                SetNativeControlsBlocked(false);
                _services.TabBar?.Update(_history.Count == 1 && !SheetScope.IsVisible, (int)_tab);
            }
        }
    }

    /// <summary>
    /// The backup blocker is an opaque Avalonia border, but a hosted UIKit
    /// segment is a platform view above the whole scene and would keep showing
    /// through it, so the native controls are swapped for the fallback while
    /// the blocker is up.
    /// </summary>
    private void SetNativeControlsBlocked(bool blocked)
    {
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSegmentGroup>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSwitchGroup>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeMenuButton>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeActionButton>())
            group.SetObscured(blocked);
        if (blocked)
        {
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSegmentGroup>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSwitchGroup>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeMenuButton>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeActionButton>())
                group.SetObscured(true);
        }
        else
        {
            SetPageNativeControlsObscured(SheetScope.IsVisible);
        }
    }

    private void ShowRestartRequired(bool recoveryIncomplete = false)
    {
        NativeRestartRequired?.Invoke();
        _backupBlocked = true;
        BackupBlockerTitle.Text = recoveryIncomplete ? "恢复未完成" : "需要重新启动";
        BackupBlockerText.Text = recoveryIncomplete
            ? "恢复失败，自动回滚未完成。请完全关闭应用并重新打开，让应用尝试恢复原数据。重新启动前无法继续使用或同步。"
            : "请完全关闭应用后重新打开，以载入恢复的数据。重新启动前无法继续使用或同步。";
        BackupBlockerProgress.IsVisible = false;
        BackupBlocker.IsVisible = true;
        SetNativeControlsBlocked(true);
        _services.TabBar?.Update(false, (int)_tab);
    }

    public Control? CurrentPage => _history.Count > 0 ? _history.Peek().Page : null;

    public bool CanGoBack => SheetScope.IsVisible || _history.Count > 1;

    /// <summary>
    /// Reader tables (progress, bookmarks, annotations) are created lazily the
    /// first time a page needs them; the desktop shell does this during startup.
    /// </summary>
    public Task EnsureReaderDataAsync()
    {
        // A failed attempt (locked database, full disk) is retried on the next
        // open instead of failing every reader for the rest of the session.
        if (_readerDataReady is { IsFaulted: true } or { IsCanceled: true }) _readerDataReady = null;
        return _readerDataReady ??= _modules.Reader.Data.InitializeAsync();
    }

    public void ShowLibrary() => ShowRoot(ShellTab.Library, new LibraryPage(_modules, _services, this));

    public void ShowMaterials() => ShowRoot(ShellTab.Materials, new MaterialsPage(_modules, this));

    public void ShowProfile() => ShowRoot(ShellTab.Profile, new ProfilePage(_modules, this));

    public void OpenBook(Book book) => Push(new BookDetailPage(book, _modules, this));

    public void OpenCollection(BookCollection collection) => Push(new CollectionPage(collection, _modules, this));

    public void OpenReader(Book book, ReaderProgressRow? location = null) =>
        Push(new ReaderPage(book, _modules, this, location));

    /// <summary>The control shown in the open sheet, for pages that update it later.</summary>
    public Control? CurrentSheet => SheetScope.IsVisible ? SheetBody.Content as Control : null;

    public void OpenBookEditor(Book book) => Push(new BookEditPage(book, _modules, this));

    public void ShowReadingData() => ShowRoot(ShellTab.ReadingData, new ReadingDataPage(_modules, this));

    public void OpenSyncSettings() => Push(new SyncSettingsPage(_modules, this));

    public void OpenAccount() => Push(new AccountPage(_modules.Accounts.Service, this, _modules.Accounts.Membership));

    public void OpenAiSettings() => Push(new AiSettingsPage(_modules, this));

    public void OpenKindleSettings() => Push(new KindleSettingsPage(_modules, this));

    /// <summary>
    /// Runs a silent sync when it is due. Deletions always wait for the user on
    /// the sync page, because a background pass must never approve them.
    /// </summary>
    public async void RequestAutoSync(TimeSpan minimumGap)
    {
        if (_backupBlocked || DateTimeOffset.UtcNow - Sync.LastRunAt < minimumGap) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules.Library.Books.InitializeAsync();
            await EnsureReaderDataAsync();
            var stored = await _modules.Sync.Service.LoadSettingsAsync();
            if (stored.Settings is not { Enabled: true, AutomaticSyncEnabled: true, IsConfigured: true }) return;
            var result = await Sync.RunAsync(stored, null, (_, _) => Task.FromResult(false), CancellationToken.None, operation);
            if (result is null) ShowToast("同步发现待确认的删除，请到 S3 同步页处理");
            else
            {
                if (result.Changed || Preferences.Current.ShowBookSyncStatus) NativeLibraryChanged?.Invoke();
                if (result.Changed && CurrentPage is LibraryPage library) await library.ReloadAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Offline phones are normal; the sync page reports errors on request.
        }
    }

    public void OnAppResumed()
    {
        if (_backupBlocked) return;
        (CurrentPage as ReaderPage)?.Resume();
        RequestAutoSync(AutoSyncGap.OnResume);
    }

    public void OnAppPaused()
    {
        if (_backupBlocked || CurrentPage is not ReaderPage reader) return;
        if (_services.BackgroundWork is { } background)
            background.Run(reader.PauseAsync);
        else
            _ = reader.PauseAsync();
    }

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

    /// <summary>
    /// Applies an appearance reported by the platform while the user's choice is
    /// "follow the system". Platforms whose SDK no longer notifies Avalonia
    /// directly call this from their own trait observer. Must run on the UI thread.
    /// </summary>
    public void ApplySystemThemeVariant(ThemeVariant variant)
    {
        if (Preferences.Current.ThemeMode != MobileThemeMode.System) return;
        if (Application.Current is not { } application) return;
        if (application.RequestedThemeVariant == variant) return;
        application.RequestedThemeVariant = variant;
    }

    public void ShowSheet(
        string title,
        Control content,
        ThemeVariant? variant = null,
        Action? onDismissed = null)
    {
        if (_backupBlocked)
        {
            onDismissed?.Invoke();
            return;
        }
        NotifySheetDismissed();
        _sheetDismissed = onDismissed;
        var version = ++_sheetVersion;
        SheetTitle.Text = title;
        SheetScrim.Classes.Set("reader-preview", content is ReaderSettingsSheet);
        SheetBody.Content = content;
        SheetScope.RequestedThemeVariant = variant ?? ThemeVariant.Default;
        SheetPanel.MaxHeight = content is ReaderSettingsSheet
            ? Math.Max(200, Bounds.Height * 0.45)
            : Math.Max(320, Bounds.Height * 0.82);
        _sheetFillsHeight = content is IFullHeightSheet { FillsHeight: true };
        SheetPanel.Height = _sheetFillsHeight ? SheetPanel.MaxHeight : double.NaN;
        // The shell is inset by the safe area; the scrim and sheet reach the screen edges.
        var safe = Padding;
        SheetScope.Margin = new Thickness(-safe.Left, -safe.Top, -safe.Right, -safe.Bottom);
        SheetPanel.Padding = new Thickness(0, 0, 0, safe.Bottom);
        if (_services.TabBar is not ISheetOccludingTabBar || _history.Count != 1)
            _services.TabBar?.Update(false, (int)_tab);
        SetPageNativeControlsObscured(true);
        SheetScope.IsVisible = true;
        StartSheetControlTracking();
        AnimateSheetOpenSoon(version);
        RevealSheetCoverageSoon(version);
        RefreshVirtualizedListsAfterOpen(version);
    }

    /// <summary>
    /// Adds the open classes only after the closed state has painted once.
    /// Applying them in the same frame the scope becomes visible leaves the
    /// transition with no starting value, so the sheet snapped into place.
    /// </summary>
    private async void AnimateSheetOpenSoon(int version)
    {
        await Task.Delay(24);
        if (version != _sheetVersion || !SheetScope.IsVisible) return;
        SheetScrim.Classes.Add("open");
        SheetPanel.Classes.Add("open");
        SetPageNativeControlsScrim(true);
    }

    /// <summary>
    /// The sheet panel slides over native page controls, which are drawn above
    /// everything Avalonia paints. Hide exactly the ones the settled panel
    /// covers as soon as its layout exists, before the slide reaches them.
    /// </summary>
    private async void RevealSheetCoverageSoon(int version)
    {
        await Task.Delay(60);
        if (version != _sheetVersion || !SheetScope.IsVisible) return;
        SetPageNativeControlsObscured(true, checkCoverage: true);
    }

    /// <summary>Updates a live sheet's theme without recreating its contents.</summary>
    public void SetSheetTheme(ThemeVariant? variant) =>
        SheetScope.RequestedThemeVariant = variant ?? ThemeVariant.Default;

    /// <summary>
    /// A virtualized list measures what is visible while the sheet is still
    /// sliding up from off-screen, realizes only a row or two, and does not
    /// re-measure when the slide ends. Measure it again once the sheet is in place.
    /// </summary>
    private async void RefreshVirtualizedListsAfterOpen(int version)
    {
        await Task.Delay(SheetCloseDelay);
        if (version != _sheetVersion) return;
        SetPageNativeControlsObscured(true, checkCoverage: true);
        UpdateSheetNativeControlClipping();
        _sheetControlTimer?.Stop();
        foreach (var panel in SheetBody.GetVisualDescendants().OfType<VirtualizingStackPanel>())
            panel.InvalidateMeasure();
    }

    /// <summary>Resolves false when the sheet is closed any way other than confirming.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (NativeConfirmation is { } native)
            return native(title, message, confirmText);
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
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSegmentGroup>())
            group.SetObscured(true);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSwitchGroup>())
            group.SetObscured(true);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetObscured(true);
        StartSheetControlTracking();
        SetPageNativeControlsScrim(false);
        SheetScrim.Classes.Remove("open");
        SheetPanel.Classes.Remove("open");
        await Task.Delay(SheetCloseDelay);
        if (version != _sheetVersion) return;
        SheetScope.IsVisible = false;
        SheetBody.Content = null;
        SetPageNativeControlsObscured(false);
        _sheetControlTimer?.Stop();
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetSheetClipRect(null);
        if (_services.TabBar is ISheetOccludingTabBar occludingTabBar)
        {
            occludingTabBar.SetSheetOcclusion(null);
            _services.TabBar.Update(!_backupBlocked && _history.Count == 1, (int)_tab);
            return;
        }
        // Let the rendered page replace the sheet before showing the native
        // glass bar, whose backdrop samples the last composited frame.
        await Task.Delay(50);
        if (version != _sheetVersion) return;
        _services.TabBar?.Update(!_backupBlocked && _history.Count == 1, (int)_tab);
    }

    private void StartSheetControlTracking()
    {
        var trackTabBar = _services.TabBar is ISheetOccludingTabBar && _history.Count == 1;
        if (!trackTabBar && !PageHost.GetVisualDescendants().OfType<NativeSliderGroup>()
                .Any(group => group.SupportsSheetClipping)) return;
        _sheetControlTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _sheetControlTimer.Tick -= OnSheetControlTick;
        _sheetControlTimer.Tick += OnSheetControlTick;
        _sheetControlTimer.Start();
    }

    private void OnSheetControlTick(object? sender, EventArgs e)
    {
        if (!SheetScope.IsVisible) return;
        UpdateSheetNativeControlClipping();
    }

    private void UpdateSheetNativeControlClipping()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || SheetPanel.TranslatePoint(default, topLevel) is not { } origin) return;
        if (_history.Count == 1 && _services.TabBar is ISheetOccludingTabBar tabBar)
            tabBar.SetSheetOcclusion(new Rect(origin, SheetPanel.Bounds.Size));
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
        {
            if (!group.SupportsSheetClipping || SheetPanel.TranslatePoint(default, group) is not { } relative)
                continue;
            var height = Math.Clamp(relative.Y, 0, group.Bounds.Height);
            group.SetSheetClipRect(height >= group.Bounds.Height
                ? null
                : new Rect(0, 0, group.Bounds.Width, height));
        }
    }

    /// <summary>
    /// Hosted native controls sit above the Avalonia sheet. Controls above the
    /// panel keep their native look and get a matching dimming overlay; only
    /// controls the settled panel actually covers are hidden.
    /// </summary>
    private void SetPageNativeControlsObscured(bool obscured, bool checkCoverage = false)
    {
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeMenuButton>())
            group.SetObscured(obscured);
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeActionButton>())
            group.SetObscured(obscured);
        Rect? sheetBounds = null;
        if (checkCoverage) sheetBounds = SettledSheetBounds();

        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSegmentGroup>())
        {
            var covered = sheetBounds is { } sheet
                && group.TranslatePoint(default, this) is { } groupOrigin
                && sheet.Intersects(new Rect(groupOrigin, group.Bounds.Size));
            group.SetSheetObscured(obscured, covered);
        }
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSwitchGroup>())
        {
            var covered = sheetBounds is { } sheet
                && group.TranslatePoint(default, this) is { } groupOrigin
                && sheet.Intersects(new Rect(groupOrigin, group.Bounds.Size));
            group.SetSheetObscured(obscured, covered);
        }
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
        {
            var covered = sheetBounds is { } sheet
                && group.TranslatePoint(default, this) is { } groupOrigin
                && sheet.Intersects(new Rect(groupOrigin, group.Bounds.Size));
            group.SetSheetObscured(obscured, covered);
        }
    }

    /// <summary>
    /// The sheet's resting rectangle. During the slide the render transform
    /// still points off-screen, but coverage must use where it will land so a
    /// hosted native control never floats over the rising panel.
    /// </summary>
    private Rect? SettledSheetBounds()
    {
        var height = SheetPanel.Bounds.Height;
        if (height <= 0)
            return SheetPanel.TranslatePoint(default, this) is { } origin
                ? new Rect(origin, SheetPanel.Bounds.Size)
                : null;
        var bottom = Bounds.Height + Padding.Bottom;
        return new Rect(0, bottom - height, Bounds.Width, height);
    }

    private void SetPageNativeControlsScrim(bool visible)
    {
        var brush = SheetScrim.Background as ISolidColorBrush;
        var color = brush?.Color ?? Color.FromArgb(0x59, 0, 0, 0);
        if (brush is not null && brush.Opacity < 1)
            color = Color.FromArgb((byte)Math.Round(color.A * brush.Opacity), color.R, color.G, color.B);
        // The scrim border fades as a whole and is lighter behind the reader
        // settings sheet; the hosted overlay must match it exactly.
        var elementOpacity = SheetScrim.Classes.Contains("open")
            ? (SheetScrim.Classes.Contains("reader-preview") ? 0.2 : 1)
            : 0;
        color = Color.FromArgb((byte)Math.Round(color.A * elementOpacity), color.R, color.G, color.B);
        // A single overlay above the hosted view dims its opaque parts (the
        // selected pill); the backdrop keeps the glass sample undimmed so the
        // same overlay does not darken the translucent track a second time.
        var backdrop = PageBackdropColor();
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSegmentGroup>())
            group.SetSheetScrim(visible ? color : null, backdrop);
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSwitchGroup>())
            group.SetSheetScrim(visible ? color : null, backdrop);
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetSheetScrim(visible ? color : null, backdrop);
    }

    private Color PageBackdropColor()
    {
        if (this.TryFindResource(PageBackgroundKey, ActualThemeVariant, out var value)
            && value is ISolidColorBrush brush && brush.Color.A > 0)
            return brush.Color;
        return ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromRgb(0x12, 0x12, 0x12)
            : Colors.White;
    }

    public async void ShowToast(string message)
    {
        if (NativeToast is { } native)
        {
            native(message);
            return;
        }
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
        if (_backupBlocked) return true;
        if (SheetScope.IsVisible)
        {
            HideSheet();
            return true;
        }
        if (_history.Count <= 1) return false;
        var (closed, _) = _history.Pop();
        Apply();
        (closed as IShellPage)?.OnClosed();
        return true;
    }

    private void ShowRoot(ShellTab tab, Control page)
    {
        if (_backupBlocked) return;
        HideSheet();
        _tab = tab;
        var closed = _history.Select(entry => entry.Page).ToArray();
        _history.Clear();
        _history.Push((page, tab));
        Apply();
        foreach (var previous in closed) (previous as IShellPage)?.OnClosed();
    }

    private void Push(Control page)
    {
        if (_backupBlocked) return;
        HideSheet();
        _history.Push((page, _tab));
        Apply();
    }

    private void Apply()
    {
        var (page, tab) = _history.Peek();
        page.Classes.Remove("page-enter");
        page.Classes.Add("page-enter");
        PageHost.Content = page;
        // Pushed pages (book detail, reader) own the full screen; the glass
        // capsule around the tab bar must disappear with it.
        var showTabs = _history.Count == 1;
        TabBar.IsVisible = showTabs;
        TabBarGlass.IsVisible = showTabs;
        _services.TabBar?.Update(showTabs && !SheetScope.IsVisible, (int)tab);
        LibraryTab.Classes.Set("active", tab == ShellTab.Library);
        MaterialsTab.Classes.Set("active", tab == ShellTab.Materials);
        ReadingDataTab.Classes.Set("active", tab == ShellTab.ReadingData);
        ProfileTab.Classes.Set("active", tab == ShellTab.Profile);
    }

    private void SyncSystemBars()
    {
        _services.TabBar?.SetDarkAppearance(ActualThemeVariant == ThemeVariant.Dark);
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

    private void OnNativeTabSelected(int index)
    {
        if (_backupBlocked) return;
        switch ((ShellTab)index)
        {
            case ShellTab.Materials: ShowMaterials(); break;
            case ShellTab.ReadingData: ShowReadingData(); break;
            case ShellTab.Profile: ShowProfile(); break;
            default: ShowLibrary(); break;
        }
    }

    private void OnLibraryTabClick(object? sender, RoutedEventArgs e) => ShowLibrary();

    private void OnMaterialsTabClick(object? sender, RoutedEventArgs e) => ShowMaterials();

    private void OnReadingDataTabClick(object? sender, RoutedEventArgs e) => ShowReadingData();

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
        var lift = open ? e.EndRect.Height : 0;
        SheetPanel.Margin = new Thickness(0, 0, 0, lift);
        // A full-height sheet would otherwise be pushed off the top of the screen.
        if (_sheetFillsHeight) SheetPanel.Height = Math.Max(200, SheetPanel.MaxHeight - lift);
        SheetPanel.Padding = new Thickness(0, 0, 0, open ? 0 : Padding.Bottom);
        if (!open && SheetScope.IsVisible
            && TopLevel.GetTopLevel(this)?.FocusManager is { } focusManager
            && focusManager.GetFocusedElement() is TextBox focused
            && focused.GetVisualAncestors().Contains(SheetBody))
            focusManager.Focus(null);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (GoBack()) e.Handled = true;
    }
}
