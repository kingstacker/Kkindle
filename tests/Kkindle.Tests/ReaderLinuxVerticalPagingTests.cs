using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderLinuxVerticalPagingTests
{
    [Fact]
    public void EmptyTextProducesSingleEmptyPage()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate("   \r\n", charsPerColumn: 10, columnsPerPage: 3);

        var page = Assert.Single(pages);
        Assert.Equal(string.Empty, page.Text);
    }

    [Fact]
    public void FullColumnsSplitPagesOnCharacterBoundary()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "一二三四五六",
            charsPerColumn: 2,
            columnsPerPage: 2);

        Assert.Equal(2, pages.Count);
        Assert.Equal(("一二三四", 0), pages[0]);
        Assert.Equal(("五六", 4), pages[1]);
    }

    [Fact]
    public void NewlineStartsNextColumnWithoutSpacer()
    {
        // One column per page proves a single separator newline never creates
        // an extra empty page between the two paragraphs.
        var perColumnPages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "第一段\n第二段",
            charsPerColumn: 10,
            columnsPerPage: 1);
        Assert.Equal(2, perColumnPages.Count);
        Assert.Equal(("第一段", 0), perColumnPages[0]);
        Assert.Equal(("第二段", 4), perColumnPages[1]);

        // With room for both columns they share one page.
        var shared = ReaderLinuxVerticalPagingPolicy.Paginate(
            "第一段\n第二段",
            charsPerColumn: 10,
            columnsPerPage: 2);
        var page = Assert.Single(shared);
        Assert.Contains('\n', page.Text);
    }

    [Fact]
    public void DoubleNewlineKeepsInnerSpacerColumnButDropsTrailingOne()
    {
        const string text = "甲乙\n\n丙丁";
        // Column 0: 甲乙 / column 1: spacer / column 2: 丙丁 — all three fit
        // on one page and the inner blank column survives in the page text.
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            text,
            charsPerColumn: 10,
            columnsPerPage: 3);
        var page = Assert.Single(pages);
        Assert.Equal(text, page.Text);

        // Two columns per page: the spacer consumes a slot, so the second
        // paragraph moves to the next page without a leading blank column.
        var split = ReaderLinuxVerticalPagingPolicy.Paginate(
            text,
            charsPerColumn: 10,
            columnsPerPage: 2);
        Assert.Equal(2, split.Count);
        Assert.Equal("甲乙", split[0].Text);
        Assert.Equal("丙丁", split[1].Text);
    }

    [Fact]
    public void PageStartOffsetsStayStableAcrossReflow()
    {
        const string text = "春眠不觉晓处处闻啼鸟夜来风雨声花落知多少";
        var before = ReaderLinuxVerticalPagingPolicy.Paginate(
            text,
            charsPerColumn: 5,
            columnsPerPage: 2);
        var after = ReaderLinuxVerticalPagingPolicy.Paginate(
            text + "。",
            charsPerColumn: 5,
            columnsPerPage: 2);

        Assert.Equal(before[0].Start, after[0].Start);
        Assert.Equal(before[1].Start, after[1].Start);
        Assert.EndsWith("。", after[^1].Text);
    }

    [Fact]
    public void ClampsDegenerateMetricsToValidGrids()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "正文内容",
            charsPerColumn: 0,
            columnsPerPage: -3);

        Assert.True(pages.Count >= 1);
        Assert.StartsWith("正", pages[0].Text);
    }

    [Fact]
    public void ShortNumericRunsOccupyOneVerticalCell()
    {
        var units = ReaderLinuxVerticalTextUnits.Tokenize("甲800乙1–4丙");

        Assert.Equal(
            [(0, 1, false), (1, 3, true), (4, 1, false), (5, 3, true), (8, 1, false)],
            units.Select(unit => (unit.Offset, unit.Length, unit.IsCombined)));
    }

    [Fact]
    public void PaginationDoesNotSplitShortNumericRuns()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲800乙",
            charsPerColumn: 2,
            columnsPerPage: 1);

        Assert.Equal(2, pages.Count);
        Assert.Equal("甲800", pages[0].Text);
        Assert.Equal("乙", pages[1].Text);
    }

    [Fact]
    public void ParagraphIndentReservesTwoRowsOnlyAtParagraphStarts()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲乙\n丙丁",
            charsPerColumn: 3,
            columnsPerPage: 1,
            paragraphIndent: true);

        Assert.Collection(
            pages,
            page => Assert.Equal("甲", page.Text),
            page => Assert.Equal("乙", page.Text),
            page => Assert.Equal("丙", page.Text),
            page => Assert.Equal("丁", page.Text));

        var continuation = ReaderLinuxVerticalPagingPolicy.Paginate(
            "丙丁",
            charsPerColumn: 3,
            columnsPerPage: 1,
            paragraphIndent: true,
            startsWithParagraph: false);
        var continuationPage = Assert.Single(continuation);
        Assert.Equal("丙丁", continuationPage.Text);
    }
}
