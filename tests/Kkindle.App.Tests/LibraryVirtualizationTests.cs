using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kkindle.Core;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
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
