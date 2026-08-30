using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class BookFormatConversionPolicyTests
{
    [Theory]
    [InlineData("epub")]
    [InlineData(".AZW3")]
    [InlineData(" PDF ")]
    [InlineData("mobi")]
    public void RecognizesConvertibleFormats(string format)
    {
        Assert.True(BookFormatConversionPolicy.IsConvertibleFormat(format));
    }

    [Fact]
    public void RecognizesKindleDictionaryFormatsAsCalibreInputOnly()
    {
        Assert.True(BookFormatConversionPolicy.IsCalibreInputFormat(".KFX"));
        Assert.True(BookFormatConversionPolicy.IsCalibreInputFormat(".AZW"));
        Assert.True(BookFormatConversionPolicy.IsCalibreInputFormat(".PRC"));
        Assert.False(BookFormatConversionPolicy.IsConvertibleFormat("kfx"));
        Assert.False(BookFormatConversionPolicy.IsConvertibleFormat("azw"));
        Assert.False(BookFormatConversionPolicy.IsConvertibleFormat("prc"));
    }

    [Fact]
    public void SelectsPreferredSourceAndSkipsRequestedTarget()
    {
        var pdf = new BookFile { Format = "pdf" };
        var azw3 = new BookFile { Format = "azw3" };
        var epub = new BookFile { Format = "epub" };
        var mobi = new BookFile { Format = "mobi" };

        Assert.Same(epub, BookFormatConversionPolicy.SelectSource([pdf, azw3, epub], "pdf"));
        Assert.Same(epub, BookFormatConversionPolicy.SelectSource([pdf, epub], "azw3"));
        Assert.Same(pdf, BookFormatConversionPolicy.SelectSource([pdf], "epub"));
        Assert.Same(mobi, BookFormatConversionPolicy.SelectSource([mobi], "epub"));
        Assert.Null(BookFormatConversionPolicy.SelectSource([epub], "EPUB"));
    }
}
