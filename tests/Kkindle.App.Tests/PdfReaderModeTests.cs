using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class PdfReaderModeTests(SettingsUiSession session)
{
    [Fact]
    public Task ContinuousPagesFollowTheViewportAndDefaultToReadableWidth() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, PdfFixture.Write(scope.Paths.ReaderCache));
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(PdfReaderDisplayMode.Continuous, pdf.DisplayMode);
        Assert.Equal(PdfReaderFitMode.Width, pdf.FitMode);
        Assert.InRange(pdf.PageBounds.Width / pdf.Bounds.Width, 0.92, 1);
        Assert.True(scope.Get<Button>("ReaderFlowButton").IsVisible);
        Assert.True(scope.Get<MenuItem>("ReaderScrollModeItem").IsChecked);
        var repaintedDuringGesture = false;
        for (var tick = 0; tick < 20; tick++)
        {
            pdf.ScrollBy(30);
            await Task.Delay(12);
            repaintedDuringGesture |= Region(pdf)?.Top > 0;
        }
        Assert.True(repaintedDuringGesture, "Continuous input must not postpone rendering until the gesture ends.");
        var beforeKey = pdf.PageBounds.Y;
        var key = new KeyEventArgs { Key = Key.Down };
        scope.Call("MainWindow_KeyDown", scope.Window, key);
        Assert.True(key.Handled);
        Assert.True(pdf.PageBounds.Y < beforeKey);
        await scope.Field<Task>("_readerPdfOutlineTask");
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        pdf.ScrollToTop(0.8);
        await pdf.RefreshViewportAsync();
        Assert.Contains(2, pdf.VisiblePageNumbers);
        Assert.Contains(3, pdf.VisiblePageNumbers);
        Capture(scope.Window, "pdf-continuous-boundary");
        pdf.ScrollBy(pdf.GetPageBounds(2).Height * 0.25);
        await pdf.RefreshViewportAsync();
        Assert.Equal(3, pdf.PageNumber);
        Assert.Equal(3, scope.Field<int>("_readerPdfPage"));
        Assert.Equal("Scanned page", Assert.IsType<ReaderTocRow>(scope.Get<ListBox>("ReaderTocList").SelectedItem).Title);
        Assert.Empty(pdf.PageContent!.Text);
        var position = pdf.CaptureViewState();
        await scope.Call<Task>("CloseReaderAsync");
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]));
        pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(3, pdf.PageNumber);
        Assert.Equal(PdfReaderDisplayMode.Continuous, pdf.DisplayMode);
        Assert.Equal(position, pdf.CaptureViewState());
    });

    [Fact]
    public Task TwoPageSelectionAnnotationsAndSpreadTurnsUseTheCorrectPage() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, PdfFixture.Write(scope.Paths.ReaderCache));
        var originalLayout = scope.Field<AppSettings>("_appSettings").DefaultReaderLayout;
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", PdfReaderDisplayMode.TwoPage);
        await pdf.SetFitModeAsync(PdfReaderFitMode.Page);
        Assert.Equal(new[] { 1, 2 }, pdf.VisiblePageNumbers);
        Assert.True(scope.Get<MenuItem>("ReaderTwoPageModeItem").IsChecked);
        Assert.Equal("1–2 / 4", scope.Call<string>("GetReaderChapterPositionLabel"));
        Assert.True(pdf.GetPageBounds(1).Right < pdf.GetPageBounds(2).Left);
        var rightText = pdf.GetPageContent(2)!.Text;
        var start = rightText.IndexOf("Select this text", StringComparison.Ordinal);
        var range = pdf.GetRangeBounds(2, start, start + 16).Single();
        await ReaderTests.Render();
        var first = pdf.TranslatePoint(new Point(range.Left, range.Center.Y), scope.Window)!.Value;
        var last = pdf.TranslatePoint(new Point(range.Right + 1, range.Center.Y), scope.Window)!.Value;
        scope.Window.MouseDown(first, MouseButton.Left);
        scope.Window.MouseMove(last);
        scope.Window.MouseUp(last, MouseButton.Left);
        Assert.Equal(2, scope.Field<int>("_readerPdfPage"));
        Assert.StartsWith("Select this text", scope.Field<string>("_readerPendingSelection"));
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Right page comment", "wavy", "#000000");
        var annotation = Assert.Single(scope.Window.ReaderAnnotations);
        Assert.Equal("pdf:page:2", annotation.ChapterPath);
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-two-pages");
        await scope.Call<Task>("MoveReaderFooterTocAsync", 1);
        Assert.Equal(new[] { 3, 4 }, pdf.VisiblePageNumbers);
        Assert.False(pdf.CanGoNext);
        await scope.Call<Task>("MoveReaderFooterTocAsync", -1);
        Assert.Equal(new[] { 1, 2 }, pdf.VisiblePageNumbers);
        await scope.Call<Task>("NavigateToReaderAnnotationAsync", annotation);
        Assert.Equal(2, pdf.PageNumber);
        await ReaderTests.Render();
        var savedRange = pdf.GetRangeBounds(annotation.StartOffset, annotation.EndOffset).Single();
        var annotationPoint = pdf.TranslatePoint(savedRange.Center, scope.Window)!.Value;
        scope.Window.MouseDown(annotationPoint, MouseButton.Left);
        scope.Window.MouseUp(annotationPoint, MouseButton.Left);
        Assert.True(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);
        Assert.Equal("Right page comment", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", PdfReaderDisplayMode.SinglePage);
        Assert.Equal(new[] { 2 }, pdf.VisiblePageNumbers);
        Assert.True(scope.Get<MenuItem>("ReaderSinglePageModeItem").IsChecked);
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-single-page");
        await scope.Call<Task>("CloseReaderAsync");
        Assert.Equal(originalLayout, (await new AppSettingsStore(scope.Paths).LoadAsync()).DefaultReaderLayout);
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]));
        pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(PdfReaderDisplayMode.SinglePage, pdf.DisplayMode);
        Assert.Equal(PdfReaderFitMode.Page, pdf.FitMode);
        Assert.Equal(2, pdf.PageNumber);
    });

    [Theory]
    [InlineData(PdfReaderDisplayMode.Continuous)]
    [InlineData(PdfReaderDisplayMode.SinglePage)]
    [InlineData(PdfReaderDisplayMode.TwoPage)]
    public Task PdfViewportHasNoBandsBetweenTheToolbars(PdfReaderDisplayMode mode) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, PdfFixture.Write(scope.Paths.ReaderCache));
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", mode);
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        await pdf.SetZoomAsync(1.4);
        pdf.ScrollBy(120);
        foreach (var theme in new[] { ReaderTheme.Green, ReaderTheme.Night })
        {
            scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = theme });
            await scope.Field<Task>("_readerAppearanceSaveTask");
            await pdf.RefreshViewportAsync();
            await ReaderTests.Render();
            var area = scope.Get<Grid>("ReaderReadingArea");
            using var frame = new RenderTargetBitmap(new PixelSize((int)area.Bounds.Width, (int)area.Bounds.Height));
            frame.Render(area);
            using var encoded = new MemoryStream();
            frame.Save(encoded, PngBitmapEncoderOptions.Default);
            using var pixels = SKBitmap.Decode(encoded.ToArray());
            var expected = ReaderPalette.ToSkia(ReaderPalette.For(theme).Sidebar);
            // The gutter must stay continuous through the old top/bottom
            // margins and the former WebView's bottom-edge cover.
            for (var y = 1; y < pixels.Height - 1; y++)
                Assert.True(pixels.GetPixel(4, y) == expected,
                    $"{mode}/{theme}: unexpected viewport band at y={y}.");
            Assert.Equal(new Rect(area.Bounds.Size), new Rect(pdf.TranslatePoint(default, area)!.Value, pdf.Bounds.Size));
            var page = pdf.PageBounds;
            var paperPoint = pdf.TranslatePoint(new Point(page.Left + 5,
                page.Intersect(new Rect(pdf.Bounds.Size)).Center.Y), area)!.Value;
            Assert.Equal(ReaderPalette.ToSkia(ReaderPalette.For(theme).Page), pixels.GetPixel((int)paperPoint.X, (int)paperPoint.Y));
            var start = pdf.PageContent!.Text.IndexOf("Select this text", StringComparison.Ordinal);
            var inkBounds = pdf.GetRangeBounds(start, start + 16).Single();
            Assert.True(ContainsColor(pixels, inkBounds, ReaderPalette.ToSkia(ReaderPalette.For(theme).Ink)),
                $"{mode}/{theme}: PDF text must remain readable on themed paper.");
            Capture(scope.Window, $"pdf-viewport-{mode}-{theme}");
        }
    });

    [Theory]
    [InlineData(PdfReaderDisplayMode.Continuous)]
    [InlineData(PdfReaderDisplayMode.SinglePage)]
    [InlineData(PdfReaderDisplayMode.TwoPage)]
    public Task RotationKeepsTextAnnotationsAndSavedPositionAligned(PdfReaderDisplayMode mode) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, PdfFixture.Write(scope.Paths.ReaderCache));
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", mode);
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await pdf.SetFitModeAsync(PdfReaderFitMode.Page);
        Assert.True(scope.Get<Button>("ReaderPdfRotateButton").IsVisible);
        var start = pdf.PageContent!.Text.IndexOf("Select this text", StringComparison.Ordinal);
        for (var turn = 1; turn <= 4; turn++)
        {
            await scope.Call<Task>("RotateReaderPdfAsync");
            Assert.Equal(turn * 90 % 360, pdf.Rotation);
            Assert.Equal(2, pdf.PageNumber);
            Assert.Equal(pdf.Rotation, Region(pdf)!.Rotation);
            await ReaderTests.Render();
            var range = pdf.GetRangeBounds(start, start + 16).Single();
            using (var pixels = SKBitmap.Decode(await pdf.CaptureVisiblePageAsync(CancellationToken.None)))
                Assert.True(ContainsColor(pixels, range, SKColors.Black), $"Rotation {pdf.Rotation}: rendered text must match its selection bounds.");
            DragText(scope, pdf, range, pdf.Rotation);
            Assert.Equal("Select this text", scope.Field<string>("_readerPendingSelection"));
            if (turn == 1) await scope.Call<Task>("SaveReaderAnnotationAsync", "Rotating comment", "double", "#000000");
            pdf.ClearSelection();
            var annotation = Assert.Single(scope.Window.ReaderAnnotations);
            var point = pdf.TranslatePoint(pdf.GetRangeBounds(annotation.StartOffset, annotation.EndOffset).Single().Center, scope.Window)!.Value;
            await ReaderTests.Render();
            Capture(scope.Window, $"pdf-rotation-{mode}-{pdf.Rotation}");
            scope.Window.MouseDown(point, MouseButton.Left);
            scope.Window.MouseUp(point, MouseButton.Left);
            Assert.True(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);
            Assert.Equal("Rotating comment", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
            scope.Call("HideReaderAnnotationInputPopup");
            if (turn == 1)
            {
                var state = pdf.CaptureViewState();
                await scope.Call<Task>("CloseReaderAsync");
                await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0],
                    new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]));
                pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
                Assert.Equal(state, pdf.CaptureViewState());
            }
        }
        // The user's rotation composes with the crop and rotation stored in a PDF.
        await scope.Call<Task>("NavigatePdfPageAsync", 4, CancellationToken.None, true);
        await scope.Call<Task>("RotateReaderPdfAsync");
        await ReaderTests.Render();
        var text = pdf.PageContent!.Text;
        var line = text[..text.IndexOf('\r')];
        DragText(scope, pdf, pdf.GetRangeBounds(0, line.Length).Single(), 180);
        Assert.Equal(line, scope.Field<string>("_readerPendingSelection"));
    });

    [Fact]
    public Task ChangingPaperThemePreservesColorFiguresAndCanRestoreTheOriginalPage() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, PdfFixture.Write(scope.Paths.ReaderCache));
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", PdfReaderDisplayMode.SinglePage);
        await pdf.SetFitModeAsync(PdfReaderFitMode.Page);
        var position = pdf.CaptureViewState();
        var point = pdf.PageBounds.TopLeft + new Vector(10, 10);
        using var original = SKBitmap.Decode(await pdf.CaptureVisiblePageAsync(CancellationToken.None));
        var color = original.GetPixel((int)point.X, (int)point.Y);
        foreach (var theme in new[] { ReaderTheme.Green, ReaderTheme.Night, ReaderTheme.Ivory, ReaderTheme.WarmBrown, ReaderTheme.Classic })
        {
            scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = theme });
            await scope.Field<Task>("_readerAppearanceSaveTask");
            await pdf.RefreshViewportAsync();
            await ReaderTests.Render();
            using var pixels = SKBitmap.Decode(await pdf.CaptureVisiblePageAsync(CancellationToken.None));
            Assert.Equal(color, pixels.GetPixel((int)point.X, (int)point.Y));
            Assert.Equal(position, pdf.CaptureViewState());
        }
    });

    [Fact]
    public Task LongPdfShowsItsFirstPageBeforeFullIndexingAndKeepsZoomRenderingBounded() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.WriteLong(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        var clock = Stopwatch.StartNew();
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        var firstPageMs = clock.Elapsed.TotalMilliseconds;
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(400, pdf.PageCount);
        Assert.NotNull(Region(pdf));
        Assert.False(scope.Field<Task>("_readerPdfIndexTask").IsCompleted);
        Assert.InRange(CachedCount(pdf), 1, 4);
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-readable-width");
        await scope.Field<Task>("_readerPdfIndexTask");
        var indexedMs = clock.Elapsed.TotalMilliseconds;
        Assert.True(scope.Field<bool>("_readerPdfIndexReady"));
        Assert.Equal(400, scope.Field<IReadOnlyList<PdfPageText>>("_readerPdfPages").Count(page => page.Text.Length > 0));
        await pdf.SetZoomAsync(8);
        var region = Region(pdf)!;
        Assert.Equal((int)Math.Round(pdf.PageBounds.Width), region.PagePixelWidth);
        Assert.Equal((int)Math.Round(pdf.PageBounds.Height), region.PagePixelHeight);
        Assert.True((long)region.PagePixelWidth * region.PagePixelHeight > 16_000_000);
        Assert.True(region.Width <= pdf.Bounds.Width + 320);
        Assert.True(region.Height <= pdf.Bounds.Height + 320);
        Assert.True((long)region.Width * region.Height < 4_000_000);
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-high-zoom");
        await pdf.SetFitModeAsync(PdfReaderFitMode.Width);
        foreach (var page in new[] { 50, 100, 200, 300, 400 })
            await scope.Call<Task>("NavigatePdfPageAsync", page, CancellationToken.None, false);
        Assert.InRange(CachedCount(pdf), 1, 6);
        await scope.Call<Task>("RefreshReaderWholeSearchAsync", "Page 400", null);
        Assert.Contains(scope.Window.ReaderSearchResults, result => result.PageNumber == 400);

        // Measure the previous blocking path on the same sample for a reviewable comparison.
        var previousMs = await Task.Run(() =>
        {
            var previous = Stopwatch.StartNew();
            using var document = new PdfDocumentService(path);
            for (var page = 1; page <= document.PageCount; page++) document.ReadPage(page);
            document.RenderPage(1);
            return previous.Elapsed.TotalMilliseconds;
        });
        var artifacts = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
            await File.WriteAllTextAsync(Path.Combine(artifacts, "pdf-performance.json"), JsonSerializer.Serialize(new
            {
                Pages = 400, FirstUsablePageMs = firstPageMs, TextIndexCompleteMs = indexedMs,
                PreviousBlockingTextAndRasterMs = previousMs, HighZoomFullPagePixels = (long)region.PagePixelWidth * region.PagePixelHeight,
                HighZoomRenderedPixels = (long)region.Width * region.Height, CachedPagesAfterNavigation = CachedCount(pdf)
            }, new JsonSerializerOptions { WriteIndented = true }));
    });

    [Fact]
    public Task ClosingDuringBackgroundIndexingReleasesThePdfAndRejectsLateUpdates() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, PdfFixture.WriteLong(scope.Paths.ReaderCache));
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        var pendingIndex = scope.Field<Task>("_readerPdfIndexTask");
        var pendingOutline = scope.Field<Task>("_readerPdfOutlineTask");
        await scope.Call<Task>("CloseReaderAsync");
        await Task.WhenAll(pendingIndex, pendingOutline);
        Assert.False(scope.Field<bool>("_readerIsPdf"));
        Assert.Empty(scope.Field<IReadOnlyList<PdfPageText>>("_readerPdfPages"));
        Assert.Equal(0, CachedCount(pdf));
        var path = new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    });

    private static bool ContainsColor(SKBitmap pixels, Rect bounds, SKColor color)
    {
        for (var y = Math.Max(0, (int)bounds.Top); y < Math.Min(pixels.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
            for (var x = Math.Max(0, (int)bounds.Left); x < Math.Min(pixels.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                var pixel = pixels.GetPixel(x, y);
                if (Math.Abs(pixel.Red - color.Red) < 8 && Math.Abs(pixel.Green - color.Green) < 8 && Math.Abs(pixel.Blue - color.Blue) < 8) return true;
            }
        return false;
    }

    private static void DragText(ReaderTestWindow scope, NativePdfReaderHost pdf, Rect range, int rotation)
    {
        var (first, last) = rotation switch
        {
            90 => (new Point(range.Center.X, range.Top), new Point(range.Center.X, range.Bottom + 1)),
            180 => (new Point(range.Right, range.Center.Y), new Point(range.Left - 1, range.Center.Y)),
            270 => (new Point(range.Center.X, range.Bottom), new Point(range.Center.X, range.Top - 1)),
            _ => (new Point(range.Left, range.Center.Y), new Point(range.Right + 1, range.Center.Y))
        };
        first = pdf.TranslatePoint(first, scope.Window)!.Value;
        last = pdf.TranslatePoint(last, scope.Window)!.Value;
        scope.Window.MouseDown(first, MouseButton.Left);
        scope.Window.MouseMove(last);
        scope.Window.MouseUp(last, MouseButton.Left);
    }

    private static PdfRasterRegion? Region(NativePdfReaderHost pdf) => (PdfRasterRegion?)typeof(NativePdfReaderHost)
        .GetMethod("GetRenderedRegion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pdf, [pdf.PageNumber]);
    private static int CachedCount(NativePdfReaderHost pdf) => (int)typeof(NativePdfReaderHost)
        .GetProperty("CachedPageCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pdf)!;
    private static async Task<BookCardViewModel> Open(ReaderTestWindow scope, string path)
    {
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible, scope.Get<TextBlock>("ReaderStatusText").Text);
        await ReaderTests.Render();
        return card;
    }
    private static void Capture(MainWindow window, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var image = window.CaptureRenderedFrame();
        image!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);
}
