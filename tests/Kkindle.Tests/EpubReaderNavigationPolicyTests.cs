using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EpubReaderNavigationPolicyTests
{
    [Fact]
    public void KeepsOnlyCoverTocPageAndEntriesFromTheBookToc()
    {
        var chapters = Enumerable.Range(0, 8)
            .Select(index => Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "kkindle-reader-policy",
                $"chapter-{index}.xhtml")))
            .ToArray();
        var chapterTitles = new[]
        {
            "封面",
            "未知",
            "图书在版编目（CIP）数据",
            "Contents",
            "献给我的母亲并纪念我的父亲",
            "有些骄傲的人都会记得",
            "进步报告—1",
            "进步报告—2"
        };
        var navigation = new[]
        {
            new EpubReaderNavigationItem("进步报告—1", new Uri(chapters[6]).AbsoluteUri, 6),
            new EpubReaderNavigationItem("进步报告—2", new Uri(chapters[7]).AbsoluteUri, 7)
        };
        var document = new EpubReaderDocument(
            Path.GetDirectoryName(chapters[0])!,
            chapters,
            navigation,
            chapterTitles);

        var actual = EpubReaderNavigationPolicy.Build(
            document,
            "封面",
            "目录",
            index => chapterTitles[index]);

        Assert.Equal(
            ["封面", "目录", "进步报告—1", "进步报告—2"],
            actual.Select(item => item.Title));
        Assert.Equal([0, 3, 6, 7], actual.Select(item => item.ChapterIndex));
    }

    [Fact]
    public void KeepsSpineFallbackWhenNoAuthoritativeTocExists()
    {
        var chapters = new[]
        {
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kkindle-reader-policy", "one.xhtml")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kkindle-reader-policy", "two.xhtml"))
        };
        var document = new EpubReaderDocument(
            Path.GetDirectoryName(chapters[0])!,
            chapters,
            [],
            ["封面", "第二章"]);

        var actual = EpubReaderNavigationPolicy.Build(
            document,
            "封面",
            "目录",
            index => document.ChapterTitles[index]);

        Assert.Equal([0, 1], actual.Select(item => item.ChapterIndex));
        Assert.Equal(["封面", "第二章"], actual.Select(item => item.Title));
    }
}
