namespace Kkindle.Tests;

public sealed class ReadingMaterialCoverMatcherTests
{
    [Theory]
    [InlineData("三体（全集）", "三体")]
    [InlineData("活着 (余华)", "活着")]
    [InlineData("The Three-Body Problem", "the three-body problem")]
    public void MatchesShortTitlesAfterParentheticalStripping(string clippingTitle, string deviceTitle)
    {
        Assert.True(ReadingMaterialCoverMatcher.AreTitlesRelated(clippingTitle, deviceTitle));
    }

    [Theory]
    [InlineData("人类简史：从动物到上帝 (尤瓦尔·赫拉利)", "人类简史")]
    [InlineData("明朝那些事儿（第壹部）", "明朝那些事儿（第壹部）(当年明月)")]
    public void MatchesLongTitlesByContainment(string clippingTitle, string deviceTitle)
    {
        Assert.True(ReadingMaterialCoverMatcher.AreTitlesRelated(clippingTitle, deviceTitle));
    }

    [Theory]
    [InlineData("三体", "新三体")]
    [InlineData("活法", "活法2")]
    [InlineData("", "三体")]
    [InlineData("   ", "活着")]
    public void RejectsAmbiguousOrEmptyShortTitles(string left, string right)
    {
        Assert.False(ReadingMaterialCoverMatcher.AreTitlesRelated(left, right));
    }
}
