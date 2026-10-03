using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task ReaderInitialization_RetiresCompletionTriggersAndPreservesReadingData()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var book = await device.AddBookAsync();
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 120, 50, 5, 10);
        await device.SqlAsync("""
            CREATE TABLE ReaderCompletions (BookId TEXT PRIMARY KEY);
            CREATE TRIGGER ReaderCompletions_Status AFTER UPDATE OF ReadingStatus ON Books
                BEGIN INSERT OR IGNORE INTO ReaderCompletions VALUES (NEW.Id); END;
            CREATE TRIGGER ReaderCompletions_Insert AFTER INSERT ON Books
                BEGIN INSERT OR IGNORE INTO ReaderCompletions VALUES (NEW.Id); END;
            CREATE TRIGGER ReaderCompletions_FileDelete BEFORE DELETE ON BookFiles
                BEGIN INSERT OR IGNORE INTO ReaderCompletions VALUES (OLD.BookId); END;
            INSERT INTO ReaderCompletions VALUES ('retained');
            """);

        await device.Reader.InitializeAsync();
        await device.Reader.InitializeAsync();

        Assert.Equal(0, await device.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'ReaderCompletions_%';"));
        Assert.Equal(1, await device.ScalarAsync("SELECT COUNT(*) FROM ReaderCompletions;"));
        Assert.Equal(120, (await device.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(120, (await device.Reader.GetReadingDashboardAsync()).TotalSeconds);
    }

    [Fact]
    public async Task Sync_AcceptsVersionFourSnapshotWithRetiredBadgeFields()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 120, 50, 5, 10);
        await a.SyncAsync();
        var json = JsonSerializer.SerializeToNode(bucket.Snapshot(a.SnapshotKey))!;
        json["Version"] = 4;
        json["ReadingAchievements"] = JsonNode.Parse("""
            {"Awards":[{"Id":"time-1","RuleVersion":1,"EarnedAt":"2026-10-02T00:00:00Z","Historical":false,"Seen":false}],"Completions":[]}
            """);
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                JsonSerializer.Serialize(gzip, json);
            bucket.Objects[a.SnapshotKey] = output.ToArray();
        }

        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();

        Assert.Equal(1, await b.BookCountAsync());
        Assert.Equal(120, (await b.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(120, (await b.Reader.GetReadingDashboardAsync()).TotalSeconds);
    }
}
