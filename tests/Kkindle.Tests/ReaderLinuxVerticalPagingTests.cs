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
    public void OnlyTwoDigitNumericRunsOccupyOneVerticalCell()
    {
        var units = ReaderLinuxVerticalTextUnits.Tokenize("甲12乙800丙1–4丁12345戊");

        Assert.Equal(
            [
                (0, 1, false, false),
                (1, 2, true, false),
                (3, 1, false, false),
                (4, 3, false, true),
                (7, 1, false, false),
                (8, 3, false, true),
                (11, 1, false, false),
                (12, 5, false, true),
                (17, 1, false, false)
            ],
            units.Select(unit => (unit.Offset, unit.Length, unit.IsCombined, unit.IsSidewaysRun)));
    }

    [Fact]
    public void LatinWordsAndPhrasesAreSingleSidewaysRuns()
    {
        // The fallback renderer used to fall through to one rotated cell per
        // Latin letter, so an English phrase looked nothing like the same text
        // in the WebKit reader. Tokenizing matches the sanitizer: connectors
        // hold a word together and a short ASCII gap merges neighbours into
        // one phrase.
        var units = ReaderLinuxVerticalTextUnits.Tokenize("他说don't go丙AT&T乙");

        Assert.Equal(
            [
                (0, 1, false),
                (1, 1, false),
                (2, 8, true),
                (10, 1, false),
                (11, 4, true),
                (15, 1, false)
            ],
            units.Select(unit => (unit.Offset, unit.Length, unit.IsSidewaysRun)));
    }

    [Fact]
    public void DigitOnlyNeighboursNeverMergeIntoOnePhrase()
    {
        // "12, 34" must keep two tate-chu-yoko cells; merging would turn two
        // upright squares into one long sideways run.
        var units = ReaderLinuxVerticalTextUnits.Tokenize("甲12, 34乙");

        Assert.Equal(
            [
                (0, 1, false, false),
                (1, 2, true, false),
                (3, 1, false, false),
                (4, 1, false, false),
                (5, 2, true, false),
                (7, 1, false, false)
            ],
            units.Select(unit => (unit.Offset, unit.Length, unit.IsCombined, unit.IsSidewaysRun)));
    }

    [Fact]
    public void SidewaysRunsConsumeTheirNaturalInlineExtentNotOneCellPerCharacter()
    {
        // A sideways run keeps horizontal metrics, so it is roughly half as
        // long as its character count in CJK cells. Charging one full cell per
        // character made the fallback break columns an order away from where
        // the WebKit reader breaks them, and left a wide empty gap after every
        // number.
        var units = ReaderLinuxVerticalTextUnits.Tokenize("12345");
        var run = Assert.Single(units);

        Assert.True(run.IsSidewaysRun);
        Assert.Equal(3, ReaderLinuxVerticalTextUnits.GetVisualRows(run, charsPerColumn: 20));
        // Never longer than the column it has to live in.
        Assert.Equal(2, ReaderLinuxVerticalTextUnits.GetVisualRows(run, charsPerColumn: 2));
    }

    [Fact]
    public void PaginationDoesNotSplitTwoDigitRuns()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲12乙",
            charsPerColumn: 2,
            columnsPerPage: 1);

        Assert.Equal(2, pages.Count);
        Assert.Equal("甲12", pages[0].Text);
        Assert.Equal("乙", pages[1].Text);
    }

    [Fact]
    public void PaginationDoesNotSplitLongNumericRuns()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲12345乙",
            charsPerColumn: 3,
            columnsPerPage: 1);

        Assert.Equal(3, pages.Count);
        Assert.Equal("甲", pages[0].Text);
        Assert.Equal("12345", pages[1].Text);
        Assert.Equal("乙", pages[2].Text);
    }

    [Fact]
    public void ExactFitParagraphBreakDoesNotCreateAnEmptySpacerColumn()
    {
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲乙\n丙丁",
            charsPerColumn: 2,
            columnsPerPage: 1,
            paragraphIndent: false);

        Assert.Equal(2, pages.Count);
        Assert.Equal(("甲乙", 0), pages[0]);
        Assert.Equal(("丙丁", 3), pages[1]);
    }

    [Fact]
    public void KinsokuKeepsOpeningAndClosingPunctuationWithTheirNeighbors()
    {
        var closing = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲乙。丙",
            charsPerColumn: 2,
            columnsPerPage: 1,
            paragraphIndent: false);
        Assert.Equal("甲", closing[0].Text);
        Assert.Equal("乙。", closing[1].Text);

        var opening = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲（乙",
            charsPerColumn: 2,
            columnsPerPage: 1,
            paragraphIndent: false);
        Assert.Equal("甲", opening[0].Text);
        Assert.Equal("（乙", opening[1].Text);
    }

    [Fact]
    public void KinsokuKeepsAWholeClosingClusterOutOfTheNextColumnTop()
    {
        // Checking only the single next unit let "」。" split: the bracket
        // still fit the column, so the full stop was stranded alone at the top
        // of the following one. The cluster has to be weighed as a whole.
        var pages = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲乙丙」。丁",
            charsPerColumn: 4,
            columnsPerPage: 1,
            paragraphIndent: false);

        Assert.Equal(2, pages.Count);
        Assert.Equal("甲乙", pages[0].Text);
        Assert.Equal("丙」。丁", pages[1].Text);
    }

    [Fact]
    public void KinsokuAlsoAppliesToStandaloneAsciiPunctuation()
    {
        // ASCII punctuation is its own one-cell unit in the fallback, so the
        // prohibition sets have to cover it as well as the CJK marks.
        var closing = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲乙?丙",
            charsPerColumn: 2,
            columnsPerPage: 1,
            paragraphIndent: false);
        Assert.Equal("甲", closing[0].Text);
        Assert.Equal("乙?", closing[1].Text);

        var opening = ReaderLinuxVerticalPagingPolicy.Paginate(
            "甲(乙",
            charsPerColumn: 2,
            columnsPerPage: 1,
            paragraphIndent: false);
        Assert.Equal("甲", opening[0].Text);
        Assert.Equal("(乙", opening[1].Text);
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
