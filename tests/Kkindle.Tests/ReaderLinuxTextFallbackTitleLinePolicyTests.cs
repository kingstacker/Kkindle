using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderLinuxTextFallbackTitleLinePolicyTests
{
    [Theory]
    [InlineData("小孩的玩意儿\n\n第一阵恶心感消失了。", "小孩的玩意儿", 0, 6)]
    [InlineData("  水世界\n我们再也不会有星际旅行了。", "水世界", 2, 3)]
    public void LeadingTocTitleCanRecoverMissingPageMetadata(
        string pageText,
        string tocTitle,
        int expectedStart,
        int expectedLength)
    {
        Assert.True(ReaderLinuxTextFallbackTitleLinePolicy.TryFindLeadingTitleRange(
            pageText,
            tocTitle,
            out var start,
            out var length));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void BodyTextWithTitlePrefixIsNotPromoted()
    {
        Assert.False(ReaderLinuxTextFallbackTitleLinePolicy.TryFindLeadingTitleRange(
            "水世界之外的正文",
            "水世界",
            out _,
            out _));
    }

    [Fact]
    public void TitleAtPageStartIsPaintedOnlyOnFirstVisualLine()
    {
        Assert.True(ReaderLinuxTextFallbackTitleLinePolicy.IsTitleLine(
            chapterTitleStart: 0,
            chapterTitleLength: 4,
            lineStart: 0,
            lineLength: 5,
            newLineLength: 1,
            lineIndex: 0));

        for (var lineIndex = 1; lineIndex < 20; lineIndex++)
        {
            Assert.False(ReaderLinuxTextFallbackTitleLinePolicy.IsTitleLine(
                chapterTitleStart: 0,
                chapterTitleLength: 4,
                lineStart: 0,
                lineLength: 18,
                newLineLength: 0,
                lineIndex));
        }
    }

    [Fact]
    public void EmptyFirstLineIsNotPaintedAsTitle()
    {
        Assert.False(ReaderLinuxTextFallbackTitleLinePolicy.IsTitleLine(
            chapterTitleStart: 0,
            chapterTitleLength: 4,
            lineStart: 0,
            lineLength: 1,
            newLineLength: 1,
            lineIndex: 0));
    }

    [Fact]
    public void NonLeadingTitleStillUsesItsTextRange()
    {
        Assert.True(ReaderLinuxTextFallbackTitleLinePolicy.IsTitleLine(
            chapterTitleStart: 6,
            chapterTitleLength: 4,
            lineStart: 5,
            lineLength: 6,
            newLineLength: 1,
            lineIndex: 1));
        Assert.False(ReaderLinuxTextFallbackTitleLinePolicy.IsTitleLine(
            chapterTitleStart: 6,
            chapterTitleLength: 4,
            lineStart: 10,
            lineLength: 8,
            newLineLength: 0,
            lineIndex: 2));
    }
}
