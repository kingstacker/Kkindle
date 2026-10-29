using Kkindle.Core;
using Avalonia.Threading;
using System.Diagnostics;

namespace Kkindle;

public partial class MainWindow
{
    private async Task<IReadOnlyList<BookCardViewModel>?> ChooseDeviceDuplicatesAsync(
        KindleDevice device, IReadOnlyList<BookCardViewModel> cards)
    {
        if (!IsCurrentDevice(device)) return null;
        SetTaskStatus(T("正在核对设备书库…"));
        try
        {
            if (_deviceBookScanTask is { IsCompleted: false } scan)
                await scan.WaitAsync(_lifetimeCancellation.Token);
            else if (!_deviceBooksLoaded || _deviceBooksDirty)
                await RefreshDeviceBooksAsync(_lifetimeCancellation.Token);
            if (!IsCurrentDevice(device)) return null;
            if (!_deviceBooksLoaded || _deviceBookScanFailed || _deviceBooksShowingCache)
            {
                return await ConfirmAsync(T("无法核对设备书籍"),
                    T("设备书库尚未核对完成，无法判断是否已有这些书。仍要发送吗？"),
                    T("仍然发送"), _lifetimeCancellation.Token, allowDiagnosticAutoConfirm: false) ? cards : null;
            }

            var matches = BookLibraryComparer.Compare(cards.Select(card => card.Book), DeviceBooks.Select(card => card.Book)).BooksOnKindle;
            var duplicates = cards.Where(card => matches.Contains(card.Book.Id)).ToArray();
            if (duplicates.Length == 0) return cards;
            var lines = string.Join(Environment.NewLine, duplicates.Select(card => T("《{0}》", card.Title)));
            var choice = await ConfirmDeviceDuplicatesAsync(
                T("以下 {0} 本书可能已在设备中：", duplicates.Length) + Environment.NewLine + Environment.NewLine + lines
                + Environment.NewLine + Environment.NewLine + T("根据最近一次扫描和书籍信息匹配，可能存在不同版本；仍然发送可能替换设备中的同名文件。"));
            if (!IsCurrentDevice(device)) return null;
            return choice switch
            {
                DeviceDuplicateChoice.Skip => cards.Where(card => !matches.Contains(card.Book.Id)).ToArray(),
                DeviceDuplicateChoice.SendAll => cards,
                _ => null
            };
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return null; }
        catch (Exception exception)
        {
            SetTaskStatus(T("核对设备书籍失败：{0}", UiText.Localize(exception.Message)));
            return null;
        }
    }

    private async Task<DeviceDuplicateChoice> ConfirmDeviceDuplicatesAsync(string message)
    {
        if (_confirmationCompletion is not null || _bookRemovalCompletion is not null || _deviceDuplicateCompletion is not null)
            return DeviceDuplicateChoice.Cancel;
        ConfirmationTitleText.Text = T("设备中可能已有这些书");
        ConfirmationMessageText.Text = message;
        ConfirmationOkButton.Content = T("跳过已有");
        ConfirmationAlternativeButton.Content = T("仍然发送");
        ConfirmationAlternativeButton.IsVisible = true;
        ShowOverlay(ConfirmationOverlay);
        _deviceDuplicateCompletion = new TaskCompletionSource<DeviceDuplicateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _deviceDuplicateCompletion;
        ConfirmationCancelButton.Focus();
        try { return await completion.Task.WaitAsync(_lifetimeCancellation.Token); }
        finally
        {
            if (ReferenceEquals(_deviceDuplicateCompletion, completion)) _deviceDuplicateCompletion = null;
            ConfirmationAlternativeButton.IsVisible = false;
            ConfirmationOverlay.IsVisible = false;
        }
    }

    private void InitializeDeviceChangeNotifier(Func<IntPtr, IDeviceChangeNotifier?>? factory)
    {
        if (factory is null || _deviceChangeNotifier is not null || _kindle is null) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        try
        {
            _deviceChangeNotifier = factory(handle);
            if (_deviceChangeNotifier is not null)
                _deviceChangeNotifier.DeviceChanged += DeviceChangeNotifier_DeviceChanged;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Device change notifications unavailable: {exception}");
        }
    }

    private void DisposeDeviceChangeNotifier()
    {
        if (_deviceChangeNotifier is null) return;
        _deviceChangeNotifier.DeviceChanged -= DeviceChangeNotifier_DeviceChanged;
        _deviceChangeNotifier.Dispose();
        _deviceChangeNotifier = null;
    }

    private void DeviceChangeNotifier_DeviceChanged(object? sender, EventArgs e)
    {
        // The native callback runs inside the window procedure; defer all work
        // and coalesce the burst of Windows device-change messages.
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetimeCancellation.IsCancellationRequested) return;
            _deviceChangePending = true;
            _deviceChangeDebounceTimer.Stop();
            _deviceChangeDebounceTimer.Start();
        });
    }

    private async void DeviceChangeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _deviceChangeDebounceTimer.Stop();
        if (_lifetimeCancellation.IsCancellationRequested || !_deviceChangePending) return;
        if (!_stage3Ready) return;
        if (_isRefreshingDevices || _deviceEjectInProgress)
        {
            _deviceChangeDebounceTimer.Start();
            return;
        }
        _deviceChangePending = false;
        _stage3IdleTicks = 0;
        await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible, _lifetimeCancellation.Token);
    }

    private bool IsCurrentDevice(KindleDevice device) => CurrentDevice is { } current && IsSameDevice(current, device);

    private static bool IsSameDevice(KindleDevice left, KindleDevice right) =>
        left.Identity.Equals(right.Identity, StringComparison.OrdinalIgnoreCase)
        && left.Transport == right.Transport && left.Profile.Family == right.Profile.Family
        && left.RootPath.TrimEnd('/', '\\').Equals(right.RootPath.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
        && left.Profile.BooksDirectory.Equals(right.Profile.BooksDirectory, StringComparison.OrdinalIgnoreCase);

    private async Task EnsureCurrentDeviceAsync(KindleDevice device)
    {
        var message = T("设备连接已变化，请确认当前设备后重试。");
        if (_kindle is null || !IsCurrentDevice(device)) throw new IOException(message);
        var detected = await DetectDevicesWithTimeoutAsync(_lifetimeCancellation.Token);
        if (!IsCurrentDevice(device)) throw new IOException(message);
        if (detected.Count == 1 && IsSameDevice(detected[0], device)) return;
        _acceptedDeviceId = null;
        _lastDeviceIdentity = null;
        SetDisconnectedDeviceUi(detected.Count > 1 ? T("检测到多台阅读设备，请只保留一台连接。") : message);
        throw new IOException(message);
    }

    private bool CanDeleteReadingMaterial(Stage3ReadingMaterialViewModel item) => item.CanDelete
        && (item.Source == ReadingMaterialSource.Local || CurrentDevice is { } device
            && device.Profile.CanDeleteNotes
            && string.Equals(item.SourceDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase));

    private static string DeviceTransferDescription(KindleDevice device) => device.Profile.UsesKindleThumbnails
        ? Environment.NewLine + Environment.NewLine + T("EPUB/MOBI 会先转换为 Kindle 兼容的 AZW3。")
        : string.Empty;

    private void UpdateDeviceResourceCapabilities()
    {
        var device = CurrentDevice;
        var supported = device?.Profile.SupportsResource(_deviceResourceKind) == true;
        ImportDeviceResourceButton.IsEnabled = supported && !_deviceResourceBusy;
        DeviceResourcePathText.Text = supported
            ? device!.Profile.ResourceDirectory(_deviceResourceKind).Replace('/', Path.DirectorySeparatorChar)
            : string.Empty;
        DeviceResourcePathText.IsVisible = supported;
        if (!supported)
        {
            DeviceResources.Clear();
            DeviceResourceList.SelectedItem = null;
            ExportDeviceResourceButton.IsEnabled = false;
            DeleteDeviceResourceButton.IsEnabled = false;
        }
    }

    private void ClearDisconnectedDeviceNotes()
    {
        foreach (var item in _allStage3ReadingMaterials.Where(item => item.Source != ReadingMaterialSource.Local).ToArray())
        {
            _allStage3ReadingMaterials.Remove(item);
            item.Dispose();
        }
        if (ReadingMaterialsPage.IsVisible) ApplyReadingMaterialsFilter();
    }
}
