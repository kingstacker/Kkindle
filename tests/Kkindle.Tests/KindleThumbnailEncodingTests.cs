using Kkindle.Infrastructure;
using SkiaSharp;

namespace Kkindle.Tests;

public sealed class KindleThumbnailEncodingTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Webp)]
    [InlineData(SKEncodedImageFormat.Png)]
    public void ThumbnailIsRealJpegWithTransparencyFlattenedOntoWhite(SKEncodedImageFormat format)
    {
        using var source = new SKBitmap(20, 30);
        source.Erase(SKColors.Transparent);
        source.SetPixel(10, 15, SKColors.Red);
        using var input = source.Encode(format, 100);
        var result = KindleThumbnailService.EncodeJpeg(input.ToArray());
        Assert.NotNull(result);
        Assert.Equal(0xff, result[0]);
        Assert.Equal(0xd8, result[1]);
        using var decoded = SKBitmap.Decode(result);
        Assert.Equal(20, decoded.Width);
        Assert.Equal(30, decoded.Height);
        var background = decoded.GetPixel(0, 0);
        Assert.True(background.Red > 240 && background.Green > 240 && background.Blue > 240);
    }

    [Fact]
    public void InvalidImageDoesNotProduceAnInvalidThumbnail()
    {
        Assert.Null(KindleThumbnailService.EncodeJpeg([1, 2, 3, 4]));
    }
}
