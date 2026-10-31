using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class PdfNavigationTargetTests
{
    [Theory]
    [InlineData("page=3&top=0.45&offset=8", 3, 0.45, 8)]
    [InlineData("page=-2&top=2&offset=-3", 1, 1, 0)]
    [InlineData("page=bad&top=NaN&offset=bad", 1, 0, -1)]
    [InlineData("other=2", 1, 0, -1)]
    public void ReadsAndBoundsDestinations(string fragment, int page, double top, int offset)
    {
        var uri = new Uri("file:///book.pdf#" + fragment);
        Assert.Equal(page, PdfNavigationTarget.ReadPage(uri));
        Assert.Equal(top, PdfNavigationTarget.ReadTop(uri));
        Assert.Equal(offset, PdfNavigationTarget.ReadOffset(uri));
    }
}
