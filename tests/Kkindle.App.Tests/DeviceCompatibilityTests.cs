using Avalonia.Controls;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task DeviceCompatibilityUsesGenericNamesInBothLanguages() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            ((Kkindle.App)Avalonia.Application.Current!).ApplyLanguage(language);
            await Render();
            var labels = scope.Get<Button>("KindleBooksButton").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Contains(language == "zh-CN" ? "设备书库" : "Device library", labels);
            Assert.Equal(language == "zh-CN" ? "设备与账户" : "Devices & accounts", scope.Get<Button>("SettingsKindleButton").Content);
            Assert.Contains("Kindle", UiText.Get("发送到 Kindle 邮箱"));
            Assert.Equal(language == "zh-CN" ? "当前设备" : "Current device",
                ((ComboBoxItem)scope.Get<ComboBox>("ReadingMaterialsSourceBox").Items[2]!).Content);
            Capture(scope.Window, "device-navigation-" + language);
        }
    });

    [Fact]
    public Task DeviceCompatibilityRequiresExactlyOneConnectedReader() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var kobo = Reader(scope, "KOBO", ReaderDeviceProfiles.Kobo);
        var kindle = Reader(scope, "KINDLE", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService { Devices = [kobo, kindle] };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        Assert.Contains("只保留一台", scope.Get<TextBlock>("DevicePageStatusText").Text);
        Assert.False(scope.Call<bool>("IsCurrentDevice", kobo));
        Assert.False(scope.Call<bool>("IsCurrentDevice", kindle));
        Assert.Equal(0, service.ContentRequests);
        scope.Call("ShowStage3Page", scope.Get<Grid>("DevicePage"), null);
        await Render();
        Capture(scope.Window, "device-multiple-blocked");

        service.Devices = [kobo];
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        Assert.True(scope.Call<bool>("IsCurrentDevice", kobo));
        Assert.Equal("Kobo", scope.Get<TextBlock>("KindleStatusText").Text);
    });

    [Fact]
    public Task DeviceCompatibilityDiscardsAnEarlierScanAfterMultipleReadersConnect() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var kobo = Reader(scope, "KOBO", ReaderDeviceProfiles.Kobo);
        var pendingScan = new TaskCompletionSource<IReadOnlyList<KindleBook>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ReaderService { Devices = [kobo], BooksReader = _ => pendingScan.Task };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await Until(() => service.ContentRequests > 0);
        var warm = scope.Field<Task>("_deviceWarmTask");

        service.Devices = [kobo, Reader(scope, "KINDLE", ReaderDeviceProfiles.Kindle)];
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        pendingScan.SetResult([new KindleBook { RelativePath = "old.epub", Title = "上一个设备的书", Format = "epub" }]);
        await warm;
        Assert.Empty(scope.Window.DeviceBooks);
        Assert.Equal(0, service.ResourceRequests);
        Assert.Contains("只保留一台", scope.Get<TextBlock>("DevicePageStatusText").Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeviceCompatibilityRechecksTheTargetBeforeAccess(bool multiple) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var kindle = Reader(scope, "KINDLE", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService { Devices = [kindle] };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        var reconnected = new KindleDevice
        {
            VolumeSerial = kindle.VolumeSerial, RootPath = kindle.RootPath + "-new-mount",
            Name = kindle.Name, Profile = kindle.Profile, IsReady = true
        };
        service.Devices = multiple ? [kindle, reconnected] : [reconnected];

        await Assert.ThrowsAsync<IOException>(() => scope.Call<Task>("EnsureCurrentDeviceAsync", kindle));
        Assert.False(scope.Call<bool>("IsCurrentDevice", kindle));
        Assert.False(scope.Call<bool>("IsCurrentDevice", reconnected));
        Assert.False(scope.Get<Button>("ImportDeviceResourceButton").IsEnabled);
    });

    [Fact]
    public Task DeviceCompatibilityDisablesUnsupportedResources() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var reader = Reader(scope, "GENERIC", new ReaderDeviceProfile());
        var service = new ReaderService { Devices = [reader] };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Call<Task>("OpenDeviceResourcePageAsync", KindleResourceKind.Dictionary);
        await Render();
        Assert.False(scope.Get<Button>("ImportDeviceResourceButton").IsEnabled);
        Assert.False(scope.Get<Button>("DeleteDeviceResourceButton").IsEnabled);
        Assert.False(scope.Get<TextBlock>("DeviceResourcePathText").IsVisible);
        Assert.Contains("暂不支持", scope.Get<TextBlock>("DeviceResourceStatusText").Text);
        Assert.Equal(0, service.ResourceRequests);
        Capture(scope.Window, "device-unsupported-dictionary");
    });

    [Fact]
    public Task DeviceCompatibilityPreservesNoteSourceAndReadOnlyCapabilities() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var kobo = Reader(scope, "KOBO-NOTES", ReaderDeviceProfiles.Kobo);
        var service = new ReaderService
        {
            Devices = [kobo],
            Notes = [new KindleClipping { Id = "kobo:note", BookTitle = "城南旧事", Content = "书中摘录", Type = KindleClippingType.Highlight,
                PairedNote = new KindleClipping { Id = "kobo:paired", Content = "我的批注", Type = KindleClippingType.Note } }]
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        var note = Assert.Single(scope.Window.ReadingMaterials);
        Assert.Equal("Kobo", note.SourceLabel);
        Assert.Equal(kobo.Identity, note.ToRecord().SourceDeviceId);
        Assert.Equal("我的批注", note.Note);
        note.IsSelected = true;
        scope.Call("UpdateReadingMaterialsActionState");
        Assert.False(scope.Get<Button>("DeleteReadingMaterialsButton").IsEnabled);
        scope.Window.ReadingMaterialGroups[0].IsExpanded = true;
        await Render();
        Capture(scope.Window, "device-kobo-notes");

        service.Devices = [];
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        Assert.Empty(scope.Window.ReadingMaterials);
    });

    private static KindleDevice Reader(TestWindow scope, string identity, ReaderDeviceProfile profile) => new()
    {
        RootPath = Path.Combine(scope.Paths.Data, identity), VolumeSerial = identity, Name = profile.DisplayName,
        Profile = profile, IsReady = true
    };

    private static async Task UseReaderServiceAsync(TestWindow scope, ReaderService service)
    {
        await scope.Field<DeviceModelStore>("_deviceModelStore").InitializeAsync();
        scope.Call("RefreshLocalizedReadingMaterialsSourceFilter");
        scope.Set("_kindle", service);
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { AutoConnectDevice = true });
    }

    private sealed class ReaderService : IKindleDeviceService
    {
        public IReadOnlyList<KindleDevice> Devices { get; set; } = [];
        public IReadOnlyList<KindleClipping> Notes { get; init; } = [];
        public Func<KindleDevice, Task<IReadOnlyList<KindleBook>>>? BooksReader { get; init; }
        public int ContentRequests { get; private set; }
        public int ResourceRequests { get; private set; }
        public Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Devices);
        public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default)
        { ContentRequests++; return BooksReader?.Invoke(device) ?? Task.FromResult<IReadOnlyList<KindleBook>>([]); }
        public Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(KindleDevice device, IProgress<KindleScanProgress>? progress = null, CancellationToken cancellationToken = default) => ScanBooksAsync(device, cancellationToken);
        public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(KindleDevice device, KindleResourceKind kind, CancellationToken cancellationToken = default)
        { ContentRequests++; ResourceRequests++; return Task.FromResult<IReadOnlyList<KindleDeviceResource>>([]); }
        public Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(KindleDevice device, CancellationToken cancellationToken = default, int maxItems = int.MaxValue)
        { ContentRequests++; return Task.FromResult(Notes); }
        public Task SendBookAsync(KindleDevice device, BookFile bookFile, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, string? coverOverridePath = null) => throw new NotSupportedException();
        public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExportBookAsync(KindleDevice device, KindleBook book, string destinationDirectory, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendResourceAsync(KindleDevice device, KindleResourceKind kind, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportResourceAsync(KindleDevice device, KindleDeviceResource resource, string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveResourceAsync(KindleDevice device, KindleDeviceResource resource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClippingsAsync(KindleDevice device, IReadOnlyCollection<string> clippingIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
