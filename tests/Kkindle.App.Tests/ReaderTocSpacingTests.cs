using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderTocSpacingTests(SettingsUiSession session)
{
    [Fact]
    public Task TocRailHasNoBlankStripAboveTheFirstChapter() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        await scope.Call<Task>("NavigatePdfPageAsync", 2, CancellationToken.None, true);
        await ReaderTests.Render();

        var list = scope.Get<ListBox>("ReaderTocList");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();

        // Scroll the rail far down, then bring it back to the top: the
        // virtualizing panel can keep a spacer for the rows above, which paints
        // as an empty strip before the first chapter.
        scroll.Offset = new Vector(0, Math.Min(400, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        await ReaderTests.Render();
        scroll.Offset = default;
        await ReaderTests.Render();

        var firstRow = list.ContainerFromIndex(0);
        Assert.NotNull(firstRow);
        var top = firstRow!.TranslatePoint(default, scroll)!.Value.Y;
        Assert.True(top <= 2, $"first TOC row starts {top:F1}px below the rail top: blank strip above it");
    });

    [Fact]
    public Task ClickingAnOutlineEntryThatSharesItsPageKeepsThatEntrySelected() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.WriteTiedOutline(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        await ReaderTests.Render();

        var list = scope.Get<ListBox>("ReaderTocList");
        var rows = scope.Field<ReaderTocRow[]>("_readerTocRows");
        var first = rows.Single(row => row.Title == "First entry");
        var second = rows.Single(row => row.Title == "Second entry");
        // Both entries point at the same page and offset.
        Assert.Equal(first.Item.ChapterIndex, second.Item.ChapterIndex);
        Assert.Contains("page=1&top=0", first.Item.Target);
        Assert.Contains("page=1&top=0", second.Item.Target);

        // Follow the second entry, then let the page-following sync run: it must
        // keep that entry instead of snapping back to the first one.
        scope.Call("NavigateToReaderTocItem", second.Item);
        await ReaderTests.Render();
        scope.Call("SyncReaderPdfTocSelection");
        await ReaderTests.Render();
        Assert.Equal("Second entry", (list.SelectedItem as ReaderTocRow)?.Title);
    });

    private Task Run(Func<Task> body) =>
        session.Session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None);
}
