using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class PinyinSearchIndexTests
{
    [Theory]
    [InlineData("maozedongxuanji")]
    [InlineData("mao ze dong xuan ji")]
    [InlineData("MZDXJ")]
    [InlineData("zedong")]
    public void FindsTitlesUsingPinyinAndInitials(string query)
    {
        var index = PinyinSearchIndex.Create("毛泽东选集", "毛泽东", "毛泽东选集.azw3");
        Assert.True(index.Matches(query));
        Assert.False(index.Matches("zhangjuzheng"));
    }

    [Fact]
    public void FindsAuthorsAndPreservesMixedTextAndSupplementaryCharacters()
    {
        var index = PinyinSearchIndex.Create("😀中文 2", "张三", "file.epub");
        Assert.True(index.Matches("zhongwen2"));
        Assert.True(index.Matches("zw2"));
        Assert.True(index.Matches("zs"));
        Assert.False(index.Matches("中文"));
    }
}
