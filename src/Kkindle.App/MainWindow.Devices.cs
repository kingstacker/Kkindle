using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
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
        var detected = await _kindle.DetectDevicesAsync(_lifetimeCancellation.Token);
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
