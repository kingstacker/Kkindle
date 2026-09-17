using System.Reflection;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using Kkindle.TestFixtures;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderStabilityTests(SettingsUiSession session) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kkindle-reader-stability", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PdfCanNavigateAgainAfterStoppingOrCancellingItsPreviousSession(bool cancelSession) => Run(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        using var host = new NativePdfReaderHost();
        host.Measure(new Size(720, 540));
        host.Arrange(new Rect(0, 0, 720, 540));
        var path = PdfFixture.Write(_directory);
        Assert.Equal(4, await host.PrepareDocumentAsync(path, cancellation.Token));
        Assert.True(await host.NavigateAsync(new Uri(path)));

        if (cancelSession) cancellation.Cancel();
        else host.Stop();

        Assert.True(await host.NavigateAsync(new Uri(new Uri(path).AbsoluteUri + "#page=2")), host.LastError);
        Assert.Equal(2, host.PageNumber);
        Assert.Contains("Chapter Two", host.PageContent!.Text);
    });

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    public Task RestoringAFragmentKeepsItsSectionVisibleAfterResizing(int flow, bool spread, bool vertical) => Run(async () =>
    {
        var settings = new ReaderLayoutSettings(FlowMode: flow, TwoPageMode: spread, VerticalWriting: vertical);
        using var host = await LoadChapter(settings);
        var layout = Layout(host);
        var fragment = layout.FragmentPages.First(pair => pair.Value > 3 && (!spread || pair.Value % 2 == 1)).Key;
        var offset = Content(host).FragmentTextOffsets[fragment];

        await host.Configure(settings, 0, fragment, restoreFromProgress: true, showVerticalDebugBoxes: false);
        AssertTextIsVisible(host, offset);
        if (spread) Assert.Equal(0, host.CurrentPage % 2);

        host.Measure(new Size(660, 510));
        host.Arrange(new Rect(0, 0, 660, 510));
        await ReaderTests.Render();
        AssertTextIsVisible(host, offset);
        if (spread) Assert.Equal(0, host.CurrentPage % 2);
    });

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    public Task ChangingParagraphIndentKeepsTheCurrentTextVisible(int flow, bool spread, bool vertical) => Run(async () =>
    {
        var settings = new ReaderLayoutSettings(FlowMode: flow, TwoPageMode: spread, VerticalWriting: vertical);
        using var host = await LoadChapter(settings);
        var offset = MoveToMiddle(host);
        var reloaded = NextNavigation(host);

        await host.Configure(settings with { ParagraphIndent = true }, host.GetScrollState().Position, null,
            restoreFromProgress: false, showVerticalDebugBoxes: false);
        Assert.True(await reloaded.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertTextIsVisible(host, offset);
    });

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    public Task ChangingPresentationOrScrollFontSizeKeepsTheCurrentTextVisible(int oldFlow, int newFlow, bool vertical) => Run(async () =>
    {
        var settings = new ReaderLayoutSettings(FlowMode: oldFlow);
        using var host = await LoadChapter(settings);
        var offset = MoveToMiddle(host);

        await host.Configure(settings with { FlowMode = newFlow, VerticalWriting = vertical, FontScale = 1.8 },
            host.GetScrollState().Position, null, restoreFromProgress: false, showVerticalDebugBoxes: false);

        AssertTextIsVisible(host, offset);
    });

    [Fact]
    public Task RapidSettingsChangesDuringIndentReloadKeepTheLatestLayoutAndPosition() => Run(async () =>
    {
        var settings = new ReaderLayoutSettings();
        using var host = await LoadChapter(settings);
        var offset = MoveToMiddle(host);
        var first = host.Configure(settings with { ParagraphIndent = true }, 0, null, false, false);
        var latest = settings with { FlowMode = 0, FontScale = 1.8 };
        var second = host.Configure(latest, 0, null, false, false);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(host.IsPaginated);
        Assert.False(Layout(host).Options.ParagraphIndent);
        Assert.Equal(16f * (float)latest.FontScale, Layout(host).Options.BaseFontSize);
        AssertTextIsVisible(host, offset);
    });

    [Fact]
    public Task ResizingDuringNavigationUsesTheFinalViewportBeforeReportingSuccess() => Run(async () =>
    {
        using var host = await LoadChapter(new());
        var loaded = NextNavigation(host);
        host.Navigate(host.Source!);
        host.Measure(new Size(650, 510));
        host.Arrange(new Rect(0, 0, 650, 510));

        Assert.True(await loaded.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(650, Layout(host).Options.ViewportWidth);
        Assert.Equal(510, Layout(host).Options.ViewportHeight);
    });

    [Fact]
    public Task ScrollModeRecognizesTheSameBookmarkBeforeAndAfterAPositionRefresh() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var settings = new ReaderLayoutSettings(FlowMode: 0);
        using var host = await LoadChapter(settings);
        MoveToMiddle(host);
        var path = host.Source!.LocalPath;
        scope.Set("_readerLayout", settings);
        scope.Set("_readerBookFile", new BookFile { Id = Guid.NewGuid(), Format = "epub" });
        scope.Set("_readerDocument", new EpubReaderDocument(_directory, [path], [], []));
        scope.Set("_readerActiveHost", host);
        try
        {
            scope.Window.ReaderBookmarks.Add(new ReaderBookmark
            {
                ChapterPath = Path.GetFileName(path), FlowMode = 0,
                ScrollPosition = (int)Math.Round(host.GetScrollState().Position)
            });
            await scope.Call<Task>("UpdateReaderBookmarkIndicatorAsync");
            Assert.True(scope.Get<Control>("ReaderBookmarkCornerMarker").IsVisible);
            await scope.Call<Task>("UpdateReaderScrollStateAsync", host);
            scope.Call("UpdateReaderBookmarkIndicatorFromTrackedLocation");
            Assert.True(scope.Get<Control>("ReaderBookmarkCornerMarker").IsVisible);
        }
        finally
        {
            scope.Set("_readerActiveHost", null);
            scope.Set("_readerBookFile", null);
            scope.Set("_readerDocument", null);
        }
    });

    [Fact]
    public Task CancellingReaderSetupStopsInitializationBeforeStartingSessionTimers() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var cancellation = new CancellationTokenSource();
        scope.Set("_readerTtsEnvironmentChecked", false);
        scope.Set("_readerTtsEnvironmentTask", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var initializing = scope.Call<Task>("InitializeReaderInteractionAsync",
            new EpubReaderDocument(scope.Paths.ReaderCache, [], [], []), cancellation.Token);
        Assert.False(initializing.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(scope.Field<Avalonia.Threading.DispatcherTimer?>("_readerStatsTimer")?.IsEnabled ?? false);
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 2)]
    public Task PersistedTextPositionSurvivesNewHostModeFontAndViewport(int oldMode, int newMode) => Run(async () =>
    {
        var paths = new AppPaths(Path.Combine(_directory, "app"));
        var data = new ReaderDataService(paths);
        await data.InitializeAsync();
        var fileId = Guid.NewGuid();
        ReaderContentPosition anchor;
        using (var original = await LoadChapter(Mode(oldMode)))
        {
            MoveToMiddle(original);
            if (oldMode == 0) original.SeekToPixelScroll(original.GetScrollState().Position + 135);
            anchor = Assert.IsType<ReaderContentPosition>(original.CaptureContentPosition());
            await data.SaveProgressAsync(new ReaderProgressRow(Guid.NewGuid(), fileId, "chapter.xhtml", "section-0", 0,
                (int)original.GetScrollState().Position, 50, original.IsPaginated ? 1 : 0, DateTimeOffset.UtcNow)
            { ContentPosition = anchor });
        }

        var saved = Assert.IsType<ReaderProgressRow>(await new ReaderDataService(paths).GetProgressAsync(fileId));
        var settings = Mode(newMode) with { FontScale = 1.6, ParagraphIndent = true };
        using var reopened = await LoadChapter(Mode(oldMode));
        reopened.Measure(new Size(650, 610));
        reopened.Arrange(new Rect(0, 0, 650, 610));
        await reopened.Configure(settings, saved.ScrollPosition, saved.Fragment, true, false, saved.ContentPosition);

        AssertTextIsVisible(reopened, anchor.TextOffset);
        Assert.Equal(settings, Field<ReaderLayoutSettings>(reopened, "_settings"));
        // A later viewport reflow must not replay the stale section fragment.
        reopened.Measure(new Size(680, 570));
        reopened.Arrange(new Rect(0, 0, 680, 570));
        await ReaderTests.Render();
        AssertTextIsVisible(reopened, anchor.TextOffset);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task RepeatedImagesKeepTheirOwnPositionAcrossModesAndCacheRelocation(int newMode) => Run(async () =>
    {
        var path = WriteImageChapter(_directory);
        ReaderContentPosition anchor;
        using (var original = await LoadNativeChapter(path, Mode(2)))
        {
            original.SeekToBoundary(true);
            Assert.Equal(3, original.CurrentPage);
            anchor = Assert.IsType<ReaderContentPosition>(original.CaptureContentPosition());
            Assert.Equal(3, anchor.ImageIndex);
            Assert.Equal(-1, anchor.TextOffset);
            Assert.True(string.IsNullOrWhiteSpace(original.BodyText));
        }
        var movedPath = WriteImageChapter(Path.Combine(_directory, "another-cache"));
        using var reopened = await LoadNativeChapter(movedPath, Mode(newMode));
        reopened.RestoreContentPosition(anchor);
        var page = Layout(reopened).Pages.SelectMany(item => item.Images.Select(image => (item, image))).ElementAt(3).item;
        if (reopened.IsPaginated)
            Assert.InRange(page.Index, reopened.CurrentPage, reopened.CurrentPage + (newMode == 3 ? 1 : 0));
        else
            Assert.InRange(page.Index * reopened.Bounds.Height - reopened.GetScrollState().Position, -reopened.Bounds.Height, reopened.Bounds.Height);
        Assert.True(reopened.IsContentPositionVisible(anchor));
        Assert.True(string.IsNullOrWhiteSpace(reopened.BodyText));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public Task ImageAnchorFollowsItsImageWhenPrecedingTextRepaginates(int newMode) => Run(async () =>
    {
        var path = WriteImageChapter(_directory);
        var paragraph = "<p>" + string.Concat(Enumerable.Repeat("前文会因排版变化而改变页数，但图片仍是同一张。", 120)) + "</p>";
        await File.WriteAllTextAsync(path, "<html><body>" +
            string.Concat(Enumerable.Repeat(paragraph + "<p><img src='page.png'/></p>", 3)) + "</body></html>");
        ReaderContentPosition anchor;
        string originalText;
        using (var original = await LoadNativeChapter(path, Mode(2)))
        {
            original.RestoreContentPosition(new ReaderContentPosition { ImageIndex = 2 });
            anchor = Assert.IsType<ReaderContentPosition>(original.CaptureContentPosition());
            Assert.Equal(2, anchor.ImageIndex);
            originalText = original.BodyText!;
        }
        using var reopened = await LoadNativeChapter(path, Mode(newMode) with { FontScale = 0.8 });
        reopened.RestoreContentPosition(anchor);
        var (page, image) = Layout(reopened).Pages.SelectMany(item => item.Images.Select(image => (item, image))).ElementAt(2);
        Assert.NotEqual(anchor.PageIndex, page.Index);
        Assert.Equal(originalText, reopened.BodyText);
        if (reopened.IsPaginated)
            Assert.InRange(page.Index, reopened.CurrentPage, reopened.CurrentPage + (newMode == 3 ? 1 : 0));
        else
        {
            var top = page.Index * reopened.Bounds.Height + image.Rect.Top - reopened.GetScrollState().Position;
            Assert.True(top < reopened.Bounds.Height && top + image.Rect.Height > 0);
        }
    });

    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(0, 1, false)]
    [InlineData(2, 2, true)]
    public Task ClosingAndReopeningBookRestoresPersistedContentAndBookmarks(int oldMode, int newMode, bool imageOnly) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = imageOnly ? WriteImageChapter(_directory) : await WriteTextChapter();
        var epub = Path.Combine(_directory, "position.epub");
        using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
        {
            WriteEntry("mimetype", "application/epub+zip");
            WriteEntry("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            WriteEntry("content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0' unique-identifier='book'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='book'>positions</dc:identifier><dc:title>Position restore</dc:title></metadata><manifest><item id='chapter' href='chapter.xhtml' media-type='application/xhtml+xml'/><item id='image' href='page.png' media-type='image/png'/></manifest><spine><itemref idref='chapter'/></spine></package>");
            WriteEntry("chapter.xhtml", await File.ReadAllTextAsync(path));
            if (imageOnly) archive.CreateEntryFromFile(Path.Combine(_directory, "page.png"), "page.png");
            void WriteEntry(string name, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        var library = scope.Field<IBookLibraryService>("_library");
        await library.ImportAsync([epub]);
        var book = Assert.Single(await library.SearchAsync());
        var file = Assert.Single(book.Files);
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { DefaultReaderLayout = Mode(oldMode) });
        scope.Set("_readerTtsEnvironmentChecked", true);
        await scope.Call<Task>("OpenEpubReaderAsync", card, file, library.GetAbsoluteFilePath(file), true).WaitAsync(TimeSpan.FromSeconds(30));
        await ReaderTests.Render();
        var original = scope.Field<NativeReaderHost>("_readerActiveHost");
        if (imageOnly)
        {
            // The third image must have its own anchor despite having no text.
            Assert.True(original.RestoreContentPosition(new ReaderContentPosition { ImageIndex = 2, PageIndex = 2 }));
            Assert.Equal(2, original.CurrentPage);
        }
        else MoveToMiddle(original);
        var expected = Assert.IsType<ReaderContentPosition>(original.CaptureContentPosition());
        await scope.Call<Task>("ToggleReaderBookmarkAsync");
        var bookmark = Assert.Single(scope.Window.ReaderBookmarks);
        Assert.NotNull(bookmark.ContentPosition);
        await scope.Call<Task>("CloseReaderAsync");

        var saved = Assert.IsType<ReaderProgressRow>(await new ReaderDataService(scope.Paths).GetProgressAsync(file.Id));
        Assert.Equal(expected.TextOffset, saved.ContentPosition!.TextOffset);
        Assert.Equal(expected.ImageIndex, saved.ContentPosition.ImageIndex);
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with
        { DefaultReaderLayout = Mode(newMode) with { FontScale = 1.6 } });
        scope.Window.Width = 1110;
        scope.Window.Height = 760;
        await ReaderTests.Render();
        await scope.Call<Task>("OpenEpubReaderAsync", card, file, library.GetAbsoluteFilePath(file), true).WaitAsync(TimeSpan.FromSeconds(30));
        await ReaderTests.Render();
        var reopened = scope.Field<NativeReaderHost>("_readerActiveHost");
        if (imageOnly) Assert.Equal(2, reopened.CurrentPage);
        else AssertTextIsVisible(reopened, expected.TextOffset);
        Assert.Equal(newMode == 2, reopened.Vertical);
        Assert.True(scope.Get<Control>("ReaderBookmarkCornerMarker").IsVisible);

        reopened.SeekToBoundary(false);
        await scope.Call<Task>("UpdateReaderBookmarkIndicatorAsync");
        Assert.False(scope.Get<Control>("ReaderBookmarkCornerMarker").IsVisible);
        await scope.Call<Task>("NavigateToReaderBookmarkAsync", bookmark);
        if (imageOnly) Assert.Equal(2, reopened.CurrentPage);
        else AssertTextIsVisible(reopened, expected.TextOffset);
        Assert.True(scope.Get<Control>("ReaderBookmarkCornerMarker").IsVisible);
        if (imageOnly)
        {
            // All four images use the same file, and legacy numeric positions
            // are all zero. Adding another image must create a second bookmark.
            reopened.SeekToBoundary(true);
            await scope.Call<Task>("ToggleReaderBookmarkAsync");
            Assert.Equal(2, scope.Window.ReaderBookmarks.Count);
        }
        await scope.Call<Task>("CloseReaderAsync");
    });

    [Fact]
    public Task LegacyRatioFallbackDoesNotTreatPixelsAsTextOffsets() => Run(async () =>
    {
        using var host = await LoadChapter(Mode(2));
        await host.Configure(Mode(2), 25920, null, true, false, legacyChapterRatio: 0.5);
        Assert.InRange(host.CurrentPage, host.PageCount / 2 - 1, host.PageCount / 2 + 1);
        Assert.NotNull(host.CaptureContentPosition());
    });

    private static ReaderLayoutSettings Mode(int mode) => new(
        FlowMode: mode == 0 ? 0 : 1, VerticalWriting: mode == 2, TwoPageMode: mode == 3);

    [Fact]
    public Task LegacyVerticalProgressKeepsItsExactCharacterOffset() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var settings = Mode(2);
        using var host = await LoadChapter(settings);
        var offset = MoveToMiddle(host);
        var expectedPage = host.CurrentPage;
        scope.Set("_readerLayout", settings);
        scope.Set("_readerDocument", new EpubReaderDocument(_directory, [host.Source!.LocalPath], [], []));
        scope.Set("_readerActiveHost", host);
        scope.Set("_readerRestoredProgress", new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "chapter.xhtml", null,
            0, offset, offset * 100d / host.BodyText!.Length, 1, DateTimeOffset.UtcNow));
        try
        {
            host.SeekToBoundary(false);
            await scope.Call<Task>("ConfigureReaderHostCoreAsync", host, CancellationToken.None);
            Assert.Equal(expectedPage, host.CurrentPage);
            AssertTextIsVisible(host, offset);
        }
        finally
        {
            scope.Set("_readerActiveHost", null);
            scope.Set("_readerDocument", null);
        }
    });

    private string WriteImageChapter(string directory)
    {
        Directory.CreateDirectory(directory);
        using var bitmap = new SKBitmap(720, 960);
        bitmap.Erase(SKColors.Beige);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using (var output = File.Create(Path.Combine(directory, "page.png"))) png.SaveTo(output);
        var path = Path.Combine(directory, "images.xhtml");
        File.WriteAllText(path, "<html><body>" + string.Concat(Enumerable.Repeat("<p><img src='page.png'/></p>", 4)) + "</body></html>");
        return path;
    }

    private async Task<string> WriteTextChapter()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "chapter.xhtml");
        var body = string.Concat(Enumerable.Range(0, 36).Select(index =>
            $"<h2 id='section-{index}'>第{index}节</h2><p>" +
            string.Concat(Enumerable.Repeat("春风吹过山林，读书应当记住眼前这一行文字。", 24)) + "</p>"));
        await File.WriteAllTextAsync(path, "<html><body>" + body + "</body></html>");
        return path;
    }

    private async Task<NativeReaderHost> LoadChapter(ReaderLayoutSettings settings)
    {
        var host = await LoadNativeChapter(await WriteTextChapter(), settings);
        Assert.True(host.PageCount > 8);
        return host;
    }

    private static async Task<NativeReaderHost> LoadNativeChapter(string path, ReaderLayoutSettings settings)
    {
        var host = new NativeReaderHost();
        host.Measure(new Size(720, 540));
        host.Arrange(new Rect(0, 0, 720, 540));
        await host.Configure(settings, 0, null, false, false);
        var loaded = NextNavigation(host);
        host.Navigate(new Uri(path));
        Assert.True(await loaded.WaitAsync(TimeSpan.FromSeconds(10)));
        return host;
    }

    private static Task<bool> NextNavigation(NativeReaderHost host)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ReaderNavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            host.NavigationCompleted -= handler;
            completion.TrySetResult(args.IsSuccess);
        };
        host.NavigationCompleted += handler;
        return completion.Task;
    }

    private static int MoveToMiddle(NativeReaderHost host)
    {
        var offset = Layout(host).Pages[host.PageCount / 2].Runs.First(run => run.TextLength > 0).TextStart;
        host.ScrollToOffset(offset);
        AssertTextIsVisible(host, offset);
        return offset;
    }

    private static void AssertTextIsVisible(NativeReaderHost host, int offset)
    {
        var layout = Layout(host);
        var page = layout.GetPageIndexOfOffset(offset);
        Assert.True(page > 0, $"The fixture must target the middle of the chapter, got page {page}.");
        if (host.IsPaginated)
        {
            var spread = Field<ReaderLayoutSettings>(host, "_settings").TwoPageMode && !host.Vertical;
            Assert.InRange(page, host.CurrentPage, host.CurrentPage + (spread ? 1 : 0));
        }
        else
        {
            var bounds = layout.GetCharRect(page, offset);
            Assert.NotNull(bounds);
            var y = page * host.Bounds.Height + bounds.Value.Top - host.GetScrollState().Position;
            Assert.InRange(y, -2, host.Bounds.Height);
        }
    }

    private static ChapterLayout Layout(NativeReaderHost host) => Field<ChapterLayout>(host, "_layout");
    private static ChapterContent Content(NativeReaderHost host) => Field<ChapterContent>(host, "_content");
    private static T Field<T>(NativeReaderHost host, string name) =>
        (T)typeof(NativeReaderHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
