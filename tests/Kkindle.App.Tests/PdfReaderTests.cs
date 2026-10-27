using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class PdfReaderTests(SettingsUiSession session)
{
    [Fact]
    public Task ReopeningPdfClearsOldTocAndRevealsOnlyTheRestoredPage() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        await pdf.SetZoomAsync(2);
        pdf.ScrollToTop(0.45);
        var saved = pdf.CaptureViewState();
        await scope.Call<Task>("CloseReaderAsync");
        var list = scope.Get<ListBox>("ReaderTocList");
        list.ItemsSource = Enumerable.Range(0, 80).Select(index => new ReaderTocRow(
            new EpubReaderNavigationItem("旧目录 " + index, "file:///old#" + index, index), false, false)).ToArray();
        var firstPaintWasHidden = false;
        var root = scope.Get<Control>("ReaderRoot");
        root.PropertyChanged += (_, e) =>
        {
            if (e.Property != Control.IsVisibleProperty || !root.IsVisible) return;
            Assert.Empty(list.Items);
            var loadingHost = scope.Field<NativePdfReaderHost>("_readerActiveHost");
            loadingHost.NavigationCompleted += (_, _) => firstPaintWasHidden = loadingHost.Opacity == 0;
        };
        var path = new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]);
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], path);
        pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.True(firstPaintWasHidden);
        Assert.Equal(1, pdf.Opacity);
        Assert.Equal(saved, pdf.CaptureViewState());
        await scope.Field<Task>("_readerPdfOutlineTask");
        await ReaderTests.Render();
        var first = list.GetVisualDescendants().OfType<ListBoxItem>().First(item => item.DataContext is ReaderTocRow row && row.Title == "Cover");
        Assert.InRange(first.TranslatePoint(default, list)!.Value.Y, 0, 20);
        AssertCurrent(scope, "Chapter Two");
        Capture(scope.Window, "pdf-reopen-restored");
    });

    [Fact]
    public Task PdfOpensWithFirstPageCoverAndFollowsItsNestedOutline() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(4, pdf.PageCount);
        Assert.Equal(4, scope.Field<IReadOnlyList<PdfPageText>>("_readerPdfPages").Count);
        Assert.NotNull(card.CoverImage);
        AssertCurrent(scope, "Cover");
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        AssertCurrent(scope, "Chapter Two");
        var toc = scope.Field<IReadOnlyList<EpubReaderNavigationItem>>("_readerTocItems");
        Assert.Equal(1, toc[2].Level);
        Assert.True(await scope.Call<Task<bool>>("NavigateToReaderItemAsync", toc[2], CancellationToken.None, ReaderNavigationIntent.Toc, null));
        AssertCurrent(scope, "Nested section");
        await scope.Call<Task>("NavigatePdfPageAsync", 3, CancellationToken.None, true);
        Assert.Empty(pdf.PageContent!.Text);
        AssertCurrent(scope, "Scanned page");
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-scan");
        await scope.Call<Task>("CloseReaderAsync");
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]));
        Assert.Equal(3, scope.Field<NativePdfReaderHost>("_readerActiveHost").PageNumber);
        await scope.Field<Task>("_readerPdfOutlineTask");
        AssertCurrent(scope, "Scanned page");
    });

    [Fact]
    public Task DraggingDiagramLabelsKeepsCopiedTextAndSavedAnnotationWithinOneColumn() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, path: PdfFixture.WriteDiagramLabels(scope.Paths.ReaderCache));
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        var text = pdf.PageContent!.Text;
        var labelOffsets = new[] { text.IndexOf("Warm", StringComparison.Ordinal), text.IndexOf("Cold", StringComparison.Ordinal), text.LastIndexOf("Warm", StringComparison.Ordinal) };
        var airOffsets = new[] { text.IndexOf("air", StringComparison.Ordinal), text.IndexOf("air", text.IndexOf("air", StringComparison.Ordinal) + 3, StringComparison.Ordinal), text.LastIndexOf("air", StringComparison.Ordinal) };
        for (var column = 0; column < 3; column++)
        foreach (var reverse in new[] { false, true })
        {
            scope.Call("HideReaderSelectionPopup");
            pdf.ClearSelection();
            await ReaderTests.Render();
            var top = pdf.GetRangeBounds(labelOffsets[column], labelOffsets[column] + 4).Single();
            var bottom = pdf.GetRangeBounds(airOffsets[column], airOffsets[column] + 3).Single();
            var first = pdf.TranslatePoint(new Point(top.Left + 0.1, top.Center.Y), scope.Window)!.Value;
            var last = pdf.TranslatePoint(new Point(bottom.Right - 0.1, bottom.Center.Y), scope.Window)!.Value;
            scope.Window.MouseDown(reverse ? last : first, MouseButton.Left);
            scope.Window.MouseMove(reverse ? first : last);
            scope.Window.MouseUp(reverse ? first : last, MouseButton.Left);
            await ReaderTests.Render();
            Capture(scope.Window, $"pdf-diagram-label-{column}-{reverse}");
            Assert.True(scope.Field<string?>("_readerPendingSelection") is not null,
                $"Missing selection in column {column}, reverse={reverse}; first={first}, last={last}, source={text}");
            Assert.Equal(column == 1 ? "Cold air" : "Warm air",
                PdfTextSelectionLayout.NormalizeCopy(scope.Field<string>("_readerPendingSelection")));
            if (column != 2 || !reverse)
            {
                await scope.Call<Task>("PerformReaderSelectionCopyAsync");
                Assert.Equal(column == 1 ? "Cold air" : "Warm air", await scope.Window.Clipboard!.TryGetTextAsync());
            }
        }
        Capture(scope.Window, "pdf-diagram-label-selection");
        await scope.Call<Task>("SaveReaderAnnotationAsync", "One diagram label", null, null);
        var annotation = Assert.Single(scope.Window.ReaderAnnotations);
        var ranges = PdfTextRanges.Read(annotation.Fragment, annotation.StartOffset, annotation.EndOffset);
        Assert.Equal(7, ranges.Sum(range => range.End - range.Start));
        Assert.Equal("Warm air", PdfTextSelectionLayout.NormalizeCopy(annotation.SelectedText));
    });

    [Fact]
    public Task TableDragCopiesOnlyTheSelectedColumnAndSavesItsExactRanges() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, path: PdfFixture.WriteTable(scope.Paths.ReaderCache));
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        var text = pdf.PageContent!.Text;
        var start = text.IndexOf("Positive ramp", StringComparison.Ordinal);
        var end = text.IndexOf("Recalibrate each cycle", StringComparison.Ordinal) + "Recalibrate each cycle".Length;
        const string expected = "Positive ramp Two’s complement = 2³⁰\nNegative ramp Recalibrate each cycle";
        foreach (var reverse in new[] { false, true })
        {
            scope.Call("HideReaderSelectionPopup");
            pdf.ClearSelection();
            await ReaderTests.Render();
            var top = pdf.GetRangeBounds(start, start + 1).Single();
            var bottom = pdf.GetRangeBounds(end - 1, end).Single();
            var first = pdf.TranslatePoint(new Point(top.Left + 0.1, top.Center.Y), scope.Window)!.Value;
            var last = pdf.TranslatePoint(new Point(bottom.Right - 0.1, bottom.Center.Y), scope.Window)!.Value;
            scope.Window.MouseDown(reverse ? last : first, MouseButton.Left);
            scope.Window.MouseMove(reverse ? first : last);
            scope.Window.MouseUp(reverse ? first : last, MouseButton.Left);
            await ReaderTests.Render();
            Assert.Equal(expected, scope.Field<string>("_readerPendingSelection"));
            if (!reverse)
            {
                Capture(scope.Window, "pdf-table-column-selection");
                await scope.Call<Task>("PerformReaderSelectionCopyAsync");
                Assert.Equal(expected, await scope.Window.Clipboard!.TryGetTextAsync());
            }
        }
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Table description", null, null);
        var annotation = Assert.Single(scope.Window.ReaderAnnotations);
        Assert.Equal(expected, annotation.SelectedText);
        var ranges = PdfTextRanges.Read(annotation.Fragment, annotation.StartOffset, annotation.EndOffset);
        var layout = new PdfTextSelectionLayout(pdf.PageContent);
        Assert.Equal(expected, layout.GetCopyText(ranges));
        Assert.DoesNotContain("RAMP", layout.GetText(ranges));
    });

    [Theory]
    [InlineData(PdfReaderFitMode.Page, 0.9)]
    [InlineData(PdfReaderFitMode.Width, 0.25)]
    public Task ScaledPdfOutlineJumpsKeepTheClickedSectionAfterRendering(PdfReaderFitMode fit, double zoom) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await pdf.SetFitModeAsync(fit);
        await pdf.SetZoomAsync(zoom);
        var toc = scope.Field<IReadOnlyList<EpubReaderNavigationItem>>("_readerTocItems");

        foreach (var index in new[] { 2, 1, 2, 3 })
        {
            var item = toc[index];
            Assert.True(await scope.Call<Task<bool>>("NavigateToReaderItemAsync", item, CancellationToken.None, ReaderNavigationIntent.Toc, null));
            await pdf.RefreshViewportAsync();
            await ReaderTests.Render();
            Assert.Equal(item.ChapterIndex + 1, pdf.PageNumber);
            Assert.Contains(pdf.PageNumber, pdf.VisiblePageNumbers);
            AssertCurrent(scope, item.Title);
            var heading = pdf.PageContent!.Text.IndexOf(item.Title, StringComparison.Ordinal);
            if (heading >= 0)
                Assert.InRange(pdf.GetRangeBounds(heading, heading + item.Title.Length).First().Top, 0, 100);
        }
    });

    [Fact]
    public Task RapidPdfOutlineClicksCommitTheLatestBodyAndTocLocation() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope);
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", PdfReaderDisplayMode.SinglePage);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        var toc = scope.Field<IReadOnlyList<EpubReaderNavigationItem>>("_readerTocItems");

        var first = scope.Call<Task<bool>>(
            "NavigateToReaderItemAsync",
            toc[2],
            CancellationToken.None,
            ReaderNavigationIntent.Toc,
            null);
        var second = scope.Call<Task<bool>>(
            "NavigateToReaderItemAsync",
            toc[1],
            CancellationToken.None,
            ReaderNavigationIntent.Toc,
            null);
        var latest = scope.Call<Task<bool>>(
            "NavigateToReaderItemAsync",
            toc[2],
            CancellationToken.None,
            ReaderNavigationIntent.Toc,
            null);

        var results = await Task.WhenAll(first, second, latest);
        Assert.False(results[0]);
        Assert.False(results[1]);
        Assert.True(results[2]);
        Assert.Equal(toc[2].ChapterIndex + 1, pdf.PageNumber);
        AssertCurrent(scope, toc[2].Title);
        // The nested outline is below the page origin. A stale parent reset
        // would leave the viewport near zero even though the final row looked
        // correct.
        Assert.InRange(pdf.VisibleTop, 0.4, 0.75);
    });

    [Fact]
    public Task SelectionStylesCommentsSearchAndPersistenceUsePageLocalTextOffsets() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        var text = pdf.PageContent!.Text;
        var words = new[] { "Select", "this text", "Underline", "highlight", "Nested section", "correct page" };
        var styles = new[] { "solid", "double", "wavy", "dashed", "marker", "dotted" };
        for (var index = 0; index < styles.Length; index++)
        {
            var offset = text.IndexOf(words[index], StringComparison.Ordinal);
            pdf.SelectRange(offset, offset + words[index].Length);
            Assert.Equal(words[index], scope.Field<string>("_readerPendingSelection"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            await scope.Call<Task>("SaveReaderAnnotationAsync", index == 0 ? "Remember this passage" : "", styles[index], "#000000");
        }
        Assert.Equal(6, scope.Window.ReaderAnnotations.Count);
        var first = scope.Window.ReaderAnnotations.Single(item => item.SelectedText == "Select");
        Assert.Equal("pdf:page:2", first.ChapterPath);
        scope.Call("EditReaderPdfAnnotation", first);
        Assert.Equal("Remember this passage", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Edited comment", null, null);
        Assert.Equal(6, scope.Window.ReaderAnnotations.Count);
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-annotations");

        var before = pdf.GetRangeBounds(first.StartOffset, first.EndOffset).Single();
        await pdf.SetZoomAsync(2);
        var zoomed = pdf.GetRangeBounds(first.StartOffset, first.EndOffset).Single();
        Assert.InRange(zoomed.Width / before.Width, 1.99, 2.01);
        var matches = pdf.Find("this text");
        Assert.Equal(2, matches.Count);
        pdf.ScrollToSearchHit(1);
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-zoom-search");

        await scope.Call<Task>("NavigatePdfPageAsync", 3, CancellationToken.None, true);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "A scanned page note", null, null);
        Assert.Equal(7, scope.Window.ReaderAnnotations.Count);
        var scanNote = scope.Window.ReaderAnnotations.Single(item => item.ChapterPath == "pdf:page:3");
        Assert.Equal(0, scanNote.EndOffset);
        scope.Call("EditReaderPdfAnnotation", scanNote);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Edited scan note", null, null);
        Assert.Equal(0, scope.Window.ReaderAnnotations.Single(item => item.Id == scanNote.Id).EndOffset);
        await scope.Call<Task>("CloseReaderAsync");
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]));
        Assert.Equal(7, scope.Window.ReaderAnnotations.Count);
        Assert.Equal("Edited comment", scope.Window.ReaderAnnotations.Single(item => item.Id == first.Id).Note);
        await scope.Call<Task>("NavigateToReaderAnnotationAsync", first);
        Assert.Equal(2, scope.Field<NativePdfReaderHost>("_readerActiveHost").PageNumber);
        scope.Call("ReaderAnnotationItemDeleteButton_Click", new Button { Tag = first }, new RoutedEventArgs());
        await ReaderTests.Render();
        Assert.DoesNotContain(scope.Window.ReaderAnnotations, item => item.Id == first.Id);
    });

    [Fact]
    public Task PointerSelectionAndRotatedCropRemainAlignedAndPdfHasPageFallbackWithoutOutline() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope, outline: false);
        Assert.Equal(4, scope.Field<IReadOnlyList<EpubReaderNavigationItem>>("_readerTocItems").Count);
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await ReaderTests.Render();
        var start = pdf.PageContent!.Text.IndexOf("Select this text", StringComparison.Ordinal);
        var box = pdf.GetRangeBounds(start, start + "Select this text".Length).Single();
        var first = pdf.TranslatePoint(new Point(box.Left, box.Center.Y), scope.Window)!.Value;
        var last = pdf.TranslatePoint(new Point(box.Right + 1, box.Center.Y), scope.Window)!.Value;
        scope.Window.MouseDown(first, MouseButton.Left);
        scope.Window.MouseMove(last);
        scope.Window.MouseUp(last, MouseButton.Left);
        Assert.Equal("Select this text", scope.Field<string>("_readerPendingSelection"));
        await scope.Call<Task>("NavigatePdfPageAsync", 4, CancellationToken.None, true);
        Assert.Equal(600, pdf.PageContent!.Width);
        var rotatedText = pdf.PageContent.Text;
        var rotatedRange = pdf.GetRangeBounds(0, rotatedText.IndexOf('\r')).Single();
        first = pdf.TranslatePoint(new Point(rotatedRange.Center.X, rotatedRange.Top), scope.Window)!.Value;
        last = pdf.TranslatePoint(new Point(rotatedRange.Center.X, rotatedRange.Bottom + 1), scope.Window)!.Value;
        scope.Window.MouseDown(first, MouseButton.Left);
        scope.Window.MouseMove(last);
        scope.Window.MouseUp(last, MouseButton.Left);
        Assert.Equal(rotatedText[..rotatedText.IndexOf('\r')], scope.Field<string>("_readerPendingSelection"));
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Rotated PDF note", "wavy", "#000000");
        await ReaderTests.Render();
        Capture(scope.Window, "pdf-rotated");
        await scope.Call<Task>("ChangeReaderFontAsync", 0.1);
        Assert.Equal(1.1, pdf.Zoom, 2);
    });

    [Theory]
    [InlineData(ReaderTheme.Classic)]
    [InlineData(ReaderTheme.Beige)]
    public Task ColoredMarkersRenderAndKeepTheirNotesAndColorsWhenReopened(ReaderTheme theme) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = await Open(scope);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = theme });
        await scope.Field<Task>("_readerAppearanceSaveTask");
        await pdf.RefreshViewportAsync();
        var text = pdf.PageContent!.Text;
        var words = new[] { "Select", "this text", "Underline", "highlight", "Nested section", "correct page" };
        for (var index = 0; index < words.Length; index++)
        {
            var start = text.IndexOf(words[index], StringComparison.Ordinal);
            pdf.ScrollToOffset(start);
            await pdf.RefreshViewportAsync();
            pdf.SelectRange(start, start + words[index].Length);
            var color = ReaderHighlightColorTests.MarkerColors[index];
            await scope.Call<Task>("SaveReaderAnnotationAsync", "Keep this PDF note", "marker", color);
            using var pixels = SKBitmap.Decode(await pdf.CaptureVisiblePageAsync(CancellationToken.None));
            var expected = ReaderHighlightColorTests.BlendMarker(color, ReaderPalette.For(theme).Page);
            var bounds = pdf.GetRangeBounds(start, start + words[index].Length).Single();
            var coloredPixels = 0;
            for (var y = Math.Max(0, (int)bounds.Top); y < Math.Min(pixels.Height, bounds.Bottom); y++)
                for (var x = Math.Max(0, (int)bounds.Left); x < Math.Min(pixels.Width, bounds.Right); x++)
                    if (ReaderHighlightColorTests.ColorDistance(pixels.GetPixel(x, y), expected) <= 3) coloredPixels++;
            Assert.True(coloredPixels > 10, $"Missing {color} PDF marker in {theme}.");
        }
        var first = scope.Window.ReaderAnnotations.Single(item => item.SelectedText == words[0]);
        pdf.ScrollToOffset(first.StartOffset);
        await pdf.RefreshViewportAsync();
        pdf.SelectRange(first.StartOffset, first.EndOffset);
        await ReaderTests.Render();
        var stylesButton = scope.Get<Button>("ReaderSelectionHighlightMenuButton");
        stylesButton.Flyout!.ShowAt(stylesButton);
        await ReaderTests.Render();
        Assert.Equal(Avalonia.Media.Color.Parse("#FFD54F"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                scope.Get<Border>("ReaderSelectionMarkerColorPreview").Background).Color);
        var picker = scope.Get<Button>("ReaderSelectionMarkerColorButton");
        ReaderHighlightColorTests.Click(picker);
        await ReaderTests.Render();
        Assert.True(Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(picker)).IsOpen);
        var pink = scope.Get<StackPanel>("ReaderSelectionMarkerPalette").Children.OfType<Button>().Single(item => item.Tag as string == "#F48FB1");
        ReaderHighlightColorTests.Click(pink);
        for (var attempt = 0; attempt < 500 && scope.Field<string?>("_readerPendingSelection") is not null; attempt++)
            await Task.Delay(10);
        Assert.Equal("#F48FB1", scope.Window.ReaderAnnotations.Single(item => item.Id == first.Id).Color);
        Assert.Equal("Keep this PDF note", scope.Window.ReaderAnnotations.Single(item => item.Id == first.Id).Note);
        var savedColors = scope.Window.ReaderAnnotations.ToDictionary(item => item.Id, item => item.Color);
        await scope.Call<Task>("CloseReaderAsync");
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]));
        Assert.Equal(6, scope.Window.ReaderAnnotations.Count);
        Assert.All(scope.Window.ReaderAnnotations, item =>
        {
            Assert.Equal(savedColors[item.Id], item.Color);
            Assert.Equal("Keep this PDF note", item.Note);
        });
    });

    private static async Task<BookCardViewModel> Open(ReaderTestWindow scope, bool outline = true, string? path = null)
    {
        path ??= PdfFixture.Write(scope.Paths.ReaderCache, outline);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible, scope.Get<TextBlock>("ReaderStatusText").Text);
        await scope.Field<Task>("_readerPdfOutlineTask");
        await ReaderTests.Render();
        return card;
    }

    [Fact]
    public Task ZoomAndPositionRestoreWithoutChangingTheEpubLayoutAndCloseReleasesTheFile() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var layout = scope.Field<AppSettings>("_appSettings").DefaultReaderLayout;
        using var card = await Open(scope);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        await pdf.SetZoomAsync(2.5);
        pdf.ScrollToTop(0.45);
        var state = pdf.CaptureViewState();
        await scope.Call<Task>("CloseReaderAsync");
        Assert.Equal(layout, (await new AppSettingsStore(scope.Paths).LoadAsync()).DefaultReaderLayout);
        var path = new SqliteBookLibraryService(scope.Paths, new BookMetadataService()).GetAbsoluteFilePath(card.Book.Files[0]);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
        await scope.Call<Task>("OpenPdfReaderAsync", card, card.Book.Files[0], path);
        pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(2, pdf.PageNumber);
        Assert.Equal(2.5, pdf.Zoom);
        Assert.Equal(state, pdf.CaptureViewState());
    });

    private static void AssertCurrent(ReaderTestWindow scope, string title)
    {
        var row = Assert.IsType<ReaderTocRow>(scope.Get<ListBox>("ReaderTocList").SelectedItem);
        Assert.Equal(title, row.Title);
        Assert.Equal(row.Item.Target, scope.Field<string>("_readerCompactSelectedTarget"));
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
