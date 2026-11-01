using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Platform.Common.Tests;

public sealed class KindleAnnotationDatabaseDeviceTests
{
    [Fact]
    public async Task MountedKindleReadsAccountAnnotationDatabaseAsReadOnlyResources()
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            var documents = Path.Combine(kindle, "documents");
            var annotations = Path.Combine(
                kindle, "system", "ksdk", ".annotations", "amzn1.account.TEST");
            Directory.CreateDirectory(documents);
            Directory.CreateDirectory(Path.Combine(
                documents, "规模_02b1ec32-aa42-4477-b395-b0dc41131082.sdr"));
            Directory.CreateDirectory(annotations);
            var database = Path.Combine(annotations, "ksdk_annotation_v1.db");
            var payload = JsonSerializer.Serialize(new
            {
                book_data = new { guid = "Gui_Mo:97D7128C" },
                created_time = 1793450100000L,
                last_modified = 1793450100001L,
                start_position = new { longPosition = "40689", shortPosition = 40689 },
                end_position = new { longPosition = "41018", shortPosition = 41018 },
                json_metadata = "{\"note_text\":\"设备数据库批注\"}",
                type = "NOTE"
            });
            await CreateDatabaseAsync(database, payload);
            var service = new MassStorageKindleDeviceService(
                new AppPaths(Path.Combine(root, "app")),
                new FakeMetadataService(),
                [root]);
            var device = new KindleDevice
            {
                RootPath = kindle,
                VolumeSerial = "KINDLE-TEST",
                Transport = KindleTransport.MassStorage
            };

            var note = Assert.Single(await service.ReadClippingsAsync(device));

            Assert.Equal("规模", note.BookTitle);
            Assert.Equal("设备数据库批注", note.Content);
            Assert.True(note.IsReadOnly);
            await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteClippingAsync(device, note.Id));
        }
        finally { TryDelete(root); }
    }

    private static async Task CreateDatabaseAsync(string path, string payload)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
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
            INSERT INTO nonsyncable_annotations VALUES (
                'note-id',
                '02b1ec32-aa42-4477-b395-b0dc41131082-EBOK-Gui_Mo:97D7128C-0',
                3,
                '{"longPosition":"40689","shortPosition":40689}',
                '{"longPosition":"41018","shortPosition":41018}',
                1793450100000,
                1793450100001,
                $payload
            );
            """;
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeMetadataService : IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BookMetadata { Title = Path.GetFileNameWithoutExtension(path), Authors = "Author" });
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
