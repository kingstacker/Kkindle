using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class PdfReaderTests(SettingsUiSession session)
{
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
        AssertCurrent(scope, "Scanned page");
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

    private static async Task<BookCardViewModel> Open(ReaderTestWindow scope, bool outline = true)
    {
        var path = PdfFixture.Write(scope.Paths.ReaderCache, outline);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible, scope.Get<TextBlock>("ReaderStatusText").Text);
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
