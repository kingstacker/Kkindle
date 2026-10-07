using System.IO.Compression;
using Kkindle.Infrastructure;
using SkiaSharp;

namespace Kkindle.Tests;

public sealed class TitleCoverTests
{
    [Fact]
    public void OnlyTheRedSealUsesColor()
    {
        using var bitmap = SKBitmap.Decode(TitleCoverService.CreatePng("黄昏分界"));
        var redPixels = 0;
        foreach (var pixel in bitmap.Pixels)
        {
            if (pixel.Red == pixel.Green && pixel.Green == pixel.Blue) continue;
            Assert.True(pixel.Red > pixel.Green && pixel.Green >= pixel.Blue);
            redPixels++;
        }
        Assert.InRange(redPixels, 100, 2000);
    }

    [Fact]
    public async Task RecognizesSavedTxtArtworkButPreservesCustomReplacement()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var txt = Path.Combine(root, "黄昏分界.txt");
            var epub = Path.Combine(root, "book.epub");
            var cover = Path.Combine(root, "cover.png");
            await File.WriteAllTextAsync(txt, "第一章 开始\n正文。");
            await TxtToEpubService.ConvertAsync(txt, epub);
            using (var archive = ZipFile.OpenRead(epub)) archive.GetEntry("OEBPS/cover.png")!.ExtractToFile(cover);
            Assert.True(TitleCoverService.IsGeneratedTxtCover(epub, cover));
            await File.WriteAllBytesAsync(cover, TitleCoverService.CreatePng("自定义封面"));
            Assert.False(TitleCoverService.IsGeneratedTxtCover(epub, cover));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task InvalidEpubDoesNotBreakCoverDetection()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "broken.epub");
            using (var zip = ZipFile.Open(epub, ZipArchiveMode.Create))
                zip.CreateEntry("OEBPS/content.opf");
            var bytes = await File.ReadAllBytesAsync(epub);
            // The end record claims two entries while the central directory contains one.
            bytes[^12] = 2;
            await File.WriteAllBytesAsync(epub, bytes);
            Assert.False(TitleCoverService.IsGeneratedTxtCover(epub, null));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
