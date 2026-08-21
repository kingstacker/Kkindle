using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderLinuxTextFallbackPagingPolicyTests
{
    [Fact]
    public void SidebarReflowKeepsPageContainingPreviousFirstLine()
    {
        var newPageOffsets = new[] { 0, 360, 720, 1080, 1440 };

        var page = ReaderLinuxTextFallbackPagingPolicy.ResolveAnchorPageIndex(
            newPageOffsets,
            anchorOffset: 950,
            spreadSize: 1);

        Assert.Equal(2, page);
    }

    [Fact]
    public void ExplicitReflowBoundaryKeepsAnchorAsPageStart()
    {
        var newPageOffsets = new[] { 0, 360, 720, 950, 1310 };

        var page = ReaderLinuxTextFallbackPagingPolicy.ResolveAnchorPageIndex(
            newPageOffsets,
            anchorOffset: 950,
            spreadSize: 1);

        Assert.Equal(3, page);
        Assert.Equal(950, newPageOffsets[page]);
    }

    [Fact]
    public void SidebarReflowAlignsAnchoredPageToTwoPageSpread()
    {
        var newPageOffsets = new[] { 0, 240, 480, 720, 960, 1200 };

        var page = ReaderLinuxTextFallbackPagingPolicy.ResolveAnchorPageIndex(
            newPageOffsets,
            anchorOffset: 800,
            spreadSize: 2);

        Assert.Equal(2, page);
    }

    [Fact]
    public void PreviousChapterEndSurvivesSecondLayoutRebuild()
    {
        var firstPass = ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
            currentPageIndex: 0,
            scrollPosition: 0,
            moveToChapterEnd: true,
            pageCount: 12,
            spreadSize: 1);
        var secondPass = ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
            currentPageIndex: firstPass,
            scrollPosition: 0,
            moveToChapterEnd: false,
            pageCount: 12,
            spreadSize: 1);

        Assert.Equal(11, firstPass);
        Assert.Equal(11, secondPass);
    }

    [Fact]
    public void ForwardChapterNavigationStaysAtFirstPage()
    {
        var page = ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
            currentPageIndex: 0,
            scrollPosition: 7,
            moveToChapterEnd: false,
            pageCount: 12,
            spreadSize: 1);

        Assert.Equal(0, page);
    }

    [Fact]
    public void TwoPageEndUsesLastCompleteSpreadStart()
    {
        var page = ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
            currentPageIndex: -1,
            scrollPosition: -1,
            moveToChapterEnd: true,
            pageCount: 11,
            spreadSize: 2);

        Assert.Equal(8, page);
    }
}
