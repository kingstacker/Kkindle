using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("DevicePage", "DeviceBookSearchBox")]
    [InlineData("ReadingMaterialsPage", "ReadingMaterialsSearchBox")]
    public Task FocusedSearchFieldsUseBlackUnderlines(string pageName, string fieldName) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>(pageName), null);
        var field = scope.Get<TextBox>(fieldName);
        field.Focus();
        await Render();
        var border = field.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "PART_BorderElement");
        Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush).Color);
        Assert.Equal(new Avalonia.Thickness(0, 0, 0, 1), border.BorderThickness);
    });

    [Fact]
    public Task BackgroundDevicePollingKeepsDisconnectedMessageStable() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var pending = new TaskCompletionSource<IReadOnlyList<KindleDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ReaderService { DetectionReader = _ => pending.Task };
        await UseReaderServiceAsync(scope, service);
        scope.Call("SetDisconnectedDeviceUi", (object?)null);
        var status = scope.Get<TextBlock>("DevicePageStatusText").Text;
        var empty = scope.Get<TextBlock>("DeviceBookEmptyText").Text;
        var refresh = scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        Assert.False(refresh.IsCompleted);
        Assert.Equal(status, scope.Get<TextBlock>("DevicePageStatusText").Text);
        Assert.Equal(empty, scope.Get<TextBlock>("DeviceBookEmptyText").Text);
        Assert.Equal("无设备书籍", empty);
        pending.SetResult([]);
        await refresh;
        Assert.Equal(status, scope.Get<TextBlock>("DevicePageStatusText").Text);
    });

    [Fact]
    public Task DeviceShelfFindsChineseTitlesByPinyinAndInitials() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ReplaceDeviceBookCards", (object)new[]
        {
            new KindleBook { RelativePath = "documents/mao.azw3", Title = "毛泽东选集", Authors = "毛泽东", Format = "azw3" },
            new KindleBook { RelativePath = "documents/other.azw3", Title = "大明王朝", Authors = "刘和平", Format = "azw3" }
        }, true);
        var search = scope.Get<TextBox>("DeviceBookSearchBox");
        search.Text = "mzdxj";
        await Until(() => scope.Window.VisibleDeviceBooks.Count == 1);
        Assert.Equal("毛泽东选集", Assert.Single(scope.Window.VisibleDeviceBooks).Title);
        search.Text = "maozedongxuanji";
        await Until(() => scope.Window.VisibleDeviceBooks.Count == 1);
        Assert.Equal("毛泽东选集", Assert.Single(scope.Window.VisibleDeviceBooks).Title);
        search.Text = "大明";
        await Until(() => scope.Window.VisibleDeviceBooks.Count == 1 && scope.Window.VisibleDeviceBooks[0].Title == "大明王朝");
        Assert.Equal("大明王朝", Assert.Single(scope.Window.VisibleDeviceBooks).Title);
    });

    [Fact]
    public Task DevicePinyinIndexTracksUpdatedMetadata() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ReplaceDeviceBookCards", (object)new[]
        {
            new KindleBook { RelativePath = "one.azw3", Title = "旧书", Authors = "未知作者", Format = "azw3" }
        }, true);
        scope.Get<TextBox>("DeviceBookSearchBox").Text = "js";
        await Until(() => scope.Window.VisibleDeviceBooks.Count == 1);
        scope.Call("ReplaceDeviceBookCards", (object)new[]
        {
            new KindleBook { RelativePath = "one.azw3", Title = "新书", Authors = "未知作者", Format = "azw3" }
        }, false);
        scope.Get<TextBox>("DeviceBookSearchBox").Text = "xs";
        await Until(() => scope.Window.VisibleDeviceBooks.Count == 1 && scope.Window.VisibleDeviceBooks[0].Title == "新书");
    });

    [Fact]
    public Task UnverifiedDeviceLibraryRequiresExplicitSendAnyway() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var device = Reader(scope, "UNVERIFIED", ReaderDeviceProfiles.Kobo);
        var service = new ReaderService { Devices = [device], ProgressiveBooksReader = _ => Task.FromException<IReadOnlyList<KindleBook>>(new IOException("offline")) };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        var card = scope.Window.ViewModel.Books[0];
        var choice = scope.Call<Task<IReadOnlyList<BookCardViewModel>?>>("ChooseDeviceDuplicatesAsync", device, new[] { card });
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Contains("无法核对", scope.Get<TextBlock>("ConfirmationTitleText").Text);
        scope.Get<Button>("ConfirmationCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(await choice);
    });

    [Fact]
    public Task BatchSendCanSkipBooksThatMayAlreadyBeOnTheDevice() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var device = Reader(scope, "DUPLICATES", ReaderDeviceProfiles.Kobo);
        var alpha = scope.Window.ViewModel.Books.Single(card => card.Title == "Alpha");
        var zulu = scope.Window.ViewModel.Books.Single(card => card.Title == "Zulu");
        var sent = new List<Guid>();
        var service = new ReaderService
        {
            Devices = [device],
            BooksReader = _ => Task.FromResult<IReadOnlyList<KindleBook>>([new KindleBook
                { RelativePath = "existing.epub", Title = alpha.Book.Title, Authors = alpha.Book.Authors, Format = "epub" }]),
            SendHandler = file => { sent.Add(file.BookId); return Task.CompletedTask; }
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        scope.Field<HashSet<Guid>>("_selectedBookIds").UnionWith([alpha.Book.Id, zulu.Book.Id]);
        var task = scope.Call<Task>("SendSelectedBooksToKindleCoreAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Button>("ConfirmationAlternativeButton").IsVisible);
        Assert.Contains("Alpha", scope.Get<TextBlock>("ConfirmationMessageText").Text);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal([zulu.Book.Id], sent);
    });

    [Theory]
    [InlineData("ConfirmationCancelButton", -1)]
    [InlineData("ConfirmationOkButton", 1)]
    [InlineData("ConfirmationAlternativeButton", 2)]
    public Task DuplicatePromptOffersCancelSkipAndSendAll(string buttonName, int expectedCount) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var device = Reader(scope, "DUPLICATE-CHOICE", ReaderDeviceProfiles.Kobo);
        var alpha = scope.Window.ViewModel.Books.Single(card => card.Title == "Alpha");
        var zulu = scope.Window.ViewModel.Books.Single(card => card.Title == "Zulu");
        var service = new ReaderService { Devices = [device], BooksReader = _ => Task.FromResult<IReadOnlyList<KindleBook>>(
            [new KindleBook { RelativePath = "existing.epub", Title = alpha.Book.Title, Authors = alpha.Book.Authors, Format = "epub" }]) };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        var choice = scope.Call<Task<IReadOnlyList<BookCardViewModel>?>>("ChooseDeviceDuplicatesAsync", device, new[] { alpha, zulu });
        await Until(() => scope.Get<Button>("ConfirmationAlternativeButton").IsVisible);
        scope.Get<Button>(buttonName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var result = await choice;
        if (expectedCount < 0) Assert.Null(result);
        else Assert.Equal(expectedCount, result!.Count);
        Assert.False(scope.Get<Button>("ConfirmationAlternativeButton").IsVisible);
    });

    [Fact]
    public Task BatchSendShowsFailuresAndRetriesOnlyFailedBooks() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var device = Reader(scope, "RETRY-SEND", ReaderDeviceProfiles.Kobo);
        var cards = scope.Window.ViewModel.Books.ToArray();
        var failId = cards[0].Book.Id;
        var attempts = new List<Guid>();
        var service = new ReaderService
        {
            Devices = [device],
            SendHandler = file =>
            {
                attempts.Add(file.BookId);
                return file.BookId == failId && attempts.Count(id => id == failId) == 1
                    ? Task.FromException(new IOException("simulated failure")) : Task.CompletedTask;
            }
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        scope.Field<HashSet<Guid>>("_selectedBookIds").UnionWith(cards.Select(card => card.Book.Id));
        var task = scope.Call<Task>("SendSelectedBooksToKindleCoreAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible
            && scope.Get<TextBlock>("ConfirmationTitleText").Text == "部分书籍发送失败");
        Assert.Contains("simulated failure", scope.Get<TextBlock>("ConfirmationMessageText").Text);
        Assert.Contains(cards[0].Title, scope.Get<TextBlock>("ConfirmationMessageText").Text);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible
            && scope.Get<TextBlock>("ConfirmationTitleText").Text!.StartsWith("发送到设备"));
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, attempts.Count(id => id == failId));
        Assert.Equal(1, attempts.Count(id => id != failId));
    });

    [Fact]
    public Task DisconnectedDevicePageExplainsHowToConnectAndCanRetry() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "RETRY", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService();
        await UseReaderServiceAsync(scope, service);
        scope.Call("ShowStage3Page", scope.Get<Grid>("DevicePage"), null);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await Render();
        Assert.True(scope.Get<TextBlock>("DevicePageStatusText").IsEffectivelyVisible);
        Assert.Contains("解锁", scope.Get<TextBlock>("DevicePageStatusText").Text);
        Assert.Equal("无设备书籍", scope.Get<TextBlock>("DeviceBookEmptyText").Text);
        Assert.Null(scope.Window.FindControl<Button>("DeviceBookRetryButton"));
        service.Devices = [device];
        scope.Get<Button>("RefreshDeviceBooksButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Field<IReadOnlyList<KindleDevice>>("_devices").Count == 1);
        Assert.Equal(device.Identity, Assert.Single(scope.Field<IReadOnlyList<KindleDevice>>("_devices")).Identity);
    });

    [Fact]
    public Task CachedDeviceBooksStayReadOnlyUntilTheCurrentSnapshotArrives() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "CACHED", ReaderDeviceProfiles.Kindle);
        var stale = new KindleBook { RelativePath = "documents/stale.azw3", Title = "旧缓存", Format = "azw3" };
        var current = new KindleBook { RelativePath = "documents/current.azw3", Title = "当前书籍", Format = "azw3" };
        var pending = new TaskCompletionSource<IReadOnlyList<KindleBook>>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<KindleScanProgress>? progress = null;
        var service = new ReaderService { Devices = [device], CachedBooks = [stale], ProgressiveBooksReader = value => { progress = value; return pending.Task; } };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await Until(() => progress is not null && scope.Field<bool>("_deviceBooksShowingCache"));
        Assert.Equal("旧缓存", Assert.Single(scope.Window.DeviceBooks).Title);
        Assert.False(scope.Get<ItemsControl>("DeviceBookGridItems").IsHitTestVisible);
        Assert.Contains("上次记录", scope.Get<TextBlock>("DevicePageStatusText").Text);
        var cachedCard = Assert.Single(scope.Window.DeviceBooks);
        await scope.Call<Task>("ExportDeviceBooksToLibraryAsync", (object)new[] { cachedCard });
        scope.Call("DeleteDeviceBookButton_Click", new Button { Tag = cachedCard }, new RoutedEventArgs());
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Equal(0, service.RemovedBooks);
        progress!.Report(new KindleScanProgress(KindleScanStage.Enumerated, [current], [], 0, 1));
        await Until(() => scope.Window.DeviceBooks.Count == 1 && scope.Window.DeviceBooks[0].Title == "当前书籍");
        Assert.True(scope.Field<bool>("_deviceBooksShowingCache"));
        Assert.Equal("当前书籍", Assert.Single(scope.Window.DeviceBooks).Title);
        Assert.False(scope.Get<ItemsControl>("DeviceBookGridItems").IsHitTestVisible);
        Assert.Contains("0 / 1", scope.Get<TextBlock>("DevicePageStatusText").Text);
        pending.SetResult([current]);
        await scope.Field<Task>("_deviceWarmTask");
        Assert.False(scope.Field<bool>("_deviceBooksShowingCache"));
        Assert.True(scope.Get<ItemsControl>("DeviceBookGridItems").IsHitTestVisible);
        Assert.Contains("已读取 1 本书", scope.Get<TextBlock>("DevicePageStatusText").Text);
    });

    [Fact]
    public Task FailedDeviceScanKeepsCachedBooksReadOnlyAndOffersRetry() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "FAILED", ReaderDeviceProfiles.Kindle);
        var attempts = 0;
        var current = new KindleBook { RelativePath = "current.epub", Title = "当前书籍", Format = "epub" };
        var service = new ReaderService
        {
            Devices = [device], CachedBooks = [new KindleBook { RelativePath = "old.epub", Title = "旧书", Format = "epub" }],
            ProgressiveBooksReader = _ => ++attempts == 1
                ? Task.FromException<IReadOnlyList<KindleBook>>(new IOException("disconnected"))
                : Task.FromResult<IReadOnlyList<KindleBook>>([current])
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        Assert.True(scope.Field<bool>("_deviceBooksShowingCache"));
        Assert.False(scope.Get<ItemsControl>("DeviceBookGridItems").IsHitTestVisible);
        Assert.Contains("核对失败", scope.Get<TextBlock>("DevicePageStatusText").Text);
        Assert.Single(scope.Window.DeviceBooks);
        await scope.Call<Task>("RefreshDeviceBooksAsync", CancellationToken.None);
        Assert.False(scope.Field<bool>("_deviceBooksShowingCache"));
        Assert.Equal("当前书籍", Assert.Single(scope.Window.DeviceBooks).Title);
    });

    [Fact]
    public Task DeviceResourcesAndNotesAreLoadedOnlyWhenOpened() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "LAZY", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService { Devices = [device] };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await scope.Field<Task>("_deviceWarmTask");
        Assert.Equal(0, service.ResourceRequests);
        Assert.Equal(0, service.NotesRequests);
        await scope.Call<Task>("OpenDeviceResourcePageAsync", KindleResourceKind.Font);
        Assert.Equal(1, service.ResourceRequests);
        Assert.Equal(0, service.NotesRequests);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Equal(1, service.NotesRequests);
    });

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
            Assert.Equal(language == "zh-CN" ? "设备与发送" : "Devices & delivery", scope.Get<Button>("SettingsKindleButton").Content);
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
    public Task DeviceShelfScansBooksOnceWhenOpenedOrManuallyRefreshed() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var kindle = Reader(scope, "KINDLE", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService
        {
            Devices = [kindle],
            BooksReader = _ => Task.FromResult<IReadOnlyList<KindleBook>>(
            [
                new KindleBook
                {
                    RelativePath = "documents/one.epub",
                    Title = "只扫描一次",
                    Format = "epub"
                }
            ])
        };
        await UseReaderServiceAsync(scope, service);

        await scope.Call<Task>("OpenKindlePageAsync");
        if (scope.Field<Task?>("_deviceWarmTask") is { } warm)
            await warm;

        Assert.Equal(1, service.ScanRequests);
        Assert.Single(scope.Window.DeviceBooks);

        await scope.Call<Task>("RefreshDevicesManuallyAsync");

        Assert.Equal(2, service.ScanRequests);
        Assert.Single(scope.Window.DeviceBooks);
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

    [Fact]
    public Task ReadingMaterialExportSelectsWholeBooksByDefault() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var reader = Reader(scope, "KOBO-EXPORT", ReaderDeviceProfiles.Kobo);
        var service = new ReaderService
        {
            Devices = [reader],
            Notes = [
                new KindleClipping { Id = "kobo:one", BookTitle = "同一本书", Content = "第一条", Type = KindleClippingType.Highlight },
                new KindleClipping { Id = "kobo:two", BookTitle = "同一本书", Content = "第二条", Type = KindleClippingType.Highlight }
            ]
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");

        Assert.Equal(2, scope.Window.ReadingMaterials.Count);
        Assert.Single(scope.Window.ReadingMaterialGroups);
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
        var summary = scope.Get<TextBlock>("ReadingMaterialsSummaryText").Text;
        var status = scope.Get<TextBlock>("ReadingMaterialsStatusText").Text;

        var exportToggle = scope.Get<Button>("ExportReadingMaterialsToggleButton");
        exportToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();

        Assert.True(scope.Get<Border>("ReadingMaterialsExportPanel").IsVisible);
        Assert.True(scope.Get<Border>("ReadingMaterialsSummaryBorder").IsVisible);
        Assert.False(scope.Get<Border>("ReadingMaterialsExportSummaryBorder").IsVisible);
        Assert.False(scope.Get<Button>("SelectAllReadingMaterialsButton").IsVisible);
        Assert.Equal(summary, scope.Get<TextBlock>("ReadingMaterialsSummaryText").Text);
        Assert.Equal(status, scope.Get<TextBlock>("ReadingMaterialsStatusText").Text);
        Assert.DoesNotContain("active", exportToggle.Classes);

        Assert.All(scope.Window.ReadingMaterials, item => Assert.True(item.IsSelected));
        var group = Assert.Single(scope.Window.ReadingMaterialGroups);
        Assert.True(group.IsExportMode);
        Assert.True(group.IsSelected);
        Assert.Contains("当前将导出 2 条", scope.Get<TextBlock>("ReadingMaterialsExportSummaryText").Text);

        group.IsSelected = false;
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
        scope.Call("UpdateReadingMaterialsActionState");
        Assert.Contains("当前将导出 0 条", scope.Get<TextBlock>("ReadingMaterialsExportSummaryText").Text);
        group.IsSelected = true;
        Assert.All(scope.Window.ReadingMaterials, item => Assert.True(item.IsSelected));

        scope.Get<Button>("ExportReadingMaterialsToggleButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeviceDeletionOnFirstConnectionWaitsForInitialSnapshot(bool singleButton) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "KINDLE", ReaderDeviceProfiles.Kindle);
        var book = new KindleBook { RelativePath = "documents/first.azw3", Title = "首次连接删除", Format = "azw3" };
        var pending = new TaskCompletionSource<IReadOnlyList<KindleBook>>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<KindleScanProgress>? scanProgress = null;
        var service = new ReaderService
        {
            Devices = [device],
            ProgressiveBooksReader = progress =>
            {
                scanProgress = progress;
                return pending.Task;
            }
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        await Until(() => scanProgress is not null);
        scope.Call("ReplaceDeviceBookCards", new KindleBook[] { book }, true);
        scope.Call("ShowStage3Page", scope.Get<Grid>("DevicePage"), null);
        var card = Assert.Single(scope.Window.DeviceBooks);
        card.IsSelected = true;
        Task? deletion = null;
        if (singleButton)
            scope.Call("DeleteDeviceBookButton_Click", new Button { Tag = card }, new RoutedEventArgs());
        else
            deletion = scope.Call<Task>("DeleteSelectedDeviceBooksAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.Equal(0, service.RemovedBooks);
        Assert.Single(scope.Window.DeviceBooks);

        // This stale final snapshot used to race the deletion and resurrect the card.
        pending.SetResult([book]);
        if (deletion is not null) await deletion;
        await Until(() => service.RemovedBooks == 1 && scope.Window.DeviceBooks.Count == 0);
        await Render();
        Assert.Empty(scope.Window.VisibleDeviceBooks);
        Assert.Equal("0", scope.Get<TextBlock>("DeviceBookCountText").Text);
        Capture(scope.Window, singleButton ? "device-first-delete-single" : "device-first-delete-selected");
    });

    [Fact]
    public Task DeviceChangeRefreshesWithoutWaitingForIdlePolling() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "FAST", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService { Devices = [device] };
        await UseReaderServiceAsync(scope, service);
        scope.Call("DeviceChangeNotifier_DeviceChanged", null, EventArgs.Empty);
        await Until(() => scope.Field<IReadOnlyList<KindleDevice>>("_devices").Count == 1);
        Assert.Equal(device.Identity, Assert.Single(scope.Field<IReadOnlyList<KindleDevice>>("_devices")).Identity);
        Assert.True(service.DetectionRequests >= 1);
    });

    [Fact]
    public Task DeviceChangeDuringDetectionRunsAnotherPass() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "LATE", ReaderDeviceProfiles.Kindle);
        var first = new TaskCompletionSource<IReadOnlyList<KindleDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ReaderService { DetectionReader = count => count == 1 ? first.Task : Task.FromResult<IReadOnlyList<KindleDevice>>([device]) };
        await UseReaderServiceAsync(scope, service);
        var initial = scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        Assert.Equal(1, service.DetectionRequests);
        scope.Call("DeviceChangeNotifier_DeviceChanged", null, EventArgs.Empty);
        await Until(() => scope.Field<bool>("_deviceChangePending"));
        scope.Call("DeviceChangeDebounceTimer_Tick", null, EventArgs.Empty);
        Assert.True(scope.Field<bool>("_deviceChangePending"));
        Assert.True(scope.Field<Avalonia.Threading.DispatcherTimer>("_deviceChangeDebounceTimer").IsEnabled);
        first.SetResult([]);
        await initial;
        await Until(() => scope.Field<IReadOnlyList<KindleDevice>>("_devices").Count == 1);
        Assert.True(service.DetectionRequests >= 2);
    });

    [Fact]
    public Task DeviceChangeDoesNotReconnectAManuallyDisconnectedDevice() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var device = Reader(scope, "EJECTED", ReaderDeviceProfiles.Kindle);
        var service = new ReaderService { Devices = [device] };
        await UseReaderServiceAsync(scope, service);
        scope.Set("_manuallyDisconnectedDeviceId", device.Identity);
        scope.Call("DeviceChangeNotifier_DeviceChanged", null, EventArgs.Empty);
        await Until(() => !scope.Field<bool>("_deviceChangePending") && service.DetectionRequests == 0);
        await Task.Delay(500);
        Assert.Empty(scope.Field<IReadOnlyList<KindleDevice>>("_devices"));
        Assert.Equal(0, service.DetectionRequests);
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
        public IReadOnlyList<KindleBook> CachedBooks { get; init; } = [];
        public IReadOnlyList<KindleClipping> Notes { get; init; } = [];
        public Func<KindleDevice, Task<IReadOnlyList<KindleBook>>>? BooksReader { get; init; }
        public Func<IProgress<KindleScanProgress>?, Task<IReadOnlyList<KindleBook>>>? ProgressiveBooksReader { get; init; }
        public Func<int, Task<IReadOnlyList<KindleDevice>>>? DetectionReader { get; init; }
        public Func<BookFile, Task>? SendHandler { get; init; }
        public int DetectionRequests { get; private set; }
        public int RemovedBooks { get; private set; }
        public int ContentRequests { get; private set; }
        public int ScanRequests { get; private set; }
        public int ResourceRequests { get; private set; }
        public int NotesRequests { get; private set; }
        public Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
        {
            DetectionRequests++;
            return DetectionReader?.Invoke(DetectionRequests) ?? Task.FromResult(Devices);
        }
        public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default)
        { ContentRequests++; ScanRequests++; return BooksReader?.Invoke(device) ?? Task.FromResult<IReadOnlyList<KindleBook>>([]); }
        public Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(KindleDevice device, IProgress<KindleScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ContentRequests++;
            ScanRequests++;
            return ProgressiveBooksReader?.Invoke(progress)
                ?? BooksReader?.Invoke(device)
                ?? Task.FromResult<IReadOnlyList<KindleBook>>([]);
        }
        public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) => Task.FromResult(CachedBooks);
        public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(KindleDevice device, KindleResourceKind kind, CancellationToken cancellationToken = default)
        { ContentRequests++; ResourceRequests++; return Task.FromResult<IReadOnlyList<KindleDeviceResource>>([]); }
        public Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(KindleDevice device, CancellationToken cancellationToken = default, int maxItems = int.MaxValue)
        { ContentRequests++; NotesRequests++; return Task.FromResult(Notes); }
        public Task SendBookAsync(KindleDevice device, BookFile bookFile, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, string? coverOverridePath = null) => SendHandler?.Invoke(bookFile) ?? throw new NotSupportedException();
        public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
        { RemovedBooks++; return Task.CompletedTask; }
        public Task<string> ExportBookAsync(KindleDevice device, KindleBook book, string destinationDirectory, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendResourceAsync(KindleDevice device, KindleResourceKind kind, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportResourceAsync(KindleDevice device, KindleDeviceResource resource, string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveResourceAsync(KindleDevice device, KindleDeviceResource resource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClippingsAsync(KindleDevice device, IReadOnlyCollection<string> clippingIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
