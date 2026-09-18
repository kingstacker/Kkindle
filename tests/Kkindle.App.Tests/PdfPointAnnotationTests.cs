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

        var marker = pdf.GetPdfPointPosition(annotation)!.Value;
        var markerScreen = pdf.TranslatePoint(marker, scope.Window)!.Value;
        scope.Window.MouseDown(markerScreen, MouseButton.Left);
        scope.Window.MouseUp(markerScreen, MouseButton.Left);
        Assert.True(scope.Get<Popup>("ReaderAnnotationInputPopup").IsOpen);
        Assert.Equal("结果区域旁的研究备注", scope.Get<TextBox>("ReaderAnnotationInputBox").Text);
    });

    private Task Run(Func<Task> action) =>
        session.Session.Dispatch(async () =>
        {
            await action();
            return true;
        }, CancellationToken.None);
}
