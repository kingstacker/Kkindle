using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("lifecycle-regression-key")]
    public async Task SoftDeletedFormatNotesSurviveSyncAndRestartUntilExplicitPurge(string encryptionKey)
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, encryptionKey);
        await using var b = await Device.CreateAsync(bucket, encryptionKey);
        var book = await a.AddBookAsync();
        await AddLifecycleSecondFormatAsync(a, book.BookId);
        var note = await AddReadingNoteAsync(a, book.BookId, book.FileId, "retain until purge");
        await a.SyncAsync();
        await b.SyncAsync();

        await a.Library.DeleteFileAsync(book.BookId, book.FileId);
        await a.SyncAsync();
        for (var i = 0; i < 3; i++)
        {
            await b.SyncAsync();
            await b.Reader.InitializeAsync();
            await b.Library.InitializeAsync();
            await b.SyncAsync();
            await a.SyncAsync();
            Assert.Equal(note.Id, Assert.Single(await a.Reader.GetAllAnnotationsAsync()).Id);
            Assert.Equal(note.Id, Assert.Single(await b.Reader.GetAllAnnotationsAsync()).Id);
        }

        await a.Library.PurgeTrashItemAsync(Assert.Single(await a.Library.GetTrashItemsAsync()).Id);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Empty(await a.Reader.GetAllAnnotationsAsync());
        Assert.Empty(await b.Reader.GetAllAnnotationsAsync());
    }

    [Fact]
    public async Task OfflineMetadataEditDoesNotResurrectDeletedFormat()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await AddLifecycleSecondFormatAsync(a, book.BookId);
        await a.SyncAsync();
        await b.SyncAsync();
        var stale = Assert.Single(await b.Library.SearchAsync());

        await a.Library.DeleteFileAsync(book.BookId, book.FileId);
        await a.SyncAsync();
        stale.Title = "New title edited offline";
        await b.Library.UpdateMetadataAsync(stale);
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var live = Assert.Single(await device.Library.SearchAsync());
            Assert.Equal(stale.Title, live.Title);
            Assert.Equal("pdf", Assert.Single(live.Files).Format);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTrashRestoreWinsOverFileAndBookTombstones(bool wholeBook)
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await AddLifecycleSecondFormatAsync(a, book.BookId);
        await a.SyncAsync();
        await b.SyncAsync();
        var siblingVersion = bucket.Snapshot(a.SnapshotKey).Files.Single(file => file.Format == "pdf").ModifiedAt;
        if (wholeBook) await a.Library.DeleteAsync(book.BookId);
        else await a.Library.DeleteFileAsync(book.BookId, book.FileId);
        await a.SyncAsync();
        await b.SyncAsync();

        await a.Library.RestoreTrashItemAsync(Assert.Single(await a.Library.GetTrashItemsAsync()).Id);
        for (var i = 0; i < 3; i++)
        {
            await a.SyncAsync();
            await b.SyncAsync();
            Assert.Equal(2, Assert.Single(await a.Library.SearchAsync()).Files.Count);
            Assert.Equal(2, Assert.Single(await b.Library.SearchAsync()).Files.Count);
            if (!wholeBook)
                Assert.Equal(siblingVersion, bucket.Snapshot(a.SnapshotKey).Files.Single(file => file.Format == "pdf").ModifiedAt);
        }
    }

    [Fact]
    public async Task FileVersionMigrationDoesNotFollowLaterBookMetadataEdits()
    {
        var bucket = new MemoryBucket();
        await using var device = await Device.CreateAsync(bucket);
        var book = await device.AddBookAsync();
        await AddLifecycleSecondFormatAsync(device, book.BookId);
        await device.SqlAsync("""
            DROP TRIGGER BookFiles_SyncVersion_Insert;
            DROP TRIGGER BookFiles_SyncVersion_ContentUpdate;
            ALTER TABLE BookFiles DROP COLUMN SyncUpdatedAt;
            """);
        await device.Library.InitializeAsync();
        await device.SyncAsync();
        var versions = bucket.Snapshot(device.SnapshotKey).Files.ToDictionary(file => file.Id, file => file.ModifiedAt);
        var live = Assert.Single(await device.Library.SearchAsync());
        live.Title = "Metadata-only edit after upgrade";
        live.IsFavorite = true;
        await device.Library.UpdateMetadataAsync(live);
        await device.Library.InitializeAsync();
        await device.SyncAsync();

        Assert.All(bucket.Snapshot(device.SnapshotKey).Files, file => Assert.Equal(versions[file.Id], file.ModifiedAt));
    }

    [Fact]
    public async Task RecoveryDoesNotReplaceNewerSyncedProgress()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        var older = NewLifecycleProgress(book.BookId, book.FileId, 100, DateTimeOffset.UtcNow.AddMinutes(-5));
        await new ReaderProgressRecoveryService(a.Paths, a.Reader).SaveAsync(older, new CancellationToken(true));
        await b.Reader.SaveProgressAsync(older with { ScrollPosition = 900, UpdatedAt = DateTimeOffset.UtcNow });
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal(900, (await a.Reader.GetProgressAsync(book.FileId))!.ScrollPosition);

        await new ReaderProgressRecoveryService(a.Paths, a.Reader).InitializeAsync();

        Assert.Equal(900, (await a.Reader.GetProgressAsync(book.FileId))!.ScrollPosition);
        Assert.False(File.Exists(Path.Combine(a.Paths.Data, ReaderProgressRecoveryService.RecoveryFileName)));
    }

    [Fact]
    public async Task RemoteReadingResetInvalidatesPendingRecoveryProgress()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        await new ReaderProgressRecoveryService(a.Paths, a.Reader).SaveAsync(
            NewLifecycleProgress(book.BookId, book.FileId, 500, DateTimeOffset.UtcNow), new CancellationToken(true));
        await b.Reader.ResetReadingDataAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await new ReaderProgressRecoveryService(a.Paths, a.Reader).InitializeAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.Null(await a.Reader.GetProgressAsync(book.FileId));
        Assert.Null(await b.Reader.GetProgressAsync(book.FileId));
    }

    private static ReaderProgressRow NewLifecycleProgress(Guid bookId, Guid fileId, int scroll, DateTimeOffset updatedAt) =>
        new(bookId, fileId, "chapter.xhtml", null, 0, scroll, 25, 0, updatedAt);

    private static async Task AddLifecycleSecondFormatAsync(Device device, Guid bookId)
    {
        var source = Path.Combine(device.Root, "second.pdf");
        await File.WriteAllTextAsync(source, "second-format-" + bookId);
        await device.Library.AddFileToBookAsync(bookId, source);
    }
}
