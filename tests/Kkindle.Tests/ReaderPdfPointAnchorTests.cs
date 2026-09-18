using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class ReaderPdfPointAnchorTests
{
    [Theory]
    [InlineData(0.25, 0.75)]
    [InlineData(0, 1)]
    public void PointAnnotationAnchorsRoundTrip(double x, double y)
    {
        var encoded = ReaderPdfPointAnchor.Encode(x, y);

        Assert.True(ReaderPdfPointAnchor.TryParse(encoded, out var actualX, out var actualY));
        Assert.Equal(x, actualX, 12);
        Assert.Equal(y, actualY, 12);
    }
}
