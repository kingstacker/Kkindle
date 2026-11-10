using System.Security.Cryptography;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed partial class KindleAnnotationDatabaseReaderTests
{
    private const string BookId =
        "02b1ec32-aa42-4477-b395-b0dc41131082-EBOK-Gui_Mo:97D7128C-0";

    [Fact]
    public async Task ReadsAccountHighlightsAndNotesWithoutChangingDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "ksdk_annotation_v1.db");
        try
        {
            var highlightPayload = JsonSerializer.Serialize(new
            {
                book_data = new
                {
                    asin = "02b1ec32-aa42-4477-b395-b0dc41131082",
                    contentType = "EBOK",
                    guid = "Gui_Mo:97D7128C"
                },
                created_time = 1793450000000L,
                last_modified = 1793450000001L,
                start_position = new { longPosition = "40689", shortPosition = 40689 },
                end_position = new { longPosition = "41018", shortPosition = 41018 },
                json_metadata = "{}",
                type = "HIGHLIGHT"
            });
            var notePayload = JsonSerializer.Serialize(new
            {
                book_data = new
                {
                    asin = "02b1ec32-aa42-4477-b395-b0dc41131082",
                    contentType = "EBOK",
                    guid = "Gui_Mo:97D7128C"
                },
                created_time = 1793450100000L,
                last_modified = 1793450100001L,
                start_position = new { longPosition = "40690", shortPosition = 40690 },
                end_position = new { longPosition = "41018", shortPosition = 41018 },
                json_metadata = "{\"note_text\":\" 我的批注\\n\"}",
                type = "NOTE"
            });
            await CreateDatabaseAsync(database,
                ("highlight", 1, highlightPayload, 1793450000001L),
                ("note", 3, notePayload, 1793450100001L),
                ("popular", 18, "{\"type\":\"POPULARHIGHLIGHT\"}", 1793450200001L));
            var before = SHA256.HashData(await File.ReadAllBytesAsync(database));
            var resolver = KindleAnnotationBookTitleResolver.Create(["规模_02b1ec32-aa42-4477-b395-b0dc41131082.sdr"]);

            var notes = await KindleAnnotationDatabaseReader.ReadClippingsAsync(database, resolver.Resolve);

            Assert.Equal(2, notes.Count);
            var note = notes.Single(item => item.Type == KindleClippingType.Note);
            Assert.Equal("规模", note.BookTitle);
            Assert.Equal("我的批注", note.Content);
            Assert.Equal("位置 40690–41018", note.Metadata);
            Assert.True(note.IsReadOnly);
            Assert.StartsWith("kindle-db:", note.Id);
            var pair = Assert.Single(KindleClippingsParser.PairForDisplay(notes));
            Assert.Equal(KindleClippingType.Highlight, pair.Clipping.Type);
            Assert.Equal("划线原文待定位", pair.Clipping.Content);
            Assert.Equal("我的批注", pair.PairedNote!.Content);
            Assert.Single(await KindleAnnotationDatabaseReader.ReadClippingsAsync(database, resolver.Resolve, 1));
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SupportsCamelCasePayloadAndPositionColumns()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "ksdk_annotation_v1.db");
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                bookData = new { guid = "Kkindle_USB_Probe_20261031" },
                created_time = 1793453319257L,
                last_modified = 1793453319257L,
                jsonMetadata = new { noteText = "设备笔记" }
            });
            await CreateDatabaseAsync(database, ("note", 3, payload, 1793453319257L),
                startPosition: "{\"longPosition\":\"727\",\"shortPosition\":727}",
                endPosition: "{\"longPosition\":\"730\",\"shortPosition\":730}");

            var note = Assert.Single(await KindleAnnotationDatabaseReader.ReadClippingsAsync(database));

            Assert.Equal("设备笔记", note.Content);
            Assert.Equal("位置 727–730", note.Metadata);
            Assert.Equal("Kkindle USB Probe 20261031", note.BookTitle);
            Assert.NotNull(note.AddedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WriterUpdatesOnlyRequestedRowsAndProducesVerifiedSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.db");
        var candidate = Path.Combine(root, "candidate.db");
        try
        {
            var payload = "{\"type\":\"NOTE\",\"json_metadata\":\"{}\",\"book_data\":{}}";
            await CreateDatabaseAsync(source, ("existing", 3, payload, 1793450000000L));
            var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var updatedPayload =
                "{\"type\":\"NOTE\",\"json_metadata\":\"{\\\"note_text\\\":\\\"同步后的笔记\\\"}\",\"book_data\":{}}";
            var mutation = new KindleAnnotationMutation(
                "existing", BookId, 3, 727, 730, 1793450000000L, 1793451000000L,
                "同步后的笔记", updatedPayload);

            var hash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(
                source, candidate, [mutation]);

            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidate))).ToLowerInvariant());
            var record = Assert.Single(await KindleAnnotationDatabaseReader.ReadRecordsAsync(candidate));
            Assert.Equal("同步后的笔记", record.Note);
            Assert.Equal(1793451000000L, record.UpdatedAt!.Value.ToUnixTimeMilliseconds());
            Assert.Equal("{\"longPosition\":\"727\",\"shortPosition\":727}",
                await ReadPositionAsync(candidate, "start_position"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CreateDatabaseAsync(
        string path,
        params (string Id, int Dataset, string Payload, long Modified)[] rows) =>
        await CreateDatabaseCoreAsync(
            path,
            rows,
            "{\"longPosition\":\"0\",\"shortPosition\":0}",
            "{\"longPosition\":\"0\",\"shortPosition\":0}");

    private static async Task CreateDatabaseAsync(
        string path,
        (string Id, int Dataset, string Payload, long Modified) first,
        (string Id, int Dataset, string Payload, long Modified)[]? extraRows = null,
        string startPosition = "{\"longPosition\":\"0\",\"shortPosition\":0}",
        string endPosition = "{\"longPosition\":\"0\",\"shortPosition\":0}") =>
        await CreateDatabaseCoreAsync(
            path,
            new[] { first }.Concat(extraRows ?? []),
            startPosition,
            endPosition);

    private static async Task CreateDatabaseCoreAsync(
        string path,
        IEnumerable<(string Id, int Dataset, string Payload, long Modified)> rows,
        string startPosition,
        string endPosition)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE nonsyncable_annotations (
                    annotation_id TEXT NOT NULL,
                    book_id TEXT NOT NULL,
                    dataset INTEGER NOT NULL,
                    start_position TEXT,
                    end_position TEXT,
                    created_time INTEGER NOT NULL,
                    modified_time INTEGER NOT NULL,
                    serialized_payload TEXT NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }
        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO nonsyncable_annotations
                    (annotation_id, book_id, dataset, start_position, end_position,
                     created_time, modified_time, serialized_payload)
                VALUES ($id, $bookId, $dataset, $start, $end, $created, $modified, $payload);
                """;
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$bookId", BookId);
            insert.Parameters.AddWithValue("$dataset", row.Dataset);
            insert.Parameters.AddWithValue("$start", startPosition);
            insert.Parameters.AddWithValue("$end", endPosition);
            insert.Parameters.AddWithValue("$created", row.Modified);
            insert.Parameters.AddWithValue("$modified", row.Modified);
            insert.Parameters.AddWithValue("$payload", row.Payload);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ReadPositionAsync(string path, string column)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM nonsyncable_annotations LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
