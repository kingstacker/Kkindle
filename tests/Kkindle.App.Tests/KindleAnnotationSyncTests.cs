using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task KindleAnnotationSyncMissingDatabaseShowsFailureInsteadOfAgreement(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var scenario = await LoadAnnotationScenarioAsync(scope);
        scenario.Service.AnnotationSnapshot = null;
        scope.Get<ComboBox>("UiLanguageBox").SelectedIndex = language == "en-US" ? 1 : 0;
        await Render();
        var run = scope.Call<Task>("PromptKindleAnnotationSyncIfNeededAsync", scenario.Device, true);
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        var message = scope.Get<TextBlock>("MessageBodyText").Text!;
        Assert.Contains(language == "en-US" ? "No supported Kindle annotation database" : "未找到可同步的 Kindle 批注数据库", message);
        Assert.DoesNotContain("已经一致", message);
        Assert.Equal(0, scenario.Service.AnnotationWriteRequests);
        scope.Get<Button>("MessageOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await run;
        Assert.True(scope.Get<Button>("SyncKindleAnnotationsButton").IsEnabled);
    });

    [Fact]
    public Task KindleAnnotationSyncCancelDoesNotImportOrWrite() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var scenario = await LoadAnnotationScenarioAsync(scope);
        var run = scope.Call<Task>("PromptKindleAnnotationSyncIfNeededAsync", scenario.Device, true);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.False(scope.Get<Button>("SyncKindleAnnotationsButton").IsEnabled);
        scope.Get<Button>("ConfirmationCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await run;
        Assert.Empty(await scope.Field<ReaderDataService>("_readerData").GetAnnotationsAsync(scenario.File.Id));
        Assert.Equal(0, scenario.Service.AnnotationWriteRequests);
        Assert.Equal(1, scenario.Service.AnnotationSnapshotRequests);
        Assert.False(scope.Get<Control>("MessageOverlay").IsVisible);
    });

    [Fact]
    public Task KindleAnnotationSyncConfirmationRechecksAndRefreshesThenReportsNoDifferences() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var scenario = await LoadAnnotationScenarioAsync(scope);
        var run = scope.Call<Task>("PromptKindleAnnotationSyncIfNeededAsync", scenario.Device, true);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        Assert.Contains("导入电脑 1 项", scope.Get<TextBlock>("MessageBodyText").Text);
        Assert.Single(scope.Window.ReadingMaterials, item => item.Source == ReadingMaterialSource.Local);
        Assert.Equal(2, scenario.Service.AnnotationSnapshotRequests);
        scope.Get<Button>("MessageOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await run;

        var second = scope.Call<Task>("PromptKindleAnnotationSyncIfNeededAsync", scenario.Device, true);
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        Assert.Contains("已检查 1 本共同书籍，未发现批注差异", scope.Get<TextBlock>("MessageBodyText").Text);
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Equal(0, scenario.Service.AnnotationWriteRequests);
        scope.Get<Button>("MessageOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await second;
        Assert.Single(await scope.Field<ReaderDataService>("_readerData").GetAnnotationsAsync(scenario.File.Id));
    });

    [Fact]
    public Task KindleAnnotationSyncWriteFailureRefreshesCommittedImportsAndDeviceCache() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var scenario = await LoadAnnotationScenarioAsync(scope, failWrite: true);
        var library = scope.Field<IBookLibraryService>("_library");
        var document = await new EpubReaderPreparationService(scope.Paths).PrepareAsync(library.GetAbsoluteFilePath(scenario.File), scenario.File.Sha256);
        var chapter = document.Chapters.First(path => new XhtmlChapterLoader().Load(path).BodyText.Contains("你好，世界。😀"));
        var body = new XhtmlChapterLoader().Load(chapter).BodyText;
        var start = body.IndexOf("你好，世界。😀", StringComparison.Ordinal);
        await scope.Field<ReaderDataService>("_readerData").SaveAnnotationAsync(new ReaderAnnotation
        {
            BookId = scenario.Book.Id, BookFileId = scenario.File.Id,
            ChapterPath = Path.GetRelativePath(document.RootPath, chapter).Replace('\\', '/'),
            StartOffset = start, EndOffset = start + "你好，世界。😀".Length,
            SelectedText = "你好，世界。😀", Note = "保留电脑端笔记",
            Prefix = body[Math.Max(0, start - 24)..start],
            Suffix = body[(start + "你好，世界。😀".Length)..Math.Min(body.Length, start + "你好，世界。😀".Length + 24)],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        var readsBefore = scenario.Service.NotesRequests;
        var run = scope.Call<Task>("PromptKindleAnnotationSyncIfNeededAsync", scenario.Device, true);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        Assert.Contains("电脑端已更新 1 项", scope.Get<TextBlock>("MessageBodyText").Text);
        Assert.Equal(2, scope.Window.ReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Local));
        Assert.True(scenario.Service.NotesRequests > readsBefore);
        Assert.Equal(1, scenario.Service.AnnotationWriteRequests);
        Assert.Equal("保留电脑端笔记", (await scope.Field<ReaderDataService>("_readerData").GetAnnotationsAsync(scenario.File.Id))
            .Single(item => item.SelectedText == "你好，世界。😀").Note);
        scope.Get<Button>("MessageOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await run;
        Assert.True(scope.Get<Button>("SyncKindleAnnotationsButton").IsEnabled);
    });

    [Fact]
    public Task KindleAnnotationSyncIsUnavailableDuringExportOrDeviceWork() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var scenario = await LoadAnnotationScenarioAsync(scope);
        var sync = scope.Get<Button>("SyncKindleAnnotationsButton");
        Assert.True(sync.IsEffectivelyVisible);
        Assert.True(sync.IsEnabled);
        scope.Get<Button>("ExportReadingMaterialsToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(sync.IsVisible);
        await scope.Call<Task>("PromptKindleAnnotationSyncIfNeededAsync", scenario.Device, true);
        Assert.Equal(0, scenario.Service.AnnotationSnapshotRequests);
        scope.Get<Button>("CancelReadingMaterialsExportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var release = new TaskCompletionSource();
        var operation = scope.Call<Task>("TrackDeviceOperationAsync", (Func<Task>)(() => release.Task));
        Assert.False(sync.IsEnabled);
        release.SetResult();
        await operation;
        Assert.True(sync.IsEnabled);
        scope.Set("_deviceBooksShowingCache", true);
        scope.Call("UpdateKindleAnnotationSyncButtonVisibility");
        Assert.False(sync.IsEnabled);
    });

    private static async Task<AnnotationUiScenario> LoadAnnotationScenarioAsync(TestWindow scope, bool failWrite = false)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3");
        var epub = Path.Combine(scope.Paths.Data, "annotation-fixture.epub");
        await Azw3ReaderService.PrepareEpubAsync(fixture, epub);
        var library = scope.Field<IBookLibraryService>("_library");
        await library.ImportAsync([epub]);
        var book = Assert.Single(await library.SearchAsync());
        var file = Assert.Single(book.Files);
        var quote = failWrite ? "世界。😀" : "你好，世界。😀";
        // These fixture positions and identity are independent of the sync
        // parser; the reconstructed byte oracle is covered by the core tests.
        var start = failWrite ? 1546 : 1537;
        var time = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        const string asin = "ed787037-f332-4e71-a7dd-240cd71acf82";
        const string guid = "native-rich:FEB7174A";
        var identity = new KindleAnnotationBookIdentity($"{asin}-EBOK-{guid}-0", asin, guid);
        var highlight = new KindleAnnotationRecord("ui-device-highlight", identity, 1, KindleClippingType.Highlight,
            start, 1558, time, time, "", "{}", book.Title);
        var device = Reader(scope, "KINDLE-ANNOTATION-UI", ReaderDeviceProfiles.Kindle);
        var deviceBook = new KindleBook { RelativePath = "documents/native.azw3", Title = book.Title, Authors = book.Authors, Format = "azw3" };
        var service = new ReaderService
        {
            Devices = [device], CachedBooks = [deviceBook], BooksReader = _ => Task.FromResult<IReadOnlyList<KindleBook>>([deviceBook]),
            ExportHandler = _ => Task.FromResult(fixture),
            AnnotationSnapshot = new("system/ksdk/.annotations/amzn1.account.test/ksdk_annotation_v1.db", new string('a', 64), [highlight]),
            AnnotationWriter = failWrite ? (_, _, _) => throw new IOException("虚构的 Kindle 写入失败") : null,
            Notes = [new KindleClipping { Id = "ui-highlight", BookTitle = book.Title, Content = quote, Type = KindleClippingType.Highlight, IsReadOnly = true }]
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", true, CancellationToken.None);
        scope.Set("_kindleAnnotationAutoCheckedIdentity", device.Identity);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        return new(device, service, book, file);
    }

    private sealed record AnnotationUiScenario(KindleDevice Device, ReaderService Service, Book Book, BookFile File);
}
