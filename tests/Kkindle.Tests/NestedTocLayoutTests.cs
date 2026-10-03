using Kkindle.Layout;
using Xunit;

namespace Kkindle.Tests;

public sealed class NestedTocLayoutTests
{
    [Theory]
    [InlineData("ul")]
    [InlineData("ol")]
    public void NestedDirectoryKeepsEachEntryInItsOwnBlock(string list)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var chapter = Path.Combine(root, "toc.xhtml");
            File.WriteAllText(chapter, $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><body><{list}><li id="volume"><a href="v.xhtml">第一卷</a><{list}><li><a href="a.xhtml">第一回</a></li><li><a href="b.xhtml">第二回</a><{list}><li><a href="c.xhtml">附录</a></li></{list}></li></{list}></li><li><a href="d.xhtml">第二卷</a></li></{list}></body></html>
                """);
            var content = new XhtmlChapterLoader().Load(chapter);
            var expected = new[] { "第一卷", "第一回", "第二回", "附录", "第二卷" };
            Assert.Equal(expected, content.Blocks.Select(block => string.Concat(block.Items.Select(item => item.Text))));
            Assert.All(content.Blocks, block => Assert.Equal(BlockKind.ListItem, block.Kind));
            Assert.Equal(string.Concat(expected), content.BodyText);
            Assert.Equal(0, content.FragmentTextOffsets["volume"]);
            Assert.All(content.Blocks, block => Assert.Contains(block.Items, item => item.LinkHref is not null));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
