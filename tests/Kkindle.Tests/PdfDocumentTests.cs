using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using SkiaSharp;
using Xunit;

namespace Kkindle.Tests;

public sealed class PdfDocumentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KkindlePdfTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MetadataUsesTheFirstPageAsItsCoverAndReadsUnicodeMetadata()
    {
        var path = PdfFixture.Write(_directory);
        var metadata = await new BookMetadataService().ReadMetadataAsync(path);
        Assert.Equal("PDF 阅读测试", metadata.Title);
        Assert.Equal("Kkindle Tests", metadata.Authors);
        Assert.Equal(".png", metadata.CoverExtension);
        using var image = SKBitmap.Decode(metadata.CoverBytes);
        Assert.Equal(480, image.Width);
        Assert.Equal(720, image.Height);
        var pixel = image.GetPixel(20, 20);
        Assert.InRange(pixel.Red, 40, 55);
        Assert.InRange(pixel.Green, 95, 110);
        Assert.InRange(pixel.Blue, 70, 85);
    }

    [Fact]
    public async Task TextOffsetsAndOutlinesShareTheRenderedPageMapIncludingScans()
    {
        var path = PdfFixture.Write(_directory);
        using var pdf = new PdfDocumentService(path);
        var info = pdf.ReadInfo();
        Assert.Equal(4, info.PageCount);
        Assert.Equal(new[] { "Cover", "Chapter Two", "Nested section", "Scanned page" }, info.Outline.Select(item => item.Title));
        Assert.Equal(new[] { 0, 0, 1, 0 }, info.Outline.Select(item => item.Level));
        Assert.Equal(new[] { 1, 2, 2, 3 }, info.Outline.Select(item => item.PageNumber));
        Assert.InRange(info.Outline[2].Top!.Value, 0.59, 0.61);
        var index = await new PdfTextService().ExtractAsync(path);
        Assert.Equal(4, index.Count);
        Assert.Empty(index[2].Text);
        var content = pdf.RenderPage(2).Content;
        Assert.Equal(content.Text, index[1].Text);
        var hits = PdfTextService.Search(index, "this text");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal("this text", content.Text.Substring(hit.MatchIndex, 9)));
        Assert.All(content.Glyphs, glyph =>
        {
            Assert.InRange(glyph.Offset, 0, content.Text.Length - 1);
            Assert.InRange(glyph.Bounds.X, 0, 1);
            Assert.InRange(glyph.Bounds.Y, 0, 1);
        });
        using var scan = SKBitmap.Decode(pdf.RenderPage(3, 400, 600).Png);
        Assert.NotEqual(SKColors.White, scan.GetPixel(100, 100));
        Assert.NotEqual(scan.GetPixel(100, 100), scan.GetPixel(300, 500));
    }

    [Fact]
    public void RotatedCropTextBoundsLandOnRenderedInk()
    {
        using var pdf = new PdfDocumentService(PdfFixture.Write(_directory));
        var page = pdf.RenderPage(4, 1200, 1200);
        Assert.Equal(600, page.Content.Width);
        Assert.Equal(400, page.Content.Height);
        using var image = SKBitmap.Decode(page.Png);
        foreach (var glyph in page.Content.Glyphs)
        {
            var bounds = glyph.Bounds;
            var dark = 0;
            for (var y = Math.Max(0, (int)(bounds.Y * image.Height) - 1); y < Math.Min(image.Height, (bounds.Y + bounds.Height) * image.Height + 1); y++)
                for (var x = Math.Max(0, (int)(bounds.X * image.Width) - 1); x < Math.Min(image.Width, (bounds.X + bounds.Width) * image.Width + 1); x++)
                    if (image.GetPixel(x, y).Red < 160) dark++;
            Assert.True(dark > 0, $"Glyph {glyph.Offset} did not intersect rendered ink: {bounds}");
        }
    }

    [Fact]
    public async Task ConcurrentCoverAndReaderRenderingDoNotCorruptDocuments()
    {
        var path = PdfFixture.Write(_directory);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            using var pdf = new PdfDocumentService(path);
            var page = pdf.RenderPage(index % 4 + 1, 200, 300);
            Assert.NotEmpty(page.Png);
            Assert.Equal(index % 4 + 1, page.PageNumber);
        })));
    }

    [Fact]
    public void TextOnlyIndexPreservesOffsetsWithoutRequiringGlyphGeometry()
    {
        using var pdf = new PdfDocumentService(PdfFixture.Write(_directory));
        for (var page = 1; page <= pdf.PageCount; page++)
            Assert.Equal(pdf.ReadPage(page).Text, pdf.ReadPageText(page));
    }

    [Fact]
    public void HighResolutionRegionsAreRedrawnFromPdfAndUseOnlyTheClippedBuffer()
    {
        using var pdf = new PdfDocumentService(PdfFixture.Write(_directory));
        var low = pdf.RenderRegion(2, 400, 600, 35, 45, 150, 32);
        var high = pdf.RenderRegion(2, 1600, 2400, 140, 180, 600, 128);
        Assert.Equal(600 * 128 * 4, high.Pixels.Length);
        var differences = 0;
        for (var y = 0; y < high.Height; y++)
            for (var x = 0; x < high.Width; x++)
            {
                var actual = high.Pixels[(y * high.Width + x) * 4];
                var enlarged = low.Pixels[((y / 4) * low.Width + x / 4) * 4];
                if (Math.Abs(actual - enlarged) > 20) differences++;
            }
        Assert.True(differences > 500, "Zoom must redraw glyph edges, rather than enlarge the original raster.");
        var extreme = pdf.RenderRegion(2, 16000, 24000, 1600, 2200, 640, 640);
        Assert.Equal(640 * 640 * 4, extreme.Pixels.Length);
        Assert.Contains(extreme.Pixels, pixel => pixel < 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.RenderRegion(2, 400, 600, 350, 0, 100, 100));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ViewRotationKeepsTheOriginalImageAndClippedRegionAligned(int rotation)
    {
        using var pdf = new PdfDocumentService(PdfFixture.Write(_directory));
        var original = pdf.RenderRegion(3, 400, 600, 0, 0, 400, 600);
        var width = rotation % 180 == 0 ? 400 : 600;
        var height = rotation % 180 == 0 ? 600 : 400;
        var rotated = pdf.RenderRegion(3, width, height, 0, 0, width, height, rotation: rotation);
        Assert.Equal(rotation, rotated.Rotation);
        foreach (var (x, y) in new[] { (20, 20), (350, 20), (20, 550), (350, 550) })
        {
            var (rx, ry) = rotation switch
            {
                90 => (599 - y, x),
                180 => (399 - x, 599 - y),
                _ => (y, 399 - x)
            };
            Assert.Equal(original.Pixels.AsSpan((y * 400 + x) * 4, 4).ToArray(),
                rotated.Pixels.AsSpan((ry * width + rx) * 4, 4).ToArray());
        }
        var clipped = pdf.RenderRegion(3, width, height, 64, 80, 128, 96, rotation: rotation);
        for (var row = 0; row < clipped.Height; row++)
            Assert.Equal(rotated.Pixels.AsSpan(((row + 80) * width + 64) * 4, 128 * 4).ToArray(),
                clipped.Pixels.AsSpan(row * 128 * 4, 128 * 4).ToArray());
    }

    [Fact]
    public async Task ExistingPdfBooksReceiveMissingCoversWithoutReplacingCustomCovers()
    {
        var paths = new AppPaths(Path.Combine(_directory, "library"));
        paths.EnsureDirectories();
        var library = new SqliteBookLibraryService(paths, new BookMetadataService());
        await library.InitializeAsync();
        await library.ImportAsync([PdfFixture.Write(_directory)]);
        var book = Assert.Single(await library.SearchAsync());
        book.CoverPath = null;
        book.Title = "My custom title";
        await library.UpdateMetadataAsync(book);
        var generated = await library.EnsurePdfCoverAsync(book.Id);
        Assert.NotNull(generated);
        Assert.True(File.Exists(Path.Combine(paths.Data, generated)));
        book = (await library.GetBookAsync(book.Id))!;
        Assert.Equal("My custom title", book.Title);
        Assert.Equal(generated, book.CoverPath);
        var custom = Path.Combine(paths.Covers, "custom.png");
        await File.WriteAllBytesAsync(custom, [1, 2, 3]);
        book.CoverPath = Path.GetRelativePath(paths.Data, custom);
        await library.UpdateMetadataAsync(book);
        Assert.Equal(book.CoverPath, await library.EnsurePdfCoverAsync(book.Id));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(custom));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task EmbeddedChineseFontHasSearchableSelectableText()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/Kkindle.App/Assets/Fonts/KingHwaOldSong-v3.0.ttf"))) root = root.Parent;
        Assert.NotNull(root);
        using var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(await File.ReadAllBytesAsync(Path.Combine(root.FullName, "src/Kkindle.App/Assets/Fonts/KingHwaOldSong-v3.0.ttf")));
        const string chinese = "二：阅读标注测试";
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(chinese, 24, new UglyToad.PdfPig.Core.PdfPoint(40, 700), font);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "中文字体.pdf");
        await File.WriteAllBytesAsync(path, builder.Build());
        using var pdf = new PdfDocumentService(path);
        var page = pdf.RenderPage(1);
        Assert.Contains(chinese, page.Content.Text);
        Assert.Equal(chinese.Length, page.Content.Glyphs.Count);
        Assert.All(page.Content.Glyphs, glyph => Assert.True(glyph.Bounds.Width > 0 && glyph.Bounds.Height > 0));
        Assert.Single(PdfTextService.Search(await new PdfTextService().ExtractAsync(path), "标注测试"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
