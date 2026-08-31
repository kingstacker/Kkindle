using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderPlatformLayoutPolicyTests
{
    [Fact]
    public void VerticalWritingIsPaginatedOnEveryPlatform()
    {
        // Linux used to force vertical writing back onto a continuous flow;
        // the calibrated edge masks and glyph-phase probes now paginate it
        // like every other platform, so the layout defaults decide.
        var settings = new ReaderLayoutSettings(
            FlowMode: 0,
            VerticalWriting: true,
            TwoPageMode: true);

        var normalized = ReaderLayoutDefaults.Normalize(settings);

        Assert.True(normalized.VerticalWriting);
        Assert.Equal(1, normalized.FlowMode);
        Assert.False(normalized.TwoPageMode);
    }

    [Fact]
    public void VerticalFallbackPagePreservesRequestedWhitespaceOnAllFourEdges()
    {
        var insets = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
            viewportWidth: 1000,
            viewportHeight: 700,
            requestedInset: 68);

        Assert.Equal(68, insets.Horizontal);
        Assert.Equal(68, insets.Vertical);
    }

    [Fact]
    public void VerticalFallbackPageCannotOverflowATinyViewport()
    {
        var insets = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
            viewportWidth: 320,
            viewportHeight: 320,
            requestedInset: 160);

        Assert.Equal(40, insets.Horizontal);
        Assert.Equal(70, insets.Vertical);
        Assert.Equal(240, 320 - insets.Horizontal * 2);
        Assert.Equal(180, 320 - insets.Vertical * 2);
    }

    [Fact]
    public void VerticalPageCentersBodyWhenMaxWidthIsNarrowerThanViewport()
    {
        var insets = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
            viewportWidth: 1200,
            viewportHeight: 700,
            requestedInset: 24,
            requestedMaxWidth: 800);

        Assert.Equal(200, insets.Horizontal);
        Assert.Equal(24, insets.Vertical);
        Assert.Equal(800, 1200 - insets.Horizontal * 2);
    }

    [Fact]
    public void VerticalPageKeepsRequestedMarginsWhenMaxWidthExceedsAvailableBody()
    {
        var insets = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
            viewportWidth: 1000,
            viewportHeight: 700,
            requestedInset: 68,
            requestedMaxWidth: 1200);

        Assert.Equal(68, insets.Horizontal);
        Assert.Equal(68, insets.Vertical);
        Assert.Equal(864, 1000 - insets.Horizontal * 2);
    }
}
