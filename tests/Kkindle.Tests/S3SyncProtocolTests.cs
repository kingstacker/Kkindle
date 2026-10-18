using System.Reflection;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task FutureProtocolStopsBeforeUploadingOrCleaningObjects()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await a.AddBookAsync();
        await a.SyncAsync();
        var snapshot = bucket.Snapshot(a.SnapshotKey);
        Assert.Equal(5, snapshot.Version);
        snapshot.Version++;
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(snapshot);
        await using var b = await Device.CreateAsync(bucket);
        await b.AddBookAsync();
        var before = bucket.Objects.ToDictionary(pair => pair.Key, pair => pair.Value);
        var writes = bucket.WriteCount;
        await Assert.ThrowsAsync<InvalidDataException>(() => b.SyncAsync());
        Assert.Equal(writes, bucket.WriteCount);
        Assert.Equal(before.Count, bucket.Objects.Count);
        foreach (var (key, bytes) in before) Assert.Equal(bytes, bucket.Objects[key]);
    }

    [Fact]
    public async Task PreviousProtocolCanStillBeRead()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await a.AddBookAsync();
        await a.SyncAsync();
        var snapshot = bucket.Snapshot(a.SnapshotKey);
        snapshot.Version = 4;
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(snapshot);
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        Assert.Equal(1, await b.BookCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeRespectsAnnotationSettingAndSynchronizesTheResult(bool deleteNotes)
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SetCoverAsync(book.BookId, "purged-cover", DateTimeOffset.UtcNow);
        await a.Reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(), BookId = book.BookId, BookFileId = book.FileId,
            ChapterPath = "chapter.xhtml", SelectedText = "quote", Note = "note"
        });
        await a.SyncAsync();
        await a.Library.DeleteAsync(book.BookId);
        // Changing the setting after deletion must also govern the purge.
        await new AppSettingsStore(a.Paths).SaveAsync(new AppSettings { DeleteAnnotationsWhenDeletingBook = deleteNotes });
        await a.Library.PurgeTrashItemAsync(Assert.Single(await a.Library.GetTrashItemsAsync()).Id);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(0, await device.BookCountAsync());
            Assert.Equal(deleteNotes ? 0 : 1, await device.ScalarAsync("SELECT COUNT(*) FROM ReaderAnnotations;"));
        }
        if (!deleteNotes)
        {
            var archived = Assert.Single(bucket.Snapshot(b.SnapshotKey).ReadingMaterialBooks);
            Assert.NotNull(archived.CoverHash);
        }
    }

    [Fact]
    public async Task ArchivedMetadataKeepsNewerLocalVersionThenPropagatesIt()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(), BookId = book.BookId, BookFileId = book.FileId,
            ChapterPath = "chapter.xhtml", SelectedText = "quote"
        });
        await a.Library.DeleteAsync(book.BookId);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        var version = DateTimeOffset.UtcNow.AddMinutes(1);
        await b.SqlAsync("UPDATE ReadingMaterialBooks SET Title='Newer retained title',UpdatedAt=$version;",
            ("$version", version.ToString("O")));
        await b.SyncAsync();
        await a.SyncAsync();
        await using var c = await Device.CreateAsync(bucket);
        await c.SyncAsync();
        foreach (var device in new[] { a, b, c })
        {
            var row = Assert.Single(bucket.Snapshot(device.SnapshotKey).ReadingMaterialBooks);
            Assert.Equal("Newer retained title", row.Title);
            Assert.Equal(version, row.UpdatedAt);
        }
    }

    [Fact]
    public async Task ArchivedMetadataIsCapturedInsideTheDatabaseTransaction()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(), BookId = book.BookId, BookFileId = book.FileId,
            ChapterPath = "chapter.xhtml", SelectedText = "quote"
        });
        await a.Library.DeleteAsync(book.BookId);
        await using var connection = new SqliteConnection($"Data Source={a.Paths.Database}");
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var capture = typeof(S3SyncService).GetMethod("CaptureDatabaseSnapshotAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var snapshot = await (Task<S3SyncSnapshot>)capture.Invoke(null,
            [connection, transaction, a.Id, Array.Empty<S3SyncTombstone>(), CancellationToken.None, DateTimeOffset.UtcNow])!;
        Assert.Single(snapshot.Annotations);
        Assert.Equal(book.BookId, Assert.Single(snapshot.ReadingMaterialBooks).Id);
    }

    [Fact]
    public void EverySnapshotRowCollectionHasARegistration()
    {
        var field = typeof(S3SyncService).GetField("SyncEntities", BindingFlags.Static | BindingFlags.NonPublic)!;
        var registrations = (Array)field.GetValue(null)!;
        var properties = registrations.Cast<object>().Select(value =>
            (string)value.GetType().GetProperty("Property")!.GetValue(value)!).Order().ToArray();
        var collections = typeof(S3SyncSnapshot).GetProperties()
            .Where(property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                && property.Name != nameof(S3SyncSnapshot.Tombstones))
            .Select(property => property.Name).Order().ToArray();
        Assert.Equal(collections, properties);
    }

    [Fact]
    public async Task LegacyArchiveSchemaMigratesWithoutLosingMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ReadingMaterialBooks(Id TEXT PRIMARY KEY,Title TEXT NOT NULL,CoverPath TEXT NULL);
            INSERT INTO ReadingMaterialBooks VALUES('old-id','Retained title','covers/old.jpg');
            """;
        await command.ExecuteNonQueryAsync();
        await ReadingMaterialBookSchema.EnsureAsync(connection, CancellationToken.None);
        command.CommandText = "SELECT Title,CoverPath,UpdatedAt,CoverHash FROM ReadingMaterialBooks;";
        string version;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Retained title", reader.GetString(0));
            Assert.Equal("covers/old.jpg", reader.GetString(1));
            version = reader.GetString(2);
            Assert.True(DateTimeOffset.Parse(version) > DateTimeOffset.UnixEpoch);
            Assert.True(reader.IsDBNull(3));
        }
        await ReadingMaterialBookSchema.EnsureAsync(connection, CancellationToken.None);
        command.CommandText = "SELECT UpdatedAt FROM ReadingMaterialBooks;";
        Assert.Equal(version, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void BookDeletionDoesNotAuthorizeCleaningTheLastCopyOfRetainedNotes()
    {
        var now = DateTimeOffset.UtcNow;
        var bookId = Guid.NewGuid();
        var annotationId = Guid.NewGuid();
        var source = new S3SyncSnapshot
        {
            Annotations = [new S3SyncAnnotation { Id = annotationId, BookId = bookId, UpdatedAt = now }]
        };
        var target = new S3SyncSnapshot
        {
            Tombstones = [new S3SyncTombstone { EntityType = "book", Key = bookId.ToString("N"), DeletedAt = now.AddDays(1) }]
        };
        var covered = typeof(S3SyncService).GetMethod("SnapshotIsSubsumed", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.False((bool)covered.Invoke(null, [source, new[] { target }])!);
        target.Tombstones.Add(new S3SyncTombstone
        {
            EntityType = "annotation", Key = annotationId.ToString("N"), DeletedAt = now.AddDays(1)
        });
        Assert.True((bool)covered.Invoke(null, [source, new[] { target }])!);
    }

    [Fact]
    public async Task ArchivedMetadataComparesFullTimestampPrecisionAndBreaksTiesDeterministically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Books(Id TEXT); CREATE TABLE ReaderAnnotations(BookId TEXT);";
        await command.ExecuteNonQueryAsync();
        await ReadingMaterialBookSchema.EnsureAsync(connection, CancellationToken.None);
        var id = Guid.NewGuid();
        command.CommandText = "INSERT INTO ReaderAnnotations VALUES($id);";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync();
        var merge = typeof(S3SyncService).GetMethod("SaveReadingMaterialBooksAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var version = DateTimeOffset.UtcNow;
        await using var transaction = connection.BeginTransaction();
        async Task<bool> Save(string title, DateTimeOffset updated, string hash) =>
            await (Task<bool>)merge.Invoke(null, [connection, transaction,
                new[] { new S3SyncBook { Id = id, Title = title, UpdatedAt = updated,
                    CoverHash = hash, LocalCoverPath = hash + ".jpg" } }, CancellationToken.None])!;
        Assert.True(await Save("B", version, "b"));
        Assert.False(await Save("Z", version.AddTicks(-1), "z"));
        Assert.True(await Save("A", version.AddTicks(1), "a"));
        Assert.False(await Save("B", version, "z"));
        Assert.True(await Save("B", version.AddTicks(1), "b"));
        Assert.True(await Save("B", version.AddTicks(1), "c"));
        Assert.False(await Save("B", version.AddTicks(1), "b"));
        Assert.False(await Save("A", version.AddTicks(1), "z"));
        command.Transaction = transaction;
        command.CommandText = "SELECT Title,CoverHash,UpdatedAt FROM ReadingMaterialBooks;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("B", reader.GetString(0));
        Assert.Equal("c", reader.GetString(1));
        Assert.Equal(version.AddTicks(1), DateTimeOffset.Parse(reader.GetString(2)));
    }

    [Fact]
    public async Task MissingRetainedCoverIsRetriedWithoutAdvancingMetadataVersion()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        var hash = await a.SetCoverAsync(book.BookId, "retry-cover", DateTimeOffset.UtcNow);
        await a.Reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(), BookId = book.BookId, BookFileId = book.FileId,
            ChapterPath = "chapter.xhtml", SelectedText = "quote"
        });
        await a.Library.DeleteAsync(book.BookId);
        await a.SyncAsync();
        var key = Assert.Single(bucket.Objects.Keys, key => key.EndsWith(hash, StringComparison.OrdinalIgnoreCase));
        Assert.True(bucket.Objects.TryRemove(key, out var blob));
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        Assert.Equal(0, await b.ScalarAsync("SELECT COUNT(*) FROM ReadingMaterialBooks;"));
        bucket.Objects[key] = blob!;
        await b.SyncAsync();
        Assert.Equal(hash, Assert.Single(bucket.Snapshot(b.SnapshotKey).ReadingMaterialBooks).CoverHash);
    }
}
