using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task ReadingDataReset_ClearsCurrentAndArchivedHistoryButKeepsBooksAndNotes()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var current = await device.AddBookAsync();
        var archived = await device.AddBookAsync();
        await device.SqlAsync("UPDATE BookFiles SET Format = 'pdf' WHERE Id = $file;", ("$file", current.FileId.ToString()));
        await device.Reader.AddReadingTimeAsync(current.BookId, current.FileId, 120, 60, 6, 10);
        await device.Reader.AddReadingTimeAsync(archived.BookId, archived.FileId, 300, 100, 10, 10);
        await SaveResetTestProgressAsync(device, current.BookId, current.FileId, 60);
        await device.DeleteBookAsync(archived.BookId);
        var note = await AddReadingNoteAsync(device, current.BookId, current.FileId, "keep this highlight and note");
        var bookmark = await AddReadingBookmarkAsync(device, current.BookId, current.FileId, "keep this bookmark");
        var layout = new ReaderLayoutSettings { FontScale = 1.3, TwoPageMode = true };
        await device.Reader.SaveLayoutSettingsAsync(current.BookId, current.FileId, layout);
        await device.SaveAppSettingsAsync(7);
        var settingsBytes = await File.ReadAllBytesAsync(device.Paths.Settings);
        var syncSettings = await device.Service.LoadSettingsAsync();
        var file = Assert.Single(Assert.Single(await device.Library.SearchAsync()).Files);
        var filePath = Path.Combine(device.Paths.Data, file.RelativePath);
        var fileBytes = await File.ReadAllBytesAsync(filePath);
        var changes = new List<LocalDataChangeKind>();
        device.Reader.DataChanged += (_, e) => changes.Add(e.Kind);

        await device.Reader.ResetReadingDataAsync();

        await AssertNoResettableReadingDataAsync(device);
        var dashboard = await device.Reader.GetReadingDashboardAsync();
        Assert.Equal(0, dashboard.TotalSeconds);
        Assert.Equal(0, dashboard.BooksStarted);
        Assert.Equal(0, dashboard.BooksFinished);
        Assert.Equal(0, dashboard.AverageProgress);
        Assert.Empty(dashboard.RecentBooks);
        Assert.All(dashboard.DailyReading, day => Assert.Equal(0, day.ActiveSeconds));
        Assert.Equal(1, dashboard.BookmarkCount);
        Assert.Equal(1, dashboard.AnnotationCount);
        Assert.Equal(note.Note, Assert.Single(await device.Reader.GetAnnotationsAsync(current.FileId)).Note);
        Assert.Equal(bookmark.Id, Assert.Single(await device.Reader.GetBookmarksAsync(current.FileId)).Id);
        Assert.Equal(layout, await device.Reader.GetLayoutSettingsAsync(current.FileId));
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(settingsBytes, await File.ReadAllBytesAsync(device.Paths.Settings));
        Assert.Equal(syncSettings, await device.Service.LoadSettingsAsync());
        Assert.Equal(new[] { LocalDataChangeKind.ReadingDataReset }, changes);
        Assert.Equal(1, await device.BookCountAsync());

        await new ReaderDataService(device.Paths).InitializeAsync();
        await device.Service.InitializeDeletionTrackingAsync(deviceId: device.Id);
        await AssertNoResettableReadingDataAsync(device);
        Assert.Equal(1, (await ReadResetTestGenerationAsync(device))!.Generation);
    }

    [Fact]
    public async Task ReadingDataReset_WorksBeforeSyncHasEverBeenConfigured()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var book = Guid.NewGuid();
            var file = Guid.NewGuid();
            await reader.AddReadingTimeAsync(book, file, 40, 25, 1, 4);
            await reader.SaveProgressAsync(new ReaderProgressRow(book, file, "page.xhtml", null, 1, 0, 25, 0, DateTimeOffset.UtcNow));
            await reader.ResetReadingDataAsync();
            await reader.InitializeAsync();
            Assert.Null(await reader.GetProgressAsync(file));
            Assert.Null(await reader.GetReadingStatsAsync(file));
            await reader.AddReadingTimeAsync(book, file, 9, 5, 0, 4);
            Assert.Equal(9, (await reader.GetReadingStatsAsync(file))!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReadingDataReset_FailureRollsBackTheEntireReset()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var book = await device.AddBookAsync();
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 100, 50, 5, 10);
        await SaveResetTestProgressAsync(device, book.BookId, book.FileId, 50);
        await device.SqlAsync("""
            CREATE TRIGGER RejectReadingReset BEFORE DELETE ON ReaderReadingStats
            BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
            """);
        var notified = false;
        device.Reader.DataChanged += (_, _) => notified = true;

        await Assert.ThrowsAsync<SqliteException>(() => device.Reader.ResetReadingDataAsync());

        Assert.False(notified);
        Assert.Null(await ReadResetTestGenerationAsync(device));
        Assert.Equal(50, (await device.Reader.GetProgressAsync(book.FileId))!.ProgressPercent);
        Assert.Equal(100, (await device.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(100, (await device.Reader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        Assert.Equal(1, await device.ScalarAsync("SELECT COUNT(*) FROM ReaderReadingSessions;"));
        Assert.Equal(0, await device.ScalarAsync("SELECT COUNT(*) FROM S3SyncDeletionLog;"));
    }

    [Fact]
    public async Task ReadingDataReset_OldOfflineSnapshotsCannotRestoreDataOrDeleteNewProgress()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 120, 60, 6, 10);
        await SaveResetTestProgressAsync(a, book.BookId, book.FileId, 60);
        await AddReadingNoteAsync(a, book.BookId, book.FileId, "original note");
        await AddReadingBookmarkAsync(a, book.BookId, book.FileId, "original bookmark");
        await a.SyncAsync();
        await b.SyncAsync();

        // Keep an indefinitely offline device in the bucket, with both newer
        // timestamps and an old deletion that would otherwise win by time.
        var stale = bucket.Snapshot(b.SnapshotKey);
        stale.DeviceId = Guid.NewGuid().ToString("N");
        stale.CreatedAt = DateTimeOffset.UtcNow.AddDays(2);
        stale.ReadingStats.ForEach(row => row.UpdatedAt = stale.CreatedAt);
        stale.Progress.ForEach(row => row.UpdatedAt = stale.CreatedAt);
        stale.Tombstones.Add(new S3SyncTombstone
        {
            EntityType = "progress", Key = book.FileId.ToString("N"), DeletedAt = stale.CreatedAt.AddDays(1)
        });
        bucket.Objects[$"sync/devices/{stale.DeviceId}/snapshot.bin"] = MemoryBucket.Encode(stale);

        await a.Reader.ResetReadingDataAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 17, 5, 0, 10);
        await SaveResetTestProgressAsync(a, book.BookId, book.FileId, 5);
        await a.SyncAsync();
        Assert.Equal(3, bucket.Snapshot(a.SnapshotKey).Version);

        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 200, 92, 9, 10);
        await SaveResetTestProgressAsync(b, book.BookId, book.FileId, 92, DateTimeOffset.UtcNow.AddDays(3));
        await AddReadingNoteAsync(b, book.BookId, book.FileId, "offline note kept through reset");
        await b.SyncAsync();
        Assert.Equal(17, (await b.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(5, (await b.Reader.GetProgressAsync(book.FileId))!.ProgressPercent);

        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 23, 9, 0, 10);
        await SaveResetTestProgressAsync(b, book.BookId, book.FileId, 9);
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        await using var newcomer = await Device.CreateAsync(bucket);
        await newcomer.SyncAsync();

        foreach (var device in new[] { a, b, newcomer })
        {
            Assert.Equal(40, (await device.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
            Assert.Equal(9, (await device.Reader.GetProgressAsync(book.FileId))!.ProgressPercent);
            Assert.Equal(40, (await device.Reader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
            Assert.Equal(2, (await device.Reader.GetAnnotationsAsync(book.FileId)).Count);
            Assert.Single(await device.Reader.GetBookmarksAsync(book.FileId));
            Assert.Equal(1, (await ReadResetTestGenerationAsync(device))!.Generation);
        }
    }

    [Fact]
    public async Task ReadingDataReset_LargeResetDoesNotNeedASecondSyncDeletionApproval()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        for (var index = 0; index < 25; index++)
        {
            var book = await a.AddBookAsync();
            await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 20, 50, 5, 10);
            await SaveResetTestProgressAsync(a, book.BookId, book.FileId, 50);
        }
        await a.SyncAsync();
        await a.Reader.ResetReadingDataAsync();
        await a.SyncAsync();
        var snapshot = bucket.Snapshot(a.SnapshotKey);
        Assert.Empty(snapshot.Progress);
        Assert.Empty(snapshot.ReadingStats);
        Assert.DoesNotContain(snapshot.Tombstones, item => item.EntityType is "stats" or "progress");
        Assert.Equal(25, snapshot.Books.Count);
        Assert.NotNull(snapshot.ReadingDataReset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingDataReset_DuringNetworkDownloadWinsInsideTheMergeTransaction(bool duringBookDownload)
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        b.Settings = b.Settings with { DownloadBookFilesOnSync = true };
        var remoteBook = await a.AddBookAsync();
        await a.Reader.ResetReadingDataAsync();
        await a.Reader.AddReadingTimeAsync(remoteBook.BookId, remoteBook.FileId, 80, 60, 6, 10);
        await SaveResetTestProgressAsync(a, remoteBook.BookId, remoteBook.FileId, 60);
        await a.SyncAsync();
        var localBook = await b.AddBookAsync();
        await b.Reader.AddReadingTimeAsync(localBook.BookId, localBook.FileId, 30, 50, 5, 10);
        var resetDuringDownload = false;
        bucket.BeforeGet = async (key, _) =>
        {
            var match = duringBookDownload ? key.Contains("/objects/", StringComparison.Ordinal) : key == a.SnapshotKey;
            if (!match || resetDuringDownload) return;
            resetDuringDownload = true;
            await b.Reader.ResetReadingDataAsync();
            await b.Reader.ResetReadingDataAsync();
        };

        await b.SyncAsync();
        bucket.BeforeGet = null;

        Assert.True(resetDuringDownload);
        await AssertNoResettableReadingDataAsync(b);
        Assert.Equal(2, (await ReadResetTestGenerationAsync(b))!.Generation);
        Assert.Equal(2, await b.BookCountAsync());
        await a.SyncAsync();
        await AssertNoResettableReadingDataAsync(a);
    }

    [Fact]
    public async Task ReadingDataReset_ConcurrentAndRepeatedResetsConverge()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 100, 50, 5, 10);
        await a.SyncAsync();
        await b.SyncAsync();
        await Task.WhenAll(a.Reader.ResetReadingDataAsync(), b.Reader.ResetReadingDataAsync());
        var resetA = (await ReadResetTestGenerationAsync(a))!;
        var resetB = (await ReadResetTestGenerationAsync(b))!;
        var winner = resetA.Id.CompareTo(resetB.Id) > 0 ? resetA : resetB;
        var expectedSeconds = winner == resetA ? 17 : 23;
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 17, 5, 0, 10);
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 23, 5, 0, 10);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(winner, await ReadResetTestGenerationAsync(device));
            Assert.Equal(expectedSeconds, (await device.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        }

        await b.Reader.ResetReadingDataAsync();
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 9, 2, 0, 10);
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(2, (await ReadResetTestGenerationAsync(device))!.Generation);
            Assert.Equal(9, (await device.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
            Assert.Equal(9, (await device.Reader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        }
    }

    [Fact]
    public async Task ReadingDataReset_OnlyAppliesProgressTombstonesFromTheCurrentGeneration()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await SaveResetTestProgressAsync(a, book.BookId, book.FileId, 90);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SqlAsync("DELETE FROM ReaderProgress; UPDATE S3SyncDeletionLog SET DeletedAt = $time WHERE EntityType = 'progress';",
            ("$time", DateTimeOffset.UtcNow.AddDays(2).ToString("O")));
        await a.SyncAsync();

        await a.Reader.ResetReadingDataAsync();
        await SaveResetTestProgressAsync(a, book.BookId, book.FileId, 10);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal(10, (await b.Reader.GetProgressAsync(book.FileId))!.ProgressPercent);

        await a.SqlAsync("DELETE FROM ReaderProgress;");
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Null(await b.Reader.GetProgressAsync(book.FileId));
        var tombstone = Assert.Single(bucket.Snapshot(a.SnapshotKey).Tombstones, item => item.EntityType == "progress");
        Assert.Equal((await ReadResetTestGenerationAsync(a))!.Id, tombstone.ReadingDataResetId);
    }

    private static Task SaveResetTestProgressAsync(Device device, Guid book, Guid file, double progress, DateTimeOffset? updatedAt = null) =>
        device.Reader.SaveProgressAsync(new ReaderProgressRow(book, file, "chapter.xhtml", null, 2, 120, progress, 0,
            updatedAt ?? DateTimeOffset.UtcNow));

    private static async Task<ReadingDataReset?> ReadResetTestGenerationAsync(Device device)
    {
        await using var connection = new SqliteConnection($"Data Source={device.Paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM S3SyncLocalMetadata WHERE Key = 'reading-data-reset-v1';";
        return await command.ExecuteScalarAsync() is string json ? JsonSerializer.Deserialize<ReadingDataReset>(json) : null;
    }

    private static async Task AssertNoResettableReadingDataAsync(Device device)
    {
        foreach (var table in new[] { "ReaderProgress", "ReaderReadingStats", "ReaderReadingSessions", "ReaderReadingHistory",
                     "S3SyncReadingTimeCounters", "S3SyncReadingDayCounters" })
            Assert.Equal(0, await device.ScalarAsync($"SELECT COUNT(*) FROM {table};"));
    }
}
