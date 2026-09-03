using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Stage 3 is intentionally kept in a separate partial file while the
/// Avalonia port is in progress. It owns page switching and presentation
/// state; device, backup, settings and network policy remain in Core and
/// Infrastructure.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _stage3Timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _transferToastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _deviceStatusToastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _s3SyncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _s3LocalChangeSyncTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan S3LocalChangeSyncDebounce = TimeSpan.FromSeconds(5);
    private IReadOnlyList<KindleDevice> _devices = [];
    private string? _lastDeviceIdentity;
    private string? _acceptedDeviceId;
    private string? _ignoredDeviceId;
    private string? _manuallyDisconnectedDeviceId;
    // Set after a send mutates the device library while the device page is
    // closed; OpenKindlePageAsync then rescans instead of showing the stale
    // list (its Count == 0 fast path would skip the rescan).
    private bool _deviceBooksDirty;
    private bool _deviceBooksLoaded;
    private Task? _deviceWarmTask;
    private bool _isRefreshingDevices;
    private double _deviceUsedRatio;
    private Point? _deviceStatusToastPosition;
    private double? _deviceStatusToastPointerPosition;
    private TaskCompletionSource<bool>? _devicePromptCompletion;
    private bool _stage3Ready;
    private bool _deviceResourceBusy;
    private KindleResourceKind _deviceResourceKind = KindleResourceKind.Font;
    private static readonly HashSet<string> LocalDictionaryExtensions =
        new([".mdx", ".azw", ".azw3", ".mobi", ".prc", ".kfx", ".txt", ".tsv", ".csv"], StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string DeviceKey, KindleResourceKind Kind), IReadOnlyList<KindleDeviceResource>> _deviceResourceCache = [];
    private readonly Dictionary<string, IReadOnlyList<KindleClipping>> _deviceClippingCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _backupBusy;
    private bool _s3SyncBusy;
    private bool _s3SyncExitInProgress;
    private bool _allowWindowCloseForS3Sync;
    private TaskCompletionSource<bool>? _s3SyncCompletion;
    private long _s3LocalChangeVersion;
    private long _s3SyncedLocalChangeVersion;
    private int _s3LocalChangeRetryAttempt;
    private bool _suppressS3SyncToggleAutoSave;
    private bool _s3SyncToggleAutoSaveInProgress;
    private DateTimeOffset? _lastS3SyncAt;
    private CancellationTokenSource? _zLibrarySearchCancellation;
    private int _zLibraryPage = 1;
    private int _zLibraryPageCount;
    private bool _readingMaterialsExportMode;
    private bool _suppressMainAiProviderChange;
    private bool _settingsPanelVisible;
    private bool _deviceGridView = true;
    private KindleBookCardViewModel? _deviceMultiSelectAnchor;
    private bool _deviceRubberBandSelecting;
    private bool _deviceRubberBandPressedOnCard;
    private bool _deviceRubberBandPointerSequenceHandled;
    private Point _deviceRubberBandStart;
    private Point _deviceRubberBandCurrent;
    private ZLibraryBookCardViewModel? _selectedZLibraryBook;
    private bool _zLibraryEmailSending;

    // Device-operation coordination (WinUI reference): every Kindle session
    // operation is tracked so eject can drain active work before requesting
    // the removal, and no new session is opened while eject is in progress.
    private readonly object _deviceOperationSync = new();
    private readonly HashSet<Task> _activeDeviceOperations = [];
    private bool _deviceEjectInProgress;
    private bool _isTransferring;
    private readonly SemaphoreSlim _deviceBookExportGate = new(1, 1);
    private CancellationTokenSource? _transferCancellation;

    // Application-settings real-time auto-save (600 ms debounce).
    private bool _suppressAppSettingsAutoSave;
    private bool _appSettingsAutoSaveConfigured;
    private CancellationTokenSource? _appSettingsAutoSaveCancellation;
    private int _settingsCapsuleSequence;
    // Template application and first layout can make NumericUpDown/ComboBox
    // re-fire change events right after the auto-save handlers are attached.
    // Until startup has fully settled, those saves persist silently instead of
    // popping the “设置已保存” capsule.
    private bool _appSettingsStartupSettled;
    private bool _calibreSetupBusy;
    private CancellationTokenSource? _calibreDetectionCancellation;

    public ObservableCollection<KindleBookCardViewModel> DeviceBooks { get; } = [];
    public ObservableCollection<KindleBookCardViewModel> VisibleDeviceBooks { get; } = [];
    public ObservableCollection<KindleDeviceResource> DeviceResources { get; } = [];
    public ObservableCollection<Stage3ReadingMaterialViewModel> ReadingMaterials { get; } = [];
    public ObservableCollection<Stage3ReadingMaterialGroupViewModel> ReadingMaterialGroups { get; } = [];
    public ObservableCollection<Stage3DashboardDayViewModel> DashboardDays { get; } = [];
    public ObservableCollection<Stage3DashboardBarViewModel> DashboardBookTimes { get; } = [];
    public ObservableCollection<Stage3DashboardBarViewModel> DashboardProgressBuckets { get; } = [];
    public ObservableCollection<Stage3DashboardRecentViewModel> DashboardRecentItems { get; } = [];
    public ObservableCollection<ZLibraryBookCardViewModel> ZLibraryBooks { get; } = [];
    public ObservableCollection<ManagedFont> ManagedFonts { get; } = [];
    public ObservableCollection<DictionaryDefinition> ManagedDictionaries { get; } = [];

    private readonly List<Stage3ReadingMaterialViewModel> _allStage3ReadingMaterials = [];
    private readonly Dictionary<string, string> _readingMaterialCoverPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(ReadingMaterialSource Source, string BookTitle), bool> _readingMaterialGroupStates =
        new(new ReadingMaterialGroupKeyComparer());
    private ObservableCollection<Stage3DashboardRecentViewModel> _readingDashboardItems => DashboardRecentItems;
    private bool _readingMaterialsDirty;

    private KindleDevice? CurrentDevice => _devices.FirstOrDefault();

    private void ConfigureStage3Timer()
    {
        _stage3Timer.Tick += async (_, _) =>
        {
            if (!_stage3Ready || _kindle is null || _deviceEjectInProgress) return;
            await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible);
        };
        _stage3Timer.Start();
        _transferToastTimer.Tick += (_, _) =>
        {
            _transferToastTimer.Stop();
            HideTransferToast();
        };
        _deviceStatusToastTimer.Tick += (_, _) =>
        {
            _deviceStatusToastTimer.Stop();
            HideDeviceStatusToast();
        };
        _s3SyncTimer.Tick += async (_, _) => await RunAutomaticS3SyncIfDueAsync();
        _s3LocalChangeSyncTimer.Tick += async (_, _) => await RunPendingS3LocalChangeSyncAsync();
        _s3SyncTimer.Start();
    }

    // Unified bottom-right task progress surface, matching the WinUI
    // TransferToast.cs behaviour. The transfer toast replaces the generic
    // task popup while a Kindle operation is active so the two overlays never
    // stack. Popups fade out over ~220 ms instead of vanishing abruptly.
    private void ShowTaskProgressPopup()
    {
        TransferToast.IsVisible = false;
        TransferToast.Opacity = 0;
        TaskProgressPopup.IsVisible = true;
        TaskProgressPopup.Opacity = 1;
    }

    private void HideTaskProgressPopup() => FadeOutPopup(TaskProgressPopup);

    private void ShowTransferToast(
        string title,
        string message,
        double? progress = null,
        bool isIndeterminate = false,
        bool autoHide = false)
    {
        TaskProgressPopup.IsVisible = false;
        TaskProgressPopup.Opacity = 0;
        TransferToastTitleText.Text = title;
        TransferToastMessageText.Text = message;
        if (isIndeterminate)
        {
            TransferToastProgress.IsIndeterminate = true;
            TransferToastProgress.IsVisible = true;
        }
        else if (progress.HasValue)
        {
            TransferToastProgress.IsIndeterminate = false;
            TransferToastProgress.Value = Math.Clamp(progress.Value, 0, 100);
            TransferToastProgress.IsVisible = true;
        }
        else
        {
            TransferToastProgress.IsVisible = false;
        }
        TransferToast.IsVisible = true;
        TransferToast.Opacity = 1;
        if (autoHide)
        {
            _transferToastTimer.Stop();
            _transferToastTimer.Start();
        }
    }

    private void HideTransferToast()
    {
        _transferToastTimer.Stop();
        FadeOutPopup(TransferToast);
    }

    private async void FadeOutPopup(Control popup)
    {
        popup.Opacity = 0;
        await Task.Delay(250);
        if (popup.Opacity < 0.5) popup.IsVisible = false;
    }

    // Black bubble anchored above the eject button, showing connection and
    // ejection feedback (WinUI DeviceStatusPopup/DeviceStatusToast). The
    // bubble floats in window coordinates and follows the eject button's
    // triangle apex, clamped to the window like the reference.
    private void ShowDeviceStatusToast(string message)
    {
        DeviceStatusToastText.Text = message;
        DeviceStatusToast.IsVisible = true;
        PositionDeviceStatusToast();
        DeviceStatusToast.Opacity = 1;
        _deviceStatusToastTimer.Stop();
        _deviceStatusToastTimer.Start();
    }

    private void PositionDeviceStatusToast()
    {
        if (DeviceStatusEjectButton is not { } anchor) return;
        DeviceStatusToast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var popupSize = DeviceStatusToast.DesiredSize;
        PositionDeviceStatusToast(popupSize);
    }

    private void PositionDeviceStatusToast(Size popupSize)
    {
        if (DeviceStatusEjectButton is not { } anchor
            || popupSize.Width <= 0
            || popupSize.Height <= 0)
            return;
        // Both eject icons are 8-DIP upward triangles centred in their
        // buttons; anchor to the triangle apex rather than the button bounds.
        var apex = anchor.TranslatePoint(
            new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2 - 4),
            this);
        if (apex is not { } apexPoint) return;

        const double edgeMargin = 8;
        const double pointerCenterFromLeft = 20;
        const double pointerWidth = 12;
        var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        var maxLeft = Math.Max(edgeMargin, windowWidth - popupSize.Width - edgeMargin);
        var popupLeft = Math.Clamp(apexPoint.X - pointerCenterFromLeft, edgeMargin, maxLeft);
        var popupTop = Math.Max(edgeMargin, apexPoint.Y - popupSize.Height - 2);
        var pointerLeft = Math.Clamp(
            apexPoint.X - popupLeft - pointerWidth / 2,
            0,
            Math.Max(0, popupSize.Width - pointerWidth));

        if (_deviceStatusToastPosition != new Point(popupLeft, popupTop))
        {
            _deviceStatusToastPosition = new Point(popupLeft, popupTop);
            DeviceStatusToast.RenderTransform = new TranslateTransform(popupLeft, popupTop);
        }
        if (_deviceStatusToastPointerPosition != pointerLeft)
        {
            _deviceStatusToastPointerPosition = pointerLeft;
            DeviceStatusToastPointer.RenderTransform = new TranslateTransform(pointerLeft, 0);
        }
    }

    // Re-anchor the bubble while it is visible so it follows the eject button
    // when the window moves or is resized (WinUI LayoutUpdated behaviour).
    //
    // Layout callbacks must NOT call Measure: measuring during a layout pass
    // invalidates layout again, so LayoutUpdated fires again, and the toast
    // loops forever ("Infinite layout loop detected" — the device-connect
    // bubble crashed the app exactly this way). Use the already-laid-out
    // Bounds instead, and move the toast with RenderTransform so its position
    // never participates in the layout pass.
    private void DeviceStatusEjectButton_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!DeviceStatusToast.IsVisible) return;
        var popupSize = DeviceStatusToast.Bounds.Size;
        PositionDeviceStatusToast(popupSize);
    }

    private void HideDeviceStatusToast()
    {
        _deviceStatusToastTimer.Stop();
        DeviceStatusToast.Opacity = 0;
        _ = Task.Delay(350).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (DeviceStatusToast.Opacity < 0.5) DeviceStatusToast.IsVisible = false;
            }),
            TaskScheduler.Default);
    }

    // Device-operation coordination (WinUI reference MainWindow.xaml.cs):
    // eject drains active operations and refuses new ones while in progress.
    private bool HasActiveDeviceOperations
    {
        get
        {
            lock (_deviceOperationSync) return _activeDeviceOperations.Count > 0;
        }
    }

    private async Task TrackDeviceOperationAsync(Func<Task> operation)
    {
        if (_deviceEjectInProgress) return;
        var task = operation();
        lock (_deviceOperationSync) _activeDeviceOperations.Add(task);
        try
        {
            await task;
        }
        finally
        {
            lock (_deviceOperationSync) _activeDeviceOperations.Remove(task);
        }
    }

    private async Task WaitForActiveDeviceOperationsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_deviceOperationSync) tasks = _activeDeviceOperations.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks);
        }
    }

    private async Task InitializeStage3Async(CancellationToken cancellationToken)
    {
        if (_stage3Ready) return;

        await _readerData.InitializeAsync(cancellationToken);
        await _deviceModelStore.InitializeAsync(cancellationToken);
        _appSettings = await _appSettingsStore.LoadAsync(cancellationToken);
        if (Application.Current is App app)
            app.ApplyLanguage(_appSettings.UiLanguage);
        RefreshLocalizedZLibraryFilterItems();
        RefreshLocalizedReadingMaterialsSourceFilter();
        LoadReaderVerticalDebugBoxesSetting();
        await DetectCalibreAtStartupAsync(cancellationToken);
        _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync(cancellationToken);
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(cancellationToken);
        RefreshS3SyncIndicatorFromSettings();
        PopulateSettingsControls();
        ConfigureAppSettingsAutoSave();
        await RefreshManagedResourcesAsync(cancellationToken);
        DevicePageDeviceText.Text = T("正在检查设备…");
        DevicePageStatusText.Text = T("正在读取 Kindle 连接状态。");
        _stage3Ready = true;
        await RunAutoBackupIfNeededAsync(cancellationToken);
        if (_s3SyncStoredSettings.Settings.Enabled
            && _s3SyncStoredSettings.Settings.AutomaticSyncEnabled
            && _s3SyncStoredSettings.Settings.IsConfigured
            && _appSettings.NetworkEnabled)
        {
            _ = Dispatcher.UIThread.InvokeAsync(() => RunLifecycleS3SyncAsync());
        }
        await RefreshDevicesAsync(scanBooks: false, cancellationToken);

        // Let any pending layout/template work run to completion, then drop a
        // debounce that was only triggered by startup-time control events —
        // it must not surface the “设置已保存” capsule.
        await Dispatcher.UIThread.InvokeAsync(() => { });
        _appSettingsAutoSaveCancellation?.Cancel();
        _appSettingsAutoSaveCancellation?.Dispose();
        _appSettingsAutoSaveCancellation = null;
        _appSettingsStartupSettled = true;
    }

    private void ShowLibraryPage()
    {
        WindowBrandText.IsVisible = false;
        HideSettingsPanel();
        SetSidebarActive(AllBooksButton);
        FadeInPage(LibraryWorkspace);
        LibraryDetailPane.IsVisible = _selectedCard is not null;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        DevicePage.IsVisible = false;
        DeviceResourcePage.IsVisible = false;
        ReadingMaterialsPage.IsVisible = false;
        ReadingDashboardPage.IsVisible = false;
        ZLibraryPage.IsVisible = false;
        SettingsPage.IsVisible = false;
    }

    private void ShowStage3Page(Control page, Button? activeButton = null)
    {
        WindowBrandText.IsVisible = false;
        HideSettingsPanel();
        activeButton ??= page switch
        {
            _ when ReferenceEquals(page, DevicePage) => KindleBooksButton,
            _ when ReferenceEquals(page, DeviceResourcePage) =>
                _deviceResourceKind == KindleResourceKind.Font ? FontManagementButton : DictionaryManagementButton,
            _ when ReferenceEquals(page, ReadingMaterialsPage) =>
                ReaderNotesNavigationButton,
            _ when ReferenceEquals(page, ReadingDashboardPage) => ReadingDashboardButton,
            _ when ReferenceEquals(page, ZLibraryPage) => ZLibraryBooksButton,
            _ => SettingsNavigationButton
        };
        SetSidebarActive(activeButton);
        LibraryWorkspace.IsVisible = false;
        LibraryDetailPane.IsVisible = false;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        FadeInPage(page);
        DevicePage.IsVisible = ReferenceEquals(page, DevicePage);
        DeviceResourcePage.IsVisible = ReferenceEquals(page, DeviceResourcePage);
        ReadingMaterialsPage.IsVisible = ReferenceEquals(page, ReadingMaterialsPage);
        ReadingDashboardPage.IsVisible = ReferenceEquals(page, ReadingDashboardPage);
        ZLibraryPage.IsVisible = ReferenceEquals(page, ZLibraryPage);
        SettingsPage.IsVisible = ReferenceEquals(page, SettingsPage);
    }

    private void ShowSettingsPanel(Control panel)
    {
        KindleEmailSettingsPane.IsVisible = false;
        ZLibraryAccountPane.IsVisible = false;
        ReaderAiSettingsPane.IsVisible = false;
        SystemSettingsPane.IsVisible = false;
        panel.IsVisible = true;
        _settingsPanelVisible = true;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
        {
            LibraryRoot.ColumnDefinitions[1].Width = new GridLength(0);
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void HideSettingsPanel()
    {
        KindleEmailSettingsPane.IsVisible = false;
        ZLibraryAccountPane.IsVisible = false;
        ReaderAiSettingsPane.IsVisible = false;
        SystemSettingsPane.IsVisible = false;
        _settingsPanelVisible = false;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
        {
            LibraryRoot.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        }
    }

    private void SetSidebarActive(Button activeButton)
    {
        Button[] buttons =
        [
            AllBooksButton,
            KindleBooksButton,
            ZLibraryBooksButton,
            FontManagementButton,
            DictionaryManagementButton,
            ReaderNotesNavigationButton,
            ReadingDashboardButton,
            SettingsNavigationButton,
            SystemBackupNavigationButton,
            SystemS3SyncNavigationButton,
            KindleEmailSettingsNavigationButton,
            ZLibraryAccountNavigationButton,
            ReaderAiSettingsNavigationButton
        ];
        foreach (var button in buttons)
            button.Classes.Remove("active");
        activeButton.Classes.Add("active");

        _activeNavigationSectionButton = activeButton switch
        {
            _ when ReferenceEquals(activeButton, FontManagementButton)
                || ReferenceEquals(activeButton, DictionaryManagementButton) => DeviceManagementSectionButton,
            _ when ReferenceEquals(activeButton, ReaderNotesNavigationButton)
                || ReferenceEquals(activeButton, ReadingDashboardButton) => ReadingSectionButton,
            _ when ReferenceEquals(activeButton, SettingsNavigationButton)
                || ReferenceEquals(activeButton, SystemBackupNavigationButton)
                || ReferenceEquals(activeButton, SystemS3SyncNavigationButton)
                || ReferenceEquals(activeButton, KindleEmailSettingsNavigationButton)
                || ReferenceEquals(activeButton, ZLibraryAccountNavigationButton)
                || ReferenceEquals(activeButton, ReaderAiSettingsNavigationButton) => SystemSectionButton,
            _ => BookManagementSectionButton
        };
        switch (_activeNavigationSectionButton)
        {
            case var section when ReferenceEquals(section, BookManagementSectionButton):
                BookManagementChildren.IsVisible = true;
                break;
            case var section when ReferenceEquals(section, DeviceManagementSectionButton):
                DeviceManagementChildren.IsVisible = true;
                break;
            case var section when ReferenceEquals(section, ReadingSectionButton):
                ReadingChildren.IsVisible = true;
                break;
            case var section when ReferenceEquals(section, SystemSectionButton):
                SystemChildren.IsVisible = true;
                break;
        }
        UpdateSidebarSectionVisuals();
    }

    private async Task RefreshDevicesAsync(
        bool scanBooks,
        CancellationToken cancellationToken = default)
    {
        if (_isRefreshingDevices || _deviceEjectInProgress) return;
        _isRefreshingDevices = true;
        try
        {
            // A manually ejected device stays disconnected until the user
            // explicitly refreshes or taps the device card (WinUI reference).
            if (_manuallyDisconnectedDeviceId is not null)
            {
                SetDisconnectedDeviceUi(T("已断开"));
                return;
            }

            if (_kindle is null)
            {
                _devices = [];
                DevicePageDeviceText.Text = T("平台服务未连接");
                DevicePageStatusText.Text = T("设备功能将在 Windows 平台启动头中启用。");
                SetEjectButtonsEnabled(false);
                return;
            }

            var detected = await _kindle.DetectDevicesAsync(cancellationToken);
            if (detected.Count == 0)
            {
                _acceptedDeviceId = null;
                _ignoredDeviceId = null;
                _manuallyDisconnectedDeviceId = null;
                _lastDeviceIdentity = null;
                SetDisconnectedDeviceUi();
                return;
            }

            var device = detected[0];
            var displayName = await _deviceModelStore.GetModelAsync(device.Identity, cancellationToken)
                ?? _appSettings.DefaultDeviceModel
                ?? device.Name;
            if (!string.Equals(_acceptedDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
            {
                if (_appSettings.AutoConnectDevice)
                {
                    _acceptedDeviceId = device.Identity;
                    _ignoredDeviceId = null;
                }
                else
                {
                    if (string.Equals(_ignoredDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
                    {
                        SetDisconnectedDeviceUi(T("已忽略 {0}", displayName));
                        return;
                    }

                    if (!await ShowDevicePromptAsync(
                            T("发现 Kindle 设备"),
                            T("发现 {0}（{1}）。是否连接到 Kkindle？", displayName, device.ConnectionLabel),
                            T("连接"),
                            T("暂不连接")))
                    {
                        _ignoredDeviceId = device.Identity;
                        _lastDeviceIdentity = null;
                        SetDisconnectedDeviceUi(T("已忽略 {0}", displayName));
                        return;
                    }

                    _acceptedDeviceId = device.Identity;
                    _ignoredDeviceId = null;
                }
            }

            var changed = !string.Equals(device.Identity, _lastDeviceIdentity, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                _deviceBooksLoaded = false;
                _deviceResourceCache.Clear();
                _deviceClippingCache.Clear();
            }
            _devices = [device];
            _lastDeviceIdentity = device.Identity;
            _deviceDisplayName = displayName;
            DevicePageDeviceText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
            DeviceNameButton.IsEnabled = true;
            DevicePageStatusText.Text = changed ? T("设备已连接，正在准备设备信息…") : T("设备已连接。");
            KindleStatusText.Text = _deviceDisplayName;
            KindleConnectionText.Text = T("{0} · 已连接", device.ConnectionLabel);
            KindleConnectionText.IsVisible = true;
            DeviceStorageText.Text = device.CapacityLabel;
            _deviceUsedRatio = device.TotalBytes > 0
                ? Math.Clamp((device.TotalBytes - device.FreeBytes) / (double)device.TotalBytes, 0, 1)
                : 0;
            UpdateDeviceStorageBar();
            SetEjectButtonsEnabled(true);
            if (changed) ShowDeviceStatusToast(T("{0} 已连接", displayName));
            if (changed)
                _deviceWarmTask = TrackDeviceOperationAsync(() => WarmDeviceCachesAsync(device, !scanBooks, cancellationToken));
            if (scanBooks && (changed || !_deviceBooksLoaded || _deviceBooksDirty))
                await RefreshDeviceBooksAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DevicePageStatusText.Text = T("设备检测失败：{0}", UiText.Localize(exception.Message));
            KindleStatusText.Text = T("设备状态读取失败");
            KindleConnectionText.Text = UiText.Localize(exception.Message);
            DeviceStorageText.Text = T("无存储信息");
            _deviceUsedRatio = 0;
            UpdateDeviceStorageBar();
            SetEjectButtonsEnabled(false);
        }
        finally
        {
            _isRefreshingDevices = false;
        }
    }

    private void SetDisconnectedDeviceUi(string? detail = null)
    {
        foreach (var book in DeviceBooks) book.Dispose();
        DeviceBooks.Clear();
        _deviceBooksLoaded = false;
        _deviceWarmTask = null;
        _deviceResourceCache.Clear();
        _deviceClippingCache.Clear();
        DeviceBookCountText.Text = "0";
        _devices = [];
        RefreshLibraryPresenceState();
        UpdateDeviceBookSelectionUi();
        _deviceDisplayName = null;
        DevicePageDeviceText.Text = T("未检测到设备");
        DeviceNameButton.IsEnabled = false;
        DevicePageStatusText.Text = detail ?? (OperatingSystem.IsWindows()
            ? T("请连接并解锁 Kindle；支持 USB 磁盘与 MTP。")
            : T("请连接并解锁 Kindle；当前平台支持挂载为 USB 磁盘的 Kindle。"));
        KindleStatusText.Text = T("无设备连接");
        KindleConnectionText.Text = detail ?? string.Empty;
        KindleConnectionText.IsVisible = !string.IsNullOrWhiteSpace(detail);
        DeviceStorageText.Text = T("无存储信息");
        _deviceUsedRatio = 0;
        UpdateDeviceStorageBar();
        SetEjectButtonsEnabled(false);
    }

    private void DeviceStorageBar_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateDeviceStorageBar();

    private void UpdateDeviceStorageBar()
    {
        var availableWidth = Math.Max(0, DeviceStorageBar.Bounds.Width - 2);
        var usedWidth = availableWidth * _deviceUsedRatio;
        if (Math.Abs(DeviceStorageUsedBar.Width - usedWidth) > 0.01)
            DeviceStorageUsedBar.Width = usedWidth;
    }

    private async Task RefreshDeviceBooksAsync(CancellationToken cancellationToken = default) =>
        await TrackDeviceOperationAsync(() => RefreshDeviceBooksCoreAsync(cancellationToken));

    private async Task RefreshDeviceBooksCoreAsync(CancellationToken cancellationToken)
    {
        if (_kindle is null || CurrentDevice is not { } device) return;

        DevicePageStatusText.Text = T("正在扫描 Kindle 书籍…");
        DeviceBookEmptyText.Text = T("正在读取设备书库…");
        DeviceBookEmptyState.IsVisible = true;
        try
        {
            var books = await _kindle.ScanBooksAsync(device, cancellationToken);
            foreach (var old in DeviceBooks) old.Dispose();
            DeviceBooks.Clear();
            foreach (var book in books)
                DeviceBooks.Add(new KindleBookCardViewModel(book));
            DeviceBookCountText.Text = DeviceBooks.Count.ToString();
            UpdateDeviceBookSelectionUi();

            RefreshLibraryPresenceState();
            DevicePageStatusText.Text = T("已读取 {0} 本书 · {1}", books.Count, device.ConnectionLabel);
            _deviceBooksLoaded = true;
            _deviceBooksDirty = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceBookEmptyText.Text = T("扫描失败：{0}", UiText.Localize(exception.Message));
            DeviceBookEmptyState.IsVisible = true;
            DevicePageStatusText.Text = T("Kindle 书库扫描失败。");
        }
    }

    private void RefreshLibraryPresenceState(bool refreshDeviceView = true)
    {
        var comparison = BookLibraryComparer.Compare(
            ViewModel.LibraryBooks,
            DeviceBooks.Select(card => card.Book));
        foreach (var localCard in ViewModel.Books)
        {
            localCard.SetLibraryPresence(
                comparison.BooksOnKindle.Contains(localCard.Book.Id)
                    ? BookLibraryPresence.Both
                    : BookLibraryPresence.ComputerOnly);
            localCard.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled);
        }
        foreach (var card in DeviceBooks)
        {
            card.SetLibraryPresence(
                comparison.KindleBooksOnComputer.Contains(card.Book.RelativePath)
                    ? BookLibraryPresence.Both
                    : BookLibraryPresence.KindleOnly);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled);
        }

        if (refreshDeviceView && _stage3Ready)
            ApplyDeviceBookFilter();
    }

    private async Task WarmDeviceCachesAsync(
        KindleDevice device,
        bool preloadBooks,
        CancellationToken cancellationToken)
    {
        if (_kindle is null || !ReferenceEquals(CurrentDevice, device)) return;
        try
        {
            var cacheKey = BuildDeviceCacheKey(device);
            var persisted = await _kindleAuxiliaryCacheStore.GetAsync(device.Identity, cancellationToken);
            if (persisted is not null)
            {
                _deviceResourceCache[(cacheKey, KindleResourceKind.Font)] = persisted.Fonts;
                _deviceResourceCache[(cacheKey, KindleResourceKind.Dictionary)] = persisted.Dictionaries;
                _deviceClippingCache[device.Identity] = persisted.Clippings;
            }

            // Refresh all four device data sets once per connection. Page switches
            // then use these identity-bound snapshots instead of reopening Kindle.
            if (preloadBooks && !_deviceBooksLoaded)
                await RefreshDeviceBooksAsync(cancellationToken);
            var fonts = await _kindle.ScanResourcesAsync(device, KindleResourceKind.Font, cancellationToken);
            var dictionaries = await _kindle.ScanResourcesAsync(device, KindleResourceKind.Dictionary, cancellationToken);
            var clippings = await _kindle.ReadClippingsAsync(device, cancellationToken);
            _deviceResourceCache[(cacheKey, KindleResourceKind.Font)] = fonts;
            _deviceResourceCache[(cacheKey, KindleResourceKind.Dictionary)] = dictionaries;
            _deviceClippingCache[device.Identity] = clippings;
            await _kindleAuxiliaryCacheStore.SaveAsync(device.Identity, new KindleDeviceAuxiliaryCacheSnapshot
            {
                Fonts = fonts.ToList(),
                Dictionaries = dictionaries.ToList(),
                Clippings = clippings.ToList(),
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DevicePageStatusText.Text = T("设备信息已读取部分内容：{0}", UiText.Localize(exception.Message));
        }
    }

    private void SetEjectButtonsEnabled(bool enabled)
    {
        DeviceStatusEjectButton.IsEnabled = enabled;
        EjectDeviceButton.IsEnabled = enabled;
        // ToolTip / automation name follow the connection state and transport,
        // exactly like the WinUI reference (WPD sessions are "stopped", USB
        // disks are "ejected").
        var action = !enabled
            ? T("未连接设备")
            : CurrentDevice?.Transport == KindleTransport.Wpd ? T("停止访问设备") : T("安全弹出设备");
        ToolTip.SetTip(DeviceStatusEjectButton, action);
        AutomationProperties.SetName(DeviceStatusEjectButton, action);
        ToolTip.SetTip(EjectDeviceButton, action);
        AutomationProperties.SetName(EjectDeviceButton, action);
    }
    private Task<bool> ShowDevicePromptAsync(
        string title,
        string message,
        string primaryText,
        string cancelText)
    {
        if (_devicePromptCompletion is not null)
            return Task.FromResult(false);

        _devicePromptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DevicePromptTitleText.Text = title;
        DevicePromptMessageText.Text = message;
        DevicePromptPrimaryButton.Content = primaryText;
        DevicePromptCancelButton.Content = cancelText;
        DevicePromptOverlay.IsVisible = true;
        DevicePromptOverlay.Opacity = 1;
        DevicePromptOverlay.Focus();
        return _devicePromptCompletion.Task;
    }

    private void DevicePromptPrimaryButton_Click(object? sender, RoutedEventArgs e)
        => CompleteDevicePrompt(true);

    private void DevicePromptCancelButton_Click(object? sender, RoutedEventArgs e)
        => CompleteDevicePrompt(false);

    private void DevicePromptOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CompleteDevicePrompt(false);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CompleteDevicePrompt(true);
        }
    }

    private void CompleteDevicePrompt(bool result)
    {
        var completion = _devicePromptCompletion;
        if (completion is null) return;
        _devicePromptCompletion = null;
        DevicePromptOverlay.IsVisible = false;
        completion.TrySetResult(result);
    }

    private async Task OpenKindlePageAsync()
    {
        ShowStage3Page(DevicePage);
        await RefreshDevicesAsync(scanBooks: true);
        if (CurrentDevice is not null && (!_deviceBooksLoaded || _deviceBooksDirty))
        {
            _deviceBooksDirty = false;
            await RefreshDeviceBooksAsync();
        }
    }

    private async void KindleBooksButton_Click(object? sender, RoutedEventArgs e) => await OpenKindlePageAsync();

    private async void RefreshDevicesButton_Click(object? sender, RoutedEventArgs e)
    {
        _manuallyDisconnectedDeviceId = null;
        await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible);
        if (DevicePage.IsVisible && CurrentDevice is not null)
            await RefreshDeviceBooksAsync();
    }

    private async void ScanDeviceBooksButton_Click(object? sender, RoutedEventArgs e) => await RefreshDeviceBooksAsync();

    private IReadOnlyList<KindleBookCardViewModel> GetSelectedDeviceBooks() =>
        DeviceBooks.Where(book => book.IsSelected).ToArray();

    private void UpdateDeviceBookSelectionUi()
    {
        // 与电脑书库一致：只有真正多选（≥2 本）时卡片才显示 ✓ 徽标。
        var multi = DeviceBooks.Count(book => book.IsSelected) > 1;
        foreach (var book in DeviceBooks)
            book.IsMultiSelected = book.IsSelected && multi;

        var selected = GetSelectedDeviceBooks();
        DeviceBookSelectionBar.IsVisible = selected.Count > 1;
        DeviceBookSelectionText.Text = selected.Count == 0 ? string.Empty : T("已选择 {0} 本书", selected.Count);
    }

    private void SetDeviceBookView(bool gridView)
    {
        _deviceGridView = gridView;
        DeviceBookGridScroll.IsVisible = gridView;
        DeviceBookListScroll.IsVisible = !gridView;
        DeviceViewToggleIcon.Data = Geometry.Parse(gridView
            ? LibraryGridGlyphData
            : LibraryListGlyphData);
        ToolTip.SetTip(DeviceViewToggleButton, gridView
            ? T("当前：网格视图，点击切换到列表视图")
            : T("当前：列表视图，点击切换到网格视图"));
    }

    // The view button cycles 网格 ↔ 列表, matching the library view button.
    private void DeviceViewToggleButton_Click(object? sender, RoutedEventArgs e) =>
        SetDeviceBookView(!_deviceGridView);

    private void DeviceBookSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter();
    }

    private void DeviceBookFilterButton_Click(object? sender, RoutedEventArgs e) =>
        DeviceBookFilterPanel.IsVisible = !DeviceBookFilterPanel.IsVisible;

    private void DeviceBookFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter();
    }

    private void ClearDeviceBookFiltersButton_Click(object? sender, RoutedEventArgs e)
    {
        DeviceBookSearchBox.Text = string.Empty;
        DeviceBookFormatFilterBox.SelectedIndex = 0;
        DeviceBookPresenceFilterBox.SelectedIndex = 0;
        DeviceBookSortBox.SelectedIndex = 0;
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter();
    }

    private void ApplyDeviceBookFilter()
    {
        var query = DeviceBookSearchBox.Text?.Trim() ?? string.Empty;
        var format = (DeviceBookFormatFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var presence = (DeviceBookPresenceFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var sort = (DeviceBookSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "title";

        IEnumerable<KindleBookCardViewModel> filtered = DeviceBooks;
        if (query.Length > 0)
        {
            filtered = filtered.Where(card =>
                card.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || card.Authors.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || card.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }
        if (format.Length > 0)
            filtered = filtered.Where(card => card.Book.Format.Equals(format, StringComparison.OrdinalIgnoreCase));
        if (presence == "kindle")
            filtered = filtered.Where(card => card.LibraryPresence == BookLibraryPresence.KindleOnly);
        else if (presence == "both")
            filtered = filtered.Where(card => card.LibraryPresence == BookLibraryPresence.Both);

        filtered = sort switch
        {
            "author" => filtered.OrderBy(card => card.Authors, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            "modified" => filtered.OrderByDescending(card => card.Book.ModifiedAt)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            "size" => filtered.OrderByDescending(card => card.Book.Size)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        VisibleDeviceBooks.Clear();
        foreach (var card in filtered)
            VisibleDeviceBooks.Add(card);
        UpdateDeviceBookEmptyState();
    }

    private void UpdateDeviceBookEmptyState()
    {
        DeviceBookEmptyState.IsVisible = VisibleDeviceBooks.Count == 0;
        if (VisibleDeviceBooks.Count > 0) return;
        DeviceBookEmptyText.Text = DeviceBooks.Count > 0
            ? T("没有符合当前搜索或筛选条件的书籍。")
            : CurrentDevice is null
                ? T("连接 Kindle 后扫描书籍。")
                : T("设备中没有可识别的书籍。");
    }

    private void ClearDeviceBookSelection()
    {
        foreach (var book in DeviceBooks)
            book.IsSelected = false;
        _deviceMultiSelectAnchor = null;
        UpdateDeviceBookSelectionUi();
    }

    private void DeviceBook_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: KindleBookCardViewModel card } control
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        if (_deviceGridView)
        {
            _deviceRubberBandStart = e.GetPosition(DeviceBookGridScroll);
            _deviceRubberBandCurrent = _deviceRubberBandStart;
            _deviceRubberBandSelecting = false;
            _deviceRubberBandPressedOnCard = true;
            _deviceRubberBandPointerSequenceHandled = false;
            e.Pointer.Capture(control);
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            card.IsSelected = !card.IsSelected;
            _deviceMultiSelectAnchor = card;
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            var cards = VisibleDeviceBooks.ToList();
            var clickedIndex = cards.IndexOf(card);
            var anchorIndex = _deviceMultiSelectAnchor is null ? -1 : cards.IndexOf(_deviceMultiSelectAnchor);
            foreach (var candidate in cards) candidate.IsSelected = false;
            var start = anchorIndex < 0 ? clickedIndex : Math.Min(anchorIndex, clickedIndex);
            var end = anchorIndex < 0 ? clickedIndex : Math.Max(anchorIndex, clickedIndex);
            for (var index = start; index <= end; index++) cards[index].IsSelected = true;
            _deviceMultiSelectAnchor = card;
        }
        else
        {
            foreach (var candidate in DeviceBooks) candidate.IsSelected = ReferenceEquals(candidate, card);
            _deviceMultiSelectAnchor = card;
        }

        UpdateDeviceBookSelectionUi();
        e.Handled = true;
    }

    private void DeviceBookGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_deviceGridView
            || !e.GetCurrentPoint(DeviceBookGridScroll).Properties.IsLeftButtonPressed)
            return;
        _deviceRubberBandPressedOnCard = IsDeviceBookCardSource(e.Source);
        if (_deviceRubberBandPressedOnCard) return;

        _deviceRubberBandStart = e.GetPosition(DeviceBookGridScroll);
        _deviceRubberBandCurrent = _deviceRubberBandStart;
        _deviceRubberBandSelecting = false;
        _deviceRubberBandPointerSequenceHandled = false;
        e.Pointer.Capture(DeviceBookGridScroll);
        DeviceBookGridScroll.Focus();
    }

    private void DeviceBookGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_deviceGridView
            || !e.GetCurrentPoint(DeviceBookGridScroll).Properties.IsLeftButtonPressed)
            return;
        _deviceRubberBandCurrent = e.GetPosition(DeviceBookGridScroll);
        if (!_deviceRubberBandSelecting)
        {
            var deltaX = _deviceRubberBandCurrent.X - _deviceRubberBandStart.X;
            var deltaY = _deviceRubberBandCurrent.Y - _deviceRubberBandStart.Y;
            if (Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) < RubberBandDragThreshold)
                return;
            _deviceRubberBandSelecting = true;
            e.Pointer.Capture(DeviceBookGridScroll);
            DeviceRubberBandRectangle.IsVisible = true;
        }
        UpdateDeviceRubberBandSelection();
        e.Handled = true;
    }

    private void DeviceBookGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_deviceRubberBandPointerSequenceHandled) return;
        _deviceRubberBandPointerSequenceHandled = true;
        if (!_deviceRubberBandSelecting)
        {
            if (!_deviceRubberBandPressedOnCard)
                ClearDeviceBookSelection();
            _deviceRubberBandPressedOnCard = false;
            return;
        }
        _deviceRubberBandCurrent = e.GetPosition(DeviceBookGridScroll);
        UpdateDeviceRubberBandSelection();
        FinishDeviceRubberBandSelection(e.Pointer);
        _deviceRubberBandPressedOnCard = false;
        e.Handled = true;
    }

    private void DeviceBookGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_deviceRubberBandSelecting
            && !ReferenceEquals(e.Pointer.Captured, DeviceBookGridScroll))
            FinishDeviceRubberBandSelection(null);
        _deviceRubberBandPressedOnCard = false;
    }

    private void UpdateDeviceRubberBandSelection()
    {
        var left = Math.Min(_deviceRubberBandStart.X, _deviceRubberBandCurrent.X);
        var top = Math.Min(_deviceRubberBandStart.Y, _deviceRubberBandCurrent.Y);
        var width = Math.Abs(_deviceRubberBandCurrent.X - _deviceRubberBandStart.X);
        var height = Math.Abs(_deviceRubberBandCurrent.Y - _deviceRubberBandStart.Y);
        Canvas.SetLeft(DeviceRubberBandRectangle, left);
        Canvas.SetTop(DeviceRubberBandRectangle, top);
        DeviceRubberBandRectangle.Width = width;
        DeviceRubberBandRectangle.Height = height;

        var selection = new Rect(left, top, width, height);
        foreach (var card in DeviceBooks) card.IsSelected = false;
        foreach (var card in VisibleDeviceBooks)
        {
            if (DeviceBookGridItems.ContainerFromItem(card) is not Control container) continue;
            var origin = container.TranslatePoint(default, DeviceBookGridScroll);
            if (origin is not { } point) continue;
            if (new Rect(point, container.Bounds.Size).Intersects(selection))
                card.IsSelected = true;
        }
        _deviceMultiSelectAnchor = VisibleDeviceBooks.FirstOrDefault(card => card.IsSelected);
        UpdateDeviceBookSelectionUi();
    }

    private void FinishDeviceRubberBandSelection(IPointer? pointer)
    {
        _deviceRubberBandSelecting = false;
        _deviceRubberBandPointerSequenceHandled = true;
        pointer?.Capture(null);
        DeviceRubberBandRectangle.IsVisible = false;
        UpdateDeviceBookSelectionUi();
    }

    private static bool IsDeviceBookCardSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Control { DataContext: KindleBookCardViewModel })
                return true;
        }
        return false;
    }

    // 与电脑书库一致：悬停整张卡片时显示黑色细边框。
    private void DeviceBook_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: KindleBookCardViewModel card })
            card.IsHovered = true;
    }

    private void DeviceBook_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: KindleBookCardViewModel card })
            card.IsHovered = false;
    }

    private void DeviceBook_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: KindleBookCardViewModel card } control) return;
        if (!card.IsSelected)
        {
            foreach (var candidate in DeviceBooks) candidate.IsSelected = ReferenceEquals(candidate, card);
            _deviceMultiSelectAnchor = card;
            UpdateDeviceBookSelectionUi();
        }

        var selectedCount = GetSelectedDeviceBooks().Count;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(
            selectedCount > 1 ? T("导出到电脑书库（{0}）", selectedCount) : T("导出到电脑书库"),
            selectedCount > 1 ? ExportSelectedDeviceBooksAsync : () => ExportDeviceBookAsync(card)));
        menu.Items.Add(CreateMenuItem(
            selectedCount > 1 ? T("从 Kindle 删除所选（{0}）", selectedCount) : T("从 Kindle 删除"),
            DeleteSelectedDeviceBooksAsync));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("取消选择"), () =>
        {
            foreach (var candidate in DeviceBooks) candidate.IsSelected = false;
            _deviceMultiSelectAnchor = null;
            UpdateDeviceBookSelectionUi();
            return Task.CompletedTask;
        }));
        menu.Open(control);
        e.Handled = true;
    }

    private void DeviceBookSelectionChanged(object? sender, RoutedEventArgs e) =>
        UpdateDeviceBookSelectionUi();

    private void ClearDeviceBookSelectionButton_Click(object? sender, RoutedEventArgs e)
        => ClearDeviceBookSelection();

    private async void EjectDeviceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_kindle is null || CurrentDevice is not { } device || _deviceEjectInProgress) return;
        _deviceEjectInProgress = true;
        var isWpd = device.Transport == KindleTransport.Wpd;
        try
        {
            // Eject always asks first (WinUI reference): WPD sessions are
            // stopped instead of ejected, and active transfers are drained
            // before the removal request is issued.
            if (!await ShowDevicePromptAsync(
                    isWpd ? T("停止访问 Kindle？") : T("安全弹出 Kindle？"),
                    isWpd
                        ? T("Kkindle 将停止访问并释放设备会话；随后请在 Kindle 屏幕上点击“断开连接”。若有传输任务正在进行，将等待其完成后自动断开。")
                        : T("若有传输任务正在进行，将等待其完成后自动断开。"),
                    isWpd ? T("停止访问") : T("弹出"),
                    T("取消"))) return;

            // Block refresh polling and re-detection before waiting so no new
            // device session can be opened between the final operation and the
            // eject request.
            _lastDeviceIdentity = null;

            if (_isTransferring || HasActiveDeviceOperations)
            {
                ShowTransferToast(
                    T("正在等待设备操作完成"),
                    T("检测到设备任务正在进行，完成后将自动断开设备。"),
                    isIndeterminate: true);
                try
                {
                    await WaitForActiveDeviceOperationsAsync();
                }
                finally
                {
                    HideTransferToast();
                }
            }

            SetEjectButtonsEnabled(false);
            DevicePageStatusText.Text = T("正在安全弹出设备…");
            // 等待正在进行的设备检测结束：3 秒轮询会打开 WPD/Shell 会话，
            // 若不释放，Windows 会拒绝安全弹出（旧版 StopDeviceAccessAsync
            // 行为）。书库扫描等传输任务已由上面的等待覆盖。
            var detectionDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (_isRefreshingDevices && DateTime.UtcNow < detectionDeadline)
                await Task.Delay(50, _lifetimeCancellation.Token);
            if (_isRefreshingDevices)
                throw new IOException(T("后台 Kindle 检测未能及时停止，请稍后重试断开。"));

            await _kindle.EjectAsync(device, _lifetimeCancellation.Token);
            foreach (var book in DeviceBooks) book.Dispose();
            DeviceBooks.Clear();
            DeviceBookCountText.Text = "0";
            RefreshLibraryPresenceState();
            UpdateDeviceBookSelectionUi();
            _acceptedDeviceId = null;
            _ignoredDeviceId = null;
            _manuallyDisconnectedDeviceId = device.Identity;
            var disconnectedMessage = isWpd
                ? T("{0} 已停止访问，现在可以安全移除你的设备", device.Name)
                : T("{0} 已安全弹出，现在可以安全移除你的设备", device.Name);
            SetDisconnectedDeviceUi(T("已断开"));
            DeviceStorageText.Text = isWpd ? T("设备已停止访问") : T("设备已弹出");
            ShowDeviceStatusToast(disconnectedMessage);
        }
        catch (Exception exception)
        {
            _lastDeviceIdentity = null;
            _manuallyDisconnectedDeviceId = null;
            DevicePageStatusText.Text = T("弹出失败：{0}", UiText.Localize(exception.Message));
            SetEjectButtonsEnabled(true);
            await ShowMessageAsync(T("无法弹出设备"), UiText.Localize(exception.Message));
        }
        finally
        {
            _deviceEjectInProgress = false;
        }
    }

    // Only the lower-left Kindle heading is interactive: connected devices open
    // the remembered model picker, while a disconnected heading starts a fresh
    // detection pass. Storage details and blank card space remain inert.
    private void DeviceStatusBox_Tapped(object? sender, TappedEventArgs e)
    {
        if (IsButtonSource(e.Source))
        {
            return;
        }

        if (CurrentDevice is not null)
        {
            ShowDeviceModelPicker();
            e.Handled = true;
            return;
        }

        // Keep the compact status card geometrically stable while detection
        // runs; inserting a temporary second line made it flash and jump.
        if (_isRefreshingDevices) return;
        _manuallyDisconnectedDeviceId = null;
        _ignoredDeviceId = null;
        _lastDeviceIdentity = null;
        _ = RefreshDevicesAsync(scanBooks: DevicePage.IsVisible, _lifetimeCancellation.Token);
        e.Handled = true;
    }

    private void ShowDeviceModelPicker()
    {
        if (CurrentDevice is null) return;

        var menu = new MenuFlyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft
        };
        menu.Items.Add(CreateMenuItem(T("默认名称（设备自带）"), () => ApplyDeviceModelAsync(null)));
        menu.Items.Add(new Separator());
        foreach (var vendor in DeviceModelCatalog.Vendors)
        {
            var vendorMenu = new MenuItem { Header = LocalizeDeviceModelLabel(vendor.Name) };
            foreach (var model in vendor.Models)
                vendorMenu.Items.Add(CreateMenuItem(LocalizeDeviceModelLabel(model), () => ApplyDeviceModelAsync(model)));
            menu.Items.Add(vendorMenu);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("自定义型号…"), () =>
        {
            ShowDeviceModelInput();
            return Task.CompletedTask;
        }));
        menu.ShowAt(DeviceStatusBox);
    }

    private static string LocalizeDeviceModelLabel(string value) => value switch
    {
        "Kindle（基础版）" => T("Kindle（基础版）"),
        "Kindle Paperwhite 11 代" => T("Kindle Paperwhite 11 代"),
        "Kindle Paperwhite 12 代" => T("Kindle Paperwhite 12 代"),
        "Kindle Scribe 1 代" => T("Kindle Scribe 1 代"),
        "Kindle Scribe 2 代" => T("Kindle Scribe 2 代"),
        "Kindle Scribe 3 代" => T("Kindle Scribe 3 代"),
        "汉王" => T("汉王"),
        "汉王 N10" => T("汉王 N10"),
        "汉王 N10 Touch" => T("汉王 N10 Touch"),
        "汉王 N10 mini" => T("汉王 N10 mini"),
        "汉王 Clear" => T("汉王 Clear"),
        "汉王 E10" => T("汉王 E10"),
        "汉王 A5" => T("汉王 A5"),
        "掌阅" => T("掌阅"),
        "掌阅 iReader Ocean 2" => T("掌阅 iReader Ocean 2"),
        "掌阅 iReader Ocean 3" => T("掌阅 iReader Ocean 3"),
        "掌阅 iReader Ocean 4" => T("掌阅 iReader Ocean 4"),
        "掌阅 iReader Smart 3" => T("掌阅 iReader Smart 3"),
        "掌阅 iReader Smart 4" => T("掌阅 iReader Smart 4"),
        "掌阅 iReader Smart X" => T("掌阅 iReader Smart X"),
        "掌阅 iReader Light 2" => T("掌阅 iReader Light 2"),
        "掌阅 iReader Light 3" => T("掌阅 iReader Light 3"),
        "掌阅 iReader Neo" => T("掌阅 iReader Neo"),
        _ => value
    };

    private void ShowDeviceModelInput()
    {
        DeviceModelInputBox.Text = _deviceDisplayName ?? CurrentDevice?.Name ?? string.Empty;
        DeviceModelInputOverlay.IsVisible = true;
        DeviceModelInputOverlay.Opacity = 1;
        DeviceModelInputBox.Focus();
        DeviceModelInputBox.SelectAll();
    }

    private async void DeviceModelInputOkButton_Click(object? sender, RoutedEventArgs e)
        => await ApplyDeviceModelInputAsync();

    private async Task ApplyDeviceModelInputAsync()
    {
        var model = DeviceModelInputBox.Text?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            DeviceModelInputStatusText.Text = T("型号不能为空。");
            await ShowMessageAsync(T("型号不能为空"), T("请输入设备型号，或选择“默认名称”。"));
            return;
        }
        DeviceModelInputOverlay.IsVisible = false;
        DeviceModelInputStatusText.Text = string.Empty;
        await ApplyDeviceModelAsync(model);
    }

    private void DeviceModelInputCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        DeviceModelInputOverlay.IsVisible = false;
        DeviceModelInputStatusText.Text = string.Empty;
    }

    private void DeviceModelInputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DeviceModelInputOverlay.IsVisible = false;
            DeviceModelInputStatusText.Text = string.Empty;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ApplyDeviceModelInputAsync();
        }
    }

    private async Task ApplyDeviceModelAsync(string? model)
    {
        if (CurrentDevice is not { } device) return;
        var normalized = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        try
        {
            if (normalized is null)
            {
                await _deviceModelStore.DeleteModelAsync(device.Identity, _lifetimeCancellation.Token);
                // A global default is only needed until the user has a chance
                // to identify this device. Choosing the device's own name from
                // the picker should remove that onboarding fallback as well.
                if (_appSettings.DefaultDeviceModel is not null)
                {
                    _appSettings = AppSettings.Normalize(_appSettings with { DefaultDeviceModel = null });
                    await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
                }
            }
            else
                await _deviceModelStore.SetModelAsync(device.Identity, normalized, _lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法保存设备型号：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法保存设备型号"), UiText.Localize(exception.Message));
            return;
        }

        _deviceDisplayName = normalized ?? device.Name;
        KindleStatusText.Text = _deviceDisplayName;
        KindleConnectionText.Text = T("{0} · 已连接", device.ConnectionLabel);
        KindleConnectionText.IsVisible = true;
        DevicePageDeviceText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
        DeviceNameButton.IsEnabled = true;
        if (DeviceResourcePage.IsVisible)
            DeviceResourceStatusText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
    }

    private void DeviceNameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is not null) ShowDeviceModelPicker();
    }

    private async void DeviceBook_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: KindleBookCardViewModel card })
            await ExportDeviceBookAsync(card);
    }

    private async void ExportDeviceBookButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: KindleBookCardViewModel card })
            await ExportDeviceBookAsync(card);
    }

    private async void SendSelectedBooksToKindleBatchButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleAsync();

    private async void SendSelectedBooksByEmailBatchButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBooksByEmailAsync();

    private sealed class PreparedKindleTransfer : IDisposable
    {
        private readonly string? _temporaryDirectory;

        public PreparedKindleTransfer(BookFile file, string sourcePath, string? temporaryDirectory = null, string? coverOverridePath = null)
        {
            File = file;
            SourcePath = sourcePath;
            CoverOverridePath = coverOverridePath;
            _temporaryDirectory = temporaryDirectory;
        }

        public BookFile File { get; }
        public string SourcePath { get; }

        // Library cover to prefer over the file's embedded one when building
        // the Kindle home-screen thumbnail (e.g. a Douban-matched cover).
        public string? CoverOverridePath { get; }

        public void Dispose()
        {
            if (_temporaryDirectory is null) return;
            try
            {
                if (System.IO.File.Exists(SourcePath)) System.IO.File.Delete(SourcePath);
                if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // CoverPath is stored relative to the data root; older entries may be
    // absolute. Returns null when the book has no readable local cover.
    private string? ResolveBookCoverAbsolutePath(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return null;
        try
        {
            var path = Path.IsPathRooted(book.CoverPath)
                ? book.CoverPath
                : Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<PreparedKindleTransfer> PrepareKindleTransferAsync(
        Book book,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceFile = KindleTransferPolicy.SelectPreferred(book.Files)
            ?? throw new NotSupportedException(T("没有可发送到 Kindle 的 AZW3、MOBI、EPUB 或 PDF 文件。"));
        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(T("找不到本地书籍文件，请先刷新书库。"), sourcePath);
        var coverOverridePath = ResolveBookCoverAbsolutePath(book);

        var requiresMetadataRepair = KindleTransferPolicy.RequiresLegacyMetadataRepair(sourceFile, sourcePath);
        if (!KindleTransferPolicy.RequiresConversionToAzw3(sourceFile) && !requiresMetadataRepair)
            return new PreparedKindleTransfer(sourceFile, sourcePath, coverOverridePath: coverOverridePath);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "kindle-ready", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var destinationPath = Path.Combine(
            temporaryDirectory,
            KindleTransferPolicy.CreateSafeFileName(book.Title, ".azw3"));
        try
        {
            var conversionProgress = progress is null
                ? null
                : new Progress<FormatConversionProgress>(value => progress.Report(new TransferProgress(
                    value.RoundedPercentage,
                    100,
                    T("正在生成 Kindle 兼容版本 · {0}%", value.RoundedPercentage))));
            try
            {
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    destinationPath,
                    conversionProgress,
                    cancellationToken,
                    new FormatConversionMetadata(book.Title, book.Authors, coverOverridePath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Some hand-built EPUBs trip a Calibre bug in mobi_output's
                // remove_html_cover (KeyError on a manifest path). Round-tripping
                // through a normalized EPUB repairs the manifest and the second
                // pass then converts cleanly.
                var normalized = Path.Combine(
                    temporaryDirectory,
                    KindleTransferPolicy.CreateSafeFileName(book.Title, ".epub"));
                var metadata = new FormatConversionMetadata(book.Title, book.Authors, coverOverridePath);
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    normalized,
                    conversionProgress,
                    cancellationToken,
                    metadata);
                await _formatConverter.ConvertAsync(
                    normalized,
                    destinationPath,
                    conversionProgress,
                    cancellationToken,
                    metadata);
            }

            var output = new FileInfo(destinationPath);
            if (!output.Exists || output.Length < 1024)
                throw new InvalidDataException(T("Kindle 兼容版本生成失败：输出文件为空。"));

            // A missing cover is not fatal: the Kindle falls back to its
            // default thumbnail, so send anyway instead of blocking the book.

            var preparedFile = new BookFile
            {
                Id = sourceFile.Id,
                BookId = sourceFile.BookId,
                Format = "azw3",
                RelativePath = Path.GetFileName(destinationPath),
                Size = output.Length,
                Sha256 = await Hashing.Sha256Async(destinationPath, cancellationToken)
            };
            return new PreparedKindleTransfer(preparedFile, destinationPath, temporaryDirectory, coverOverridePath);
        }
        catch
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task SendSelectedBooksToKindleAsync() =>
        await TrackDeviceOperationAsync(SendSelectedBooksToKindleCoreAsync);

    private async Task SendSelectedBooksToKindleCoreAsync()
    {
        var cards = GetSelectedCards();
        if (cards.Count == 0)
        {
            SetTaskStatus(T("请先选择至少一本书。"));
            return;
        }
        if (_kindle is null)
        {
            SetTaskStatus(T("当前启动头未提供 Kindle 平台服务。"));
            return;
        }
        if (_isTransferring)
        {
            ShowTransferToast(T("发送到 Kindle 设备"), T("已有发送任务正在进行中。"), autoHide: true);
            return;
        }

        await RefreshDevicesAsync(scanBooks: false, _lifetimeCancellation.Token);
        if (CurrentDevice is not { } device)
        {
            SetTaskStatus(T("请先连接并解锁 Kindle。"));
            ShowTransferToast(T("发送到 Kindle 设备"), T("未检测到 Kindle，请连接并解锁设备。"), autoHide: true);
            return;
        }

        var sent = 0;
        var skipped = 0;
        var titleLines = string.Join(Environment.NewLine, cards.Take(3).Select(card => T("《{0}》", card.Title)));
        if (cards.Count > 3) titleLines += T("{0}…等 {1} 本", Environment.NewLine, cards.Count);
        if (!await ConfirmAsync(
                T("发送到 Kindle：{0} 本书", cards.Count),
                T("将以下书籍发送到 {0}：{1}{1}{2}{1}{1}EPUB/MOBI 会先转换为 Kindle 兼容的 AZW3。", device.Name, Environment.NewLine, titleLines)))
            return;

        _isTransferring = true;
        _transferCancellation?.Dispose();
        _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var cancellation = _transferCancellation;
        TaskProgressPopupBar.Value = 0;
        ShowTaskProgressPopup();
        ShowTransferToast(T("发送到 Kindle 设备"), T("正在发送 {0} 本书…", cards.Count), progress: 0);
        var acceptProgressUpdates = true;
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                TaskProgressPopupBar.Value = value.Percentage;
                TaskProgressPopupText.Text = UiText.Localize(value.Message);
                ShowTransferToast(T("发送到 Kindle 设备"), UiText.Localize(value.Message), progress: value.Percentage);
            });
            for (var index = 0; index < cards.Count; index++)
            {
                var card = cards[index];
                ShowTransferToast(
                    T("发送到 Kindle 设备"),
                    T("正在发送《{0}》（{1}/{2}）…", card.Title, index + 1, cards.Count),
                    progress: index * 100 / cards.Count);
                try
                {
                    using var prepared = await PrepareKindleTransferAsync(card.Book, progress, cancellation.Token);
                    await _kindle.SendBookAsync(
                        device,
                        prepared.File,
                        prepared.SourcePath,
                        progress,
                        cancellationToken: cancellation.Token,
                        coverOverridePath: prepared.CoverOverridePath);
                    sent++;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    skipped++;
                    ShowTransferToast(T("发送到 Kindle 设备"), T("《{0}》发送失败：{1}", card.Title, UiText.Localize(exception.Message)), autoHide: true);
                }
            }

            acceptProgressUpdates = false;
            var completionMessage = skipped > 0
                ? T("发送完成：成功 {0} 本，失败 {1} 本。", sent, skipped)
                : T("已发送 {0} 本书到 {1}。", sent, device.Name);
            ShowTransferToast(T("发送到 Kindle 设备"), completionMessage, progress: 100, autoHide: true);
            SetTaskStatus(completionMessage);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetTaskStatus(T("发送已中断。"));
            ShowTransferToast(T("发送到 Kindle 设备"), T("发送已中断，未完成的临时文件已清理。"), autoHide: true);
        }
        catch (Exception exception)
        {
            LogSendDiagnostic("SendSelectedBooksToKindleCoreAsync", exception);
            SetTaskStatus(T("发送失败：{0}", UiText.Localize(exception.Message)));
            ShowTransferToast(T("发送到 Kindle 设备"), T("发送失败：{0}", UiText.Localize(exception.Message)), autoHide: true);
        }
        finally
        {
            _isTransferring = false;
            if (ReferenceEquals(_transferCancellation, cancellation)) _transferCancellation = null;
            cancellation.Dispose();
            HideTaskProgressPopup();
            if (DevicePage.IsVisible)
                await RefreshDeviceBooksAsync(_lifetimeCancellation.Token);
            else
                _deviceBooksDirty = true;
        }
    }

    private async Task SendSelectedBooksByEmailAsync()
    {
        var cards = GetSelectedCards();
        if (cards.Count == 0)
        {
            SetTaskStatus(T("请先选择至少一本书。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后再发送 Kindle 邮件。"));
            return;
        }
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        if (!_kindleEmailSettings.IsConfigured)
        {
            await ShowMessageAsync(T("无法发送"), T("请先在设置与备份中填写并保存 Kindle 邮箱设置。"));
            KindleEmailSettingsButton_Click(null, new RoutedEventArgs());
            return;
        }

        var candidates = cards
            .Select(card => (Card: card, File: KindleEmailSelectionPolicy.SelectPreferred(card.Book.Files)))
            .Where(entry => entry.File is not null)
            .Select(entry => (entry.Card, File: entry.File!, Path: ViewModel.GetAbsoluteFilePath(entry.File!)))
            .Where(entry => File.Exists(entry.Path))
            .Select(entry => (entry.Card, entry.File, entry.Path, SizeBytes: new FileInfo(entry.Path).Length))
            .ToArray();
        if (candidates.Length == 0)
        {
            SetTaskStatus(T("发送到 Kindle 邮箱只支持 EPUB 或 PDF；所选书籍没有可发送格式。"));
            return;
        }

        var oversized = candidates
            .Where(entry => !KindleEmailSelectionPolicy.IsWithinAttachmentLimit(entry.SizeBytes))
            .ToArray();
        if (oversized.Length > 0)
        {
            var oversizedLines = string.Join(
                Environment.NewLine,
                oversized.Take(4).Select(entry =>
                    T("《{0}》（{1}）", entry.Card.Title, FormatKindleEmailAttachmentSize(entry.SizeBytes))));
            if (oversized.Length > 4)
                oversizedLines += T("{0}…等 {1} 本", Environment.NewLine, oversized.Length);

            SetTaskStatus(T("有 {0} 本书超过 50 MB，无法发送到 Kindle 邮箱。", oversized.Length));
            await ShowMessageAsync(
                T("无法发送到 Kindle 邮箱"),
                T("以下书籍超过 Send to Kindle 邮箱单本 50 MB 的限制，将不会发送：{0}{0}{1}", Environment.NewLine, oversizedLines));
        }

        var pending = candidates
            .Where(entry => KindleEmailSelectionPolicy.IsWithinAttachmentLimit(entry.SizeBytes))
            .ToArray();
        if (pending.Length == 0) return;

        var titleLines = string.Join(Environment.NewLine, pending.Take(3).Select(entry => T("《{0}》", entry.Card.Title)));
        if (pending.Length > 3) titleLines += T("{0}…等 {1} 本", Environment.NewLine, pending.Length);
        if (!await ConfirmAsync(
                T("发送到 Kindle 邮箱"),
                T("确定将以下 {0} 本书发送到 {1}？{2}{2}{3}", pending.Length, _kindleEmailSettings.KindleEmailAddress, Environment.NewLine, titleLines)))
            return;

        var sent = 0;
        var skipped = cards.Count - pending.Length;
        foreach (var (card, _, sourcePath, _) in pending)
        {
            try
            {
                SetTaskStatus(T("正在通过邮件发送《{0}》…", card.Title));
                await _kindleEmailSender.SendAsync(
                    _kindleEmailSettings,
                    sourcePath,
                    $"Kkindle：{card.Title}",
                    _lifetimeCancellation.Token);
                sent++;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                skipped++;
                SetTaskStatus(T("《{0}》邮件发送失败：{1}", card.Title, UiText.Localize(exception.Message)));
            }
        }

        var emailCompletionMessage = skipped > 0
            ? T("已通过邮件发送 {0} 本书，跳过或失败 {1} 本。", sent, skipped)
            : T("已通过邮件发送 {0} 本书。", sent);
        SetTaskStatus(emailCompletionMessage);
        if (sent > 0)
            await ShowMessageAsync(T("发送成功"), T("邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。"));
    }

    private async void SendSelectedBookToKindleButton_Click(object? sender, RoutedEventArgs e) =>
        await TrackDeviceOperationAsync(SendSelectedBookToKindleCoreAsync);

    private void LogSendDiagnostic(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            File.AppendAllText(
                Path.Combine(_paths.Logs, "send-diagnostic.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }
    }

    private async Task SendSelectedBookToKindleCoreAsync()
    {
        // Snapshot the card: awaits below (device refresh, confirm dialog) can
        // re-enter UI handlers that clear _selectedCard, which previously
        // crashed the send with a NullReferenceException.
        if (_selectedCard is not { } card)
        {
            SetTaskStatus(T("请先选择一本书。"));
            return;
        }
        if (_kindle is null)
        {
            SetTaskStatus(T("当前启动头未提供 Kindle 平台服务。"));
            return;
        }
        if (_isTransferring)
        {
            ShowTransferToast(T("发送到 Kindle 设备"), T("已有发送任务正在进行中。"), autoHide: true);
            return;
        }

        await RefreshDevicesAsync(scanBooks: false, _lifetimeCancellation.Token);
        if (CurrentDevice is not { } device)
        {
            SetTaskStatus(T("请先连接并解锁 Kindle。"));
            ShowTransferToast(T("发送到 Kindle 设备"), T("未检测到 Kindle，请连接并解锁设备。"), autoHide: true);
            return;
        }

        try
        {
            if (!await ConfirmAsync(T("发送到 Kindle"), T("将《{0}》发送到 {1}？EPUB/MOBI 会先转换为 AZW3。", card.Title, device.Name)))
                return;
            _isTransferring = true;
            _transferCancellation?.Dispose();
            _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            var cancellation = _transferCancellation;
            TaskProgressPopupBar.Value = 0;
            ShowTaskProgressPopup();
            ShowTransferToast(T("发送到 Kindle 设备"), T("正在发送《{0}》…", card.Title), progress: 0);
            try
            {
                var progress = new Progress<TransferProgress>(value =>
                {
                    TaskProgressPopupBar.Value = value.Percentage;
                    TaskProgressPopupText.Text = UiText.Localize(value.Message);
                    ShowTransferToast(T("发送到 Kindle 设备"), UiText.Localize(value.Message), progress: value.Percentage);
                });
                using var prepared = await PrepareKindleTransferAsync(card.Book, progress, cancellation.Token);
                await _kindle.SendBookAsync(
                    device,
                    prepared.File,
                    prepared.SourcePath,
                    progress,
                    cancellationToken: cancellation.Token,
                    coverOverridePath: prepared.CoverOverridePath);
                ShowTransferToast(T("发送到 Kindle 设备"), T("已发送《{0}》到 {1}。", card.Title, device.Name), progress: 100, autoHide: true);
                SetTaskStatus(T("已发送《{0}》到 {1}。", card.Title, device.Name));
            }
            finally
            {
                if (ReferenceEquals(_transferCancellation, cancellation)) _transferCancellation = null;
                cancellation.Dispose();
                _isTransferring = false;
                HideTaskProgressPopup();
            }
            if (DevicePage.IsVisible)
                await RefreshDeviceBooksAsync(_lifetimeCancellation.Token);
            else
                _deviceBooksDirty = true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogSendDiagnostic("SendSelectedBookToKindleCoreAsync", exception);
            SetTaskStatus(T("发送到 Kindle 失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
    }

    private async void SendSelectedBookByEmailButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCard is null)
        {
            SetTaskStatus(T("请先选择一本书。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后再发送 Kindle 邮件。"));
            return;
        }
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        if (!_kindleEmailSettings.IsConfigured)
        {
            await ShowMessageAsync(T("无法发送"), T("请先在设置与备份中填写并保存 Kindle 邮箱设置。"));
            KindleEmailSettingsButton_Click(null, e);
            return;
        }

        var file = KindleEmailSelectionPolicy.SelectPreferred(_selectedCard.Book.Files);
        if (file is null)
        {
            await ShowMessageAsync(T("无法发送"), T("发送到 Kindle 邮箱目前只支持 EPUB 或 PDF 文件。"));
            return;
        }
        var sourcePath = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus(T("找不到文件：{0}", file.RelativePath));
            return;
        }

        try
        {
            if (!await EnsureKindleEmailAttachmentWithinLimitAsync(_selectedCard.Title, sourcePath))
                return;
            if (!await ConfirmAsync(T("发送到 Kindle 邮箱"), T("确定将《{0}》发送到 {1}？", _selectedCard.Title, _kindleEmailSettings.KindleEmailAddress)))
                return;
            SetTaskStatus(T("正在通过邮件发送《{0}》…", _selectedCard.Title));
            await _kindleEmailSender.SendAsync(
                _kindleEmailSettings,
                sourcePath,
                $"Kkindle：{_selectedCard.Title}",
                _lifetimeCancellation.Token);
            SetTaskStatus(T("已通过邮件发送《{0}》。", _selectedCard.Title));
            await ShowMessageAsync(T("发送成功"), T("邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogSendDiagnostic("SendSelectedBookByEmailButton_Click", exception);
            SetTaskStatus(T("邮件发送失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
    }

    private static string FormatKindleEmailAttachmentSize(long fileSizeBytes) =>
        $"{fileSizeBytes / (1024d * 1024d):0.#} MB";

    private async Task<bool> EnsureKindleEmailAttachmentWithinLimitAsync(string title, string filePath)
    {
        var fileSizeBytes = new FileInfo(filePath).Length;
        if (KindleEmailSelectionPolicy.IsWithinAttachmentLimit(fileSizeBytes)) return true;

        var message = T("《{0}》文件大小为 {1}，超过 Send to Kindle 邮箱单本 50 MB 的限制。", title, FormatKindleEmailAttachmentSize(fileSizeBytes));
        SetTaskStatus(message);
        await ShowMessageAsync(T("无法发送到 Kindle 邮箱"), message);
        return false;
    }

    private async Task ExportDeviceBookAsync(KindleBookCardViewModel card) =>
        await ExportDeviceBooksToLibraryAsync([card]);

    private async void ExportSelectedDeviceBooksButton_Click(object? sender, RoutedEventArgs e)
        => await ExportSelectedDeviceBooksAsync();

    private async Task ExportSelectedDeviceBooksAsync()
    {
        var selected = GetSelectedDeviceBooks();
        if (selected.Count == 0) return;
        await ExportDeviceBooksToLibraryAsync(selected);
    }

    private async Task ExportDeviceBooksToLibraryAsync(IReadOnlyList<KindleBookCardViewModel> cards)
    {
        // A single export owns the device read and subsequent library import
        // as one unit. Without this gate, two card gestures can overlap after
        // the first await and race while importing their output.
        if (!_deviceBookExportGate.Wait(0))
        {
            SetTaskStatus(T("已有导出任务正在进行中。"));
            return;
        }

        try
        {
            await TrackDeviceOperationAsync(() => ExportDeviceBooksToLibraryCoreAsync(cards));
        }
        finally
        {
            _deviceBookExportGate.Release();
        }
    }

    private async Task ExportDeviceBooksToLibraryCoreAsync(IReadOnlyList<KindleBookCardViewModel> cards)
    {
        if (_kindle is null || CurrentDevice is not { } device || cards.Count == 0) return;
        var pending = cards.Where(card =>
        {
            var format = BookFormatConversionPolicy.Normalize(card.Book.Format);
            return format is "epub" or "pdf" or "mobi" or "azw3" or "kfx";
        }).ToArray();
        if (pending.Length == 0)
        {
            DevicePageStatusText.Text = T("所选书籍的格式电脑书库暂不支持。");
            await ShowMessageAsync(T("无法导出"), T("所选书籍的格式电脑书库暂不支持。"));
            return;
        }

        if (pending.Any(card => BookFormatConversionPolicy.Normalize(card.Book.Format) == "kfx"))
        {
            using var calibreSetup = new CalibreSetupService();
            if (string.IsNullOrWhiteSpace(calibreSetup.LocateCalibre(_appSettings.CalibrePath)))
            {
                var message = T("未安装 Calibre，无法完成此导出。请先安装 Calibre，或在设置中指定 ebook-convert.exe 后重试。");
                DevicePageStatusText.Text = message;
                await ShowMessageAsync(T("未安装 Calibre"), message);
                return;
            }
        }

        var titleLines = string.Join(Environment.NewLine, pending.Take(3).Select(card => T("《{0}》", card.Title)));
        if (pending.Length > 3) titleLines += T("{0}…等 {1} 本", Environment.NewLine, pending.Length);
        if (!await ConfirmAsync(
                T("导出到电脑书库：{0} 本书", pending.Length),
                T("将从 {0} 导出以下书籍并导入电脑书库：{1}{1}{2}", device.Name, Environment.NewLine, titleLines)))
            return;

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "device-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var importPaths = new List<string>();
        var failed = 0;
        var calibreMissing = false;
        var failureDetails = new List<string>();
        TaskProgressPopupBar.Value = 0;
        ShowTaskProgressPopup();
        ShowTransferToast(T("导出到电脑书库"), T("正在从 Kindle 导出 {0} 本书…", pending.Length), progress: 0);
        try
        {
            for (var index = 0; index < pending.Length; index++)
            {
                var card = pending[index];
                var bookDirectory = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bookDirectory);
                try
                {
                    var progress = new Progress<TransferProgress>(value =>
                    {
                        var message = string.IsNullOrWhiteSpace(value.Message)
                            ? T("正在导出《{0}》：{1:0}%", card.Title, value.Percentage)
                            : UiText.Localize(value.Message);
                        DevicePageStatusText.Text = message;
                        ShowTransferToast(
                            T("导出到电脑书库"),
                            T("正在导出《{0}》（{1}/{2}）…", card.Title, index + 1, pending.Length),
                            progress: (index * 100 + Math.Min(100, value.Percentage)) / pending.Length);
                        TaskProgressPopupBar.Value = (index * 100 + Math.Min(100, value.Percentage)) / pending.Length;
                    });
                    DevicePageStatusText.Text = T("正在从 Kindle 读取《{0}》（{1}/{2}）…", card.Title, index + 1, pending.Length);
                    var localSource = await _kindle.ExportBookAsync(
                        device,
                        card.Book,
                        bookDirectory,
                        progress,
                        _lifetimeCancellation.Token);
                    var importPath = localSource;
                    if (BookFormatConversionPolicy.Normalize(card.Book.Format) == "kfx")
                    {
                        importPath = Path.Combine(bookDirectory, Path.GetFileNameWithoutExtension(localSource) + ".epub");
                        await _formatConverter.ConvertAsync(
                            localSource,
                            importPath,
                            new Progress<FormatConversionProgress>(value =>
                                DevicePageStatusText.Text = T("正在将《{0}》转换为 EPUB：{1}%", card.Title, value.RoundedPercentage)),
                            _lifetimeCancellation.Token,
                            new FormatConversionMetadata(card.Book.Title, card.Book.Authors));
                    }
                    importPaths.Add(importPath);
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    var detail = UiText.Localize(exception.Message);
                    var isCalibreMissing = IsCalibreMissingMessage(detail);
                    calibreMissing |= isCalibreMissing;
                    failureDetails.Add(T("《{0}》导出失败：{1}", card.Title, detail));
                    DevicePageStatusText.Text = isCalibreMissing
                        ? T("《{0}》导出失败：未检测到 Calibre。", card.Title)
                        : T("《{0}》导出失败：{1}", card.Title, detail);
                }
            }

            var imported = 0;
            if (importPaths.Count > 0)
            {
                DevicePageStatusText.Text = T("正在导入电脑书库…");
                var result = await ViewModel.ImportAsync(importPaths, cancellationToken: _lifetimeCancellation.Token);
                var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(result, _lifetimeCancellation.Token);
                imported = result.SuccessCount;
                failed += result.FailureCount + automaticFormats.Failures.Count;
                foreach (var item in result.Items.Where(item => !item.Succeeded))
                {
                    if (!string.IsNullOrWhiteSpace(item.Message))
                        failureDetails.Add(UiText.Localize(item.Message));
                }
                failureDetails.AddRange(automaticFormats.Failures);
                calibreMissing |= automaticFormats.Failures.Any(IsCalibreMissingMessage);
                await RefreshCollectionsAsync();
                UpdateLibraryUi();
            }
            var completionMessage = failed == 0
                ? T("已从 {0} 导出并导入电脑书库 {1} 本书。", device.Name, imported)
                : T("导出完成：成功 {0} 本，失败 {1} 本。", imported, failed);
            DevicePageStatusText.Text = completionMessage;
            ShowTransferToast(T("导出到电脑书库"), completionMessage, progress: 100);
            if (failed > 0)
            {
                if (calibreMissing)
                {
                    await ShowMessageAsync(
                        T("未安装 Calibre"),
                        T("未安装 Calibre，无法完成此导出。请先安装 Calibre，或在设置中指定 ebook-convert.exe 后重试。"));
                }
                else
                {
                    var message = T("成功 {0} 本，失败 {1} 本。", imported, failed);
                    var details = failureDetails
                        .Where(detail => !string.IsNullOrWhiteSpace(detail))
                        .Distinct(StringComparer.CurrentCulture)
                        .Take(3)
                        .ToArray();
                    if (details.Length > 0)
                        message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details);
                    await ShowMessageAsync(T("导出到电脑书库失败"), message);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            DevicePageStatusText.Text = T("导出已取消。");
        }
        finally
        {
            HideTaskProgressPopup();
            HideTransferToast();
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsCalibreMissingMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("未找到 Calibre", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Calibre converter was not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Calibre was not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Calibre not found", StringComparison.OrdinalIgnoreCase));

    private async void DeleteSelectedDeviceBooksButton_Click(object? sender, RoutedEventArgs e)
        => await DeleteSelectedDeviceBooksAsync();

    private async Task DeleteSelectedDeviceBooksAsync() =>
        await TrackDeviceOperationAsync(DeleteSelectedDeviceBooksCoreAsync);

    private async Task DeleteSelectedDeviceBooksCoreAsync()
    {
        var selected = GetSelectedDeviceBooks();
        if (selected.Count == 0 || _kindle is null || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync(T("从 Kindle 删除书籍"), T("确定从 {0} 删除选中的 {1} 本书吗？电脑书库不受影响。", device.Name, selected.Count))) return;

        var removed = 0;
        ShowTransferToast(T("从 Kindle 删除书籍"), T("正在删除 {0} 本书…", selected.Count), progress: 0);
        string? firstFailure = null;
        for (var index = 0; index < selected.Count; index++)
        {
            var card = selected[index];
            try
            {
                await _kindle.RemoveBookAsync(device, card.Book, _lifetimeCancellation.Token);
                DeviceBooks.Remove(card);
                card.Dispose();
                removed++;
                DeviceBookCountText.Text = DeviceBooks.Count.ToString();
                ShowTransferToast(T("从 Kindle 删除书籍"), T("正在删除（{0}/{1}）…", index + 1, selected.Count), progress: (index + 1) * 100 / selected.Count);
            }
            catch (Exception exception)
            {
                DevicePageStatusText.Text = T("《{0}》删除失败：{1}", card.Title, UiText.Localize(exception.Message));
                firstFailure ??= UiText.Localize(exception.Message);
            }
        }
        RefreshLibraryPresenceState();
        UpdateDeviceBookSelectionUi();
        var completionMessage = T("已从 Kindle 删除 {0} 本书。", removed);
        DevicePageStatusText.Text = completionMessage;
        ShowTransferToast(T("从 Kindle 删除书籍"), completionMessage, progress: 100, autoHide: true);
        if (firstFailure is not null)
            await ShowMessageAsync(T("无法从 Kindle 删除"), UiText.Localize(firstFailure));
    }

    private async void DeleteDeviceBookButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KindleBookCardViewModel card }
            || _kindle is null
            || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync(T("从 Kindle 删除书籍"), T("确定从 {0} 删除《{1}》吗？电脑书库中的文件不会受影响。", device.Name, card.Title))) return;
        await TrackDeviceOperationAsync(async () =>
        {
            try
            {
                await _kindle.RemoveBookAsync(device, card.Book, _lifetimeCancellation.Token);
                DeviceBooks.Remove(card);
                card.Dispose();
                DeviceBookCountText.Text = DeviceBooks.Count.ToString();
                RefreshLibraryPresenceState();
                UpdateDeviceBookSelectionUi();
                DevicePageStatusText.Text = T("已从 Kindle 删除《{0}》。", card.Title);
                ShowTransferToast(T("从 Kindle 删除书籍"), T("已从 Kindle 删除《{0}》。", card.Title), progress: 100, autoHide: true);
            }
            catch (Exception exception)
            {
                DevicePageStatusText.Text = T("删除失败：{0}", UiText.Localize(exception.Message));
            }
        });
    }

    private async void ReaderNotesNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        _readingMaterialsExportMode = false;
        ShowStage3Page(ReadingMaterialsPage);
        ReadingMaterialsPageTitle.Text = T("笔记管理");
        ReadingMaterialsStatusText.Text = T("统一浏览本地书籍与 Kindle 的划线、笔记和批注。");
        ReadingMaterialsNotesActions.IsVisible = true;
        SelectAllReadingMaterialsButton.IsVisible = false;
        ExportReadingMaterialsToggleButton.Content = T("导出记录");
        ReadingMaterialsSummaryBorder.IsVisible = true;
        ReadingMaterialsExportPanel.IsVisible = false;
        ReadingMaterialsExportSummaryBorder.IsVisible = false;
        await RefreshReadingMaterialsAsync();
    }

    private void ExportReadingMaterialsToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        _readingMaterialsExportMode = !_readingMaterialsExportMode;
        ExportReadingMaterialsToggleButton.Content = _readingMaterialsExportMode
            ? T("取消")
            : T("导出记录");
        SelectAllReadingMaterialsButton.IsVisible = _readingMaterialsExportMode;
        ReadingMaterialsSummaryBorder.IsVisible = !_readingMaterialsExportMode;
        ReadingMaterialsExportPanel.IsVisible = _readingMaterialsExportMode;
        ReadingMaterialsExportSummaryBorder.IsVisible = _readingMaterialsExportMode;
        ReadingMaterialsStatusText.Text = _readingMaterialsExportMode
            ? T("先勾选要导出的记录，再选择文件格式保存到电脑。")
            : T("统一浏览本地书籍与 Kindle 的划线、笔记和批注。");
        ApplyReadingMaterialsFilter();
    }

    private async Task RefreshReadingMaterialsAsync()
    {
        RememberReadingMaterialGroupStates();
        foreach (var item in _allStage3ReadingMaterials) item.Dispose();
        _allStage3ReadingMaterials.Clear();
        ReadingMaterials.Clear();
        foreach (var group in ReadingMaterialGroups) group.Dispose();
        ReadingMaterialGroups.Clear();
        _readingMaterialCoverPaths.Clear();
        try
        {
            if (_deviceWarmTask is not null)
                await _deviceWarmTask;
            // Kindle covers below come from DeviceBooks. A same-identity
            // reconnect never re-arms _deviceWarmTask and leaves that list
            // empty while clippings can still be re-read, which would drop
            // every Kindle cover from this page.
            if (_kindle is not null && CurrentDevice is not null && !_deviceBooksLoaded)
                await RefreshDeviceBooksAsync(_lifetimeCancellation.Token);
            var books = await _library.SearchAsync(cancellationToken: _lifetimeCancellation.Token);
            var titles = books.ToDictionary(book => book.Id, book => book.Title);
            foreach (var book in books)
            {
                if (string.IsNullOrWhiteSpace(book.CoverPath)) continue;
                var path = Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
                if (File.Exists(path))
                    _readingMaterialCoverPaths[BuildReadingMaterialCoverKey(ReadingMaterialSource.Local, book.Title)] = path;
            }
            foreach (var card in DeviceBooks)
            {
                if (!string.IsNullOrWhiteSpace(card.Book.CoverPath) && File.Exists(card.Book.CoverPath))
                    _readingMaterialCoverPaths[BuildReadingMaterialCoverKey(ReadingMaterialSource.Kindle, card.Title)] = card.Book.CoverPath;
            }
            var annotations = await _readerData.GetAllAnnotationsAsync(_lifetimeCancellation.Token);
            var chapterTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bookId in annotations.Select(annotation => annotation.BookId).Distinct())
            {
                foreach (var chunk in await _readerData.GetBookOverviewChunksAsync(
                             bookId,
                             int.MaxValue,
                             _lifetimeCancellation.Token))
                {
                    if (string.IsNullOrWhiteSpace(chunk.ChapterTitle)) continue;
                    chapterTitles.TryAdd(
                        BuildReadingMaterialChapterKey(chunk.BookFileId, chunk.ChapterPath),
                        chunk.ChapterTitle.Trim());
                }
            }
            foreach (var annotation in annotations)
            {
                var chapter = ResolveReadingMaterialChapterLabel(annotation, chapterTitles);
                _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                    ReadingMaterialSource.Local,
                    titles.GetValueOrDefault(annotation.BookId, T("已删除的本地书籍")),
                    string.IsNullOrWhiteSpace(annotation.Note) ? "划线" : "划线与笔记",
                    chapter,
                    $"{annotation.ChapterPath} · {annotation.StartOffset}-{annotation.EndOffset}",
                    annotation.SelectedText,
                    annotation.Note,
                    annotation.UpdatedAt,
                    annotation,
                    null));
            }

            if (CurrentDevice is { } device && _kindle is not null)
            {
                IReadOnlyList<KindleClipping>? clippings = null;
                await TrackDeviceOperationAsync(async () =>
                {
                    if (_deviceClippingCache.TryGetValue(device.Identity, out var cached))
                        clippings = cached;
                    else
                    {
                        clippings = await _kindle.ReadClippingsAsync(device, _lifetimeCancellation.Token);
                        _deviceClippingCache[device.Identity] = clippings;
                        await PersistDeviceAuxiliaryCacheAsync(device);
                    }
                });
                foreach (var pair in KindleClippingsParser.PairForDisplay(clippings ?? []))
                {
                    var clipping = pair.Clipping;
                    _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                        ReadingMaterialSource.Kindle,
                        string.IsNullOrWhiteSpace(clipping.BookTitle) || clipping.BookTitle == "未知书籍"
                            ? T("未知书籍")
                            : clipping.BookTitle,
                        pair.PairedNote is null ? string.Empty : "划线与笔记",
                        clipping.Metadata,
                        clipping.Metadata,
                        clipping.Type == KindleClippingType.Note ? string.Empty : clipping.Content,
                        clipping.Type == KindleClippingType.Note
                            ? clipping.Content
                            : pair.PairedNote?.Content ?? string.Empty,
                        MaxAddedAt(clipping.AddedAt, pair.PairedNote?.AddedAt),
                        null,
                        clipping,
                        pair.PairedNote));
                }
            }

            ApplyReadingMaterialsFilter();
            _readingMaterialsDirty = false;
            var kindleCount = _allStage3ReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Kindle);
            ReadingMaterialsStatusText.Text = _readingMaterialsExportMode
                ? T("导出预览已准备 · Kindle {0} 条", kindleCount)
                : T("本地资料已读取 · Kindle {0} 条", kindleCount);
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = T("读取阅读资料失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void ApplyReadingMaterialsFilter()
    {
        if (!_stage3Ready
            || ReadingMaterialsSearchBox is null
            || ReadingMaterialsSourceBox is null
            || ReadingMaterialsEmptyText is null
            || ReadingMaterialsStatusText is null)
            return;
        var source = (ReadingMaterialsSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var query = ReadingMaterialsSearchBox.Text?.Trim() ?? string.Empty;
        RememberReadingMaterialGroupStates();

        var filtered = _allStage3ReadingMaterials
            .Where(item => source == "all"
                || source == "local" && item.Source == ReadingMaterialSource.Local
                || source == "kindle" && item.Source == ReadingMaterialSource.Kindle)
            .Where(item => query.Length == 0 || item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        ReadingMaterials.Clear();
        foreach (var item in filtered) ReadingMaterials.Add(item);
        foreach (var group in ReadingMaterialGroups) group.Dispose();
        ReadingMaterialGroups.Clear();
        var grouped = source == "all"
            ? filtered.GroupBy(item => item.BookTitleKey, StringComparer.CurrentCultureIgnoreCase)
                .Select(items => (Source: items.First().Source, Title: items.First().BookTitle, Items: items.ToArray(), IsMixed: items.Select(item => item.Source).Distinct().Count() > 1))
            : filtered.GroupBy(item => (item.Source, item.BookTitleKey), new ReadingMaterialGroupKeyComparer())
                .Select(items => (Source: items.Key.Source, Title: items.First().BookTitle, Items: items.ToArray(), IsMixed: false));
        foreach (var group in grouped
            .Select(items => new Stage3ReadingMaterialGroupViewModel(
                items.Source,
                items.Title,
                items.Items,
                GetReadingMaterialCoverPathForGroup(items.Items),
                isExpanded: _readingMaterialGroupStates.TryGetValue((items.Source, items.Title), out var wasExpanded)
                    ? wasExpanded
                    : !_appSettings.ReadingMaterialsCollapsedByDefault,
                isMixedSource: items.IsMixed))
            .OrderByDescending(group => group.Items.Max(item => item.UpdatedAt ?? DateTimeOffset.MinValue)))
        {
            ReadingMaterialGroups.Add(group);
        }
        ReadingMaterialsEmptyText.IsVisible = filtered.Length == 0;
        ReadingMaterialsEmptyText.Text = _readingMaterialsExportMode
            ? T("当前筛选范围没有可导出的阅读资料。")
            : T("没有符合条件的划线、笔记与批注");
        var localCount = filtered.Count(item => item.Source == ReadingMaterialSource.Local);
        var kindleCount = filtered.Count(item => item.Source == ReadingMaterialSource.Kindle);
        ReadingMaterialsSummaryText.Text = _readingMaterialsExportMode
            ? T("导出预览 · 本地 {0} 条 · Kindle {1} 条 · 当前将导出 {2} 条", localCount, kindleCount, filtered.Length)
            : T("本地 {0} 条 · Kindle {1} 条 · 当前显示 {2} 条", localCount, kindleCount, filtered.Length);
        ReadingMaterialsExportSummaryText.Text = T("导出预览 · 本地 {0} 条 · Kindle {1} 条 · 当前将导出 {2} 条", localCount, kindleCount, filtered.Length);
        ReadingMaterialsExportScopeText.Text = T("当前筛选范围：{0} · 共 {1} 条记录", GetReadingMaterialsSourceLabel(source), filtered.Length);
        UpdateReadingMaterialsActionState();
    }

    private void ReadingMaterialsSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyReadingMaterialsFilter();
    private void ReadingMaterialsSourceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyReadingMaterialsFilter();

    private void SelectAllReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectAll = ReadingMaterials.Count > 0 && ReadingMaterials.Any(item => !item.IsSelected);
        foreach (var item in ReadingMaterials)
            item.IsSelected = selectAll;
        UpdateReadingMaterialsActionState();
    }

    private void ReadingMaterialSelectionChanged(object? sender, RoutedEventArgs e)
    {
        UpdateReadingMaterialsActionState();
    }

    private void UpdateReadingMaterialsActionState()
    {
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        DeleteReadingMaterialsButton.IsEnabled = !_readingMaterialsExportMode && selected.Length > 0;
        ReadingMaterialsLocateButton.IsEnabled = !_readingMaterialsExportMode
            && selected.Any(item => item.LocalAnnotation is not null);
        if (SelectAllReadingMaterialsButton is not null)
            SelectAllReadingMaterialsButton.Content = ReadingMaterials.Count > 0
                && ReadingMaterials.All(item => item.IsSelected)
                ? T("取消全选")
                : T("全选");
        if (_readingMaterialsExportMode && ReadingMaterialsExportSummaryText is not null)
        {
            var localCount = ReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Local);
            var kindleCount = ReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Kindle);
            ReadingMaterialsExportSummaryText.Text =
                T("导出预览 · 本地 {0} 条 · Kindle {1} 条 · 当前将导出 {2} 条", localCount, kindleCount, selected.Length);
        }
    }

    private void ReadingMaterialGroupToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Stage3ReadingMaterialGroupViewModel group })
            group.IsExpanded = !group.IsExpanded;
    }

    private void MarkReadingMaterialsDirty() => _readingMaterialsDirty = true;

    private void RememberReadingMaterialGroupStates()
    {
        foreach (var group in ReadingMaterialGroups)
            _readingMaterialGroupStates[(group.Source, group.BookTitleKey)] = group.IsExpanded;
    }

    private async Task RefreshReadingMaterialsIfDirtyAsync()
    {
        if (!_readingMaterialsDirty || !ReadingMaterialsPage.IsVisible) return;
        await RefreshReadingMaterialsAsync();
    }

    private async void ReadingMaterialsLocateButton_Click(object? sender, RoutedEventArgs e)
    {
        var item = ReadingMaterials.FirstOrDefault(candidate => candidate.IsSelected);
        if (item is null)
        {
            ReadingMaterialsStatusText.Text = T("请先勾选一条本地批注。");
            return;
        }
        await LocateReadingMaterialAsync(item);
    }

    private async void ReadingMaterialEntry_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: Stage3ReadingMaterialViewModel item }) return;
        await LocateReadingMaterialAsync(item);
    }

    private async Task LocateReadingMaterialAsync(Stage3ReadingMaterialViewModel item)
    {
        if (item.LocalAnnotation is not { } annotation)
        {
            ReadingMaterialsStatusText.Text = T("Kindle 剪贴没有可在电脑端定位的正文位置。");
            return;
        }

        var card = ViewModel.Books.FirstOrDefault(candidate => candidate.Book.Id == annotation.BookId);
        var file = card?.Book.Files.FirstOrDefault(candidate => candidate.Id == annotation.BookFileId);
        if (card is null || file is null)
        {
            ReadingMaterialsStatusText.Text = T("找不到这条批注对应的本地书籍文件。");
            return;
        }

        await OpenBookAsync(card, file);
        if (!ReaderRoot.IsVisible) return;
        ShowReaderNotesTab();
        var loaded = ReaderAnnotations.FirstOrDefault(candidate => candidate.Id == annotation.Id);
        if (loaded is not null) await NavigateToReaderAnnotationAsync(loaded);
    }

    private static string BuildReadingMaterialCoverKey(ReadingMaterialSource source, string title) =>
        $"{source}\u001F{title}";

    private static string BuildReadingMaterialChapterKey(Guid bookFileId, string? chapterPath) =>
        $"{bookFileId:N}\u001F{(chapterPath ?? string.Empty).Replace('\\', '/').TrimStart('/')}";

    private static string ResolveReadingMaterialChapterLabel(
        ReaderAnnotation annotation,
        IReadOnlyDictionary<string, string> chapterTitles)
    {
        if (chapterTitles.TryGetValue(
                BuildReadingMaterialChapterKey(annotation.BookFileId, annotation.ChapterPath),
                out var title)
            && !string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase))
        {
            var pageText = annotation.ChapterPath.Split(':').LastOrDefault();
            return int.TryParse(pageText, out var page) && page > 0 ? T("第 {0} 页", page) : "PDF";
        }

        var fileName = Path.GetFileNameWithoutExtension(
            annotation.ChapterPath.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(fileName) ? T("未指定章节") : fileName;
    }

    private string? GetReadingMaterialCoverPath(ReadingMaterialSource source, string title) =>
        _readingMaterialCoverPaths.GetValueOrDefault(BuildReadingMaterialCoverKey(source, title))
        ?? _readingMaterialCoverPaths
            .Where(pair => pair.Key.StartsWith($"{source}\u001F", StringComparison.OrdinalIgnoreCase))
            .Select(pair => (Title: pair.Key[(pair.Key.IndexOf('\u001F') + 1)..], Path: pair.Value))
            .Where(pair => ReadingMaterialCoverMatcher.AreTitlesRelated(pair.Title, title))
            .OrderByDescending(pair => Math.Min(
                ReadingMaterialCoverMatcher.NormalizeTitle(pair.Title).Length,
                ReadingMaterialCoverMatcher.NormalizeTitle(title).Length))
            .Select(pair => pair.Path)
            .FirstOrDefault();

    private string? GetReadingMaterialCoverPathForGroup(IReadOnlyList<Stage3ReadingMaterialViewModel> items)
    {
        var title = items.FirstOrDefault()?.BookTitleKey;
        if (string.IsNullOrWhiteSpace(title)) return null;
        return GetReadingMaterialCoverPath(ReadingMaterialSource.Local, title)
            ?? GetReadingMaterialCoverPath(ReadingMaterialSource.Kindle, title);
    }

    private sealed class ReadingMaterialGroupKeyComparer : IEqualityComparer<(ReadingMaterialSource Source, string BookTitle)>
    {
        public bool Equals(
            (ReadingMaterialSource Source, string BookTitle) left,
            (ReadingMaterialSource Source, string BookTitle) right) =>
            left.Source == right.Source
            && string.Equals(left.BookTitle, right.BookTitle, StringComparison.CurrentCultureIgnoreCase);

        public int GetHashCode((ReadingMaterialSource Source, string BookTitle) value) =>
            HashCode.Combine(value.Source, StringComparer.CurrentCultureIgnoreCase.GetHashCode(value.BookTitle));
    }

    private async void RefreshReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(scanBooks: false);
        await RefreshReadingMaterialsAsync();
    }

    private async void DeleteReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            ReadingMaterialsStatusText.Text = T("请先勾选要删除的记录。");
            return;
        }
        if (!await ConfirmAsync(T("删除阅读资料"), T("确定删除选中的 {0} 条记录吗？Kindle 记录只会从 My Clippings.txt 删除。", selected.Length))) return;
        try
        {
            foreach (var item in selected.Where(item => item.LocalAnnotation is not null))
            {
                if (item.LocalAnnotation is { } annotation)
                    await _readerData.DeleteAnnotationAsync(annotation.Id, _lifetimeCancellation.Token);
            }
            var kindleIds = selected
                .SelectMany(item => new[] { item.KindleClipping?.Id, item.PairedKindleClipping?.Id })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();
            if (kindleIds.Length > 0 && CurrentDevice is { } device && _kindle is not null)
            {
                await TrackDeviceOperationAsync(() => _kindle.DeleteClippingsAsync(device, kindleIds, _lifetimeCancellation.Token));
                _deviceClippingCache.Remove(device.Identity);
                await PersistDeviceAuxiliaryCacheAsync(device);
            }
            await RefreshReadingMaterialsAsync();
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = T("删除失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ExportReadingMaterialsMarkdownButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReadingMaterialsAsync(markdown: true);

    private async void ExportReadingMaterialsTextButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReadingMaterialsAsync(markdown: false);

    private async Task ExportReadingMaterialsAsync(bool markdown)
    {
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            ReadingMaterialsStatusText.Text = T("请先勾选要导出的记录，或点击“全选”。");
            return;
        }
        var records = selected.Select(item => item.ToRecord()).ToArray();
        if (records.Length == 0)
        {
            ReadingMaterialsStatusText.Text = T("当前筛选结果没有可导出的记录。");
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出阅读资料"),
            SuggestedFileName = T("Kkindle-阅读资料-{0}.{1}", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), markdown ? "md" : "txt"),
            FileTypeChoices = [new FilePickerFileType(markdown ? "Markdown" : T("文本"))
            {
                Patterns = [markdown ? "*.md" : "*.txt"]
            }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        var content = markdown
            ? ReadingMaterialsExport.BuildMarkdown(records)
            : ReadingMaterialsExport.BuildPlainText(records);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), _lifetimeCancellation.Token);
        ReadingMaterialsStatusText.Text = T("已导出 {0} 条记录到 {1}。", records.Length, path);
    }

    private static string GetReadingMaterialsSourceLabel(string source) => source switch
    {
        "local" => T("本地书库"),
        "kindle" => "Kindle",
        _ => T("全部来源")
    };

    private static DateTimeOffset? MaxAddedAt(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    private async Task OpenDeviceResourcePageAsync(KindleResourceKind kind)
    {
        _deviceResourceKind = kind;
        ShowStage3Page(DeviceResourcePage);
        DeviceResourcePageTitle.Text = kind == KindleResourceKind.Font ? T("Kindle 字体") : T("Kindle 字典");
        DeviceResourcePathText.Text = kind == KindleResourceKind.Font ? @"Kindle\fonts" : @"Kindle\documents\dictionaries";
        DeviceResourceSafetyText.Text = kind == KindleResourceKind.Font
            ? T("仅读写 Kindle 的 fonts 目录；支持 TTF、OTF。导入或删除后建议断开设备并重启 Kindle。")
            : T("仅读写 Kindle 的 documents\\dictionaries 目录；支持 AZW、AZW3、MOBI、PRC、KFX。删除前请确认不是当前正在使用的主词典。" );
        await RefreshDeviceResourcesAsync();
    }

    private async Task RefreshDeviceResourcesAsync(bool forceRefresh = false)
    {
        DeviceResources.Clear();
        DeviceResourceList.SelectedItem = null;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        if (_kindle is null || CurrentDevice is not { } device)
        {
            DeviceResourceDeviceText.Text = T("未检测到设备");
            DeviceResourceCountText.Text = T("0 个文件");
            DeviceResourceStatusText.Text = T("请先连接 Kindle。");
            DeviceResourceEmptyText.IsVisible = true;
            return;
        }
        try
        {
            if (_deviceWarmTask is not null)
                await _deviceWarmTask;
            var cacheKey = BuildDeviceCacheKey(device);
            if (!forceRefresh && _deviceResourceCache.TryGetValue((cacheKey, _deviceResourceKind), out var cached))
            {
                ApplyDeviceResources(cached, device);
                return;
            }

            var resources = await _kindle.ScanResourcesAsync(device, _deviceResourceKind, _lifetimeCancellation.Token);
            _deviceResourceCache[(cacheKey, _deviceResourceKind)] = resources;
            ApplyDeviceResources(resources, device);
        }
        catch (Exception exception)
        {
            DeviceResourceDeviceText.Text = device.Name;
            DeviceResourceCountText.Text = T("读取失败");
            DeviceResourceStatusText.Text = T("读取失败：{0}", UiText.Localize(exception.Message));
            DeviceResourceEmptyText.IsVisible = true;
        }
    }

    private async Task<bool> RefreshDeviceResourceCachesAsync(KindleDevice device)
    {
        if (_kindle is null || !ReferenceEquals(CurrentDevice, device)) return false;

        try
        {
            if (_deviceWarmTask is not null)
                await _deviceWarmTask;

            var cacheKey = BuildDeviceCacheKey(device);
            IReadOnlyList<KindleDeviceResource>? fonts = null;
            IReadOnlyList<KindleDeviceResource>? dictionaries = null;
            Exception? firstFailure = null;

            try
            {
                fonts = await ScanDeviceResourcesWithRetryAsync(device, KindleResourceKind.Font);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                firstFailure = exception;
                _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Font));
            }

            try
            {
                dictionaries = await ScanDeviceResourcesWithRetryAsync(device, KindleResourceKind.Dictionary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                firstFailure ??= exception;
                _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Dictionary));
            }

            if (!ReferenceEquals(CurrentDevice, device)) return false;
            if (fonts is not null)
                _deviceResourceCache[(cacheKey, KindleResourceKind.Font)] = fonts;
            if (dictionaries is not null)
                _deviceResourceCache[(cacheKey, KindleResourceKind.Dictionary)] = dictionaries;

            var current = _deviceResourceKind == KindleResourceKind.Font ? fonts : dictionaries;
            if (current is null)
            {
                if (_deviceResourceCache.TryGetValue((cacheKey, _deviceResourceKind), out var cachedCurrent))
                    ApplyDeviceResources(cachedCurrent, device);
                throw firstFailure ?? new IOException("无法读取 Kindle 资源目录。");
            }

            ApplyDeviceResources(current, device);
            if (fonts is not null && dictionaries is not null)
                await PersistDeviceAuxiliaryCacheBestEffortAsync(device);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = T("刷新失败：{0}", UiText.Localize(exception.Message));
            return false;
        }
    }

    private async Task<IReadOnlyList<KindleDeviceResource>> ScanDeviceResourcesWithRetryAsync(
        KindleDevice device,
        KindleResourceKind kind)
    {
        if (_kindle is null) throw new InvalidOperationException("Kindle 服务不可用。");

        Exception? lastFailure = null;
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await _kindle.ScanResourcesAsync(device, kind, _lifetimeCancellation.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                lastFailure = exception;
                if (attempt + 1 >= maxAttempts) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(350 + attempt * 350), _lifetimeCancellation.Token);
            }
        }

        throw lastFailure ?? new IOException("无法读取 Kindle 资源目录。");
    }

    private void ApplyDeviceResources(IReadOnlyList<KindleDeviceResource> resources, KindleDevice device)
    {
        DeviceResources.Clear();
        DeviceResourceList.SelectedItem = null;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        foreach (var resource in resources) DeviceResources.Add(resource);
        DeviceResourceDeviceText.Text = device.Name;
        DeviceResourceCountText.Text = T("{0} 个文件", resources.Count);
        DeviceResourceStatusText.Text = T("已读取 {0} 个文件", resources.Count);
        DeviceResourceEmptyText.IsVisible = resources.Count == 0;
    }

    private static string BuildDeviceCacheKey(KindleDevice device) =>
        $"{device.Transport}:{device.Identity}:{Path.GetFullPath(device.RootPath)}";

    private void InvalidateCurrentDeviceResourceCaches()
    {
        if (CurrentDevice is not { } device) return;
        var cacheKey = BuildDeviceCacheKey(device);
        _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Font));
        _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Dictionary));
    }

    private void RemoveDeletedDeviceResourceFromCacheAndView(
        KindleDevice device,
        KindleDeviceResource deletedResource)
    {
        var cacheKey = BuildDeviceCacheKey(device);
        var remaining = DeviceResources
            .Where(resource => !string.Equals(
                resource.RelativePath,
                deletedResource.RelativePath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _deviceResourceCache[(cacheKey, deletedResource.Kind)] = remaining;
        if (_deviceResourceKind == deletedResource.Kind)
            ApplyDeviceResources(remaining, device);
    }

    private async Task PersistDeviceAuxiliaryCacheAsync(KindleDevice device)
    {
        var key = BuildDeviceCacheKey(device);
        var previous = await _kindleAuxiliaryCacheStore.GetAsync(device.Identity, _lifetimeCancellation.Token);
        await _kindleAuxiliaryCacheStore.SaveAsync(device.Identity, new KindleDeviceAuxiliaryCacheSnapshot
        {
            Fonts = (_deviceResourceCache.GetValueOrDefault((key, KindleResourceKind.Font)) ?? previous?.Fonts ?? []).ToList(),
            Dictionaries = (_deviceResourceCache.GetValueOrDefault((key, KindleResourceKind.Dictionary)) ?? previous?.Dictionaries ?? []).ToList(),
            Clippings = (_deviceClippingCache.GetValueOrDefault(device.Identity) ?? previous?.Clippings ?? []).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        }, _lifetimeCancellation.Token);
    }

    private async Task PersistDeviceAuxiliaryCacheBestEffortAsync(KindleDevice device)
    {
        try
        {
            await PersistDeviceAuxiliaryCacheAsync(device);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The device operation has already succeeded. A locked local
            // cache must not turn a successful resource import/delete into a
            // false failure in the device page.
            DevicePageStatusText.Text = T("设备缓存暂未更新：{0}", UiText.Localize(exception.Message));
        }
    }

    private void DeviceResourceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var hasSelection = DeviceResourceList.SelectedItem is KindleDeviceResource;
        ExportDeviceResourceButton.IsEnabled = hasSelection;
        DeleteDeviceResourceButton.IsEnabled = hasSelection;
    }

    private async void FontManagementButton_Click(object? sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Font);
    private async void DictionaryManagementButton_Click(object? sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Dictionary);

    private async void RefreshDeviceResourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(scanBooks: false);
        if (CurrentDevice is { } device)
            await RefreshDeviceResourceCachesAsync(device);
        else
            await RefreshDeviceResourcesAsync(forceRefresh: true);
    }

    private async void ImportDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_deviceResourceBusy || _kindle is null || CurrentDevice is null) return;
        try
        {
            var extensions = _deviceResourceKind == KindleResourceKind.Font
                ? new[] { "*.ttf", "*.otf" }
                : new[] { "*.azw", "*.azw3", "*.mobi", "*.prc", "*.kfx" };
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入 Kindle 资源"),
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(T("Kindle 资源")) { Patterns = extensions }]
            });
            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => path is not null)
                .Select(path => path!)
                .ToArray();
            if (paths.Length == 0) return;
            await ImportDeviceResourcePathsAsync(paths);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = T("导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
    }

    private void DeviceResourcePage_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = LibraryDropImportPolicy.CanAccept(e.DataTransfer)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DeviceResourcePage_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var draggedPaths = LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer);
            var paths = draggedPaths
                .Where(path => KindleResourcePolicy.IsSupportedFile(_deviceResourceKind, path))
                .ToArray();
            if (paths.Length > 0)
            {
                // Explorer can keep the drag-source handle open until the
                // Drop event returns. Yield once so the WPD reader starts
                // after that drag transaction has been released.
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await ImportDeviceResourcePathsAsync(paths);
            }
            else if (draggedPaths.Length > 0)
            {
                var formats = _deviceResourceKind == KindleResourceKind.Font ? "TTF / OTF" : "AZW / AZW3 / MOBI / PRC / KFX";
                await ShowMessageAsync(T("无法导入"), T("拖入的文件中没有可用的 {0} 文件。", formats));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = T("导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
    }

    private void DictionaryManagementSection_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = LibraryDropImportPolicy.CanAccept(e.DataTransfer)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DictionaryManagementSection_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var draggedPaths = LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer);
            var paths = draggedPaths
                .Where(IsSupportedLocalDictionaryPath)
                .ToArray();
            if (paths.Length > 0)
            {
                // The source application may still hold a handle during the
                // Drop event; let it finish before parsing the dictionary.
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await ImportLocalDictionaryPathsAsync(paths);
            }
            else if (draggedPaths.Length > 0)
            {
                DictionaryResultText.Text = T("拖入的文件中没有可用的词典文件。支持 MDX、AZW、AZW3、MOBI、PRC、KFX、TXT、TSV 和 CSV。");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DictionaryResultText.Text = T("字典导入失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private static bool IsSupportedLocalDictionaryPath(string path) =>
        LocalDictionaryExtensions.Contains(Path.GetExtension(path));

    private async Task ImportLocalDictionaryPathsAsync(IEnumerable<string> sourcePaths)
    {
        var paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(IsSupportedLocalDictionaryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            DictionaryResultText.Text = T("没有找到可导入的词典文件。");
            return;
        }

        foreach (var path in paths)
            await _dictionaryService.ImportAsync(path, cancellationToken: _lifetimeCancellation.Token);

        await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
        DictionaryResultText.Text = paths.Length == 1
            ? T("字典已导入。")
            : T("已导入 {0} 个词典。", paths.Length);
    }

    private async Task ImportDeviceResourcePathsAsync(IEnumerable<string> sourcePaths) =>
        await TrackDeviceOperationAsync(() => ImportDeviceResourcePathsCoreAsync(sourcePaths));

    private async Task ImportDeviceResourcePathsCoreAsync(IEnumerable<string> sourcePaths)
    {
        if (_deviceResourceBusy || _kindle is null || CurrentDevice is null) return;
        var paths = sourcePaths
            .Where(path => KindleResourcePolicy.IsSupportedFile(_deviceResourceKind, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0) return;

        _deviceResourceBusy = true;
        var resourceChanged = false;
        try
        {
            ShowTransferToast(T("导入 Kindle 资源"), T("正在导入 {0} 个文件…", paths.Length), progress: 0);
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                DeviceResourceStatusText.Text = T("正在导入 {0}…", Path.GetFileName(path));
                await _kindle.SendResourceAsync(
                    CurrentDevice!,
                    _deviceResourceKind,
                    path,
                    cancellationToken: _lifetimeCancellation.Token);
                resourceChanged = true;
                ShowTransferToast(T("导入 Kindle 资源"), T("正在导入 {0}…", Path.GetFileName(path)), progress: (index + 1) * 100 / paths.Length);
            }
            InvalidateCurrentDeviceResourceCaches();
            if (CurrentDevice is { } currentDevice && !await RefreshDeviceResourceCachesAsync(currentDevice))
            {
                DeviceResourceStatusText.Text = T("已导入 {0} 个文件，但设备资源缓存刷新失败，请点击刷新。", paths.Length);
                return;
            }
            ShowTransferToast(T("导入 Kindle 资源"), T("已导入 {0} 个文件。", paths.Length), progress: 100, autoHide: true);
        }
        catch (Exception exception)
        {
            if (resourceChanged)
            {
                InvalidateCurrentDeviceResourceCaches();
                if (CurrentDevice is { } currentDevice)
                    await RefreshDeviceResourceCachesAsync(currentDevice);
            }
            DeviceResourceStatusText.Text = T("导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
        finally
        {
            _deviceResourceBusy = false;
        }
    }

    private async void ExportDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        var resource = sender is Button { Tag: KindleDeviceResource taggedResource }
            ? taggedResource
            : DeviceResourceList.SelectedItem as KindleDeviceResource;
        if (resource is null || _kindle is null || CurrentDevice is not { } device) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var extension = Path.GetExtension(resource.FileName);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出 Kindle 资源"),
            SuggestedFileName = resource.FileName,
            FileTypeChoices = [new FilePickerFileType(resource.Kind == KindleResourceKind.Font ? T("字体") : T("字典")) { Patterns = [$"*{extension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await TrackDeviceOperationAsync(async () =>
        {
            try
            {
                await _kindle.ExportResourceAsync(device, resource, path, _lifetimeCancellation.Token);
                DeviceResourceStatusText.Text = T("已导出 {0}", resource.FileName);
                ShowTransferToast(T("导出 Kindle 资源"), T("已导出 {0}", resource.FileName), progress: 100, autoHide: true);
            }
            catch (Exception exception) { DeviceResourceStatusText.Text = T("导出失败：{0}", UiText.Localize(exception.Message)); }
        });
    }

    private async void DeleteDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        var resource = sender is Button { Tag: KindleDeviceResource taggedResource }
            ? taggedResource
            : DeviceResourceList.SelectedItem as KindleDeviceResource;
        if (resource is null || _kindle is null || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync(T("删除 Kindle 资源"), T("确定删除设备文件 {0} 吗？", resource.RelativePath))) return;
        await TrackDeviceOperationAsync(async () =>
        {
            try
            {
                await _kindle.RemoveResourceAsync(device, resource, _lifetimeCancellation.Token);
                InvalidateCurrentDeviceResourceCaches();
                RemoveDeletedDeviceResourceFromCacheAndView(device, resource);
                await PersistDeviceAuxiliaryCacheBestEffortAsync(device);
                await RefreshDeviceResourceCachesAsync(device);
                DeviceResourceStatusText.Text = T("已删除 {0}", resource.FileName);
                ShowTransferToast(T("删除 Kindle 资源"), T("已删除 {0}", resource.FileName), progress: 100, autoHide: true);
            }
            catch (Exception exception) { DeviceResourceStatusText.Text = T("删除失败：{0}", UiText.Localize(exception.Message)); }
        });
    }

    private async void ReadingDashboardButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(ReadingDashboardPage);
        await RefreshReadingDashboardAsync();
    }

    private async Task RefreshReadingDashboardAsync()
    {
        try
        {
            var dashboard = await _readerData.GetReadingDashboardAsync(100, _lifetimeCancellation.Token);
            DashboardBooksStartedText.Text = T("{0} 本", dashboard.BooksStarted);
            DashboardBooksFinishedText.Text = T("{0} 本", dashboard.BooksFinished);
            DashboardTotalTimeText.Text = FormatReadingTime(dashboard.TotalSeconds);
            DashboardAverageProgressText.Text = T("平均进度 {0:0.#}%", dashboard.AverageProgress);
            DashboardBookmarksText.Text = $"{dashboard.BookmarkCount.ToString(CultureInfo.InvariantCulture)} / {dashboard.AnnotationCount.ToString(CultureInfo.InvariantCulture)}";

            var weekSeconds = dashboard.DailyReading.Skip(7).Sum(day => day.ActiveSeconds);
            DashboardDailyAverageText.Text = weekSeconds == 0
                ? T("近 7 天日均 —")
                : T("近 7 天日均 {0}", FormatReadingTime((long)Math.Round(weekSeconds / 7d)));
            DashboardStreakText.Text = T("{0} 天", ComputeReadingStreakDays(dashboard.DailyReading));
            DashboardStatusText.IsVisible = false;

            _readingDashboardItems.Clear();
            foreach (var item in dashboard.RecentBooks)
            {
                var title = ViewModel.LibraryBooks.FirstOrDefault(book => book.Id == item.BookId)?.Title
                    ?? T("未导入的书籍");
                var recent = new Stage3DashboardRecentViewModel(
                    title,
                    item.ProgressPercent,
                    item.CumulativeSeconds,
                    item.UpdatedAt);
                _readingDashboardItems.Add(recent);
                DashboardRecentItems.Add(recent);
            }
            DashboardRecentEmptyText.IsVisible = _readingDashboardItems.Count == 0;

            DashboardDays.Clear();
            var maximumSeconds = Math.Max(1, dashboard.DailyReading.Max(day => day.ActiveSeconds));
            var today = DateOnly.FromDateTime(DateTime.Today);
            foreach (var day in dashboard.DailyReading)
            {
                DashboardDays.Add(new Stage3DashboardDayViewModel(
                    day.Date.ToString("MM-dd", CultureInfo.InvariantCulture),
                    day.ActiveSeconds == 0 ? "" : T("{0:0.#} 分", day.ActiveSeconds / 60d),
                    day.ActiveSeconds == 0 ? 4 : 10 + 108d * day.ActiveSeconds / maximumSeconds,
                    day.Date == today ? 1d : 0.3));
            }

            PopulateDashboardBars(DashboardBookTimes, dashboard.RecentBooks
                .OrderByDescending(item => item.CumulativeSeconds)
                .Take(8)
                .Select(item => (
                    ViewModel.LibraryBooks.FirstOrDefault(book => book.Id == item.BookId)?.Title ?? T("未导入的书籍"),
                    (double)item.CumulativeSeconds,
                    FormatReadingTime(item.CumulativeSeconds))));

            var progressValues = dashboard.RecentBooks.Select(item => item.ProgressPercent).ToArray();
            PopulateDashboardBars(DashboardProgressBuckets,
            [
                ("0–24%", (double)progressValues.Count(value => value < 25), T("{0} 本", progressValues.Count(value => value < 25))),
                ("25–49%", (double)progressValues.Count(value => value >= 25 && value < 50), T("{0} 本", progressValues.Count(value => value >= 25 && value < 50))),
                ("50–74%", (double)progressValues.Count(value => value >= 50 && value < 75), T("{0} 本", progressValues.Count(value => value >= 50 && value < 75))),
                ("75–99%", (double)progressValues.Count(value => value >= 75 && value < 99.5), T("{0} 本", progressValues.Count(value => value >= 75 && value < 99.5))),
                ("完成", (double)progressValues.Count(value => value >= 99.5), T("{0} 本", progressValues.Count(value => value >= 99.5)))
            ]);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DashboardStatusText.Text = T("阅读数据暂时不可用：{0}", UiText.Localize(exception.Message));
            DashboardStatusText.IsVisible = true;
        }
    }

    // Consecutive active days inside the 14-day window, ending today or
    // yesterday (today alone may not have started yet without breaking it).
    private static int ComputeReadingStreakDays(IReadOnlyList<ReadingDashboardDay> days)
    {
        var streak = 0;
        for (var i = days.Count - 1; i >= 0; i--)
        {
            if (days[i].ActiveSeconds > 0) streak++;
            else if (streak == 0 && i == days.Count - 1) continue;
            else break;
        }
        return streak;
    }

    private static void PopulateDashboardBars(
        ObservableCollection<Stage3DashboardBarViewModel> target,
        IEnumerable<(string Label, double Value, string ValueLabel)> values)
    {
        target.Clear();
        var materialized = values.ToArray();
        var maximum = Math.Max(1, materialized.Select(item => item.Value).DefaultIfEmpty().Max());
        foreach (var item in materialized)
            target.Add(new Stage3DashboardBarViewModel(
                item.Label,
                item.ValueLabel,
                item.Value <= 0 ? 2 : Math.Max(8, 210d * item.Value / maximum)));
    }

    private async void RefreshReadingDashboardButton_Click(object? sender, RoutedEventArgs e) =>
        await RefreshReadingDashboardAsync();

    private async void ExportReadingDashboardButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshReadingDashboardAsync();
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出阅读数据"),
            SuggestedFileName = T("Kkindle-阅读数据-{0}.csv", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var csv = new StringBuilder(T("书名,进度,阅读秒数,最近阅读") + "\r\n");
        foreach (var item in _readingDashboardItems)
        {
            csv.Append('"')
                .Append(item.Title.Replace("\"", "\"\"", StringComparison.Ordinal))
                .Append("\",")
                .Append(item.ProgressPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(item.Seconds.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .AppendLine(item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        }
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(true), _lifetimeCancellation.Token);
        DashboardStatusText.Text = T("已导出 {0} 条阅读数据到 {1}", _readingDashboardItems.Count, path);
        DashboardStatusText.IsVisible = true;
    }

    private static string FormatReadingTime(long seconds)
    {
        if (seconds < 60) return T("{0} 秒", seconds);
        if (seconds < 3600) return T("{0:0.#} 分", seconds / 60d);
        return T("{0:0.#} 小时", seconds / 3600d);
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        SettingsDataPathText.Text = _paths.Data;
        ShowSettingsSection("Library");
    }

    private void SystemBackupNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, SystemBackupNavigationButton);
        ShowSettingsSection("Library");
        ShowSystemSettingsSection("Backup");
        ShowSettingsPanel(SystemSettingsPane);
    }

    private void SystemS3SyncNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, SystemS3SyncNavigationButton);
        ShowSettingsSection("Library");
        ShowSystemSettingsSection("Sync");
        ShowSettingsPanel(SystemSettingsPane);
    }

    private async void KindleEmailSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
        KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
        KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
        KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
        KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
        KindleEmailPasswordBox.Text = _kindleEmailSettings.SmtpPassword;
        KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
        ShowStage3Page(SettingsPage, KindleEmailSettingsNavigationButton);
        ShowSettingsSection("Kindle");
        KindleEmailSettingsStatusText.Text = string.Empty;
        ShowSettingsPanel(KindleEmailSettingsPane);
        KindleEmailRecipientBox.Focus();
    }

    private async void ReaderAiSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, ReaderAiSettingsNavigationButton);
        ShowSettingsSection("Kindle");
        await LoadMainReaderAiSettingsAsync();
        ShowSettingsPanel(ReaderAiSettingsPane);
        MainReaderAiBaseUrlBox.Focus();
    }

    private void SettingsCategoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: not null } button)
            ShowSettingsSection(button.Tag.ToString()!);
    }

    private void ShowSettingsSection(string tag)
    {
        var sections = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            ["Library"] = SettingsLibrarySection,
            ["Calibre"] = SettingsCalibreSection,
            ["Kindle"] = SettingsKindleSection,
            ["Reading"] = SettingsReadingSection,
            ["About"] = SettingsAboutSection
        };

        foreach (var section in sections.Values)
            section.IsVisible = false;
        if (!sections.TryGetValue(tag, out var activeSection))
        {
            tag = "Library";
            activeSection = SettingsLibrarySection;
        }
        activeSection.IsVisible = true;

        var buttons = new[]
        {
            SettingsLibraryButton,
            SettingsCalibreButton,
            SettingsKindleButton,
            SettingsReadingButton,
            SettingsAboutButton
        };
        foreach (var button in buttons)
            button.Classes.Set("active", string.Equals(button.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));

        SettingsScrollViewer.Offset = new Vector(0, 0);
    }

    private void ShowSystemSettingsSection(string tag)
    {
        var sections = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backup"] = SettingsBackupSection,
            ["Sync"] = SettingsSyncSection
        };

        foreach (var section in sections.Values)
            section.IsVisible = false;
        if (!sections.TryGetValue(tag, out var activeSection))
        {
            tag = "Sync";
            activeSection = SettingsSyncSection;
        }
        activeSection.IsVisible = true;

        SystemSettingsPaneTitle.Text = string.Equals(tag, "Sync", StringComparison.OrdinalIgnoreCase)
            ? "S3 同步"
            : T("备份");

        SystemSettingsScrollViewer.Offset = new Vector(0, 0);
    }

    private async Task LoadMainReaderAiSettingsAsync()
    {
        try
        {
            _readerAiSettings = await _aiSettingsStore.LoadAsync(_lifetimeCancellation.Token);
            _suppressMainAiProviderChange = true;
            try
            {
                var provider = _readerAiSettings.Provider.Trim().ToLowerInvariant();
                MainReaderAiProviderBox.SelectedItem = MainReaderAiProviderBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                    ?? MainReaderAiProviderBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
                MainReaderAiBaseUrlBox.Text = _readerAiSettings.BaseUrl;
                MainReaderAiModelBox.Text = _readerAiSettings.Model;
                MainReaderAiApiKeyBox.Text = _readerAiSettings.ApiKey;
                MainReaderAiSettingsStatusText.Text = string.Empty;
            }
            finally
            {
                _suppressMainAiProviderChange = false;
            }
        }
        catch (Exception exception)
        {
            MainReaderAiSettingsStatusText.Text = T("读取 AI 设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void MainReaderAiProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMainAiProviderChange
            || MainReaderAiProviderBox is null
            || MainReaderAiBaseUrlBox is null
            || MainReaderAiModelBox is null
            || MainReaderAiSettingsStatusText is null
            || MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item)
            return;
        var defaults = AiConnectionSettings.GetDefaults(item.Tag.ToString()!);
        MainReaderAiBaseUrlBox.Text = defaults.BaseUrl;
        MainReaderAiModelBox.Text = defaults.Model;
        MainReaderAiSettingsStatusText.Text = item.Tag.ToString() == "custom"
            ? T("自定义服务使用 OpenAI-compatible Chat Completions。")
            : string.Empty;
    }

    private async void MainReaderAiSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item) return;
        var provider = item.Tag.ToString()!;
        var baseUrl = MainReaderAiBaseUrlBox.Text?.Trim() ?? string.Empty;
        var model = MainReaderAiModelBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            MainReaderAiSettingsStatusText.Text = T("请输入有效的 HTTP 或 HTTPS Base URL。");
            return;
        }
        if (model.Length == 0)
        {
            MainReaderAiSettingsStatusText.Text = T("请输入模型名称。");
            return;
        }

        var settings = new AiConnectionSettings
        {
            Provider = provider,
            BaseUrl = baseUrl,
            Model = AiConnectionSettings.NormalizeModel(provider, model),
            ApiKey = (MainReaderAiApiKeyBox.Text ?? string.Empty).Trim()
        };
        try
        {
            MainReaderAiSettingsStatusText.Text = T("正在安全保存…");
            await _aiSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            _readerAiSettings = settings;
            ApplyReaderAiSettingsToControls();
            MainReaderAiSettingsStatusText.Text = T("AI 设置已保存。");
            HideSettingsPanel();
        }
        catch (Exception exception)
        {
            MainReaderAiSettingsStatusText.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void PopulateSettingsControls()
    {
        _suppressAppSettingsAutoSave = true;
        try
        {
            PreferredOpenFormatBox.SelectedIndex = _appSettings.PreferredOpenFormat switch
            {
                "pdf" => 1,
                "azw3" => 2,
                "mobi" => 3,
                _ => 0
            };
            UiLanguageBox.SelectedIndex = _appSettings.UiLanguage.Equals("en-US", StringComparison.Ordinal)
                ? 1
                : 0;
            CalibrePathBox.Text = _appSettings.CalibrePath;
            UpdateCalibreDetectionStatus();
            AiEnabledCheck.IsChecked = _appSettings.AiEnabled;
            AutoBackupCheck.IsChecked = _appSettings.AutoBackupEnabled;
            AutoBackupRetentionBox.Value = _appSettings.AutoBackupRetention;
            AutoGenerateReaderFormatsCheck.IsChecked = _appSettings.AutoGenerateEpubAndAzw3OnImport;
            CollectionsMutuallyExclusiveCheck.IsChecked = _appSettings.CollectionsMutuallyExclusive;
            NetworkEnabledCheck.IsChecked = _appSettings.NetworkEnabled;
            AutoUpdateCheck.IsChecked = _appSettings.AutoUpdateCheckEnabled;
            AutoDoubanMatchCheck.IsChecked = _appSettings.AutoDoubanMatchOnImport;
            AutoConnectDeviceCheck.IsChecked = _appSettings.AutoConnectDevice;
            CompareKindleLibraryCheck.IsChecked = _appSettings.CompareKindleLibraryEnabled;
            GridGalleryDisplayCheck.IsChecked = _appSettings.GridGalleryDisplay;
            ReadingMaterialsCollapsedByDefaultCheck.IsChecked = _appSettings.ReadingMaterialsCollapsedByDefault;
            DefaultVerticalWritingCheck.IsChecked = _appSettings.DefaultReaderLayout.VerticalWriting;
            AboutVersionText.Text = T("版本 {0}", ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly));
            CheckForUpdatesButton.IsEnabled = _updateService is not null;
            AboutUpdateStatusText.Text = _updateService is null
                ? T("当前平台暂不支持应用内更新")
                : T("尚未检查更新");
            SettingsDataPathText.Text = _paths.Data;
            ZLibraryEmailBox.Text = _zLibrarySettings.Email;
            ZLibraryPasswordBox.Text = _zLibrarySettings.Password;
            ZLibraryBaseUrlBox.Text = _zLibrarySettings.BaseUrl;
            KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
            KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
            KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
            KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
            KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
            KindleEmailPasswordBox.Text = _kindleEmailSettings.SmtpPassword;
            KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
            PopulateS3SyncControls();
            UpdateZLibraryAccountStatus();
        }
        finally
        {
            _suppressAppSettingsAutoSave = false;
        }
    }

    private void PopulateS3SyncControls(S3SyncSettings? settingsOverride = null)
    {
        var settings = settingsOverride ?? _s3SyncStoredSettings.Settings;
        var wasSuppressing = _suppressS3SyncToggleAutoSave;
        _suppressS3SyncToggleAutoSave = true;
        try
        {
            S3SyncEnabledCheck.IsChecked = settings.Enabled;
            S3AutomaticSyncCheck.IsChecked = settings.AutomaticSyncEnabled;
            S3SyncIntervalBox.Value = settings.IntervalMinutes;
            S3EndpointBox.Text = settings.Endpoint;
            S3AccessKeyBox.Text = settings.AccessKey;
            S3SecretKeyBox.Text = settings.SecretKey;
            S3BucketBox.Text = settings.Bucket;
            S3RegionBox.Text = settings.Region;
            S3PrefixBox.Text = settings.Prefix;
            S3PathStyleCheck.IsChecked = settings.PathStyle;
            S3SkipTlsVerifyCheck.IsChecked = settings.SkipTlsVerify;
            S3EncryptionKeyBox.Text = settings.EncryptionKey;
            S3TimeoutBox.Value = settings.TimeoutSeconds;
            S3ConcurrencyBox.Value = settings.ConcurrentRequests;
            S3SyncDeviceText.Text = T("当前设备 ID：{0}", _s3SyncStoredSettings.DeviceId[..Math.Min(12, _s3SyncStoredSettings.DeviceId.Length)]);
        }
        finally
        {
            _suppressS3SyncToggleAutoSave = wasSuppressing;
        }
    }

    private S3SyncSettings ReadS3SyncSettingsFromControls() => S3SyncSettings.Normalize(new S3SyncSettings
    {
        Enabled = S3SyncEnabledCheck.IsChecked == true,
        AutomaticSyncEnabled = S3AutomaticSyncCheck.IsChecked == true,
        IntervalMinutes = S3SyncIntervalBox.Value is { } interval ? (int)interval : 30,
        Endpoint = S3EndpointBox.Text?.Trim() ?? string.Empty,
        AccessKey = S3AccessKeyBox.Text?.Trim() ?? string.Empty,
        SecretKey = S3SecretKeyBox.Text ?? string.Empty,
        Bucket = S3BucketBox.Text?.Trim() ?? string.Empty,
        Region = S3RegionBox.Text?.Trim() ?? string.Empty,
        Prefix = S3PrefixBox.Text?.Trim() ?? string.Empty,
        PathStyle = S3PathStyleCheck.IsChecked == true,
        SkipTlsVerify = S3SkipTlsVerifyCheck.IsChecked == true,
        EncryptionKey = S3EncryptionKeyBox.Text ?? string.Empty,
        TimeoutSeconds = S3TimeoutBox.Value is { } timeout ? (int)timeout : 60,
        ConcurrentRequests = S3ConcurrencyBox.Value is { } concurrency ? (int)concurrency : 4
    });

    private async Task<bool> SaveS3SyncSettingsFromControlsAsync(bool showStatus = true)
    {
        var settings = ReadS3SyncSettingsFromControls();
        // Allow the feature to be disabled and saved before credentials are
        // entered. Connection tests and actual sync still validate fully.
        var validation = settings.Enabled ? settings.Validate() : null;
        if (validation is not null)
        {
            if (showStatus) S3SyncStatusText.Text = UiText.Localize(validation);
            return false;
        }

        await _s3SyncService.SaveSettingsAsync(
            _s3SyncStoredSettings.DeviceId,
            settings,
            _lifetimeCancellation.Token);
        _s3SyncStoredSettings = new S3SyncStoredSettings(_s3SyncStoredSettings.DeviceId, settings);
        RefreshS3SyncIndicatorFromSettings();
        PopulateS3SyncControls();
        if (IsAutomaticS3SyncReady())
            ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce);
        else
            _s3LocalChangeSyncTimer.Stop();
        if (showStatus) S3SyncStatusText.Text = T("S3 同步设置已保存。");
        return true;
    }

    private async void S3SyncEnabledCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressS3SyncToggleAutoSave || !_stage3Ready || _s3SyncBusy || _s3SyncToggleAutoSaveInProgress)
            return;

        _s3SyncToggleAutoSaveInProgress = true;
        var enabled = S3SyncEnabledCheck.IsChecked == true;
        try
        {
            var saved = await SaveS3SyncSettingsFromControlsAsync();
            if (!saved && enabled && !_s3SyncStoredSettings.Settings.Enabled)
            {
                // Keep the switch in sync with the persisted state when the
                // user turns it on before completing the connection fields.
                var wasSuppressing = _suppressS3SyncToggleAutoSave;
                _suppressS3SyncToggleAutoSave = true;
                try
                {
                    S3SyncEnabledCheck.IsChecked = false;
                }
                finally
                {
                    _suppressS3SyncToggleAutoSave = wasSuppressing;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            S3SyncStatusText.Text = T("保存 S3 设置失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _s3SyncToggleAutoSaveInProgress = false;
        }
    }

    private async void S3SaveSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveS3SyncSettingsFromControlsAsync();
        }
        catch (Exception exception)
        {
            S3SyncStatusText.Text = T("保存 S3 设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ExportS3ConnectionProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                T("导出 S3 连接参数"),
                T("导出的文件会包含 Access Key 和 Secret Key，请像密码一样妥善保管，不要上传到公共位置。确定继续吗？")))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出 S3 连接参数"),
            SuggestedFileName = T(
                "Kkindle-S3连接参数-{0}{1}",
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                S3ConnectionProfileService.FileExtension),
            FileTypeChoices =
            [
                new FilePickerFileType(T("S3 连接参数"))
                {
                    Patterns = [$"*{S3ConnectionProfileService.FileExtension}"]
                }
            ]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            await S3ConnectionProfileService.ExportAsync(
                path,
                ReadS3SyncSettingsFromControls(),
                _lifetimeCancellation.Token);
            S3ConnectionProfileStatusText.Text = T("已导出 S3 连接参数：{0}", path);
        }
        catch (Exception exception)
        {
            S3ConnectionProfileStatusText.Text = T("导出 S3 连接参数失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ImportS3ConnectionProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入 S3 连接参数"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("S3 连接参数"))
                {
                    Patterns = [$"*{S3ConnectionProfileService.FileExtension}"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var imported = await S3ConnectionProfileService.ImportAsync(
                path,
                ReadS3SyncSettingsFromControls(),
                _lifetimeCancellation.Token);
            PopulateS3SyncControls(imported);
            S3ConnectionProfileStatusText.Text = T("已导入 S3 连接参数；请检查后点击“保存设置”写入。 ");
        }
        catch (Exception exception)
        {
            S3ConnectionProfileStatusText.Text = T("导入 S3 连接参数失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void S3TestConnectionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_appSettings.NetworkEnabled)
        {
            S3SyncStatusText.Text = T("请先开启“允许网络功能”。");
            return;
        }

        var settings = ReadS3SyncSettingsFromControls();
        var validation = settings.Validate();
        if (validation is not null)
        {
            S3SyncStatusText.Text = UiText.Localize(validation);
            return;
        }

        S3TestConnectionButton.IsEnabled = false;
        S3SyncStatusText.Text = T("正在测试 S3 连接…");
        try
        {
            await _s3SyncService.TestConnectionAsync(settings, _lifetimeCancellation.Token);
            S3SyncStatusText.Text = T("S3 连接成功。");
            RefreshS3SyncIndicatorFromSettings();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("S3 连接失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
        finally
        {
            S3TestConnectionButton.IsEnabled = !_s3SyncBusy;
        }
    }

    private async void S3SyncNowButton_Click(object? sender, RoutedEventArgs e) => await RunS3SyncAsync(silent: false);

    private void LocalLibraryDataChanged(object? sender, LocalDataChangedEventArgs e) =>
        HandleLocalDataChanged(e.Kind);

    private void LocalReaderDataChanged(object? sender, LocalDataChangedEventArgs e) =>
        HandleLocalDataChanged(e.Kind);

    private void HandleLocalDataChanged(LocalDataChangeKind kind)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleLocalDataChanged(kind));
            return;
        }

        if (_lifetimeCancellation.IsCancellationRequested) return;
        _s3LocalChangeVersion++;
        if (!IsAutomaticS3SyncReady())
        {
            _s3LocalChangeSyncTimer.Stop();
            return;
        }

        _s3LocalChangeRetryAttempt = 0;
        if (!_s3SyncBusy)
            UpdateS3SyncIndicator(S3SyncIndicatorState.Pending);
        ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce, resetRetry: false);
    }

    private bool HasPendingS3LocalChanges =>
        _s3LocalChangeVersion != _s3SyncedLocalChangeVersion;

    private bool IsAutomaticS3SyncReady()
    {
        if (!_stage3Ready || _s3SyncExitInProgress || !_appSettings.NetworkEnabled)
            return false;

        var settings = _s3SyncStoredSettings.Settings;
        return settings.Enabled
            && settings.AutomaticSyncEnabled
            && settings.IsConfigured;
    }

    private void ScheduleS3LocalChangeSync(TimeSpan delay, bool resetRetry = true)
    {
        if (!HasPendingS3LocalChanges || !IsAutomaticS3SyncReady())
        {
            _s3LocalChangeSyncTimer.Stop();
            return;
        }

        if (resetRetry)
            _s3LocalChangeRetryAttempt = 0;
        _s3LocalChangeSyncTimer.Stop();
        _s3LocalChangeSyncTimer.Interval = delay;
        _s3LocalChangeSyncTimer.Start();
    }

    private TimeSpan GetNextS3LocalChangeRetryDelay()
    {
        var delay = _s3LocalChangeRetryAttempt switch
        {
            0 => TimeSpan.FromSeconds(15),
            1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(1),
            3 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };
        _s3LocalChangeRetryAttempt = Math.Min(_s3LocalChangeRetryAttempt + 1, 4);
        return delay;
    }

    private async Task RunPendingS3LocalChangeSyncAsync()
    {
        _s3LocalChangeSyncTimer.Stop();
        if (!HasPendingS3LocalChanges || !IsAutomaticS3SyncReady()) return;

        var succeeded = await RunS3SyncAsync(silent: true);
        if (succeeded)
        {
            _s3LocalChangeRetryAttempt = 0;
            if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
                ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce, resetRetry: false);
            return;
        }

        if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
        {
            // A new local write may have scheduled its own short debounce
            // while the previous sync was in flight. Keep that earlier retry
            // instead of replacing it with the backoff for the old failure.
            if (_s3LocalChangeSyncTimer.IsEnabled)
                return;
            if (!_s3SyncBusy && _s3SyncIndicatorState != S3SyncIndicatorState.Failed)
                UpdateS3SyncIndicator(S3SyncIndicatorState.Pending);
            ScheduleS3LocalChangeSync(GetNextS3LocalChangeRetryDelay(), resetRetry: false);
        }
    }

    private async Task RunLifecycleS3SyncAsync(bool waitForExistingSync = false)
    {
        if (!_stage3Ready)
            return;

        try
        {
            if (!_appSettings.NetworkEnabled)
            {
                if (waitForExistingSync) await WaitForS3SyncAsync();
                return;
            }

            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(_lifetimeCancellation.Token);
            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.Enabled || !settings.AutomaticSyncEnabled || !settings.IsConfigured)
            {
                if (waitForExistingSync) await WaitForS3SyncAsync();
                return;
            }

            // A local write can land while a startup/exit checkpoint is
            // running. Give that write another checkpoint before continuing;
            // the cap prevents a busy shutdown from waiting forever if some
            // background activity keeps producing updates.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var succeeded = await RunS3SyncAsync(silent: true);
                if (!succeeded || !HasPendingS3LocalChanges)
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("自动 S3 同步失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
    }

    private async Task WaitForS3SyncAsync()
    {
        if (_s3SyncCompletion is { } completion)
            await completion.Task;
    }

    private async Task CompleteWindowCloseAfterS3SyncAsync()
    {
        try
        {
            _s3LocalChangeSyncTimer.Stop();
            await RunLifecycleS3SyncAsync(waitForExistingSync: true);
        }
        finally
        {
            _allowWindowCloseForS3Sync = true;
            Close();
        }
    }

    private async Task RunAutomaticS3SyncIfDueAsync()
    {
        if (!_stage3Ready || _s3SyncBusy || _s3SyncExitInProgress || !_appSettings.NetworkEnabled) return;
        try
        {
            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(_lifetimeCancellation.Token);
            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.Enabled || !settings.AutomaticSyncEnabled) return;
            if (!HasPendingS3LocalChanges
                && _lastS3SyncAt is { } last
                && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(settings.IntervalMinutes))
                return;
            await RunS3SyncAsync(silent: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("自动 S3 同步失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
    }

    private async Task<bool> RunS3SyncAsync(bool silent)
    {
        if (_s3SyncBusy)
        {
            if (_s3SyncCompletion is { } completion)
                return await completion.Task;
            return false;
        }
        if (!_appSettings.NetworkEnabled)
        {
            if (!silent)
            {
                var status = T("请先开启“允许网络功能”。");
                S3SyncStatusText.Text = status;
                SetTaskStatus(status);
                UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, status);
            }
            return false;
        }
        if (_readerDocument is not null || _readerIsPdf)
        {
            if (!silent) S3SyncStatusText.Text = T("请先返回书库，再执行同步。");
            return false;
        }

        var localChangeVersionAtStart = _s3LocalChangeVersion;
        var succeeded = false;
        // Claim the sync slot before the first await. Saving/loading settings
        // can yield to the dispatcher; without claiming it here, a manual
        // click and an automatic tick could both start an S3 operation.
        _s3SyncBusy = true;
        _s3SyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!silent)
            {
                if (!await SaveS3SyncSettingsFromControlsAsync(showStatus: false)) return false;
            }
            else
            {
                _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(_lifetimeCancellation.Token);
            }

            var settings = _s3SyncStoredSettings.Settings;
            var validation = settings.Validate();
            if (validation is not null)
            {
                var validationStatus = UiText.Localize(validation);
                if (!silent)
                {
                    S3SyncStatusText.Text = validationStatus;
                    UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, validationStatus);
                }
                else
                {
                    UpdateS3SyncIndicator(S3SyncIndicatorState.NotConfigured, validationStatus);
                }
                return false;
            }

            UpdateS3SyncIndicator(S3SyncIndicatorState.Syncing);
            S3SyncEnabledCheck.IsEnabled = false;
            S3SaveSettingsButton.IsEnabled = false;
            S3TestConnectionButton.IsEnabled = false;
            S3SyncNowButton.IsEnabled = false;
            var startStatus = T("正在同步到 S3…");
            S3SyncStatusText.Text = startStatus;
            if (!silent) SetTaskStatus(startStatus);
            var progress = new Progress<string>(message =>
            {
                var status = UiText.Localize(message);
                S3SyncStatusText.Text = status;
                if (!silent) SetTaskStatus(status);
            });
            var result = await _s3SyncService.SyncAsync(
                _s3SyncStoredSettings.DeviceId,
                settings,
                progress,
                _lifetimeCancellation.Token);
            _lastS3SyncAt = DateTimeOffset.UtcNow;

            if (result.SettingsApplied > 0)
            {
                _appSettings = await _appSettingsStore.LoadAsync(_lifetimeCancellation.Token);
                _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync(_lifetimeCancellation.Token);
                _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
                _readerAiSettings = await _aiSettingsStore.LoadAsync(_lifetimeCancellation.Token);
                if (Application.Current is App app) app.ApplyLanguage(_appSettings.UiLanguage);
                PopulateSettingsControls();
                ApplyReaderAiSettingsToControls();
                UpdateZLibraryAccountStatus();
            }

            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            SetupFilterControls();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            var status = result.Changed
                ? T("同步完成：新增 {0} 本书，下载 {1} 个文件，应用 {2} 条批注。", result.BooksAdded, result.FilesDownloaded, result.AnnotationsApplied)
                : T("同步完成，当前已是最新。 ");
            if (!string.IsNullOrWhiteSpace(result.Warning)) status += " " + UiText.Localize(result.Warning);
            S3SyncStatusText.Text = status;
            SetTaskStatus(status);
            _s3SyncedLocalChangeVersion = Math.Max(
                _s3SyncedLocalChangeVersion,
                localChangeVersionAtStart);
            succeeded = true;
            // A write that landed while the network operation was running is
            // deliberately left pending and is shown as such. This avoids a
            // misleading green check between the two sync passes.
            UpdateS3SyncIndicator(
                HasPendingS3LocalChanges
                    ? S3SyncIndicatorState.Pending
                    : S3SyncIndicatorState.Succeeded);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            if (!silent)
            {
                var status = T("S3 同步已取消。");
                S3SyncStatusText.Text = status;
                SetTaskStatus(status);
                UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, status);
            }
        }
        catch (Exception exception)
        {
            var error = UiText.Localize(exception.Message);
            var status = T("S3 同步失败：{0}", error);
            S3SyncStatusText.Text = status;
            if (!silent) SetTaskStatus(status);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
        finally
        {
            _s3SyncBusy = false;
            _s3SyncCompletion?.TrySetResult(succeeded);
            _s3SyncCompletion = null;
            S3SyncEnabledCheck.IsEnabled = true;
            S3SaveSettingsButton.IsEnabled = true;
            S3TestConnectionButton.IsEnabled = true;
            S3SyncNowButton.IsEnabled = true;
        }

        return succeeded;
    }

    // Basic settings auto-save (WinUI reference MainWindow.Productivity.cs):
    // any toggle/combo/number/text change schedules a 600 ms debounced save so
    // the user never needs the save button for the basic options.
    private void ConfigureAppSettingsAutoSave()
    {
        if (_appSettingsAutoSaveConfigured) return;
        _appSettingsAutoSaveConfigured = true;
        AiEnabledCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        NetworkEnabledCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoUpdateCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoDoubanMatchCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        CollectionsMutuallyExclusiveCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        ReadingMaterialsCollapsedByDefaultCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoGenerateReaderFormatsCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        CompareKindleLibraryCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        GridGalleryDisplayCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoConnectDeviceCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoBackupCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        DefaultVerticalWritingCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        PreferredOpenFormatBox.SelectionChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoBackupRetentionBox.ValueChanged += (_, _) => ScheduleAppSettingsAutoSave();
        CalibrePathBox.TextChanged += (_, _) =>
        {
            UpdateCalibreDetectionStatus();
            ScheduleAppSettingsAutoSave();
        };
    }

    private void UiLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave
            || UiLanguageBox is null
            || UiLanguageBox.SelectedItem is not ComboBoxItem { Tag: not null })
            return;

        var language = UiText.NormalizeLanguage(UiLanguageBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString()
            : null);
        if (Application.Current is App app)
            app.ApplyLanguage(language);
        _appSettings = AppSettings.Normalize(_appSettings with { UiLanguage = language });
        ScheduleAppSettingsAutoSave();
        UpdateLibraryUi();
        ViewModel.RefreshView();
    }

    private async Task DetectCalibreAtStartupAsync(CancellationToken cancellationToken)
    {
        using var setup = new CalibreSetupService();
        var detectedPath = setup.LocateCalibre(_appSettings.CalibrePath);
        if (string.IsNullOrWhiteSpace(detectedPath))
        {
            Environment.SetEnvironmentVariable(
                "KKINDLE_CALIBRE_CONVERT",
                null,
                EnvironmentVariableTarget.Process);
            return;
        }

        Environment.SetEnvironmentVariable(
            "KKINDLE_CALIBRE_CONVERT",
            detectedPath,
            EnvironmentVariableTarget.Process);
        if (string.Equals(_appSettings.CalibrePath, detectedPath, StringComparison.OrdinalIgnoreCase)) return;

        _appSettings = AppSettings.Normalize(_appSettings with { CalibrePath = detectedPath });
        await _appSettingsStore.SaveAsync(_appSettings, cancellationToken);
    }

    private void UpdateCalibreDetectionStatus(bool updateKfxStatusText = true)
    {
        var configuredPath = (CalibrePathBox.Text ?? string.Empty).Trim().Trim('"');
        var configuredFileName = Path.GetFileName(configuredPath);
        var isDetected = !string.IsNullOrWhiteSpace(configuredPath)
            && File.Exists(configuredPath)
            && (configuredFileName.Equals("ebook-convert", StringComparison.OrdinalIgnoreCase)
                || configuredFileName.Equals("ebook-convert.exe", StringComparison.OrdinalIgnoreCase));
        var status = isDetected ? T("已检测到 Calibre") : T("未检测到 Calibre");
        CalibreDetectionStatusDot.Fill = new SolidColorBrush(Color.Parse(isDetected ? "#2E8B57" : "#D6A100"));
        ToolTip.SetTip(CalibreDetectionStatusDot, status);
        AutomationProperties.SetName(CalibreDetectionStatusDot, status);

        InstallCalibreButton.IsEnabled = !_calibreSetupBusy && !isDetected;
        _calibreDetectionCancellation?.Cancel();
        _calibreDetectionCancellation?.Dispose();
        _calibreDetectionCancellation = null;

        if (!isDetected)
        {
            InstallKfxInputButton.IsEnabled = !_calibreSetupBusy;
            ToolTip.SetTip(InstallKfxInputButton, T("安装 Calibre KFX Input 插件"));
            return;
        }

        InstallKfxInputButton.IsEnabled = false;
        ToolTip.SetTip(InstallKfxInputButton, T("正在检查 KFX Input 安装状态"));
        if (_calibreSetupBusy) return;

        var detectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        detectionCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        _calibreDetectionCancellation = detectionCancellation;
        _ = DetectKfxInputInstallationAsync(configuredPath, detectionCancellation, updateKfxStatusText);
    }

    private async Task DetectKfxInputInstallationAsync(
        string calibrePath,
        CancellationTokenSource detectionCancellation,
        bool updateStatusText)
    {
        try
        {
            using var setup = new CalibreSetupService();
            var installed = await setup.IsKfxInputInstalledAsync(calibrePath, detectionCancellation.Token);
            if (!ReferenceEquals(_calibreDetectionCancellation, detectionCancellation)) return;

            InstallKfxInputButton.IsEnabled = !installed;
            ToolTip.SetTip(
                InstallKfxInputButton,
                installed ? T("KFX Input 已安装") : T("未检测到 KFX Input，点击安装"));
            if (updateStatusText)
            {
                CalibreSetupStatusText.Text = installed
                    ? T("已检测到 KFX Input 插件。")
                    : T("未检测到 KFX Input，可点击安装。");
            }
        }
        catch (OperationCanceledException) when (detectionCancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_calibreDetectionCancellation, detectionCancellation)
                && !_lifetimeCancellation.IsCancellationRequested)
            {
                InstallKfxInputButton.IsEnabled = true;
                ToolTip.SetTip(InstallKfxInputButton, T("未能确认 KFX Input 状态，点击可重新安装"));
                if (updateStatusText)
                    CalibreSetupStatusText.Text = T("KFX Input 状态检查超时，可点击安装。");
            }
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_calibreDetectionCancellation, detectionCancellation)) return;
            InstallKfxInputButton.IsEnabled = true;
            ToolTip.SetTip(InstallKfxInputButton, T("未能确认 KFX Input 状态，点击可重新安装"));
            if (updateStatusText)
                CalibreSetupStatusText.Text = T("KFX Input 状态检查失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_calibreDetectionCancellation, detectionCancellation))
            {
                _calibreDetectionCancellation = null;
                detectionCancellation.Dispose();
            }
        }
    }

    private void ScheduleAppSettingsAutoSave()
    {
        if (_suppressAppSettingsAutoSave) return;
        _appSettingsAutoSaveCancellation?.Cancel();
        _appSettingsAutoSaveCancellation?.Dispose();
        _appSettingsAutoSaveCancellation = new CancellationTokenSource();
        var token = _appSettingsAutoSaveCancellation.Token;
        _ = Task.Delay(600, token).ContinueWith(
            _ => Dispatcher.UIThread.Post(async () =>
            {
                if (token.IsCancellationRequested) return;
                await SaveAppSettingsCoreAsync();
            }),
            TaskScheduler.Default);
    }

    // "设置已保存" feedback appears after every auto-save, then hides itself.
    private void ShowSettingsSavedStatus() => ShowSettingsCapsule(T("设置已保存"), 1000, success: true);

    // Bottom-center capsule: the single feedback surface for settings actions.
    // Routine saves show for one second with a check glyph; error notices stay
    // a little longer and appear without it. The capsule rises into place and
    // sinks away again; a newer call supersedes any pending hide of an older one.
    private void ShowSettingsCapsule(string message, int visibleMilliseconds, bool success = false)
    {
        var sequence = ++_settingsCapsuleSequence;
        SettingsSavedCapsuleText.Text = UiText.Localize(message);
        SettingsCapsuleCheckIcon.IsVisible = success;
        SettingsSavedCapsule.RenderTransform =
            Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        SettingsSavedCapsule.IsVisible = true;
        SettingsSavedCapsule.Opacity = 1;
        _ = Task.Delay(visibleMilliseconds).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (sequence != _settingsCapsuleSequence) return;
                SettingsSavedCapsule.Opacity = 0;
                SettingsSavedCapsule.RenderTransform =
                    Avalonia.Media.Transformation.TransformOperations.Parse("translateY(12px)");
                _ = Task.Delay(260).ContinueWith(
                    __ => Dispatcher.UIThread.Post(() =>
                    {
                        if (sequence == _settingsCapsuleSequence)
                            SettingsSavedCapsule.IsVisible = false;
                    }));
            }),
            TaskScheduler.Default);
    }

    private async Task RefreshManagedResourcesAsync(CancellationToken cancellationToken = default)
    {
        ManagedFonts.Clear();
        foreach (var font in await _fontLibrary.ListAsync(cancellationToken)) ManagedFonts.Add(font);
        ManagedDictionaries.Clear();
        foreach (var dictionary in await _dictionaryService.ListAsync(cancellationToken)) ManagedDictionaries.Add(dictionary);
    }

    private async void RefreshLocalResourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
        SettingsStatusText.Text = T("本地字体与字典列表已刷新。");
    }

    private async void ImportFontButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入本地字体"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(T("字体")) { Patterns = ["*.ttf", "*.otf", "*.woff", "*.woff2"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            await _fontLibrary.ImportAsync(path, _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            FontManagementStatusText.Text = T("字体已导入。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FontManagementStatusText.Text = T("字体导入失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void RemoveFontButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ManagedFontList.SelectedItem is not ManagedFont font) return;
        try
        {
            await _fontLibrary.RemoveAsync(font.Id, _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            FontManagementStatusText.Text = T("字体已移除。");
        }
        catch (Exception exception)
        {
            FontManagementStatusText.Text = T("字体移除失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ImportDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入本地字典"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(T("支持的词典"))
                    {
                        Patterns = ["*.mdx", "*.azw", "*.azw3", "*.mobi", "*.prc", "*.kfx", "*.txt", "*.tsv", "*.csv"]
                    }
                ]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            await ImportLocalDictionaryPathsAsync([path]);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DictionaryResultText.Text = T("字典导入失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void RemoveDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ManagedDictionaryList.SelectedItem is not DictionaryDefinition dictionary) return;
        try
        {
            await _dictionaryService.RemoveAsync(dictionary.Id, _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            DictionaryResultText.Text = T("词典已移除。");
        }
        catch (Exception exception)
        {
            DictionaryResultText.Text = T("词典移除失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task LookupSettingsDictionaryAsync()
    {
        var term = DictionaryTestBox.Text?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            DictionaryResultText.Text = T("请输入要查询的词条。");
            return;
        }

        var entries = await _dictionaryService.LookupAsync(term, _lifetimeCancellation.Token);
        DictionaryResultText.Text = entries.Count == 0
            ? T("没有找到“{0}”。", term)
            : string.Join(Environment.NewLine + Environment.NewLine,
                entries.Select(entry => $"{entry.Term} · {entry.DictionaryName}{Environment.NewLine}{entry.Definition}"));
    }

    private async void DictionaryTestButton_Click(object? sender, RoutedEventArgs e) => await LookupSettingsDictionaryAsync();

    private async void DictionaryTestBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LookupSettingsDictionaryAsync();
    }

    private async void BrowseCalibreButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择 Calibre ebook-convert"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Calibre ebook-convert") { Patterns = ["ebook-convert", "ebook-convert.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) CalibrePathBox.Text = path;
    }

    private async void InstallCalibreButton_Click(object? sender, RoutedEventArgs e) =>
        await RunCalibreSetupAsync(installPlugin: false);

    private async void InstallKfxInputButton_Click(object? sender, RoutedEventArgs e) =>
        await RunCalibreSetupAsync(installPlugin: true);

    private async Task RunCalibreSetupAsync(bool installPlugin)
    {
        if (_calibreSetupBusy) return;
        if (NetworkEnabledCheck.IsChecked == false)
        {
            CalibreSetupStatusText.Text = T("请先开启“允许网络功能”。");
            return;
        }

        _calibreSetupBusy = true;
        InstallCalibreButton.IsEnabled = false;
        InstallKfxInputButton.IsEnabled = false;
        CalibreSetupProgressBar.IsVisible = true;
        CalibreSetupProgressBar.IsIndeterminate = true;
        var progress = new Progress<CalibreSetupProgress>(value =>
        {
            CalibreSetupStatusText.Text = UiText.Localize(value.Message);
            CalibreSetupProgressBar.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage) CalibreSetupProgressBar.Value = percentage;
        });

        var setupFailed = false;
        try
        {
            using var setup = new CalibreSetupService();
            if (installPlugin)
            {
                var executable = await setup.InstallKfxInputAsync(
                    CalibrePathBox.Text,
                    progress,
                    _lifetimeCancellation.Token);
                CalibrePathBox.Text = executable;
                CalibreSetupStatusText.Text = T("KFX Input 已安装，可以转换无 DRM 的 KFX 文件。");
            }
            else
            {
                var result = await setup.InstallCalibreAsync(progress, _lifetimeCancellation.Token);
                CalibrePathBox.Text = result.ExecutablePath;
                CalibreSetupStatusText.Text = UiText.Localize(result.Message);
            }
            await SaveAppSettingsCoreAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            CalibreSetupStatusText.Text = T("安装已取消。");
        }
        catch (Exception exception)
        {
            setupFailed = true;
            CalibreSetupStatusText.Text = T("安装失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _calibreSetupBusy = false;
            CalibreSetupProgressBar.IsVisible = false;
            CalibreSetupProgressBar.IsIndeterminate = false;
            UpdateCalibreDetectionStatus(updateKfxStatusText: !setupFailed);
        }
    }

    private void OpenDataDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureDirectories();
            Process.Start(new ProcessStartInfo { FileName = _paths.Data, UseShellExecute = true });
        }
        catch (Exception exception) { ShowSettingsCapsule(T("无法打开数据目录：{0}", UiText.Localize(exception.Message)), 4000); }
    }

    private void KindleEmailGuideLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://bookfere.com/post/3.html") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("无法打开链接：{0}", UiText.Localize(exception.Message)), 4000);
        }
    }

    private async Task SaveAppSettingsCoreAsync()
    {
        var selectedFormat = (PreferredOpenFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "epub";
        var autoConnectChanged = _appSettings.AutoConnectDevice != (AutoConnectDeviceCheck.IsChecked != false);
        _appSettings = AppSettings.Normalize(_appSettings with
        {
            UiLanguage = UiText.NormalizeLanguage(UiLanguageBox.SelectedItem is ComboBoxItem languageItem
                ? languageItem.Tag?.ToString()
                : _appSettings.UiLanguage),
            PreferredOpenFormat = selectedFormat,
            CalibrePath = CalibrePathBox.Text ?? string.Empty,
            AutoBackupEnabled = AutoBackupCheck.IsChecked == true,
            AutoBackupRetention = AutoBackupRetentionBox.Value is { } retention ? (int)retention : 5,
            AutoGenerateEpubAndAzw3OnImport = AutoGenerateReaderFormatsCheck.IsChecked == true,
            CollectionsMutuallyExclusive = CollectionsMutuallyExclusiveCheck.IsChecked != false,
            AiEnabled = AiEnabledCheck.IsChecked != false,
            NetworkEnabled = NetworkEnabledCheck.IsChecked != false,
            AutoUpdateCheckEnabled = AutoUpdateCheck.IsChecked != false,
            AutoDoubanMatchOnImport = AutoDoubanMatchCheck.IsChecked == true,
            AutoConnectDevice = AutoConnectDeviceCheck.IsChecked != false,
            CompareKindleLibraryEnabled = CompareKindleLibraryCheck.IsChecked != false,
            GridGalleryDisplay = GridGalleryDisplayCheck.IsChecked == true,
            ReadingMaterialsCollapsedByDefault = ReadingMaterialsCollapsedByDefaultCheck.IsChecked != false,
            // 排版默认值已在阅读器内设置，这里只同步竖排开关。
            DefaultReaderLayout = _appSettings.DefaultReaderLayout with
            {
                VerticalWriting = DefaultVerticalWritingCheck.IsChecked == true
            }
        });
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            Environment.SetEnvironmentVariable(
                "KKINDLE_CALIBRE_CONVERT",
                string.IsNullOrWhiteSpace(_appSettings.CalibrePath) ? null : _appSettings.CalibrePath,
                EnvironmentVariableTarget.Process);
            foreach (var group in ReadingMaterialGroups)
                group.IsExpanded = !_appSettings.ReadingMaterialsCollapsedByDefault;
            UpdateLibraryUi();
            SettingsStatusText.Text = T("管理本地数据、阅读偏好与设备设置。");
            if (_appSettingsStartupSettled)
                ShowSettingsSavedStatus();
            if (autoConnectChanged && _appSettings.AutoConnectDevice)
            {
                _ignoredDeviceId = null;
                await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible, _lifetimeCancellation.Token);
            }
        }
        catch (Exception exception) { ShowSettingsCapsule(T("保存失败：{0}", UiText.Localize(exception.Message)), 4000); }
    }

    private async void MigrateDataDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerDocument is not null || _readerIsPdf)
        {
            ShowSettingsCapsule(T("请先关闭阅读器，再迁移数据目录。"), 4000);
            await ShowMessageAsync(T("请先返回书库"), T("迁移数据目录前请关闭阅读器。"));
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("选择新的 Kkindle 数据目录"),
            AllowMultiple = false
        });
        var targetRoot = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(targetRoot)) return;
        targetRoot = Path.GetFullPath(targetRoot);
        if (string.Equals(targetRoot, Path.GetFullPath(_paths.Root), StringComparison.OrdinalIgnoreCase))
        {
            ShowSettingsCapsule(T("所选目录已经是当前数据根目录。"), 4000);
            return;
        }

        try
        {
            var migrationBackup = AppRootConfiguration.MigrationBackupPath(targetRoot);
            await _backupService.ExportAsync(migrationBackup, _lifetimeCancellation.Token);
            AppRootConfiguration.Save(_rootConfigurationDirectory, targetRoot);
            ShowSettingsCapsule(T("迁移包已准备；重启 Kkindle 后自动完成迁移。"), 4000);
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("迁移准备失败：{0}", UiText.Localize(exception.Message)), 4000);
        }
    }

    private async void ExportBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出 Kkindle 备份"),
            SuggestedFileName = T("Kkindle-备份-{0}{1}", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), AppBackupService.FileExtension),
            FileTypeChoices = [new FilePickerFileType(T("Kkindle 备份")) { Patterns = [$"*{AppBackupService.FileExtension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _backupBusy = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在导出备份…");
        try
        {
            var result = await _backupService.ExportAsync(path, _lifetimeCancellation.Token);
            SettingsBackupStatusText.Text = T("已导出 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            TaskProgressPopupText.Text = T("已导出 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
        }
        catch (Exception exception)
        {
            SettingsBackupStatusText.Text = T("备份导出失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("导出失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            _backupBusy = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async Task RunAutoBackupIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (!_appSettings.AutoBackupEnabled || _backupBusy) return;
        try
        {
            Directory.CreateDirectory(_paths.Backups);
            var existing = Directory.GetFiles(_paths.Backups, $"*{AppBackupService.FileExtension}")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            if (existing.FirstOrDefault() is { } latest
                && DateTime.UtcNow - latest.LastWriteTimeUtc < TimeSpan.FromHours(20))
                return;

            _backupBusy = true;
            var destination = Path.Combine(
                _paths.Backups,
                $"Kkindle-auto-{DateTime.Now:yyyyMMdd-HHmmss}{AppBackupService.FileExtension}");
            await _backupService.ExportAsync(destination, cancellationToken);
            existing = Directory.GetFiles(_paths.Backups, $"*{AppBackupService.FileExtension}")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            foreach (var oldBackup in existing.Skip(_appSettings.AutoBackupRetention))
                oldBackup.Delete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("自动备份失败：{0}", UiText.Localize(exception.Message)), 4000);
        }
        finally
        {
            _backupBusy = false;
        }
    }

    private async void ImportBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        if (_s3SyncBusy)
        {
            await ShowMessageAsync(T("请稍候"), T("S3 同步正在进行，请完成后再导入备份。"));
            return;
        }
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("请先返回书库"), T("导入备份前请先关闭阅读器，避免正在保存的阅读记录被打断。"));
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入 Kkindle 备份"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Kkindle 备份")) { Patterns = [$"*{AppBackupService.FileExtension}"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !await ConfirmAsync(T("导入 Kkindle 备份"), T("导入会覆盖当前书库、封面和阅读记录，确定继续吗？"))) return;
        _backupBusy = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在导入备份…");
        try
        {
            var result = await _backupService.ImportAsync(path, _lifetimeCancellation.Token);
            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(_lifetimeCancellation.Token);
            // The imported archive may represent an older or different local
            // state. Reset the previous sync baseline so its missing rows are
            // not interpreted as user deletions on the next S3 sync.
            await _s3SyncService.ResetLocalBaselineAsync(
                _s3SyncStoredSettings.DeviceId,
                _lifetimeCancellation.Token);
            _lastS3SyncAt = null;
            await _library.InitializeAsync(_lifetimeCancellation.Token);
            await _readerData.InitializeAsync(_lifetimeCancellation.Token);
            _appSettings = await _appSettingsStore.LoadAsync(_lifetimeCancellation.Token);
            LoadReaderVerticalDebugBoxesSetting();
            _readerAiSettings = result.AiSettings;
            _kindleEmailSettings = result.KindleEmailSettings;
            PopulateSettingsControls();
            ApplyReaderAiSettingsToControls();
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            SettingsBackupStatusText.Text = T("已导入 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            S3SyncStatusText.Text = T("本地备份已导入；下次 S3 同步将按导入内容建立新的本地基线。 ");
            TaskProgressPopupText.Text = T("已导入 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            UpdateLibraryUi();
            HandleLocalDataChanged(LocalDataChangeKind.Library);
        }
        catch (Exception exception)
        {
            SettingsBackupStatusText.Text = T("备份导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("导入失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            _backupBusy = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private void UpdateZLibraryAccountStatus()
    {
        ZLibraryStatusText.Text = _zLibrarySettings.IsConfigured
            ? T("已配置账号：{0}", _zLibrarySettings.Email)
            : T("未配置账号，可搜索书籍；下载前需要登录。");
    }

    private async void ZLibraryBooksButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(ZLibraryPage);
        UpdateZLibraryAccountStatus();
        await Task.CompletedTask;
    }

    private async void ZLibrarySearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await StartZLibrarySearchAsync();
    }

    private async void ZLibrarySearchButton_Click(object? sender, RoutedEventArgs e) => await StartZLibrarySearchAsync();

    private int _zLibrarySearchGeneration;

    private async Task StartZLibrarySearchAsync()
    {
        var query = ZLibrarySearchBox.Text?.Trim() ?? string.Empty;
        if (!_appSettings.NetworkEnabled)
        {
            ZLibraryResultText.Text = T("网络功能已关闭，请在设置中开启。");
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后使用在线书库。"));
            return;
        }
        if (query.Length == 0)
        {
            ZLibraryResultText.Text = T("请输入书名或作者。");
            return;
        }
        _zLibraryPage = 1;
        await PerformZLibrarySearchAsync(query, _zLibraryPage);
    }

    private async Task PerformZLibrarySearchAsync(string query, int page)
    {
        // A new search supersedes the previous one (WinUI reference): the old
        // request is cancelled so a slow query never blocks a newer keyword.
        _zLibrarySearchCancellation?.Cancel();
        _zLibrarySearchCancellation?.Dispose();
        _zLibrarySearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var cancellation = _zLibrarySearchCancellation;
        var cancellationToken = cancellation.Token;
        var generation = ++_zLibrarySearchGeneration;

        ZLibrarySearchButton.IsEnabled = false;
        ZLibraryPrevPageButton.IsEnabled = false;
        ZLibraryNextPageButton.IsEnabled = false;
        ZLibraryResultText.Text = T("正在搜索《{0}》…", query);
        try
        {
            if (_zLibrarySettings.IsConfigured && !_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(_zLibrarySettings.Email, _zLibrarySettings.Password, _zLibrarySettings.BaseUrl, cancellationToken);
            var extension = (ZLibraryExtensionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var language = (ZLibraryLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var result = await _zLibraryService.SearchAsync(
                query,
                page,
                extensions: string.IsNullOrWhiteSpace(extension) ? null : [extension],
                languages: string.IsNullOrWhiteSpace(language) ? null : [language],
                cancellationToken: cancellationToken);
            foreach (var old in ZLibraryBooks) old.Dispose();
            ZLibraryBooks.Clear();
            CloseZLibraryDetailPanel();
            foreach (var book in result.Books)
            {
                var item = new ZLibraryBookCardViewModel(book);
                ZLibraryBooks.Add(item);
                _ = item.LoadCoverAsync(cancellationToken);
            }
            _zLibraryPage = result.Page;
            _zLibraryPageCount = result.PageCount;
            ZLibraryPageText.Text = T("第 {0} / {1} 页", _zLibraryPage, Math.Max(1, _zLibraryPageCount));
            ZLibraryResultText.Text = result.Books.Count == 0 ? T("没有找到匹配书籍。") : T("共找到 {0} 本相关书籍", result.Total);
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one.
        }
        catch (Exception exception)
        {
            ZLibraryResultText.Text = T("搜索失败：{0}", UiText.Localize(exception.Message));
            ZLibraryPageText.Text = string.Empty;
            await ShowMessageAsync(T("搜索失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (generation == _zLibrarySearchGeneration)
            {
                ZLibrarySearchButton.IsEnabled = true;
                ZLibraryPrevPageButton.IsEnabled = _zLibraryPage > 1;
                ZLibraryNextPageButton.IsEnabled = _zLibraryPageCount > 0 && _zLibraryPage < _zLibraryPageCount;
            }
            if (ReferenceEquals(_zLibrarySearchCancellation, cancellation))
                _zLibrarySearchCancellation = null;
        }
    }

    private async void ZLibraryPrevPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_zLibraryPage > 1) await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text?.Trim() ?? string.Empty, _zLibraryPage - 1);
    }

    private async void ZLibraryNextPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_zLibraryPageCount > 0 && _zLibraryPage < _zLibraryPageCount)
            await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text?.Trim() ?? string.Empty, _zLibraryPage + 1);
    }

    private void ZLibraryDetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZLibraryBookCardViewModel item })
        {
            _selectedZLibraryBook = item;
            ZLibraryDetailPanel.DataContext = item;
            ZLibraryDetailPanel.IsVisible = true;
        }
    }

    // Clicking a search result row opens the detail panel (WinUI reference
    // selection behaviour); the row's own buttons keep their actions.
    private void ZLibraryBookRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ZLibraryBookCardViewModel item }) return;
        if (IsButtonSource(e.Source)) return;
        e.Handled = true;
        _selectedZLibraryBook = item;
        ZLibraryDetailPanel.DataContext = item;
        ZLibraryDetailPanel.IsVisible = true;
    }

    private static bool IsButtonSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button) return true;
        }
        return false;
    }

    private void ZLibraryDetailCloseButton_Click(object? sender, RoutedEventArgs e) => CloseZLibraryDetailPanel();

    private void CloseZLibraryDetailPanel()
    {
        _selectedZLibraryBook = null;
        ZLibraryDetailPanel.DataContext = null;
        ZLibraryDetailPanel.IsVisible = false;
    }

    private async void ZLibraryOfficialDetailButton_Click(object? sender, RoutedEventArgs e) =>
        await OpenZLibraryUrlAsync(_selectedZLibraryBook?.Book.OfficialDetailUrl, T("官网详情"));

    private async void ZLibraryReadOnlineButton_Click(object? sender, RoutedEventArgs e) =>
        await OpenZLibraryUrlAsync(_selectedZLibraryBook?.Book.ReadOnlineUrl, T("在线阅读"));

    private async Task OpenZLibraryUrlAsync(string? value, string actionName)
    {
        if (!_appSettings.NetworkEnabled)
        {
            SetTaskStatus(T("网络功能已关闭，请在设置中启用后使用{0}。", actionName));
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后使用{0}。", actionName));
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SetTaskStatus(T("无法打开{0}：这本书没有提供有效链接。", actionName));
            await ShowMessageAsync(T("无法打开{0}", actionName), T("这本书没有提供有效链接。"));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法打开{0}：{1}", actionName, UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法打开{0}", actionName), UiText.Localize(exception.Message));
        }
        await Task.CompletedTask;
    }

    private async void ZLibrarySendEmailButton_Click(object? sender, RoutedEventArgs e)
    {
        var item = _selectedZLibraryBook;
        if (item is null || item.IsDownloading || _zLibraryEmailSending) return;
        if (!item.CanSendToEmail)
        {
            SetTaskStatus(T("该书当前不支持邮件发送，或文件不是 EPUB/PDF 格式。"));
            await ShowMessageAsync(T("无法发送"), T("该书当前不支持邮件发送，或文件不是 EPUB/PDF 格式。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            SetTaskStatus(T("网络功能已关闭，请在设置中启用后再发送邮件。"));
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            SetTaskStatus(T("下载并发送前请先配置 Z-Library 账号。"));
            await ShowZLibraryAccountAsync(T("下载并发送前请先配置 Z-Library 账号。"));
            return;
        }

        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        var validationError = _kindleEmailSettings.Validate();
        if (validationError is not null)
        {
            SetTaskStatus(T("请先完成 Kindle 邮箱设置：{0}", UiText.Localize(validationError)));
            KindleEmailSettingsButton_Click(null, e);
            return;
        }

        if (!await ConfirmAsync(
                T("发送到 Kindle 邮箱"),
                T("将下载《{0}》并发送到 {1}。此操作会消耗一次 Z-Library 下载额度，是否继续？", item.Title, _kindleEmailSettings.KindleEmailAddress)))
            return;

        _zLibraryEmailSending = true;
        item.IsDownloading = true;
        item.SetStatus(T("正在准备邮件…"));
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在下载《{0}》并发送邮件…", item.Title);
        string? downloadedPath = null;
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(
                    _zLibrarySettings.Email,
                    _zLibrarySettings.Password,
                    _zLibrarySettings.BaseUrl,
                    _lifetimeCancellation.Token);

            var downloadsDirectory = Path.Combine(_paths.Data, "downloads");
            downloadedPath = await _zLibraryService.DownloadAsync(
                item.Book,
                downloadsDirectory,
                new Progress<TransferProgress>(item.SetDownloadProgress),
                _lifetimeCancellation.Token);
            if (!await EnsureKindleEmailAttachmentWithinLimitAsync(item.Title, downloadedPath))
            {
                item.SetStatus(T("文件超过 50 MB，无法发送到 Kindle 邮箱"));
                return;
            }
            item.SetStatus(T("正在发送邮件…"));
            SetTaskStatus(T("正在发送《{0}》到 Kindle 邮箱…", item.Title));
            await _kindleEmailSender.SendAsync(
                _kindleEmailSettings,
                downloadedPath,
                $"Send to Kindle: {item.Title}",
                _lifetimeCancellation.Token);
            item.MarkDownloadCompleted();
            item.SetStatus(T("已发送到 Kindle 邮箱"));
            SetTaskStatus(T("《{0}》已提交到 Kindle 邮箱", item.Title));
            await ShowMessageAsync(T("发送成功"), T("邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            item.SetStatus(T("邮件发送已取消"));
        }
        catch (Exception exception)
        {
            item.SetStatus(T("邮件发送失败：{0}", UiText.Localize(exception.Message)));
            SetTaskStatus(T("Kindle 邮箱发送失败"));
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(downloadedPath))
                try { File.Delete(downloadedPath); } catch { }
            item.IsDownloading = false;
            _zLibraryEmailSending = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async void ZLibraryDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ZLibraryBookCardViewModel item } || item.IsDownloading) return;
        if (!_appSettings.NetworkEnabled)
        {
            item.SetStatus(T("网络功能已关闭"));
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后下载书籍。"));
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            item.SetStatus(T("请先配置账号"));
            SetTaskStatus(T("请先配置 Z-Library 账号。"));
            await ShowZLibraryAccountAsync(T("请先配置 Z-Library 账号。"));
            return;
        }
        item.IsDownloading = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在下载《{0}》…", item.Title);
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(_zLibrarySettings.Email, _zLibrarySettings.Password, _zLibrarySettings.BaseUrl, _lifetimeCancellation.Token);
            var downloadDirectory = Path.Combine(_paths.Data, "downloads");
            var downloaded = await _zLibraryService.DownloadAsync(item.Book, downloadDirectory, new Progress<TransferProgress>(item.SetDownloadProgress), _lifetimeCancellation.Token);
            var result = await _library.ImportAsync([downloaded], cancellationToken: _lifetimeCancellation.Token);
            if (result.FailureCount > 0) throw new IOException(result.Items.FirstOrDefault()?.Message ?? T("导入书库失败。"));
            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(result, _lifetimeCancellation.Token);
            item.MarkDownloadCompleted();
            item.SetStatus(automaticFormats.Failures.Count == 0
                ? T("已下载并导入电脑书库")
                : T("已导入；格式补齐失败 {0} 项", automaticFormats.Failures.Count));
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            try { File.Delete(downloaded); } catch { }
        }
        catch (Exception exception)
        {
            item.SetStatus(T("下载失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("下载失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            item.IsDownloading = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async void ZLibraryAccountButton_Click(object? sender, RoutedEventArgs e) =>
        await ShowZLibraryAccountAsync();

    private async Task ShowZLibraryAccountAsync(string? status = null)
    {
        _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync(_lifetimeCancellation.Token);
        ZLibraryEmailBox.Text = _zLibrarySettings.Email;
        ZLibraryPasswordBox.Text = _zLibrarySettings.Password;
        ZLibraryBaseUrlBox.Text = _zLibrarySettings.BaseUrl;
        ShowStage3Page(SettingsPage, ZLibraryAccountNavigationButton);
        ShowSettingsSection("Kindle");
        ZLibraryAccountStatusText.Text = status ?? string.Empty;
        ShowSettingsPanel(ZLibraryAccountPane);
        ZLibraryEmailBox.Focus();
    }

    private void KindleEmailSettingsCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        KindleEmailSettingsStatusText.Text = string.Empty;
    }

    private void ZLibraryAccountCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        ZLibraryAccountStatusText.Text = string.Empty;
    }

    private void MainReaderAiSettingsCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        MainReaderAiSettingsStatusText.Text = string.Empty;
    }

    private async void ZLibraryAccountSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ZLibrarySettings.Normalize(new ZLibrarySettings
        {
            Email = ZLibraryEmailBox.Text ?? string.Empty,
            Password = ZLibraryPasswordBox.Text ?? string.Empty,
            BaseUrl = ZLibraryBaseUrlBox.Text ?? string.Empty
        });
        var validation = settings.Validate();
        if (validation is not null)
        {
            ZLibraryAccountStatusText.Text = UiText.Localize(validation);
            return;
        }
        try
        {
            if (_appSettings.NetworkEnabled)
            {
                await _zLibraryService.LoginAsync(settings.Email, settings.Password, settings.BaseUrl, _lifetimeCancellation.Token);
                settings.BaseUrl = _zLibraryService.ActiveBaseUrl;
            }
            await _zLibrarySettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            _zLibrarySettings = settings;
            UpdateZLibraryAccountStatus();
            ZLibraryAccountStatusText.Text = T("账号已保存。");
            HideSettingsPanel();
        }
        catch (Exception exception) { ZLibraryAccountStatusText.Text = T("保存或验证失败：{0}", UiText.Localize(exception.Message)); }
    }

    private async void KindleEmailSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = KindleEmailSettings.Normalize(new KindleEmailSettings
        {
            KindleEmailAddress = KindleEmailRecipientBox.Text ?? string.Empty,
            SenderEmailAddress = KindleEmailSenderBox.Text ?? string.Empty,
            SmtpHost = KindleEmailSmtpHostBox.Text ?? string.Empty,
            SmtpPort = int.TryParse(KindleEmailSmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 587,
            SmtpUsername = KindleEmailUsernameBox.Text ?? string.Empty,
            SmtpPassword = KindleEmailPasswordBox.Text ?? string.Empty,
            EnableSsl = KindleEmailSslCheck.IsChecked != false
        });
        var validation = settings.Validate();
        if (validation is not null)
        {
            KindleEmailSettingsStatusText.Text = UiText.Localize(validation);
            return;
        }
        try
        {
            await _kindleEmailSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            _kindleEmailSettings = settings;
            KindleEmailSettingsStatusText.Text = T("Kindle 邮箱设置已保存。");
            HideSettingsPanel();
        }
        catch (Exception exception) { KindleEmailSettingsStatusText.Text = T("保存失败：{0}", UiText.Localize(exception.Message)); }
    }

}

public sealed record Stage3DashboardDayViewModel(
    string DateLabel,
    string TimeLabel,
    double BarHeight,
    double BarOpacity);

public sealed record Stage3DashboardBarViewModel(
    string Label,
    string ValueLabel,
    double BarWidth);

public sealed record Stage3DashboardRecentViewModel(
    string Title,
    double ProgressPercent,
    long Seconds,
    DateTimeOffset UpdatedAt)
{
    public string ProgressLabel => $"{ProgressPercent:0.#}%";

    public string DurationLabel => FormatTime(Seconds);

    public string UpdatedLabel => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatTime(long seconds) => seconds switch
    {
        < 60 => UiText.Get("{0} 秒", seconds),
        < 3600 => UiText.Get("{0} 分钟", Math.Max(1, seconds / 60)),
        _ => UiText.Get("{0:0.#} 小时", seconds / 3600d)
    };
}

public sealed class Stage3ReadingMaterialGroupViewModel : ObservableObject, IDisposable
{
    private bool _isExpanded;
    private readonly string _bookTitleSource;

    public Stage3ReadingMaterialGroupViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        IReadOnlyList<Stage3ReadingMaterialViewModel> items,
        string? coverPath,
        bool isExpanded,
        bool isMixedSource = false)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Source = source;
        _bookTitleSource = string.IsNullOrWhiteSpace(bookTitle) ? "未命名书籍" : bookTitle;
        Items = items;
        IsMixedSource = isMixedSource;
        _isExpanded = isExpanded;
        if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
        {
            try { CoverImage = new Bitmap(coverPath); } catch { }
        }
    }

    public ReadingMaterialSource Source { get; }
    public bool IsMixedSource { get; }
    public string SourceLabel => IsMixedSource ? UiText.Get("本地书籍 + Kindle") : Source == ReadingMaterialSource.Local ? UiText.Get("本地书籍") : "Kindle";
    internal string BookTitleKey => _bookTitleSource;
    public string BookTitle => UiText.Localize(_bookTitleSource);
    public IReadOnlyList<Stage3ReadingMaterialViewModel> Items { get; }
    public string CountLabel => UiText.Get("{0} 条批注", Items.Count);
    public Bitmap? CoverImage { get; }
    public bool HasCover => CoverImage is not null;
    public bool HasNoCover => CoverImage is null;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(ExpandLabel));
        }
    }
    public bool IsCollapsed => !IsExpanded;
    public string ExpandLabel => IsExpanded ? UiText.Get("收起") : UiText.Get("展开");
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(ExpandLabel));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        CoverImage?.Dispose();
    }
}

public sealed class Stage3ReadingMaterialViewModel : ObservableObject, IDisposable
{
    private bool _isSelected;
    private readonly string _bookTitleSource;
    private readonly string _typeLabelSource;

    public Stage3ReadingMaterialViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        string typeLabel,
        string chapterLabel,
        string location,
        string quote,
        string note,
        DateTimeOffset? updatedAt,
        ReaderAnnotation? localAnnotation,
        KindleClipping? kindleClipping,
        KindleClipping? pairedKindleClipping = null)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Source = source;
        _bookTitleSource = string.IsNullOrWhiteSpace(bookTitle) ? "未命名书籍" : bookTitle;
        ChapterLabel = chapterLabel;
        Location = location;
        Quote = quote;
        Note = note;
        UpdatedAt = updatedAt;
        LocalAnnotation = localAnnotation;
        KindleClipping = kindleClipping;
        PairedKindleClipping = pairedKindleClipping;
        _typeLabelSource = kindleClipping is { } clipping && pairedKindleClipping is null
            ? clipping.Type switch
            {
                KindleClippingType.Highlight => "划线",
                KindleClippingType.Note => "笔记",
                KindleClippingType.Bookmark => "书签",
                _ => "记录"
            }
            : typeLabel;
    }

    public ReadingMaterialSource Source { get; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? UiText.Get("本地") : "Kindle";
    internal string BookTitleKey => _bookTitleSource;
    public string BookTitle => UiText.Localize(_bookTitleSource);
    public string TypeLabel => UiText.Get(_typeLabelSource);
    public string ChapterLabel { get; }
    public string Location { get; }
    public string Quote { get; }
    public string Note { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public ReaderAnnotation? LocalAnnotation { get; }
    public KindleClipping? KindleClipping { get; }
    public KindleClipping? PairedKindleClipping { get; }
    public string QuoteLabel => string.IsNullOrWhiteSpace(Quote) ? UiText.Get("无划线内容") : UiText.Get("“{0}”", Quote);
    public string NoteLabel => string.IsNullOrWhiteSpace(Note) ? "" : UiText.Get("批注：{0}", Note);
    public string ChapterDisplayLabel => UiText.Get("章节：{0}", ChapterLabel);
    public string SelectedContentLabel => string.IsNullOrWhiteSpace(Quote) ? UiText.Get("选中内容：无") : UiText.Get("选中内容：{0}", Quote);
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public string DateLabel => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? UiText.Get("时间未知");
    public string SearchText => string.Join('\n', SourceLabel, BookTitle, TypeLabel, ChapterLabel, Location, Quote, Note);
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ReadingMaterialRecord ToRecord() => new(Source, BookTitle, TypeLabel, Location, Quote, Note, UpdatedAt);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(QuoteLabel));
        OnPropertyChanged(nameof(NoteLabel));
        OnPropertyChanged(nameof(ChapterDisplayLabel));
        OnPropertyChanged(nameof(SelectedContentLabel));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(SearchText));
    }

    public void Dispose() =>
        UiText.LanguageChanged -= OnLanguageChanged;
}

public sealed class KindleBookCardViewModel : ObservableObject, IDisposable
{
    private Bitmap? _coverImage;
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.KindleOnly;
    private bool _isDownloading;
    private bool _isSelected;
    private bool _isMultiSelected;
    private bool _isHovered;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

    public KindleBookCardViewModel(KindleBook book)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Book = book;
        if (!string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(book.CoverPath))
        {
            try { _coverImage = new Bitmap(book.CoverPath); } catch { }
        }
    }

    public KindleBook Book { get; }
    public Bitmap? CoverImage => _coverImage;
    public string Title => UiText.Localize(Book.Title);
    public string Authors => UiText.Localize(Book.Authors);
    public string FormatLabel => Book.Format.ToUpperInvariant();
    public string SizeLabel => Book.SizeLabel;
    public string InfoLabel => $"{FormatLabel} · {Book.SizeLabel}";
    public string FileName => Book.FileName;
    public string RelativePath => Book.RelativePath;
    public string ModifiedLabel => Book.ModifiedAt is { } modifiedAt
        ? UiText.Get("修改于 {0:yyyy-MM-dd HH:mm}", modifiedAt.ToLocalTime())
        : UiText.Get("修改时间未知");
    public string HashLabel => string.IsNullOrWhiteSpace(Book.Sha256)
        ? UiText.Get("SHA-256 未计算")
        : UiText.Get("SHA-256 {0}…", Book.Sha256[..Math.Min(12, Book.Sha256.Length)]);
    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => UiText.Get("电脑与 Kindle 都有"),
        BookLibraryPresence.ComputerOnly => UiText.Get("仅电脑书库"),
        _ => UiText.Get("仅 Kindle")
    };

    // Gallery mode (shared with the PC library grid): hide the title/author/
    // info text so only the cover image remains.
    private bool _galleryTextVisible = true;
    private bool _isLibraryPresenceVisible = true;

    public bool GalleryTextVisibility
    {
        get => _galleryTextVisible;
        private set => SetProperty(ref _galleryTextVisible, value);
    }
    public double CardHeight => _galleryTextVisible ? 292 : 214;

    public void SetGalleryTextVisible(bool visible)
    {
        if (_galleryTextVisible == visible) return;
        _galleryTextVisible = visible;
        OnPropertyChanged(nameof(GalleryTextVisibility));
        OnPropertyChanged(nameof(PresenceVisibility));
        OnPropertyChanged(nameof(CardHeight));
    }

    public bool PresenceVisibility
    {
        get => _isLibraryPresenceVisible && _galleryTextVisible;
        private set
        {
            if (_isLibraryPresenceVisible == value) return;
            _isLibraryPresenceVisible = value;
            OnPropertyChanged(nameof(PresenceVisibility));
        }
    }

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }
    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (!SetProperty(ref _libraryPresence, value)) return;
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
            OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
            OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        }
    }
    public bool ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => SetProperty(ref _isMultiSelected, value);
    }
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetProperty(ref _isHovered, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }
    // 与电脑书库一致：悬停或选中时整卡显示黑色细边框。
    public bool IsFrameVisible => IsSelected || IsHovered;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }
    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;
    public void SetDownloadProgress(TransferProgress progress) => DownloadProgress = progress.Percentage;
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(InfoLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(HashLabel));
        OnPropertyChanged(nameof(PresenceLabel));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        _coverImage?.Dispose();
    }
}

public sealed class ZLibraryBookCardViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string[] CoverFallbackHosts = ["https://covers.z-library.sk"];
    private Bitmap? _coverImage;
    private bool _isDownloading;
    private bool _isDownloadCompleted;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

    public ZLibraryBookCardViewModel(ZLibraryBook book)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Book = book;
    }
    public ZLibraryBook Book { get; }
    public string Title => UiText.Localize(Book.Title);
    public string Authors => UiText.Localize(Book.Author);
    public string InfoLabel => Book.InfoLabel;
    public string YearLabel => Book.Year is > 0 ? Book.Year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    public string PublicationLabel => string.Join(" · ", new[]
    {
        Book.Publisher ?? string.Empty,
        YearLabel,
        string.IsNullOrWhiteSpace(Book.Series) ? string.Empty : UiText.Get("系列：{0}", Book.Series),
        string.IsNullOrWhiteSpace(Book.Edition) ? string.Empty : UiText.Get("版本：{0}", Book.Edition)
    }.Where(value => value.Length > 0));
    public string IdentifierLabel => string.IsNullOrWhiteSpace(Book.Identifier)
        ? string.Empty
        : $"ISBN {Book.Identifier.Replace(",", " / ", StringComparison.Ordinal)}";
    public string AvailabilityLabel => string.Join(" · ", new[]
    {
        Book.ReadOnlineAvailable ? UiText.Get("可在线阅读") : string.Empty,
        Book.KindleAvailable ? UiText.Get("支持 Kindle") : string.Empty
    }.Where(value => value.Length > 0));
    public string ExtraInfoLabel => string.Join(" · ", new[] { IdentifierLabel, AvailabilityLabel }
        .Where(value => value.Length > 0));
    public string VolumeLabel => string.IsNullOrWhiteSpace(Book.Volume) ? UiText.Get("未提供") : Book.Volume;
    public string DetailDescription
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Book.Description)) return UiText.Get("暂无简介。");
            var withoutTags = Regex.Replace(Book.Description, "<[^>]+>", " ");
            return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
        }
    }
    public string DetailMetadataLabel => string.Join(" · ", new[] { PublicationLabel, InfoLabel, IdentifierLabel }
        .Where(value => value.Length > 0));
    public bool CanOpenOfficialDetail => Uri.TryCreate(Book.OfficialDetailUrl, UriKind.Absolute, out _);
    public bool CanReadOnline => Book.ReadOnlineAvailable && Uri.TryCreate(Book.ReadOnlineUrl, UriKind.Absolute, out _);
    public bool CanSendToEmail => Book.SendToEmailAvailable
        && (Book.Extension.Equals("epub", StringComparison.OrdinalIgnoreCase)
            || Book.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase));
    public Bitmap? CoverImage => _coverImage;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (value)
            {
                DownloadProgress = 0;
                if (SetProperty(ref _isDownloadCompleted, false, nameof(IsDownloadCompleted)))
                    OnPropertyChanged(nameof(IsDownloadIdle));
            }
            if (!SetProperty(ref _isDownloading, value)) return;
            OnPropertyChanged(nameof(IsNotDownloading));
            OnPropertyChanged(nameof(IsDownloadIdle));
        }
    }
    public bool IsNotDownloading => !IsDownloading;
    public bool IsDownloadCompleted => _isDownloadCompleted;
    public bool IsDownloadIdle => !IsDownloading && !IsDownloadCompleted;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (!SetProperty(ref _downloadProgress, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(DownloadFillWidth));
        }
    }
    public double DownloadFillWidth => DownloadProgress * 1.08;
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }
    public void SetDownloadProgress(TransferProgress progress)
    {
        DownloadProgress = progress.Percentage;
        StatusMessage = UiText.Get("正在下载 {0:0}%", progress.Percentage);
    }
    public void MarkDownloadCompleted()
    {
        DownloadProgress = 100;
        if (!SetProperty(ref _isDownloadCompleted, true, nameof(IsDownloadCompleted))) return;
        OnPropertyChanged(nameof(IsDownloadIdle));
    }
    public void SetStatus(string message) => StatusMessage = message;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(InfoLabel));
        OnPropertyChanged(nameof(PublicationLabel));
        OnPropertyChanged(nameof(AvailabilityLabel));
        OnPropertyChanged(nameof(ExtraInfoLabel));
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(DetailDescription));
        OnPropertyChanged(nameof(DetailMetadataLabel));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        _coverImage?.Dispose();
        _coverImage = null;
    }

    public async Task LoadCoverAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Book.CoverUrl) || _coverImage is not null) return;

        var attempts = new List<string> { Book.CoverUrl };
        if (Uri.TryCreate(Book.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            foreach (var fallbackHost in CoverFallbackHosts)
            {
                var fallback = fallbackHost + coverUri.PathAndQuery;
                if (!string.Equals(fallback, Book.CoverUrl, StringComparison.OrdinalIgnoreCase))
                    attempts.Add(fallback);
            }
        }

        foreach (var url in attempts)
        {
            try
            {
                using var response = await CoverClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!LooksLikeCoverImage(bytes)) continue;
                await using var stream = new MemoryStream(bytes, writable: false);
                _coverImage = new Bitmap(stream);
                OnPropertyChanged(nameof(CoverImage));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Covers are decorative and must never fail the search result.
            }
        }
    }

    private static bool LooksLikeCoverImage(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        return (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            || (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
            || (bytes[0] == 0x42 && bytes[1] == 0x4D);
    }
}
