using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReaderContentPositionTests
{
    [Fact]
    public async Task MigratesLegacyProgressAndBookmarksWithoutLosingTheirPositions()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var bookmarkId = Guid.NewGuid();
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ReaderProgress (
                        BookFileId TEXT PRIMARY KEY, BookId TEXT NOT NULL, ChapterPath TEXT NOT NULL,
                        Fragment TEXT NULL, ChapterIndex INTEGER NOT NULL, ScrollPosition INTEGER NOT NULL,
                        ProgressPercent REAL NOT NULL, FlowMode INTEGER NOT NULL, UpdatedAt TEXT NOT NULL);
                    CREATE TABLE ReaderBookmarks (
                        Id TEXT PRIMARY KEY, BookId TEXT NOT NULL, BookFileId TEXT NOT NULL, ChapterPath TEXT NOT NULL,
                        Fragment TEXT NULL, ChapterIndex INTEGER NOT NULL, Title TEXT NOT NULL, Quote TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL);
                    INSERT INTO ReaderProgress VALUES ($file, $book, 'chapter.xhtml', 'section', 2, 25920, 50, 1, $time);
                    INSERT INTO ReaderBookmarks VALUES ($bookmark, $book, $file, 'chapter.xhtml', 'section', 2, 'Keep me', 'A quote', $time);
                    """;
                command.Parameters.AddWithValue("$book", bookId.ToString());
                command.Parameters.AddWithValue("$file", fileId.ToString());
                command.Parameters.AddWithValue("$bookmark", bookmarkId.ToString());
                command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
            var data = new ReaderDataService(paths);
            await data.InitializeAsync();
            await new ReaderDataService(paths).InitializeAsync();
            var progress = Assert.IsType<ReaderProgressRow>(await data.GetProgressAsync(fileId));
            Assert.Equal(25920, progress.ScrollPosition);
            Assert.Equal("section", progress.Fragment);
            Assert.Null(progress.ContentPosition);
            var bookmark = Assert.Single(await data.GetBookmarksAsync(fileId));
            Assert.Equal("Keep me", bookmark.Title);
            Assert.Null(bookmark.ContentPosition);
            Assert.Null(bookmark.ScrollPosition);

            var textPosition = new ReaderContentPosition { TextOffset = 9134, PageIndex = 36, RunY = 24, SliceFraction = 0.25 };
            var imagePosition = new ReaderContentPosition { ImageIndex = 2, PageIndex = 2, RunY = 48 };
            await data.SaveProgressAsync(progress with { ContentPosition = textPosition });
            bookmark.ContentPosition = imagePosition;
            bookmark.ScrollPosition = 0;
            await data.SaveBookmarkAsync(bookmark);
            var reopened = new ReaderDataService(paths);
            Assert.Equal(textPosition, (await reopened.GetProgressAsync(fileId))!.ContentPosition);
            Assert.Equal(imagePosition, Assert.Single(await reopened.GetBookmarksAsync(fileId)).ContentPosition);

            // A newer legacy writer must clear an anchor that no longer
            // describes its numeric position, rather than retain stale text.
            await data.SaveProgressAsync(progress with { ScrollPosition = 400 });
            bookmark.ContentPosition = null;
            await data.SaveBookmarkAsync(bookmark);
            Assert.Null((await reopened.GetProgressAsync(fileId))!.ContentPosition);
            Assert.Null(Assert.Single(await reopened.GetBookmarksAsync(fileId)).ContentPosition);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Version\":99,\"TextOffset\":9134}")]
    [InlineData("{\"Version\":\"one\",\"TextOffset\":9134}")]
    [InlineData("{\"Version\":1,\"TextOffset\":-2}")]
    [InlineData("{\"Version\":1,\"ImageIndex\":-1}")]
    [InlineData("{\"Version\":1,\"SliceFraction\":2}")]
    [InlineData("{\"Version\":1,\"RunY\":1e999}")]
    public async Task InvalidOrUnknownMetadataKeepsLegacyRowsReadable(string? json)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var data = new ReaderDataService(paths);
            await data.InitializeAsync();
            var progress = new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "chapter.xhtml", "anchor", 1, 25920, 50, 1, DateTimeOffset.UtcNow);
            var bookmark = new ReaderBookmark { BookId = progress.BookId, BookFileId = progress.BookFileId, ChapterPath = progress.ChapterPath, ScrollPosition = 321, Title = "Preserved" };
            await data.SaveProgressAsync(progress);
            await data.SaveBookmarkAsync(bookmark);
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE ReaderProgress SET ContentPositionJson=$json; UPDATE ReaderBookmarks SET ContentPositionJson=$json;";
                command.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
            var restored = Assert.IsType<ReaderProgressRow>(await data.GetProgressAsync(progress.BookFileId));
            Assert.Equal(progress, restored);
            var restoredBookmark = Assert.Single(await data.GetBookmarksAsync(progress.BookFileId));
            Assert.Null(restoredBookmark.ContentPosition);
            Assert.Equal(321, restoredBookmark.ScrollPosition);
            Assert.Equal("Preserved", restoredBookmark.Title);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
