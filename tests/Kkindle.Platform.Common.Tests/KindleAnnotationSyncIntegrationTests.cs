using System.Security.Cryptography;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Platform.Common.Tests;

public sealed partial class KindleAnnotationDatabaseDeviceTests
{
    [Fact]
    public async Task MountedKindleAnnotationUnionRoundTripsRealDatabaseAndDoesNotRepeatChanges()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3");
            var epub = Path.Combine(root, "fixture.epub");
            await Azw3ReaderService.PrepareEpubAsync(fixture, epub);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            await library.ImportAsync([epub]);
            var book = Assert.Single(await library.SearchAsync());
            var file = Assert.Single(book.Files);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(library.GetAbsoluteFilePath(file), file.Sha256);
            var chapter = document.Chapters.First(path => new XhtmlChapterLoader().Load(path).BodyText.Contains("你好，世界。😀"));
            var body = new XhtmlChapterLoader().Load(chapter).BodyText;
            var start = body.IndexOf("你好，世界。😀", StringComparison.Ordinal);
            var local = new ReaderAnnotation
            {
                BookId = book.Id, BookFileId = file.Id,
                ChapterPath = Path.GetRelativePath(document.RootPath, chapter).Replace('\\', '/'),
                StartOffset = start, EndOffset = start + "你好，世界。😀".Length,
                SelectedText = "你好，世界。😀", Note = "电脑端测试笔记",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2), UpdatedAt = DateTimeOffset.UtcNow
            };
            await reader.SaveAnnotationAsync(local);
            var kindle = Path.Combine(root, "Kindle");
            Directory.CreateDirectory(Path.Combine(kindle, "documents"));
            File.Copy(fixture, Path.Combine(kindle, "documents", "native.azw3"));
            var database = Path.Combine(kindle, "system", "ksdk", ".annotations", "amzn1.account.TEST", "ksdk_annotation_v1.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            // The fixture's independent oracle places this native selection
            // at closed byte positions 1546..1558.
            const string asin = "ed787037-f332-4e71-a7dd-240cd71acf82";
            const string guid = "native-rich:FEB7174A";
            var bookId = $"{asin}-EBOK-{guid}-0";
            var payload = JsonSerializer.Serialize(new
            {
                type = "HIGHLIGHT", book_data = new { asin, guid },
                start_position = new { longPosition = "1546", shortPosition = 1546 },
                end_position = new { longPosition = "1558", shortPosition = 1558 },
                created_time = 1793450100000L, last_modified = 1793450100001L, json_metadata = "{}"
            });
            await CreateDatabaseAsync(database, payload);
            await using (var connection = new SqliteConnection(
                             new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE nonsyncable_annotations SET annotation_id='native-highlight', book_id=$book, dataset=1, start_position=$start, end_position=$end;";
                command.Parameters.AddWithValue("$book", bookId);
                command.Parameters.AddWithValue("$start", "{\"longPosition\":\"1546\",\"shortPosition\":1546}");
                command.Parameters.AddWithValue("$end", "{\"longPosition\":\"1558\",\"shortPosition\":1558}");
                await command.ExecuteNonQueryAsync();
            }
            var before = await File.ReadAllBytesAsync(database);
            var (service, device) = MountedSnapshotService(root, kindle);
            var deviceBook = new KindleBook { RelativePath = "documents/native.azw3", Title = book.Title, Authors = book.Authors, Format = "azw3" };
            var sync = new KindleAnnotationSyncService(paths, library, reader, new EpubBookContentService(reader));
            var first = await sync.SynchronizeAsync(device, [deviceBook], service);
            Assert.Equal(1, first.ChangesToComputer);
            Assert.Equal(1, first.ChangesToDevice);
            Assert.Equal(0, first.UnsupportedCount);
            Assert.Equal(before, await File.ReadAllBytesAsync(first.LocalBackupPath!));
            Assert.Equal(before, await File.ReadAllBytesAsync(first.DeviceBackupPath!));
            var installed = (await service.ReadAnnotationSnapshotAsync(device))!;
            Assert.Equal(3, installed.Records.Count);
            Assert.Equal(payload, installed.Records.Single(record => record.AnnotationId == "native-highlight").SerializedPayload);
            Assert.Equal("电脑端测试笔记", installed.Records.Single(record => record.Dataset == 3).NoteText);
            var after = await File.ReadAllBytesAsync(database);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(after)).ToLowerInvariant(), installed.Sha256);
            Assert.False((await sync.AnalyzeAsync(device, [deviceBook], service)).HasChanges);
            var second = await sync.SynchronizeAsync(device, [deviceBook], service);
            Assert.Equal(0, second.ChangesToComputer);
            Assert.Equal(0, second.ChangesToDevice);
            Assert.Equal(after, await File.ReadAllBytesAsync(database));
            Assert.Equal(2, (await reader.GetAnnotationsAsync(file.Id)).Count);
        }
        finally { TryDelete(root); }
    }
}
