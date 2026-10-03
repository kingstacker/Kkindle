using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task DuplicateFileConsolidation_PreservesReaderDataAndPublishesIt()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var canonical = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-1));
        // Older imports can store an uppercase SHA; SQLite's unique index is
        // case-sensitive, while sync correctly treats both hashes as equal.
        var duplicate = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2), uppercaseHash: true);
        var canonicalNote = await AddReadingNoteAsync(a, canonical.BookId, canonical.FileId, "canonical");
        var duplicateNote = await AddReadingNoteAsync(a, duplicate.BookId, duplicate.FileId, "duplicate");
        var canonicalBookmark = await AddReadingBookmarkAsync(a, canonical.BookId, canonical.FileId, "canonical");
        var duplicateBookmark = await AddReadingBookmarkAsync(a, duplicate.BookId, duplicate.FileId, "duplicate");
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(
            duplicate.BookId, duplicate.FileId, "chapter.xhtml", null, 2, 500, 60, 1, DateTimeOffset.UtcNow));
        await a.Reader.AddReadingTimeAsync(duplicate.BookId, duplicate.FileId, 120, 60, 6, 10);

        await a.SyncAsync();

        Assert.Equal(1, await a.BookCountAsync());
        await AssertReadingNotesAsync(a, canonical.BookId, canonical.FileId, canonicalNote.Id, duplicateNote.Id);
        Assert.Equal(new[] { canonicalBookmark.Id, duplicateBookmark.Id }.Order(),
            (await a.Reader.GetBookmarksAsync(canonical.FileId)).Select(row => row.Id).Order());
        Assert.Equal(60, (await a.Reader.GetProgressAsync(canonical.FileId))!.ProgressPercent);
        Assert.Equal(120, (await a.Reader.GetReadingStatsAsync(canonical.FileId))!.CumulativeSeconds);
        Assert.Null(await a.Reader.GetReadingStatsAsync(duplicate.FileId));

        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        await AssertReadingNotesAsync(b, canonical.BookId, canonical.FileId, canonicalNote.Id, duplicateNote.Id);
        Assert.Equal(2, (await b.Reader.GetBookmarksAsync(canonical.FileId)).Count);
        Assert.Equal(120, (await b.Reader.GetReadingStatsAsync(canonical.FileId))!.CumulativeSeconds);
        Assert.Equal(120, (await b.Reader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
    }

    [Fact]
    public async Task IndependentlyImportedFiles_MergeReadingDataWithoutDoublingTime()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var bookA = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2));
        var bookB = await AddMatchingReadingBookAsync(b, DateTimeOffset.UtcNow.AddMinutes(-1));
        var noteA = await AddReadingNoteAsync(a, bookA.BookId, bookA.FileId, "device A");
        var noteB = await AddReadingNoteAsync(b, bookB.BookId, bookB.FileId, "device B");
        await AddReadingBookmarkAsync(a, bookA.BookId, bookA.FileId, "device A");
        await AddReadingBookmarkAsync(b, bookB.BookId, bookB.FileId, "device B");
        await a.Reader.AddReadingTimeAsync(bookA.BookId, bookA.FileId, 120, 70, 7, 10);
        await b.Reader.AddReadingTimeAsync(bookB.BookId, bookB.FileId, 300, 20, 2, 10);
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(
            bookA.BookId, bookA.FileId, "chapter.xhtml", null, 7, 700, 70, 1, DateTimeOffset.UtcNow.AddMinutes(-2)));
        await b.Reader.SaveProgressAsync(new ReaderProgressRow(
            bookB.BookId, bookB.FileId, "chapter.xhtml", null, 2, 200, 20, 1, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var book = Assert.Single(await device.Library.SearchAsync());
            var file = Assert.Single(book.Files);
            await AssertReadingNotesAsync(device, book.Id, file.Id, noteA.Id, noteB.Id);
            Assert.Equal(2, (await device.Reader.GetBookmarksAsync(file.Id)).Count);
            Assert.Equal(20, (await device.Reader.GetProgressAsync(file.Id))!.ProgressPercent);
            var stats = await device.Reader.GetReadingStatsAsync(file.Id);
            Assert.NotNull(stats);
            Assert.Equal(book.Id, stats.BookId);
            Assert.Equal(420, stats.CumulativeSeconds);
            Assert.Equal(420, (await device.Reader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        }
    }

    [Fact]
    public async Task ReaderRowsWithAnOldBookId_AreBoundToTheCurrentFileOwner()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 100, 10, 1, 10);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 200, 30, 3, 10);
        var note = await AddReadingNoteAsync(b, book.BookId, book.FileId, "retained after a book merge");
        await b.SyncAsync();
        var oldSnapshot = bucket.Snapshot(b.SnapshotKey);
        var oldBookId = Guid.NewGuid();
        Assert.Single(oldSnapshot.ReadingStats).BookId = oldBookId;
        Assert.Single(oldSnapshot.Annotations).BookId = oldBookId;
        bucket.Objects[b.SnapshotKey] = MemoryBucket.Encode(oldSnapshot);

        await a.SyncAsync();

        var stats = await a.Reader.GetReadingStatsAsync(book.FileId);
        Assert.Equal(book.BookId, stats!.BookId);
        Assert.Equal(300, stats.CumulativeSeconds);
        await AssertReadingNotesAsync(a, book.BookId, book.FileId, note.Id);
        Assert.True(Assert.Single((await a.Reader.GetReadingDashboardAsync()).RecentBooks).IsInLibrary);
    }

    [Fact]
    public async Task DeletedBookHistory_SyncsAfterTrashPurgeWithoutRestoringTheBook()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-1));
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 120, 10, 1, 10);
        await AddReadingBookmarkAsync(a, book.BookId, book.FileId, "Before deletion");
        await AddReadingNoteAsync(a, book.BookId, book.FileId, "Before deletion");
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 300, 40, 4, 10);
        await a.Library.DeleteAsync(book.BookId);
        await a.Library.PurgeTrashItemAsync(Assert.Single(await a.Library.GetTrashItemsAsync()).Id);

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await using var c = await Device.CreateAsync(bucket);
        await c.SyncAsync();
        await b.SyncAsync();

        foreach (var device in new[] { a, b, c })
        {
            Assert.Equal(0, await device.BookCountAsync());
            Assert.Equal(0, await device.ScalarAsync("SELECT COUNT(*) FROM BookFiles;"));
            var dashboard = await device.Reader.GetReadingDashboardAsync();
            Assert.Equal(420, dashboard.TotalSeconds);
            Assert.Equal(0, dashboard.BookmarkCount);
            Assert.Equal(0, dashboard.AnnotationCount);
            Assert.Equal(420, dashboard.DailyReading.Sum(day => day.ActiveSeconds));
            var history = Assert.Single(dashboard.RecentBooks);
            Assert.Equal("Shared reading book", history.Title);
            Assert.False(history.IsInLibrary);
        }
    }

    [Fact]
    public async Task ReimportedBook_AddsNewReadingTimeAndRetainsSharedHistoryOnce()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var original = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2));
        await a.Reader.AddReadingTimeAsync(original.BookId, original.FileId, 120, 10, 1, 10);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.Library.DeleteAsync(original.BookId);
        await a.SyncAsync();
        await b.SyncAsync();
        var reimported = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow);
        await a.Reader.AddReadingTimeAsync(reimported.BookId, reimported.FileId, 300, 40, 4, 10);

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var book = Assert.Single(await device.Library.SearchAsync());
            var dashboard = await device.Reader.GetReadingDashboardAsync();
            Assert.Equal(420, dashboard.TotalSeconds);
            Assert.Equal(420, dashboard.DailyReading.Sum(day => day.ActiveSeconds));
            var history = Assert.Single(dashboard.RecentBooks);
            Assert.Equal(book.Id, history.BookId);
            Assert.True(history.IsInLibrary);
        }
    }

    [Fact]
    public async Task DeletedIndependentImports_MergeArchivedIdentitiesWithoutDoublingTime()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var bookA = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2));
        var bookB = await AddMatchingReadingBookAsync(b, DateTimeOffset.UtcNow.AddMinutes(-1));
        await a.Reader.AddReadingTimeAsync(bookA.BookId, bookA.FileId, 120, 10, 1, 10);
        await b.Reader.AddReadingTimeAsync(bookB.BookId, bookB.FileId, 300, 40, 4, 10);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await a.Library.DeleteAsync(bookA.BookId);
        await b.Library.DeleteAsync(bookB.BookId);

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await using var c = await Device.CreateAsync(bucket);
        await c.SyncAsync();

        foreach (var device in new[] { a, b, c })
        {
            Assert.Equal(0, await device.BookCountAsync());
            var dashboard = await device.Reader.GetReadingDashboardAsync();
            Assert.Equal(420, dashboard.TotalSeconds);
            Assert.Equal(420, dashboard.DailyReading.Sum(day => day.ActiveSeconds));
            Assert.Equal("Shared reading book", Assert.Single(dashboard.RecentBooks).Title);
        }
    }

    [Fact]
    public async Task ExistingTrashHistory_RecoversBookTitleAndSurvivesPurge()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var book = await AddMatchingReadingBookAsync(device, DateTimeOffset.UtcNow);
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 123, 10, 1, 10);
        await device.Library.DeleteAsync(book.BookId);
        // Reproduce an existing installation whose stats predate title retention.
        await device.SqlAsync("DELETE FROM ReaderReadingHistory;");

        await device.Reader.InitializeAsync();
        var recovered = Assert.Single((await device.Reader.GetReadingDashboardAsync()).RecentBooks);
        Assert.Equal("Shared reading book", recovered.Title);
        Assert.False(recovered.IsInLibrary);
        await device.Library.PurgeTrashItemAsync(Assert.Single(await device.Library.GetTrashItemsAsync()).Id);
        await device.Reader.InitializeAsync();

        var retained = Assert.Single((await device.Reader.GetReadingDashboardAsync()).RecentBooks);
        Assert.Equal("Shared reading book", retained.Title);
        Assert.Equal(123, retained.CumulativeSeconds);
    }

    [Fact]
    public async Task DeletedFormatHistory_IsRetainedWithTheCurrentBookOnBothDevices()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        var otherFormat = await a.AddBookAsync();
        await a.SqlAsync("UPDATE BookFiles SET BookId = $book, Format = 'pdf' WHERE Id = $file; DELETE FROM Books WHERE Id = $extra;",
            ("$book", book.BookId.ToString()), ("$file", otherFormat.FileId.ToString()),
            ("$extra", otherFormat.BookId.ToString()));
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 120, 10, 1, 10);
        await a.Reader.AddReadingTimeAsync(book.BookId, otherFormat.FileId, 300, 40, 4, 10);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.Library.DeleteFileAsync(book.BookId, book.FileId);
        await a.SqlAsync("DELETE FROM ReaderReadingHistory WHERE BookFileId = $file;", ("$file", book.FileId.ToString()));
        await a.Reader.InitializeAsync();

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var currentBook = Assert.Single(await device.Library.SearchAsync());
            Assert.Single(currentBook.Files);
            var dashboard = await device.Reader.GetReadingDashboardAsync();
            Assert.Equal(420, dashboard.TotalSeconds);
            Assert.Equal(420, dashboard.DailyReading.Sum(day => day.ActiveSeconds));
            var history = Assert.Single(dashboard.RecentBooks);
            Assert.Equal(currentBook.Title, history.Title);
            Assert.True(history.IsInLibrary);
        }
    }

    [Fact]
    public async Task Dashboard_GroupsBookFormatsAndRanksAllHistory()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var longRead = await device.AddBookAsync();
        var recent = await device.AddBookAsync();
        var secondFormat = await device.AddBookAsync();
        await device.SqlAsync("UPDATE BookFiles SET BookId = $book WHERE Id = $file; DELETE FROM Books WHERE Id = $extra;",
            ("$book", longRead.BookId.ToString()), ("$file", secondFormat.FileId.ToString()),
            ("$extra", secondFormat.BookId.ToString()));
        await device.Reader.AddReadingTimeAsync(longRead.BookId, longRead.FileId, 600, 50, 5, 10);
        await device.Reader.AddReadingTimeAsync(longRead.BookId, secondFormat.FileId, 300, 100, 10, 10);
        await device.Reader.AddReadingTimeAsync(recent.BookId, recent.FileId, 30, 10, 1, 10);

        var dashboard = await device.Reader.GetReadingDashboardAsync(recentLimit: 1);

        Assert.Equal(2, dashboard.BooksStarted);
        Assert.Equal(2, dashboard.Books.Count);
        Assert.Equal(1, dashboard.BooksFinished);
        Assert.Equal(930, dashboard.TotalSeconds);
        Assert.Equal(recent.BookId, Assert.Single(dashboard.RecentBooks).BookId);
        Assert.Equal(longRead.BookId, dashboard.MostReadBooks[0].BookId);
        Assert.Equal(900, dashboard.MostReadBooks[0].CumulativeSeconds);
    }

    private static async Task<(Guid BookId, Guid FileId)> AddMatchingReadingBookAsync(
        Device device, DateTimeOffset updatedAt, bool uppercaseHash = false)
    {
        var created = await device.AddBookAsync(updatedAt);
        var book = (await device.Library.GetBookAsync(created.BookId))!;
        var file = Assert.Single(book.Files);
        var bytes = Encoding.UTF8.GetBytes("The same book independently imported on each device.");
        await File.WriteAllBytesAsync(device.Library.GetAbsoluteFilePath(file), bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await device.SqlAsync("""
            UPDATE Books SET Title = 'Shared reading book' WHERE Id = $book;
            UPDATE BookFiles SET Sha256 = $hash, Size = $size WHERE Id = $file;
            """, ("$book", created.BookId.ToString()), ("$file", created.FileId.ToString()),
            ("$hash", uppercaseHash ? hash : hash.ToLowerInvariant()), ("$size", bytes.Length));
        return (created.BookId, created.FileId);
    }

    private static async Task<ReaderAnnotation> AddReadingNoteAsync(Device device, Guid bookId, Guid fileId, string text)
    {
        var note = new ReaderAnnotation
        {
            BookId = bookId, BookFileId = fileId, ChapterPath = "chapter.xhtml",
            SelectedText = text, EndOffset = text.Length, Note = "Note: " + text
        };
        await device.Reader.SaveAnnotationAsync(note);
        return note;
    }

    private static async Task<ReaderBookmark> AddReadingBookmarkAsync(Device device, Guid bookId, Guid fileId, string title)
    {
        var bookmark = new ReaderBookmark
        {
            BookId = bookId, BookFileId = fileId, ChapterPath = "chapter.xhtml", Title = title
        };
        await device.Reader.SaveBookmarkAsync(bookmark);
        return bookmark;
    }

    private static async Task AssertReadingNotesAsync(Device device, Guid bookId, Guid fileId, params Guid[] ids)
    {
        var notes = await device.Reader.GetAnnotationsAsync(fileId);
        Assert.Equal(ids.Order(), notes.Select(row => row.Id).Order());
        Assert.All(notes, note => Assert.Equal(bookId, note.BookId));
    }
}
