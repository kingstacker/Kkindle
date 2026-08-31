using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EpubReaderSearchPolicyTests
{
    [Fact]
    public void FiltersNonNavigationSpinePagesAndOrdersRemainingHits()
    {
        var chapters = Enumerable.Range(0, 6)
            .Select(index => Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "kkindle-reader-search-policy",
                $"chapter-{index}.xhtml")))
            .ToArray();
        var document = new EpubReaderDocument(
            Path.GetDirectoryName(chapters[0])!,
            chapters,
            [
                new EpubReaderNavigationItem("序幕", new Uri(chapters[3]).AbsoluteUri, 3),
                new EpubReaderNavigationItem("第一章", new Uri(chapters[4]).AbsoluteUri, 4)
            ],
            []);
        var bookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        BookContentChunk[] candidates =
        [
            CreateChunk(bookId, fileId, chapterIndex: 4, chunkIndex: 1, startOffset: 100, "第一章"),
            CreateChunk(bookId, fileId, chapterIndex: 2, chunkIndex: 0, startOffset: 0, "前言"),
            CreateChunk(bookId, fileId, chapterIndex: 3, chunkIndex: 2, startOffset: 200, "序幕"),
            CreateChunk(bookId, fileId, chapterIndex: 3, chunkIndex: 0, startOffset: 0, "序幕")
        ];

        var actual = EpubReaderSearchPolicy.FilterAndOrder(document, candidates);

        Assert.Equal(
            ["序幕", "序幕", "第一章"],
            actual.Select(chunk => chunk.ChapterTitle));
        Assert.Equal([0, 2, 1], actual.Select(chunk => chunk.ChunkIndex));
        Assert.DoesNotContain(actual, chunk => chunk.ChapterIndex == 2);
    }

    [Fact]
    public void KeepsAllValidSpinePagesWhenTheBookHasNoNavigation()
    {
        var chapters = Enumerable.Range(0, 2)
            .Select(index => Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "kkindle-reader-search-policy",
                $"fallback-{index}.xhtml")))
            .ToArray();
        var document = new EpubReaderDocument(
            Path.GetDirectoryName(chapters[0])!,
            chapters,
            [],
            []);
        var bookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var candidates = new[]
        {
            CreateChunk(bookId, fileId, chapterIndex: 1, chunkIndex: 0, startOffset: 0, "第二页"),
            CreateChunk(bookId, fileId, chapterIndex: 0, chunkIndex: 0, startOffset: 0, "第一页")
        };

        var actual = EpubReaderSearchPolicy.FilterAndOrder(document, candidates);

        Assert.Equal(["第一页", "第二页"], actual.Select(chunk => chunk.ChapterTitle));
    }

    private static BookContentChunk CreateChunk(
        Guid bookId,
        Guid fileId,
        int chapterIndex,
        int chunkIndex,
        int startOffset,
        string title) =>
        new(
            Id: chapterIndex * 100L + chunkIndex,
            BookId: bookId,
            BookFileId: fileId,
            SourceHash: "search-policy",
            ChapterIndex: chapterIndex,
            ChunkIndex: chunkIndex,
            ChapterTitle: title,
            ChapterPath: $"chapter-{chapterIndex}.xhtml",
            StartOffset: startOffset,
            EndOffset: startOffset + 20,
            Content: $"{title} 搜索命中");
}
