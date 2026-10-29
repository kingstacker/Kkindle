using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class PdfPointAnnotationTests(SettingsUiSession session)
{
    [Fact]
    public Task RightClickShowsDeleteMenuAndDeleteKeyRemovesHighlight() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        pdf.SelectRange(0, 6);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Delete me", null, null);
        var annotation = Assert.Single(scope.Window.ReaderAnnotations);

        // Right click opens the small context menu, never the full-window dialog.
        var hit = pdf.GetRangeBounds(annotation.StartOffset, annotation.EndOffset)[0];
        var screenPoint = pdf.TranslatePoint(new Point(hit.Left + 2, hit.Center.Y), scope.Window)!.Value;
        scope.Window.MouseDown(screenPoint, MouseButton.Right);
        scope.Window.MouseUp(screenPoint, MouseButton.Right);
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Equal(annotation.Id, pdf.SelectedAnnotationId);
        var menu = scope.Field<ContextMenu?>("_readerAnnotationMenu");
        Assert.NotNull(menu);
        var deleteItem = Assert.Single(menu!.Items.OfType<MenuItem>(), item => (string?)item.Tag == "delete");
        deleteItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 30 && scope.Window.ReaderAnnotations.Count > 0; i++)
            await ReaderTests.Render();
        Assert.Empty(scope.Window.ReaderAnnotations);

        // Delete key removes a selected highlight directly as well.
        pdf.SelectRange(0, 6);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "Delete me again", null, null);
        var second = Assert.Single(scope.Window.ReaderAnnotations);
        pdf.SelectAnnotation(second.Id);
        pdf.HandleReaderKey(new KeyEventArgs { Key = Key.Delete });
        for (var i = 0; i < 30 && scope.Window.ReaderAnnotations.Count > 0; i++)
            await ReaderTests.Render();
        Assert.Empty(scope.Window.ReaderAnnotations);
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
    });

    [Fact]
    public Task LeftClickSelectsHighlightAndDeleteRemovesIt() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        pdf.SelectRange(0, 6);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "click me", null, null);
        var annotation = Assert.Single(scope.Window.ReaderAnnotations);
        Assert.Null(pdf.SelectedAnnotationId);

        var hit = pdf.GetRangeBounds(annotation.StartOffset, annotation.EndOffset)[0];
        var point = pdf.TranslatePoint(new Point(hit.Left + 2, hit.Center.Y), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left);
        scope.Window.MouseUp(point, MouseButton.Left);
        await ReaderTests.Render();
        Assert.Equal(annotation.Id, pdf.SelectedAnnotationId);
        Assert.Single(scope.Window.ReaderAnnotations);
        Assert.False(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);

        pdf.HandleReaderKey(new KeyEventArgs { Key = Key.Delete });
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
        for (var i = 0; i < 30 && scope.Window.ReaderAnnotations.Count > 0; i++)
            await ReaderTests.Render();
        Assert.Empty(scope.Window.ReaderAnnotations);
        Assert.Null(pdf.SelectedAnnotationId);
    });
    [Fact]
    public Task TextNoteAnchorsToSelectedGlyphsAndCardsUseAvailableGutterWidth() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        using var pdf = new NativePdfReaderHost();
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = pdf;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await ReaderTests.Render();
        Assert.True(await pdf.NavigateAsync(new Uri(new Uri(path).AbsoluteUri + "#page=2")));
        await pdf.SetZoomAsync(0.5);
        await ReaderTests.Render();
        var text = pdf.PageContent!.Text;
        var start = text.IndexOf("Select", StringComparison.Ordinal);
        var annotation = new ReaderAnnotation
        {
            ChapterPath = "pdf:page:2", StartOffset = start, EndOffset = start + 6
        };
        Assert.True(pdf.TryGetAnnotationAnchor(annotation, out var page, out var anchor));
        var selected = pdf.GetRangeBounds(page, start, start + 6)[^1];
        Assert.Equal(new Point(selected.Right, selected.Bottom + 2), anchor);
        Assert.True(pdf.TryGetAnnotationCalloutBounds(page, anchor, new Size(1000, 88), out var card));
        Assert.InRange(card.Width, 144, 999);
        Assert.True(card.Left >= pdf.GetPageBounds(page).Right + 18);
        Assert.True(card.Right <= pdf.Bounds.Width - 8);
        Assert.Equal(88, card.Height);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AnnotationEditorRemainsVisibleWithoutARightGutter(bool pointNote) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        await pdf.SetFitModeAsync(PdfReaderFitMode.Width);
        await pdf.SetZoomAsync(1);
        await ReaderTests.Render();
        var anchor = new Point(pdf.PageBounds.Left + pdf.PageBounds.Width * 0.5,
            Math.Max(20, pdf.PageBounds.Top + 40));
        Assert.False(pdf.TryGetAnnotationCalloutBounds(2, anchor, new Size(264, 152), out _));
        if (pointNote)
        {
            pdf.SetPointAnnotationMode(true);
            var windowPoint = pdf.TranslatePoint(anchor, scope.Window)!.Value;
            scope.Window.MouseDown(windowPoint, MouseButton.Left);
            scope.Window.MouseUp(windowPoint, MouseButton.Left);
        }
        else
        {
            pdf.SelectRange(0, 6);
            scope.Call("ShowReaderAnnotationInputPopup");
        }
        var popup = scope.Get<Popup>("ReaderAnnotationInputPopup");
        Assert.True(popup.IsOpen);
        Assert.True(popup.Child!.IsVisible);
        await scope.Call<Task>("SaveReaderAnnotationAsync", "可见输入框保存的批注", null, null);
        Assert.Equal("可见输入框保存的批注", Assert.Single(scope.Window.ReaderAnnotations).Note);
    });

    [Fact]
    public Task PDFPointAnnotationsPersistTheirNormalizedLocation() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>(
            "OpenPdfReaderAsync",
            card,
            book.Files[0],
            library.GetAbsoluteFilePath(book.Files[0]));

        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        var noteButton = scope.Get<Button>("ReaderPdfPointNoteButton");
        Assert.IsType<Avalonia.Controls.Shapes.Path>(noteButton.Content);
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        pdf.SetPointAnnotationMode(true);
        var localPoint = new Point(pdf.PageBounds.Left + pdf.PageBounds.Width * 0.82,
            pdf.PageBounds.Top + pdf.PageBounds.Height * 0.25);
        var screenPoint = pdf.TranslatePoint(localPoint, scope.Window)!.Value;
        scope.Window.MouseDown(screenPoint, MouseButton.Left);
        scope.Window.MouseUp(screenPoint, MouseButton.Left);

        Assert.True(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);
        scope.Get<TextBox>("ReaderAnnotationInputBox").Text = "结果区域旁的研究备注";
        await scope.Call<Task>("SaveReaderAnnotationAsync", "结果区域旁的研究备注", null, null);

        var annotation = Assert.Single(scope.Window.ReaderAnnotations);
        Assert.Equal("pdf:page:2", annotation.ChapterPath);
        Assert.Equal(0, annotation.StartOffset);
        Assert.Equal(0, annotation.EndOffset);
        Assert.Equal("结果区域旁的研究备注", annotation.Note);
        Assert.True(ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var x, out var y));
        Assert.InRange(x, 0.78, 0.86);
        Assert.InRange(y, 0.21, 0.30);
        Assert.NotNull(pdf.GetPdfPointPosition(annotation));

        scope.Window.Width = 1600;
        scope.Window.Height = 1000;
        await ReaderTests.Render();
        await pdf.SetZoomAsync(0.5);
        await ReaderTests.Render();
        var marker = pdf.GetPdfPointPosition(annotation)!.Value;
        var markerScreen = pdf.TranslatePoint(marker, scope.Window)!.Value;
        scope.Window.MouseDown(markerScreen, MouseButton.Left);
        scope.Window.MouseUp(markerScreen, MouseButton.Left);
        // A single left click only picks the annotation so Delete or a right
        // click can remove it; the editor opens on a double click / edit call.
        Assert.Equal(annotation.Id, pdf.SelectedAnnotationId);
        Assert.False(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);
        scope.Call("EditReaderPdfAnnotation", annotation);
        Assert.True(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);
        Assert.Equal("结果区域旁的研究备注", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);

        var popup = scope.Get<Popup>("ReaderAnnotationInputPopup");
        await ReaderTests.Render();
        var beforeAnchor = pdf.GetPdfPointPosition(annotation)!.Value;
        var beforePopup = popup.PlacementRect!.Value.TopLeft;
        pdf.ScrollBy(60);
        await ReaderTests.Render();
        var delta = pdf.GetPdfPointPosition(annotation)!.Value - beforeAnchor;
        Assert.True(Math.Abs(delta.Y) > 1, "The page must actually scroll.");
        Assert.Equal(beforePopup.X + delta.X, popup.PlacementRect!.Value.X, 3);
        Assert.Equal(beforePopup.Y + delta.Y, popup.PlacementRect!.Value.Y, 3);
        Assert.True(popup.IsOpen);
        Assert.Equal("结果区域旁的研究备注", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);

        pdf.ScrollBy(-60);
        await ReaderTests.Render();
        Assert.Equal(beforePopup.Y, popup.PlacementRect!.Value.Y, 3);

        scope.Call("HideReaderAnnotationInputPopup");
        void AssertAttached(Popup cardPopup)
        {
            Assert.True(cardPopup.IsOpen);
            var callout = ((Point Anchor, Rect Bounds))typeof(NativePdfReaderHost)
                .GetField("_annotationCallout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(pdf)!;
            var currentMarker = pdf.GetPdfPointPosition(annotation)!.Value;
            Assert.Equal(currentMarker.X, callout.Anchor.X, 3);
            Assert.Equal(currentMarker.Y, callout.Anchor.Y, 3);
            if (!pdf.TryGetAnnotationCalloutBounds(2, currentMarker, callout.Bounds.Size, out var expected))
            {
                Assert.False(cardPopup.Child!.IsVisible);
                return;
            }
            Assert.True(cardPopup.Child!.IsVisible);
            Assert.True(callout.Bounds.Left >= pdf.GetPageBounds(2).Right + 18);
            Assert.True(callout.Bounds.Right <= pdf.Bounds.Width - 8);
            var origin = pdf.TranslatePoint(callout.Bounds.TopLeft, scope.Get<Control>("ReaderWebViewHost"))!.Value;
            Assert.Equal(origin.X, cardPopup.PlacementRect!.Value.X, 3);
            Assert.Equal(origin.Y, cardPopup.PlacementRect!.Value.Y, 3);
            Assert.Equal(expected, callout.Bounds);
        }
        scope.Call("EditReaderPdfAnnotation", annotation);
        scope.Get<TextBox>("ReaderAnnotationInputBox").Text = "尚未保存的备注";
        await pdf.SetZoomAsync(0.45);
        scope.Window.Width = 1800;
        await ReaderTests.Render();
        AssertAttached(popup);
        Assert.Equal("尚未保存的备注", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
        await pdf.SetZoomAsync(2);
        await ReaderTests.Render();
        AssertAttached(popup);
        Assert.False(popup.Child!.IsVisible);
        Assert.Equal("尚未保存的备注", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
        await pdf.SetZoomAsync(0.45);
        await ReaderTests.Render();
        AssertAttached(popup);
        Assert.True(popup.Child.IsVisible);
        var scrollPastAnchor = pdf.GetPdfPointPosition(annotation)!.Value.Y + 20;
        pdf.ScrollBy(scrollPastAnchor);
        await ReaderTests.Render();
        Assert.True(pdf.GetPdfPointPosition(annotation)!.Value.Y < 0);
        Assert.False(popup.Child.IsVisible);
        Assert.False(pdf.TryGetAnnotationCalloutBounds(2,
            pdf.GetPdfPointPosition(annotation)!.Value, new Size(264, 152), out _));
        Assert.Equal("尚未保存的备注", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
        pdf.ScrollBy(-scrollPastAnchor);
        await ReaderTests.Render();
        AssertAttached(popup);
        Assert.True(popup.Child.IsVisible);
        await scope.Call<Task>("NavigatePdfPageAsync", 3, CancellationToken.None, true);
        await ReaderTests.Render();
        Assert.False(popup.IsOpen);
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        await scope.Call<Task>("CloseReaderAsync");
        await scope.Call<Task>("OpenBookCoreAsync", card, book.Files[0], true);
        await ReaderTests.Render();
        pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(2, pdf.PageNumber);
        Assert.Equal(annotation.Note, Assert.Single(scope.Window.ReaderAnnotations).Note);
        Assert.NotNull(pdf.GetPdfPointPosition(annotation));
        Assert.False(scope.Get<Popup>("ReaderAnnotationHoverHostPopup").IsOpen);
    });

    private Task Run(Func<Task> action) =>
        session.Session.Dispatch(async () =>
        {
            await action();
            return true;
        }, CancellationToken.None);
}
