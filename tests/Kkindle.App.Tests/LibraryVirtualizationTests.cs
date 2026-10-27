using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task SwitchingLibraryViewsKeepsCoversAndCloudOnlyBooksVisible() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var coverPath = Path.Combine(scope.Paths.Data, "switch-cover.png");
        using (var bitmap = new SKBitmap(32, 48))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var stream = File.Create(coverPath);
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }

        var cards = Enumerable.Range(0, 24).Select(index => new BookCardViewModel(new Book
        {
            Id = Guid.NewGuid(), Title = $"Switch book {index}", Authors = "Test",
            CoverPath = "switch-cover.png",
            Files = [new BookFile { Id = Guid.NewGuid(), Format = "epub", RelativePath = $"missing-{index}.epub" }]
        }, scope.Paths.Data)).ToArray();
        cards[1].SetSyncStatus(BookSyncStatus.NotDownloaded);
        cards[^1].SetSyncStatus(BookSyncStatus.NotDownloaded);
        foreach (var card in cards) scope.Window.ViewModel.Books.Add(card);
        await Render();
        await Until(() => cards[1].CoverImage is not null);

        scope.Get<MenuItem>("LibraryListViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Render();
        var list = scope.Get<ListBox>("BookList");
        Assert.Contains(cards[1], list.ItemsSource!.Cast<object>());
        var cloudRow = Assert.Single(list.GetVisualDescendants().OfType<Border>(),
            row => row.Classes.Contains("bookRow") && ReferenceEquals(row.DataContext, cards[1]));
        Assert.True(cloudRow.IsEffectivelyVisible);
        Assert.True(cards[1].SyncNotDownloadedVisibility);
        await Until(() => cards[1].CoverImage is not null);

        var listViewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
        listViewer.Offset = new Vector(0, listViewer.Extent.Height);
        await Render();
        var lastCloudRow = Assert.Single(list.GetVisualDescendants().OfType<Border>(),
            row => row.Classes.Contains("bookRow") && ReferenceEquals(row.DataContext, cards[^1]));
        Assert.True(lastCloudRow.IsEffectivelyVisible);
        Assert.True(cards[^1].SyncNotDownloadedVisibility);
        await Until(() => cards[^1].CoverImage is not null);
        scope.Get<Button>("LibraryViewToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        scope.Get<Button>("LibraryViewToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        var grid = scope.Get<ListBox>("BookGrid");
        var visibleCards = grid.GetVisualDescendants().OfType<Grid>()
            .Where(control => control.Width == 154 && control.DataContext is BookCardViewModel)
            .Select(control => (BookCardViewModel)control.DataContext!).Distinct().ToArray();
        Assert.NotEmpty(visibleCards);
        await Until(() => visibleCards.All(card => card.CoverImage is not null));
        Assert.Contains(cards[1], visibleCards);
        var gridCard = Assert.Single(grid.GetVisualDescendants().OfType<Grid>(),
            control => control.Width == 154 && ReferenceEquals(control.DataContext, cards[1]));
        Assert.Contains(gridCard.GetVisualDescendants().OfType<Image>(), image => ReferenceEquals(image.Source, cards[1].CoverImage));
    });

    [Fact]
    public Task LibraryRefreshKeepsCardSelectionAndCoverAndShowsAsyncDetailCover() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var id = Guid.NewGuid();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={scope.Paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Books(Id,Title,Authors,CreatedAt,UpdatedAt) VALUES($id,'Cache book','Author',$time,$time);";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        await scope.Window.ViewModel.RefreshViewAsync();
        var card = Assert.Single(scope.Window.ViewModel.Books);
        scope.Get<ListBox>("BookGrid").SelectedItem = card;
        await Render();
        await card.CoverLoadingTask;
        var image = Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(card.CoverImage);
        Assert.Same(image, scope.Get<Image>("DetailCoverImage").Source);
        await scope.Window.ViewModel.RefreshViewAsync();
        Assert.Same(card, Assert.Single(scope.Window.ViewModel.Books));
        Assert.True(card.IsSelected);
        Assert.Same(image, card.CoverImage);
        command.CommandText = "UPDATE Books SET Title='New cover title';";
        await command.ExecuteNonQueryAsync();
        await scope.Window.ViewModel.RefreshViewAsync();
        await card.CoverLoadingTask;
        Assert.NotSame(image, card.CoverImage);
        Assert.Equal("New cover title", card.Title);
        Assert.Same(card.CoverImage, scope.Get<Image>("DetailCoverImage").Source);
    });

    [Fact]
    public Task LibraryGridRealizesOnlyTheViewportAndLoadsCoversOnDemand() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var coverPath = Path.Combine(scope.Paths.Data, "virtualized-cover.png");
        using (var bitmap = new SKBitmap(32, 48))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(58, 60, 54));
            using var stream = File.Create(coverPath);
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }

        var cards = Enumerable.Range(0, 240)
            .Select(index => new BookCardViewModel(new Book
            {
                Id = Guid.NewGuid(),
                Title = $"Virtualized {index}",
                Authors = "Layout test",
                CoverPath = "virtualized-cover.png",
                Files = [new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = Guid.NewGuid(),
                    Format = "epub",
                    RelativePath = $"virtualized-{index}.epub"
                }]
            }, scope.Paths.Data))
            .ToArray();

        try
        {
            foreach (var card in cards)
                scope.Window.ViewModel.Books.Add(card);
            await Render();

            await Until(() => cards.Any(card => card.CoverImage is not null));
            var grid = scope.Get<ListBox>("BookGrid");
            var panel = Assert.Single(grid.GetVisualDescendants().OfType<VirtualizingWrapPanel>());
            Assert.InRange(panel.RealizedCount, 1, cards.Length - 1);
            Assert.InRange(cards.Count(card => card.CoverImage is not null), 1, cards.Length - 1);

            var viewer = Assert.Single(grid.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height);
            var first = panel.FirstRealizedIndex;
            viewer.Offset = new Vector(0, viewer.Extent.Height);
            await Render();

            Assert.True(panel.FirstRealizedIndex > first);
            Assert.InRange(panel.RealizedCount, 1, cards.Length - 1);
        }
        finally
        {
            foreach (var card in cards)
                card.Dispose();
        }
    });
}
