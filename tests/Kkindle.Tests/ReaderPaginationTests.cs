using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderPaginationTests
{
    [Theory]
    [InlineData(true, false, -1)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, -1)]
    public void ClickDirectionMirrorsOnlyVerticalWriting(
        bool onLeft,
        bool verticalWriting,
        int expectedDirection)
    {
        Assert.Equal(
            expectedDirection,
            ReaderPaginationPolicy.GetClickDirection(onLeft, verticalWriting));
    }

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(-1, false, -1)]
    [InlineData(1, true, -1)]
    [InlineData(-1, true, 1)]
    [InlineData(0, true, 0)]
    public void VisualTurnDirectionMirrorsOnlyVerticalWriting(
        int navigationDirection,
        bool verticalWriting,
        int expectedDirection)
    {
        Assert.Equal(
            expectedDirection,
            ReaderPaginationPolicy.GetVisualTurnDirection(
                navigationDirection,
                verticalWriting));
    }

    [Fact]
    public void ColumnWidthSupportsSingleAndTwoPageSpreads()
    {
        Assert.Equal(952, ReaderPaginationDefaults.GetColumnWidth(1000), precision: 6);
        Assert.Equal(452, ReaderPaginationDefaults.GetColumnWidth(1000, pagesPerView: 2), precision: 6);
        Assert.Equal(452, ReaderPaginationDefaults.GetColumnWidth(1000, pagesPerView: 99), precision: 6);
    }

    [Fact]
    public void ColumnWidthUsesConfiguredReadingMargin()
    {
        Assert.Equal(
            864,
            ReaderPaginationDefaults.GetColumnWidth(1000, horizontalPadding: 68),
            precision: 6);
        Assert.Equal(
            364,
            ReaderPaginationDefaults.GetColumnWidth(1000, pagesPerView: 2, horizontalPadding: 68),
            precision: 6);

        var column = ReaderPaginationDefaults.GetColumnWidth(
            1000,
            pagesPerView: 2,
            horizontalPadding: 68);
        Assert.Equal(1000, column * 2 + 68 * 4, precision: 6);
    }

    [Fact]
    public void TwoPageColumnsAndInsetsFillExactlyOneViewport()
    {
        const double viewport = 1000;
        var column = ReaderPaginationDefaults.GetColumnWidth(viewport, pagesPerView: 2);

        var spreadWidth = ReaderPaginationDefaults.HorizontalPadding * 2
            + column * 2
            + ReaderPaginationDefaults.ColumnGap;

        Assert.Equal(viewport, spreadWidth, precision: 6);
    }

    [Fact]
    public void SnapStartsAtViewportOriginSoPageMarginsStayEven()
    {
        var snapped = ReaderPaginationPolicy.SnapScrollLeft(
            scrollLeft: 0,
            clientWidth: 1000,
            scrollWidth: 6000);

        Assert.Equal(0, snapped);
        Assert.False(ReaderPaginationPolicy.CanTurn(0, -1, 1000, 6000));
        Assert.True(ReaderPaginationPolicy.CanTurn(0, 1, 1000, 6000));
    }

    [Fact]
    public void SnapUsesTheScrollContainerWidth()
    {
        var snapped = ReaderPaginationPolicy.SnapScrollLeft(
            scrollLeft: 1130,
            clientWidth: 997.5,
            scrollWidth: 6000);

        Assert.Equal(997.5, snapped, precision: 6);
    }

    [Theory]
    [InlineData(-1, 1000, 0)]
    [InlineData(1, 1000, 2000)]
    public void TurnTargetAdvancesByOneViewport(int direction, double expectedCurrent, double expectedTarget)
    {
        var target = ReaderPaginationPolicy.GetTurnTarget(
            scrollLeft: expectedCurrent,
            direction,
            clientWidth: 1000,
            scrollWidth: 6000);

        Assert.Equal(expectedTarget, target, precision: 6);
    }

    [Fact]
    public void TurnTargetClampsAtTheLastFullPageBoundaryBeforeTrailingInset()
    {
        var target = ReaderPaginationPolicy.GetTurnTarget(
            scrollLeft: 4000,
            direction: 1,
            clientWidth: 1000,
            scrollWidth: 5024);

        Assert.Equal(4000, target);
        Assert.False(ReaderPaginationPolicy.CanTurn(4000, 1, 1000, 5024));
        Assert.True(ReaderPaginationPolicy.CanTurn(4000, -1, 1000, 5024));
    }

    [Fact]
    public void LastPageBoundaryRemovesTheTrailingBodyInset()
    {
        Assert.Equal(4000, ReaderPaginationPolicy.GetMaxScrollLeft(1000, 5000));
        Assert.Equal(4000, ReaderPaginationPolicy.GetLastPageScrollLeft(1000, 5024));
    }

    [Fact]
    public void InvalidViewportMetricsFailClosed()
    {
        Assert.Equal(0, ReaderPaginationPolicy.SnapScrollLeft(20, 0, 1000));
        Assert.False(ReaderPaginationPolicy.CanTurn(0, 1, double.NaN, 1000));
        Assert.Equal(0, ReaderPaginationPolicy.GetTurnTarget(0, 1, double.PositiveInfinity, 1000));
    }
}
