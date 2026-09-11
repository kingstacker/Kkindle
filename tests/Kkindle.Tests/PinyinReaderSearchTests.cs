using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class PinyinReaderSearchTests : IDisposable
{
    private readonly string _root = TestHelpers.CreateTempDirectory();
    private const string BankRuby = "<ruby>银<rt>yín</rt></ruby><ruby>行<rt>háng</rt></ruby>";

    [Fact]
    public void SearchesBaseTextWithoutChangingBookmarkOrHighlightOffsets()
    {
        var chapter = WriteChapter("<p>" + BankRuby + " ABC yín</p>");
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.Equal("银yín行háng ABC yín", content.BodyText);
        Assert.Equal(new[] { (1, 3), (5, 4) }, content.RubyAnnotationRanges);
        Assert.Equal(new ReaderSearchMatch(0, 5), Assert.Single(ReaderSearchTextPolicy.FindMatches(
            content.BodyText, "银行", content.RubyAnnotationRanges)));
        Assert.Equal(content.BodyText.LastIndexOf("yín", StringComparison.Ordinal),
            Assert.Single(ReaderSearchTextPolicy.FindMatches(content.BodyText, "yín", content.RubyAnnotationRanges)).Start);
        Assert.Equal(content.BodyText.IndexOf("ABC", StringComparison.Ordinal),
            Assert.Single(ReaderSearchTextPolicy.FindMatches(content.BodyText, "abc", content.RubyAnnotationRanges)).Start);
    }

    [Fact]
    public void FullBookResultContextSelectsTheCorrectRepeatedOccurrence()
    {
        var chapter = WriteChapter("<p>旧" + BankRuby + "关门。</p><p>新" + BankRuby + "开门。</p>");
        var content = new XhtmlChapterLoader().Load(chapter);
        var target = content.BodyText.LastIndexOf('银');
        var offset = ReaderSearchTextPolicy.FindBestMatchOffset(content.BodyText, "银行", "新银行开门。",
            offsetHint: 0, excludedRanges: content.RubyAnnotationRanges);
        Assert.Equal(target, offset);
    }

    [Fact]
    public void RubyFallbackParenthesesAndFormattedPronunciationAreExcluded()
    {
        var chapter = WriteChapter("<p><ruby><rb>银</rb><rp>(</rp><rt><span>yín</span></rt><rp>)</rp></ruby>"
            + "<strong><ruby>行<rt>háng</rt></ruby></strong></p>");
        var content = new XhtmlChapterLoader().Load(chapter);
        var match = Assert.Single(ReaderSearchTextPolicy.FindMatches(content.BodyText, "银行", content.RubyAnnotationRanges));
        Assert.Equal(0, match.Start);
        Assert.Equal(content.BodyText.IndexOf('行') + 1, match.Length);
        Assert.Empty(ReaderSearchTextPolicy.FindMatches(content.BodyText, "yín", content.RubyAnnotationRanges));
    }

    [Fact]
    public void ReusingTheLoaderDoesNotReuseOrClearAnotherChaptersRubyRanges()
    {
        var loader = new XhtmlChapterLoader();
        var first = loader.Load(WriteChapter("<p>" + BankRuby + "</p>", "one.xhtml"));
        var second = loader.Load(WriteChapter("<p>银行</p>", "two.xhtml"));
        Assert.Empty(second.RubyAnnotationRanges);
        Assert.Equal(2, first.RubyAnnotationRanges.Count);
        Assert.Single(ReaderSearchTextPolicy.FindMatches(first.BodyText, "银行", first.RubyAnnotationRanges));
    }

    [Theory]
    [InlineData("")]
    [InlineData("&nbsp;")]
    public async Task FullTextIndexExcludesRubyInXmlAndTolerantHtmlParsing(string entity)
    {
        var chapter = WriteChapter("<p>" + BankRuby + "业务。</p><p>"
            + entity + "<span>银</span><strong>行</strong> ABC</p><script>隐藏文本</script>");
        var text = await EpubBookContentService.ExtractPlainTextAsync(chapter);
        Assert.Equal("银行业务。\n\n银行 ABC", text);
        Assert.DoesNotContain("yín", text);
        Assert.DoesNotContain("háng", text);
    }

    [Fact]
    public async Task UpgradesLegacyIndexAndRebuildsWithoutChangingSourceHash()
    {
        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureDirectories();
        var book = new Book { Title = "拼音版" };
        var file = new BookFile { BookId = book.Id, Sha256 = new string('a', 64), Format = "epub" };
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database }.ToString()))
        {
            await connection.OpenAsync();
            using var legacy = connection.CreateCommand();
            legacy.CommandText = """
                CREATE TABLE BookContentChunks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BookId TEXT NOT NULL, BookFileId TEXT NOT NULL, SourceHash TEXT NOT NULL,
                    ChapterIndex INTEGER NOT NULL, ChunkIndex INTEGER NOT NULL,
                    ChapterTitle TEXT NOT NULL, ChapterPath TEXT NOT NULL,
                    StartOffset INTEGER NOT NULL, EndOffset INTEGER NOT NULL, Content TEXT NOT NULL);
                INSERT INTO BookContentChunks
                    (BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex,
                     ChapterTitle, ChapterPath, StartOffset, EndOffset, Content)
                VALUES ($book, $file, $hash, 0, 0, '第一章', 'chapter.xhtml', 0, 9, '银yín行háng');
                """;
            legacy.Parameters.AddWithValue("$book", book.Id.ToString());
            legacy.Parameters.AddWithValue("$file", file.Id.ToString());
            legacy.Parameters.AddWithValue("$hash", file.Sha256);
            await legacy.ExecuteNonQueryAsync();
        }

        var readerData = new ReaderDataService(paths);
        await readerData.InitializeAsync();
        Assert.False(await readerData.IsIndexCurrentAsync(file.Id, file.Sha256));
        var oldChunk = Assert.Single(await readerData.GetBookChunksAsync(book.Id));
        await readerData.UpsertChunkEmbeddingsAsync(book.Id, file.Id, file.Sha256, "test", 2,
            [new ReaderChunkEmbedding(oldChunk.Id, book.Id, file.Id, file.Sha256, "test", 2, new float[] { 1, 0 })]);
        Assert.Single(await readerData.GetBookChunkEmbeddingsAsync(book.Id, "test", 2));

        var chapter = WriteChapter("<p>" + BankRuby + "业务。</p>");
        var document = new EpubReaderDocument(_root, [chapter], [], []);
        var indexer = new EpubBookContentService(readerData);
        Assert.Equal(1, await indexer.EnsureIndexedAsync(book, file, document));
        Assert.Equal(0, await indexer.EnsureIndexedAsync(book, file, document));
        Assert.True(await readerData.IsIndexCurrentAsync(file.Id, file.Sha256));
        Assert.Empty(await readerData.GetBookChunkEmbeddingsAsync(book.Id, "test", 2));
        var match = Assert.Single(await readerData.SearchBookAsync(book.Id, "银行"));
        Assert.Equal("银行业务。", match.Content);
        Assert.Equal(file.Sha256, match.SourceHash);

        await readerData.UpsertChunkEmbeddingsAsync(book.Id, file.Id, file.Sha256, "test", 2,
            [new ReaderChunkEmbedding(match.Id, book.Id, file.Id, file.Sha256, "test", 2, new float[] { 0, 1 })]);
        Assert.Single(await readerData.GetBookChunkEmbeddingsAsync(book.Id, "test", 2));
    }

    private string WriteChapter(string body, string fileName = "chapter.xhtml")
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>" + body + "</body></html>");
        return path;
    }

    public void Dispose() => TestHelpers.TryDelete(_root);
}
