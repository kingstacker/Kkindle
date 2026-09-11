using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData(1024, 664, "zh-CN")]
    [InlineData(1294, 804, "zh-CN")]
    [InlineData(1920, 1080, "zh-CN")]
    [InlineData(1024, 664, "en-US")]
    public Task LibraryToolbarAndDockedDetailsRemainAccessible(int width, int height, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Window.Width = width;
        scope.Window.Height = height;
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        await Until(() => !scope.Window.ViewModel.IsBusy);
        await Render();

        AssertLibraryToolbar(scope);
        var shelf = scope.Get<Grid>("LibraryContentHost");
        var originalWidth = shelf.Bounds.Width;
        Capture(scope.Window, $"{language}-{width}-library-toolbar");

        var grid = scope.Get<ListBox>("BookGrid");
        grid.SelectedItem = scope.Window.ViewModel.Books[0];
        var detail = scope.Get<Border>("LibraryDetailPane");
        await Until(() => detail.IsVisible);
        await Task.Delay(600);
        await Render();
        AssertLibraryToolbar(scope);
        AssertWithinWindow(scope.Get<Button>("LibraryDetailCloseButton"), scope.Window);
        Assert.True(shelf.Bounds.Width < originalWidth - 250);
        var shelfRight = shelf.TranslatePoint(default, scope.Window)!.Value.X + shelf.Bounds.Width;
        var detailLeft = detail.TranslatePoint(default, scope.Window)!.Value.X;
        Assert.True(shelfRight <= detailLeft, "The detail pane must not cover books.");
        Assert.True(grid.Bounds.Width <= shelf.Bounds.Width + 1);
        Assert.Contains("EPUB", scope.Get<TextBlock>("DetailFormatText").Text);
        Capture(scope.Window, $"{language}-{width}-library-details");

        // Opening and closing filters must leave the independent sort control available.
        scope.Get<Button>("FilterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.True(scope.Get<Border>("FilterPanel").IsVisible);
        Assert.True(scope.Get<ComboBox>("LibrarySortBox").IsEffectivelyVisible);
        Capture(scope.Window, $"{language}-{width}-library-filters");
        scope.Get<Button>("FilterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        scope.Get<Button>("LibraryDetailCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !detail.IsVisible);
        await Render();
        Assert.InRange(Math.Abs(shelf.Bounds.Width - originalWidth), 0, 1);
        await scope.Call<Task>("RefreshLibraryAsync");
        Assert.False(detail.IsVisible);

        // Selecting several books releases the detail column for batch actions.
        grid.SelectedItem = scope.Window.ViewModel.Books[0];
        await Render();
        grid.SelectedItems!.Add(scope.Window.ViewModel.Books[1]);
        await Until(() => !detail.IsVisible);
        await Render();
        Assert.True(scope.Get<Border>("MultiSelectionBar").IsVisible);
        AssertWithinWindow(scope.Get<Border>("MultiSelectionBar"), scope.Window);
        Assert.InRange(Math.Abs(shelf.Bounds.Width - originalWidth), 0, 1);
    });

    [Fact]
    public Task LibraryViewMenuAndSortingPreserveCollectionScope() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var viewModel = scope.Window.ViewModel;
        var library = scope.Field<IBookLibraryService>("_library");
        var collection = await library.CreateCollectionAsync("Layout collection");
        var alpha = viewModel.Books.Single(card => card.Title == "Alpha");
        await library.AddBookToCollectionAsync(alpha.Book.Id, collection.Id);
        await scope.Call<Task>("RefreshLibraryAsync");

        var sort = scope.Get<ComboBox>("LibrarySortBox");
        sort.SelectedIndex = (int)LibrarySortMode.TitleAscending;
        Assert.Equal(LibrarySortMode.TitleAscending, viewModel.SortMode);
        await Until(() => !viewModel.IsBusy && viewModel.Books.FirstOrDefault()?.Title == "Alpha");
        Assert.Equal(new[] { "Alpha", "Zulu" }, viewModel.Books.Select(card => card.Title));
        Assert.False(scope.Get<Border>("FilterPanel").IsVisible);

        viewModel.CollectionFilterId = collection.Id;
        viewModel.CollectionFilterName = collection.Name;
        await viewModel.RefreshViewAsync();
        Assert.Single(viewModel.Books);
        var viewButton = scope.Get<Button>("LibraryViewToggleButton");
        viewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(viewButton.ContextMenu!.IsOpen);
        Assert.True(scope.Get<ListBox>("BookGrid").IsVisible);
        Assert.True(scope.Get<MenuItem>("LibraryGridViewMenuItem").IsChecked);
        viewButton.ContextMenu.Close();

        scope.Get<MenuItem>("LibraryListViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(scope.Get<ListBox>("BookList").IsVisible);
        Assert.Equal(collection.Id, viewModel.CollectionFilterId);
        Assert.Equal(LibrarySortMode.TitleAscending, viewModel.SortMode);
        Assert.True(scope.Get<MenuItem>("LibraryListViewMenuItem").IsChecked);

        scope.Get<MenuItem>("LibraryGridViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(scope.Get<ListBox>("BookGrid").IsVisible);
        Assert.Equal(collection.Id, viewModel.CollectionFilterId);

        scope.Get<MenuItem>("LibraryCollectionsViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !viewModel.IsBusy);
        Assert.True(scope.Get<ScrollViewer>("CollectionScroll").IsVisible);
        Assert.Null(viewModel.CollectionFilterId);
        Assert.True(scope.Get<MenuItem>("LibraryCollectionsViewMenuItem").IsChecked);
        Assert.Equal(LibrarySortMode.TitleAscending, viewModel.SortMode);
    });

    private static void AssertLibraryToolbar(TestWindow scope)
    {
        foreach (var name in new[] { "SearchBox", "FilterButton", "LibrarySortBox", "LibraryViewToggleButton", "ImportButton" })
            AssertWithinWindow(scope.Get<Control>(name), scope.Window);
        var import = scope.Get<Button>("ImportButton");
        var importPosition = import.TranslatePoint(default, scope.Window)!.Value;
        Assert.True(importPosition.Y >= 38, "Import must remain below the title-bar hit region.");
        var workspace = scope.Get<Grid>("LibraryWorkspace");
        var workspaceRight = workspace.TranslatePoint(default, scope.Window)!.Value.X + workspace.Bounds.Width
                             - scope.Get<Grid>("LibraryToolbar").Margin.Right;
        Assert.InRange(Math.Abs(workspaceRight - importPosition.X - import.Bounds.Width), 0, 1);
        var search = scope.Get<TextBox>("SearchBox");
        var actions = scope.Get<StackPanel>("LibraryToolbarActions");
        var searchPosition = search.TranslatePoint(default, scope.Window)!.Value;
        var actionsPosition = actions.TranslatePoint(default, scope.Window)!.Value;
        Assert.True(search.Bounds.Width >= 239);
        Assert.True(searchPosition.X + search.Bounds.Width + 20 <= actionsPosition.X
                    || searchPosition.Y + search.Bounds.Height <= actionsPosition.Y,
            "Search and actions must not overlap.");
        Assert.InRange(Math.Abs(workspaceRight - actionsPosition.X - actions.Bounds.Width), 0, 1);
    }

    private static async Task SeedLayoutLibrary(TestWindow scope)
    {
        var sources = new List<string>();
        foreach (var title in new[] { "Alpha", "Zulu" })
        {
            var path = Path.Combine(scope.Paths.Data, title + ".epub");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry("mimetype", "application/epub+zip");
                WriteEntry("META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                      <rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml" /></rootfiles>
                    </container>
                    """);
                WriteEntry("content.opf", $$"""
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                      <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                        <dc:identifier id="id">layout-{{title}}</dc:identifier><dc:title>{{title}}</dc:title>
                        <dc:creator>Library layout test</dc:creator><dc:language>en</dc:language>
                        <meta name="cover" content="cover" />
                      </metadata>
                      <manifest><item id="cover" href="cover.png" media-type="image/png" properties="cover-image" />
                        <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" /></manifest>
                      <spine><itemref idref="chapter" /></spine>
                    </package>
                    """);
                WriteEntry("chapter.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>{title}</p></body></html>");
                using var bitmap = new SKBitmap(152, 212);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(title == "Alpha" ? new SKColor(216, 215, 207) : new SKColor(185, 192, 188));
                using var ink = new SKPaint { Color = new SKColor(58, 60, 54), IsAntialias = true };
                using var typeface = SKTypeface.FromFamilyName("Georgia");
                using var font = new SKFont(typeface, 26);
                canvas.DrawText(title, 20, 110, font, ink);
                using var cover = archive.CreateEntry("cover.png").Open();
                bitmap.Encode(cover, SKEncodedImageFormat.Png, 100);

                void WriteEntry(string name, string content)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(content);
                }
            }
            sources.Add(path);
        }
        await scope.Field<IBookLibraryService>("_library").ImportAsync(sources);
        await scope.Call<Task>("RefreshLibraryAsync");
        // This fixture initializes services explicitly instead of running the
        // desktop startup sequence that normally enables filter interactions.
        scope.Set("_filterControlsReady", true);
        await Render();
    }
}
