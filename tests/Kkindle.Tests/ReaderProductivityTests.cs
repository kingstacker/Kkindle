using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReaderProductivityTests
{
    [Fact]
    public async Task SavesAndRestoresReadingProgress()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            await service.SaveProgressAsync(new ReaderProgressRow(
                bookId,
                fileId,
                "text/chapter-2.xhtml",
                "part-3",
                ChapterIndex: 1,
                ScrollPosition: 4820,
                ProgressPercent: 23,
                FlowMode: 0,
                UpdatedAt: DateTimeOffset.UtcNow));

            var restored = await service.GetProgressAsync(fileId);
            Assert.NotNull(restored);
            Assert.Equal(bookId, restored!.BookId);
            Assert.Equal(1, restored.ChapterIndex);
            Assert.Equal(4820, restored.ScrollPosition);
            Assert.Equal("part-3", restored.Fragment);
            Assert.Equal(0, restored.FlowMode);

            // Overwrite on the same file and make sure the row updates in place.
            await service.SaveProgressAsync(restored with
            {
                ChapterIndex = 2,
                ScrollPosition = 900,
                ProgressPercent = 30,
                FlowMode = 1
            });
            var updated = await service.GetProgressAsync(fileId);
            Assert.Equal(2, updated!.ChapterIndex);
            Assert.Equal(900, updated.ScrollPosition);
            Assert.Equal(1, updated.FlowMode);
            Assert.Null(await service.GetProgressAsync(Guid.NewGuid()));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task AddsDeletesAndListsBookmarks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var first = new ReaderBookmark
            {
                BookId = bookId,
                BookFileId = fileId,
                ChapterPath = "text/chapter-1.xhtml",
                Fragment = null,
                ChapterIndex = 0,
                Title = "第一章",
                Quote = "规模法则描述系统尺度变化"
            };
            var second = new ReaderBookmark
            {
                BookId = bookId,
                BookFileId = fileId,
                ChapterPath = "text/chapter-3.xhtml",
                Fragment = "section-2",
                ChapterIndex = 2,
                ScrollPosition = 1840,
                FlowMode = 1,
                Title = "第三章",
                Quote = "城市人口和基础设施"
            };
            await service.SaveBookmarkAsync(first);
            await service.SaveBookmarkAsync(second);

            var list = await service.GetBookmarksAsync(fileId);
            Assert.Equal(2, list.Count);
            Assert.Contains(list, bookmark => bookmark.Quote.Contains("城市人口", StringComparison.Ordinal));
            var restoredSecond = Assert.Single(list, bookmark => bookmark.Id == second.Id);
            Assert.Equal(1840, restoredSecond.ScrollPosition);
            Assert.Equal(1, restoredSecond.FlowMode);

            await service.DeleteBookmarkAsync(second.Id);
            var remaining = await service.GetBookmarksAsync(fileId);
            Assert.Single(remaining);
            Assert.Equal("第一章", remaining[0].Title);

            // Another book's bookmarks are never mixed in.
            var other = await service.GetBookmarksAsync(Guid.NewGuid());
            Assert.Empty(other);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PersistsPerBookLayoutSettingsAndDefaultsAreRestored()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            Assert.Null(await service.GetLayoutSettingsAsync(fileId));

            var settings = new ReaderLayoutSettings(
                FontScale: 1.2,
                LineHeight: 2.1,
                MaxWidth: 960,
                BodyPadding: 96,
                FontFamily: "SimSun",
                FlowMode: 1,
                VerticalWriting: true,
                TwoPageMode: true)
            {
                ParagraphIndent = false
            };
            await service.SaveLayoutSettingsAsync(bookId, fileId, settings);

            var restored = await service.GetLayoutSettingsAsync(fileId);
            Assert.NotNull(restored);
            Assert.Equal(1.2, restored!.FontScale);
            Assert.Equal(2.1, restored.LineHeight);
            Assert.Equal(960, restored.MaxWidth);
            Assert.Equal(96, restored.BodyPadding);
            Assert.Equal("SimSun", restored.FontFamily);
            Assert.Equal(1, restored.FlowMode);
            Assert.True(restored.VerticalWriting);
            Assert.True(restored.TwoPageMode);
            Assert.False(restored.ParagraphIndent);

            // A book with no saved settings still resolves to the default record.
            Assert.Null(await service.GetLayoutSettingsAsync(Guid.NewGuid()));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task MigratesLegacyLayoutRowsWithParagraphIndentEnabled()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var fileId = Guid.NewGuid();
            var bookId = Guid.NewGuid();
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ReaderLayoutSettings (
                        BookFileId TEXT PRIMARY KEY,
                        BookId TEXT NOT NULL,
                        FontScale REAL NOT NULL,
                        LineHeight REAL NOT NULL,
                        MaxWidth REAL NOT NULL,
                        BodyPadding REAL NOT NULL,
                        FontFamily TEXT NULL,
                        FlowMode INTEGER NOT NULL,
                        VerticalWriting INTEGER NOT NULL,
                        TwoPageMode INTEGER NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    INSERT INTO ReaderLayoutSettings (
                        BookFileId, BookId, FontScale, LineHeight, MaxWidth,
                        BodyPadding, FontFamily, FlowMode, VerticalWriting,
                        TwoPageMode, UpdatedAt)
                    VALUES ($fileId, $bookId, 1.2, 1.8, 1200, 24,
                        'SimSun', 1, 0, 0, $updatedAt);
                    """;
                command.Parameters.AddWithValue("$fileId", fileId.ToString());
                command.Parameters.AddWithValue("$bookId", bookId.ToString());
                command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var service = new ReaderDataService(paths);
            await service.InitializeAsync();

            var migrated = await service.GetLayoutSettingsAsync(fileId);
            Assert.NotNull(migrated);
            Assert.True(migrated!.ParagraphIndent);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ListsAnnotationsAcrossAllLocalBooks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var first = new ReaderAnnotation
            {
                BookId = Guid.NewGuid(), BookFileId = Guid.NewGuid(), ChapterPath = "one.xhtml",
                SelectedText = "first", EndOffset = 5
            };
            var second = new ReaderAnnotation
            {
                BookId = Guid.NewGuid(), BookFileId = Guid.NewGuid(), ChapterPath = "pdf:2",
                SelectedText = "second", EndOffset = 6
            };
            await service.SaveAnnotationAsync(first);
            await service.SaveAnnotationAsync(second);

            var all = await service.GetAllAnnotationsAsync();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, item => item.Id == first.Id);
            Assert.Contains(all, item => item.Id == second.Id);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void BuildsUnifiedLocalAndKindleReadingMaterialsExports()
    {
        ReadingMaterialRecord[] records =
        [
            new(ReadingMaterialSource.Local, "Local Book", "划线与笔记", "chapter.xhtml · 1-8", "local quote", "local note", new DateTimeOffset(2026, 8, 10, 1, 0, 0, TimeSpan.Zero)),
            new(ReadingMaterialSource.Kindle, "Kindle Book", "划线", "Location 20", "kindle quote", "", null)
        ];

        var markdown = ReadingMaterialsExport.BuildMarkdown(records);
        var plain = ReadingMaterialsExport.BuildPlainText(records);
        Assert.Contains("本地书籍 · Local Book", markdown);
        Assert.Contains("Kindle · Kindle Book", markdown);
        Assert.Contains("> local quote", markdown);
        Assert.Contains("[本地书籍] Local Book", plain);
        Assert.Contains("kindle quote", plain);
    }

    [Fact]
    public async Task SearchBookDoesNotRepeatTitleOnlyMatchesAcrossChapterChunks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('f', 64);

            await service.ReplaceBookChunksAsync(bookId, fileId, hash,
            [
                new BookContentChunkDraft(0, 0, "1. 简介概述和总结", "text/one.xhtml", 0, 32,
                    "第一段正文不包含搜索词。"),
                new BookContentChunkDraft(0, 1, "1. 简介概述和总结", "text/one.xhtml", 32, 64,
                    "第二段正文也不包含搜索词。"),
                new BookContentChunkDraft(1, 0, "2. 其他章节", "text/two.xhtml", 0, 32,
                    "另一章的正文。")
            ]);

            var shortQuery = await service.SearchBookAsync(bookId, "简介");
            var longQuery = await service.SearchBookAsync(bookId, "简介概述");

            Assert.Single(shortQuery);
            Assert.Equal(0, shortQuery[0].ChunkIndex);
            Assert.Single(longQuery);
            Assert.Equal(0, longQuery[0].ChunkIndex);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SearchBookCollapsesMatchesRepeatedByOverlappingChunks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('d', 64);
            var chapter = new string('甲', 900) + "这个月份值得记录。" + new string('乙', 300);

            await service.ReplaceBookChunksAsync(bookId, fileId, hash,
            [
                new BookContentChunkDraft(0, 0, "第一章", "text/one.xhtml", 0, 1000,
                    chapter[..1000]),
                // Simulate an older index whose normalized overlap retained a
                // stale numeric offset. Text context must still identify it as
                // the same visible result as the first chunk.
                new BookContentChunkDraft(0, 1, "第一章", "text/one.xhtml", 1200, 1600,
                    chapter[840..]),
                new BookContentChunkDraft(1, 0, "第一章副本", "text/duplicate.xhtml", 0, 400,
                    chapter[840..]),
                new BookContentChunkDraft(2, 0, "第二章", "text/two.xhtml", 0, 20,
                    "另一个月份发生了不同的事情。")
            ]);

            var results = await service.SearchBookAsync(bookId, "月份", 20);

            Assert.Equal(2, results.Count);
            Assert.Single(results, result => result.ChapterPath == "text/one.xhtml");
            Assert.Single(results, result => result.ChapterPath == "text/two.xhtml");
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task WholeBookSearchIsNotTruncatedAtTheInteractiveResultLimit()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('a', 64);
            var chunks = Enumerable.Range(0, 140)
                .Select(index => new BookContentChunkDraft(
                    index,
                    0,
                    $"第 {index + 1} 章",
                    $"text/{index:D3}.xhtml",
                    0,
                    40,
                    $"第 {index + 1} 个全书命中拥有不同的上下文。"))
                .ToArray();
            await service.ReplaceBookChunksAsync(bookId, fileId, hash, chunks);

            var bounded = await service.SearchBookAsync(bookId, "命中", 40);
            var wholeBook = await service.SearchBookAsync(bookId, "命中", int.MaxValue);

            Assert.Equal(40, bounded.Count);
            Assert.Equal(140, wholeBook.Count);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task WholeBookExactSearchRejectsPartialChineseNgramMatches()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            await service.ReplaceBookChunksAsync(bookId, fileId, new string('c', 64),
            [
                new BookContentChunkDraft(0, 0, "第一章", "text/one.xhtml", 0, 80,
                    "系统的变化可以解释。这在数量上是通过代谢率体现的。"),
                new BookContentChunkDraft(1, 0, "第二章", "text/two.xhtml", 0, 80,
                    "曲线显示时间加速现象，研究指出数量上存在明显差异。")
            ]);

            var broad = await service.SearchBookAsync(bookId, "这在数量上是通过", int.MaxValue);
            var exact = await service.SearchBookAsync(
                bookId,
                "这在数量上是通过",
                int.MaxValue,
                exactPhraseOnly: true);

            Assert.Equal(2, broad.Count);
            var result = Assert.Single(exact);
            Assert.Equal("text/one.xhtml", result.ChapterPath);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SearchBookMergesDifferentHitsFromOverlappingPartsOfOneParagraph()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('b', 64);
            var paragraph = new string('甲', 700) + "保罗" + new string('乙', 250) + "保罗" + new string('丙', 250);

            await service.ReplaceBookChunksAsync(bookId, fileId, hash,
            [
                new BookContentChunkDraft(0, 0, "第一章", "text/one.xhtml", 0, 900,
                    paragraph[..900]),
                new BookContentChunkDraft(0, 1, "第一章", "text/one.xhtml", 740, paragraph.Length,
                    paragraph[740..]),
                new BookContentChunkDraft(1, 0, "第二章", "text/two.xhtml", 0, 40,
                    "另一个段落也提到了保罗，但应当保留为独立结果。")
            ]);

            var results = await service.SearchBookAsync(bookId, "保罗", int.MaxValue);

            Assert.Equal(2, results.Count);
            Assert.Single(results, result => result.ChapterPath == "text/one.xhtml");
            Assert.Single(results, result => result.ChapterPath == "text/two.xhtml");
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task AccumulatesReadingTimeWithoutLosingExistingStats()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            await service.AddReadingTimeAsync(bookId, fileId, activeSeconds: 95, 12, completedChapters: 2, totalChapters: 10);
            await service.AddReadingTimeAsync(bookId, fileId, activeSeconds: 30, 18, completedChapters: 3, totalChapters: 10);

            var stats = await service.GetReadingStatsAsync(fileId);
            Assert.NotNull(stats);
            Assert.Equal(125, stats!.CumulativeSeconds);
            Assert.Equal(18, stats.ProgressPercent);
            Assert.Equal(3, stats.CompletedChapters);

            // Zero-length sessions are ignored so no bogus rows are created.
            await service.AddReadingTimeAsync(bookId, fileId, activeSeconds: 0, 18, 3, 10);
            var after = await service.GetReadingStatsAsync(fileId);
            Assert.Equal(125, after!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SearchesIndexedBookAndUsesLikeFallbackForShortTerms()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('e', 64);

            await service.ReplaceBookChunksAsync(bookId, fileId, hash,
            [
                new BookContentChunkDraft(0, 0, "第一章", "text/one.xhtml", 0, 40,
                    "规模法则描述系统尺度变化时仍然保持的数量关系。"),
                new BookContentChunkDraft(1, 0, "第二章", "text/two.xhtml", 0, 30,
                    "城市人口和基础设施之间存在可测量的统计关系。")
            ]);

            // Long CJK term: FTS/trigram should find the relevant chapter.
            var results = await service.SearchBookAsync(bookId, "城市人口和基础设施");
            Assert.Contains(results, chunk => chunk.ChapterTitle == "第二章");

            // Short term (1-2 characters) falls back to LIKE and still works.
            var fallback = await service.SearchBookAsync(bookId, "规模");
            Assert.Contains(fallback, chunk => chunk.ChapterTitle == "第一章");
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void BuildsMarkdownAndPlainTextAnnotationExports()
    {
        var annotations = new[]
        {
            new ReaderAnnotation
            {
                Id = Guid.NewGuid(),
                BookId = Guid.NewGuid(),
                BookFileId = Guid.NewGuid(),
                ChapterPath = "text/one.xhtml",
                StartOffset = 10,
                EndOffset = 20,
                SelectedText = "规模法则描述系统",
                Prefix = "本书提出",
                Suffix = "尺度变化",
                Note = "关键定义",
                CreatedAt = new DateTimeOffset(2026, 8, 7, 4, 0, 0, TimeSpan.Zero)
            }
        };
        string? chapterTitle = null;
        string Resolve(string path)
        {
            chapterTitle = "第一章";
            return chapterTitle;
        }

        var markdown = ReaderAnnotationExport.BuildMarkdown("规模与规律", "测试作者", annotations, Resolve);
        var plain = ReaderAnnotationExport.BuildPlainText("规模与规律", "测试作者", annotations, Resolve);

        Assert.Contains("# 规模与规律", markdown);
        Assert.Contains("作者：测试作者", markdown);
        Assert.Contains("## 第一章", markdown);
        Assert.Contains("> 规模法则描述系统", markdown);
        Assert.Contains("关键定义", markdown);
        Assert.Contains("2026-08-07", markdown);
        Assert.Contains("text/one.xhtml（偏移 10–20）", markdown);

        Assert.Contains("规模与规律", plain);
        Assert.Contains("[1] 第一章", plain);
        Assert.Contains("关键定义", plain);
        Assert.DoesNotContain("##", plain);

        // Empty annotation list still produces a valid, explicit document.
        var empty = ReaderAnnotationExport.BuildMarkdown("空书", "作者", [], null);
        Assert.Contains("暂无划线与批注", empty);
    }
}
