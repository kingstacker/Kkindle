using System.Text;
using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class KindleTransferPolicyTests
{
    [Theory]
    [InlineData("converted.azw3", true)]
    [InlineData("0123456789abcdef0123456789abcdef.azw3", true)]
    [InlineData("雪的练习生.azw3", false)]
    public void LegacyGeneratedAzw3NamesRequireMetadataRepair(string fileName, bool expected)
    {
        var file = new BookFile { Format = "azw3" };

        Assert.Equal(expected, KindleTransferPolicy.RequiresLegacyMetadataRepair(file, fileName));
    }

    [Fact]
    public void PrefersAzw3OverEpubForUsbTransfer()
    {
        var epub = new BookFile { Format = "epub" };
        var azw3 = new BookFile { Format = "azw3" };

        Assert.Same(azw3, KindleTransferPolicy.SelectPreferred([epub, azw3]));
    }

    [Fact]
    public void CreatesConciseUtf8BoundedKindleFileName()
    {
        var title = "毛姆短篇小说全集（套装共7册，“英语文学中最好的短篇故事”——这是很长的副标题）";

        var fileName = KindleTransferPolicy.CreateSafeFileName(title, ".azw3");

        Assert.Equal("毛姆短篇小说全集.azw3", fileName);
        Assert.True(Encoding.UTF8.GetByteCount(fileName) <= 120);
    }

    [Fact]
    public void ConvertsEpubAndMobiButKeepsExistingAzw3AndPdf()
    {
        Assert.False(KindleTransferPolicy.RequiresConversionToAzw3(new BookFile { Format = "azw3" }));
        Assert.True(KindleTransferPolicy.RequiresConversionToAzw3(new BookFile { Format = "epub" }));
        Assert.True(KindleTransferPolicy.RequiresConversionToAzw3(new BookFile { Format = "mobi" }));
        Assert.False(KindleTransferPolicy.RequiresConversionToAzw3(new BookFile { Format = "pdf" }));
    }
}
