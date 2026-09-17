using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task SyncStatusesSurviveLibraryCardRebuild() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);

        var cards = scope.Window.ViewModel.Books.ToArray();
        var statusCache = scope.Field<System.Collections.Concurrent.ConcurrentDictionary<Guid, BookSyncStatus>>(
            "_bookSyncStatusCache");
        statusCache[cards[0].Book.Id] = BookSyncStatus.Synced;
        statusCache[cards[1].Book.Id] = BookSyncStatus.NotDownloaded;
        scope.Call("UpdateLibraryUi");

        await scope.Window.ViewModel.RefreshAsync();
        await Render();

        var refreshed = scope.Window.ViewModel.Books.ToDictionary(card => card.Book.Id);
        Assert.Equal(BookSyncStatus.Synced, refreshed[cards[0].Book.Id].SyncStatus);
        Assert.Equal(BookSyncStatus.NotDownloaded, refreshed[cards[1].Book.Id].SyncStatus);
    });

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
        scope.Get<Button>("DeviceManagementSectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        scope.Get<Button>("ReadingSectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        var navigationNames = new[] { "AllBooksButton", "KindleBooksButton",
            "FontManagementButton", "DictionaryManagementButton", "ReaderNotesNavigationButton", "ReadingDashboardButton" };
        double? iconCenter = null;
        foreach (var navigationName in navigationNames)
        {
            var glyph = scope.Get<Button>(navigationName).GetVisualDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Classes.Contains("sidebarGlyph"));
            // Check the painted geometry, not just identical control boxes: a
            // narrow Path can sit left of centre inside a centred square slot.
            // Pixel hinting may move an edge by half a physical pixel, while
            // the layout slots must still share exactly the same column.
            var paintedCenter = glyph.RenderedGeometry!.Bounds.Center;
            var tolerance = 0.5 / scope.Window.RenderScaling + 0.01;
            Assert.InRange(Math.Abs(paintedCenter.X - glyph.Bounds.Width / 2), 0, tolerance);
            Assert.InRange(Math.Abs(paintedCenter.Y - glyph.Bounds.Height / 2), 0, tolerance);
            var center = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), scope.Window)!.Value.X;
            iconCenter ??= center;
            Assert.Equal(iconCenter.Value, center, 2);
        }
        Capture(scope.Window, $"{language}-{width}-sidebar-expanded");

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
        var detailButtons = new[]
        {
            scope.Get<Button>("LibraryDetailCloseButton"),
            scope.Get<Button>("EditBookReflectionButton"),
            scope.Get<Button>("DetailDoubanButton"),
            scope.Get<Button>("DetailFavoriteButton"),
            scope.Get<Button>("DetailReadingStatusButton")
        };
        var buttonCenters = detailButtons
            .Select(button => button.TranslatePoint(
                new Point(button.Bounds.Width / 2, button.Bounds.Height / 2),
                scope.Window)!.Value.X)
            .ToArray();
        Assert.All(buttonCenters, center => Assert.Equal(buttonCenters[0], center, 1));
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
    public Task BookReflectionPreviewIsLoadedIntoBookDetails() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        var reader = scope.Field<ReaderDataService>("_readerData");
        await reader.SaveBookReflectionAsync(new ReaderBookReflection
        {
            BookId = card.Book.Id,
            Content = "A reflection long enough to verify that the book details expose a dedicated preview.",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow
        });

        scope.Get<ListBox>("BookGrid").SelectedItem = card;
        await Until(() => scope.Get<Border>("LibraryDetailPane").IsVisible
            && scope.Get<KreaderMarkdownTextBlock>("DetailReflectionPreviewText").Markdown?.Contains("A reflection", StringComparison.Ordinal) == true);
        await Render();

        var reflectionPanel = scope.Get<Border>("DetailReflectionPanel");
        Assert.True(reflectionPanel.Bounds.Width >= 240);
        var editReflectionButton = scope.Get<Button>("EditBookReflectionButton");
        Assert.True(editReflectionButton.IsEffectivelyVisible);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(editReflectionButton.Content);
        var detailActionButton = scope.Get<Button>("DetailDoubanButton");
        Assert.Equal(detailActionButton.Bounds.Width, editReflectionButton.Bounds.Width, 1);
        Assert.Equal(detailActionButton.Bounds.Height, editReflectionButton.Bounds.Height, 1);
        var preview = scope.Get<KreaderMarkdownTextBlock>("DetailReflectionPreviewText");
        Assert.Contains("A reflection", preview.Markdown ?? string.Empty);
        Assert.Equal(6, preview.MaxLines);
        var hoverArea = scope.Get<Border>("DetailReflectionHoverArea");
        var reflectionFlyout = scope.Field<Flyout>("_bookReflectionFlyout");
        var flyoutBorder = Assert.IsType<Border>(reflectionFlyout.Content);
        var flyoutScroll = Assert.IsType<ScrollViewer>(flyoutBorder.Child);
        var flyoutPreview = Assert.IsType<KreaderMarkdownTextBlock>(flyoutScroll.Content);
        Assert.Contains("A reflection", flyoutPreview.Markdown ?? string.Empty);
        Assert.Equal(new CornerRadius(0), flyoutBorder.CornerRadius);
        Assert.Equal(0, flyoutBorder.BoxShadow.Count);
        Assert.Equal(PlacementMode.LeftEdgeAlignedTop, reflectionFlyout.Placement);
        reflectionFlyout.ShowAt(hoverArea);
        await Render();
        Assert.True(reflectionFlyout.IsOpen);
        Assert.True(flyoutPreview.Bounds.Width > 0);
        Assert.True(flyoutPreview.Bounds.Width <= flyoutScroll.Bounds.Width + 1);
        reflectionFlyout.Hide();
        var detailStack = scope.Get<StackPanel>("DetailContentStack");
        var separator = scope.Get<Avalonia.Controls.Shapes.Rectangle>("DetailReflectionSeparator");
        var cover = scope.Get<Grid>("DetailCoverAndActions");
        var format = scope.Get<TextBlock>("DetailFormatText");
        var saveRow = Assert.IsType<Grid>(scope.Get<Button>("SaveDetailsButton").Parent);
        Assert.True(detailStack.Children.IndexOf(cover) < detailStack.Children.IndexOf(format));
        Assert.True(detailStack.Children.IndexOf(format) < detailStack.Children.IndexOf(separator));
        Assert.True(detailStack.Children.IndexOf(separator) < detailStack.Children.IndexOf(reflectionPanel));
        Assert.True(detailStack.Children.IndexOf(reflectionPanel) < detailStack.Children.IndexOf(saveRow));
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
        await Render();
        AssertCollectionHeaderMatchesToolbar(scope);
        var gridToolbarBounds = GetToolbarControlBounds(scope.Get<StackPanel>("LibraryToolbarActions"), scope.Window);
        Capture(scope.Window, "view-switch-grid");
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
        await Render();
        Assert.Equal(gridToolbarBounds, GetToolbarControlBounds(scope.Get<StackPanel>("LibraryToolbarActions"), scope.Window));
        Capture(scope.Window, "view-switch-list");

        scope.Get<MenuItem>("LibraryGridViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(scope.Get<ListBox>("BookGrid").IsVisible);
        Assert.Equal(collection.Id, viewModel.CollectionFilterId);

        scope.Get<MenuItem>("LibraryCollectionsViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !viewModel.IsBusy);
        Assert.True(scope.Get<ScrollViewer>("CollectionScroll").IsVisible);
        Assert.Null(viewModel.CollectionFilterId);
        Assert.True(scope.Get<MenuItem>("LibraryCollectionsViewMenuItem").IsChecked);
        Assert.Equal(LibrarySortMode.TitleAscending, viewModel.SortMode);
        await Render();
        Assert.Equal(gridToolbarBounds, GetToolbarControlBounds(scope.Get<StackPanel>("LibraryToolbarActions"), scope.Window));
        Capture(scope.Window, "view-switch-collections");

        // Hidden grid controls retain their old bounds. A resize in another view
        // must still place the buttons where the grid will place them next.
        scope.Window.Width = 1024;
        await Render();
        var resizedToolbarBounds = GetToolbarControlBounds(scope.Get<StackPanel>("LibraryToolbarActions"), scope.Window);
        Capture(scope.Window, "view-switch-resized-collections");
        scope.Get<MenuItem>("LibraryGridViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Render();
        Assert.Equal(resizedToolbarBounds, GetToolbarControlBounds(scope.Get<StackPanel>("LibraryToolbarActions"), scope.Window));
        Capture(scope.Window, "view-switch-resized-grid");
    });

    [Fact]
    public Task LibraryListRowsStayWithinToolbarBoundaryAndHideScrollbarWhenIdle() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);

        for (var index = 0; index < 12; index++)
        {
            var id = Guid.NewGuid();
            scope.Window.ViewModel.Books.Add(new BookCardViewModel(new Book
            {
                Id = id,
                Title = $"Synthetic {index}",
                Authors = "Layout test",
                Files = [new BookFile { Id = Guid.NewGuid(), BookId = id, Format = "epub", RelativePath = $"synthetic-{index}.epub" }]
            }, scope.Paths.Data));
        }

        scope.Get<MenuItem>("LibraryListViewMenuItem")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Render();

        var list = scope.Get<ListBox>("BookList");
        var toolbar = scope.Get<Grid>("LibraryToolbar");
        var listOrigin = list.TranslatePoint(default, scope.Window)!.Value;
        var toolbarOrigin = toolbar.TranslatePoint(default, scope.Window)!.Value;
        var listRight = listOrigin.X + list.Bounds.Width;
        var toolbarRight = toolbarOrigin.X + toolbar.Bounds.Width;
        Assert.InRange(Math.Abs(listRight - toolbarRight), 0, 1);
        var rows = list.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("bookRow"))
            .ToArray();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var rowOrigin = row.TranslatePoint(default, scope.Window)!.Value;
            Assert.InRange(Math.Abs(rowOrigin.X + row.Bounds.Width - toolbarRight), 0, 1);
        }

        var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
        var thumb = Assert.Single(viewer.GetVisualDescendants().OfType<Thumb>());
        Assert.Contains("bookScroll", viewer.Classes);
        Assert.True(viewer.Extent.Height > viewer.Viewport.Height);

        viewer.Offset = new Vector(0, 50);
        await Render();
        Assert.Contains("scrolling", viewer.Classes);
        Assert.Equal(1, thumb.Opacity);
        await Task.Delay(800);
        await Render();
        Assert.DoesNotContain("scrolling", viewer.Classes);
        Assert.Equal(0, thumb.Opacity);
    });

    [Fact]
    public Task LibraryViewModeRestoresAndPersists() => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = "zh-CN",
            OnboardingCompleted = true,
            NetworkEnabled = false,
            AutoUpdateCheckEnabled = false,
            AutoConnectDevice = false,
            LibraryViewMode = "Collections"
        });

        Assert.True(scope.Get<ScrollViewer>("CollectionScroll").IsVisible);
        Assert.True(scope.Get<MenuItem>("LibraryCollectionsViewMenuItem").IsChecked);

        scope.Get<MenuItem>("LibraryListViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(await scope.Call<Task<bool>>("FlushAppSettingsAsync"));
        Assert.Equal("List", (await new AppSettingsStore(scope.Paths).LoadAsync()).LibraryViewMode);
        Assert.False(scope.Get<Border>("SettingsSavedCapsule").IsVisible);

        scope.Get<MenuItem>("LibraryCollectionsViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(await scope.Call<Task<bool>>("FlushAppSettingsAsync"));
        Assert.Equal("Collections", (await new AppSettingsStore(scope.Paths).LoadAsync()).LibraryViewMode);
        Assert.False(scope.Get<Border>("SettingsSavedCapsule").IsVisible);
    });

    [Fact]
    public Task CollectionModeSearchFiltersFoldersByMatchingBooks() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var library = scope.Field<IBookLibraryService>("_library");
        var collection = await library.CreateCollectionAsync("Alpha collection");
        var alpha = scope.Window.ViewModel.Books.Single(card => card.Title == "Alpha");
        await library.AddBookToCollectionAsync(alpha.Book.Id, collection.Id);
        await scope.Call<Task>("RefreshLibraryAsync");

        scope.Get<MenuItem>("LibraryCollectionsViewMenuItem")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !scope.Window.ViewModel.IsBusy);
        await Render();
        Assert.Equal(2, scope.Window.FilteredCollectionFolders.Count);

        scope.Get<TextBox>("SearchBox").Text = "Alpha";
        await Until(() => !scope.Window.ViewModel.IsBusy
            && scope.Window.FilteredCollectionFolders.Count == 1);
        Assert.Equal(collection.Id, Assert.Single(scope.Window.FilteredCollectionFolders).Collection.Id);
    });

    private static Rect[] GetToolbarControlBounds(Control toolbar, Window window) => toolbar
        .GetVisualDescendants().OfType<Control>()
        .Where(control => control is Button or ComboBox)
        .Select(control => new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size))
        .ToArray();

    private static void AssertCollectionHeaderMatchesToolbar(TestWindow scope)
    {
        var toolbar = scope.Get<Grid>("LibraryToolbar");
        var header = scope.Get<Border>("CollectionHeader");
        var toolbarOrigin = toolbar.TranslatePoint(default, scope.Window)!.Value;
        var headerOrigin = header.TranslatePoint(default, scope.Window)!.Value;
        var toolbarRight = toolbarOrigin.X + toolbar.Bounds.Width;
        var headerRight = headerOrigin.X + header.Bounds.Width;
        Assert.InRange(Math.Abs(header.Margin.Left - toolbar.Margin.Left), 0, 1);
        Assert.InRange(Math.Abs(header.Margin.Right - toolbar.Margin.Right), 0, 1);
        Assert.InRange(Math.Abs(headerOrigin.X - toolbarOrigin.X), 0, 1);
        Assert.InRange(Math.Abs(headerRight - toolbarRight), 0, 1);
    }

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
