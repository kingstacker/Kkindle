using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class WebDavSyncIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHttpTwoDevicesSyncBooksAnnotationsProgressSettingsAndDeletions(bool encrypted)
    {
        var server = new WebDavTestServer { AbsoluteHrefs = true };
        await using var listener = server.Listen();
        var settings = server.Settings(listener.Origin) with { EncryptionKey = encrypted ? "shared-key" : "" };
        await using var a = await Device.CreateAsync(server, settings, realHttp: true);
        await using var b = await Device.CreateAsync(server, settings, realHttp: true);
        var book = await a.AddBookAsync(large: true);
        var annotation = new ReaderAnnotation
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            StartOffset = 0, EndOffset = 4, SelectedText = "text", Note = "来自设备 A"
        };
        await a.Reader.SaveAnnotationAsync(annotation);
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(book.BookId, book.FileId,
            "chapter.xhtml", "p1", 1, 12, 45, 0, DateTimeOffset.UtcNow));
        await new AppSettingsStore(a.Paths).SaveAsync(new AppSettings { AutoBackupRetention = 17 });

        Assert.False((await a.SyncAsync()).IsPartial);
        var result = await b.SyncAsync();
        Assert.Equal(1, result.BooksAdded);
        Assert.Equal(1, result.FilesDownloaded);
        Assert.False(result.IsPartial);
        var downloaded = Assert.Single(await b.Library.SearchAsync());
        Assert.Equal(book.Bytes, await File.ReadAllBytesAsync(b.Library.GetAbsoluteFilePath(Assert.Single(downloaded.Files))));
        var remoteAnnotation = Assert.Single(await b.Reader.GetAllAnnotationsAsync());
        Assert.Equal(annotation.Note, remoteAnnotation.Note);
        Assert.Equal(45, (await b.Reader.GetProgressAsync(book.FileId))!.ProgressPercent);
        Assert.Equal(17, (await new AppSettingsStore(b.Paths).LoadAsync()).AutoBackupRetention);
        if (encrypted)
        {
            Assert.True(server.Files[a.SnapshotPath].AsSpan().StartsWith("KKINDLE-SYNC1"u8));
            Assert.True(Assert.Single(server.Files, pair => pair.Key.Contains("/objects/", StringComparison.Ordinal)).Value.AsSpan().StartsWith("KKINDLE-SYNC2"u8));
        }
        else
        {
            var snapshotJson = JsonSerializer.Serialize(Decode(server.Files[a.SnapshotPath]));
            Assert.DoesNotContain(WebDavTestServer.Username, snapshotJson, StringComparison.Ordinal);
            Assert.DoesNotContain("app:password", snapshotJson, StringComparison.Ordinal);
        }
        var objectPuts = server.Requests.Count(request => request.Method == "PUT" && request.Path.Contains("/objects/", StringComparison.Ordinal));
        remoteAnnotation.Note = "来自设备 B";
        remoteAnnotation.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await b.Reader.SaveAnnotationAsync(remoteAnnotation);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal("来自设备 B", Assert.Single(await a.Reader.GetAllAnnotationsAsync()).Note);
        Assert.Equal(objectPuts, server.Requests.Count(request => request.Method == "PUT" && request.Path.Contains("/objects/", StringComparison.Ordinal)));

        await b.DeleteAllBooksAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Empty(await a.Library.SearchAsync());
        Assert.Empty(await a.Reader.GetAllAnnotationsAsync());
        Assert.DoesNotContain(server.Files.Keys, path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-key")]
    public async Task MismatchedEncryptionFailsBeforeSyncOrConnectionProbeWrites(string key)
    {
        var server = new WebDavTestServer();
        await using var a = await Device.CreateAsync(server, server.Settings() with { EncryptionKey = "original-key" });
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(server, server.Settings() with { EncryptionKey = key });
        var writes = server.Requests.Count(request => request.Method == "PUT");
        await Assert.ThrowsAsync<InvalidDataException>(() => b.SyncAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => b.Service.TestConnectionAsync(b.Settings));
        Assert.Equal(writes, server.Requests.Count(request => request.Method == "PUT"));
    }

    [Fact]
    public async Task EncryptionChangeRequiresNewDirectoryAndPreservesSavedKeyOnFailure()
    {
        var server = new WebDavTestServer();
        await using var a = await Device.CreateAsync(server, server.Settings() with { EncryptionKey = "original-key" });
        await a.SyncAsync();
        var changed = a.Settings with { EncryptionKey = "new-key" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Service.SaveSettingsAsync(a.Id, changed));
        Assert.Equal("original-key", (await a.Service.LoadSettingsAsync()).Settings.EncryptionKey);
        a.Settings = changed with { Prefix = "new-encrypted-directory" };
        await a.Service.SaveSettingsAsync(a.Id, a.Settings);
        Assert.False((await a.SyncAsync()).IsPartial);
    }

    [Fact]
    public async Task DeniedBlobListingFallsBackToHeadAndUploadsMissingBooks()
    {
        var server = new WebDavTestServer();
        server.BeforeRequest = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.Method == "PROPFIND" && request.Depth == "1" && request.Path.Contains("/objects/", StringComparison.Ordinal)
                ? new(HttpStatusCode.Forbidden) : null);
        await using var a = await Device.CreateAsync(server);
        await a.AddBookAsync();
        Assert.False((await a.SyncAsync()).IsPartial);
        Assert.Contains(server.Requests, request => request.Method == "HEAD");
    }

    [Fact]
    public async Task MissingBookIsPartialAndCanBeRecoveredOnRetry()
    {
        var server = new WebDavTestServer();
        await using var a = await Device.CreateAsync(server);
        await a.AddBookAsync();
        await a.SyncAsync();
        var file = Assert.Single(server.Files, pair => pair.Key.Contains("/objects/", StringComparison.Ordinal));
        server.Files.TryRemove(file.Key, out _);
        await using var b = await Device.CreateAsync(server);
        Assert.True((await b.SyncAsync()).IsPartial);
        server.Files[file.Key] = file.Value;
        var retried = await b.SyncAsync();
        Assert.False(retried.IsPartial);
        Assert.Equal(1, retried.FilesDownloaded);
    }

    [Fact]
    public async Task ForbiddenBookReadFailsWithoutPublishingAnIncompleteSnapshot()
    {
        var server = new WebDavTestServer();
        await using var a = await Device.CreateAsync(server);
        await a.AddBookAsync();
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(server);
        server.BeforeRequest = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.Method == "GET" && request.Path.Contains("/objects/", StringComparison.Ordinal)
                ? new(HttpStatusCode.Forbidden) : null);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => b.SyncAsync());
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.False(server.Files.ContainsKey(b.SnapshotPath));
    }

    [Fact]
    public async Task SwitchingFromS3DoesNotReuseItsDeletionBaseline()
    {
        var server = new WebDavTestServer();
        var legacy = server.Settings() with
        {
            Provider = SyncProvider.S3, Endpoint = "http://localhost", Bucket = "s3-books",
            AccessKey = "old-access", SecretKey = "old-secret", Prefix = "sync"
        };
        await using var a = await Device.CreateAsync(server, legacy);
        var previous = new S3SyncSnapshot
        {
            DeviceId = a.Id,
            Books = Enumerable.Range(0, 12).Select(index => new S3SyncBook
            {
                Id = Guid.NewGuid(), Title = "Old S3 book " + index, UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            }).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(a.Paths.Data, "s3-sync-state.json"), JsonSerializer.Serialize(new S3SyncState
        {
            DeviceId = a.Id, StorageIdentity = "http://localhost|s3-books|us-east-1|sync|plain",
            LastUploadedSnapshot = previous,
            Tombstones = [new S3SyncTombstone { EntityType = "book", Key = Guid.NewGuid().ToString("N"), DeletedAt = DateTimeOffset.UtcNow }]
        }));
        a.Settings = a.Settings with { Provider = SyncProvider.WebDav };
        await a.Service.SaveSettingsAsync(a.Id, a.Settings);
        Assert.False((await a.SyncAsync()).IsPartial);
        Assert.Empty(Decode(server.Files[a.SnapshotPath]).Tombstones);
        Assert.Equal("old-secret", (await a.Service.LoadSettingsAsync()).Settings.SecretKey);
        var state = await File.ReadAllTextAsync(Path.Combine(a.Paths.Data, "s3-sync-state.json"));
        Assert.DoesNotContain(WebDavTestServer.Username, state, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeDeletionsStillRequireExplicitConfirmation()
    {
        var server = new WebDavTestServer();
        await using var a = await Device.CreateAsync(server);
        for (var index = 0; index < 12; index++) await a.AddBookAsync();
        await a.SyncAsync();
        await a.DeleteAllBooksAsync();
        var writes = server.Requests.Count(request => request.Method == "PUT");
        var confirmation = await Assert.ThrowsAsync<S3SyncDeletionConfirmationRequiredException>(() => a.SyncAsync());
        Assert.Equal(writes, server.Requests.Count(request => request.Method == "PUT"));
        await a.Service.SyncAsync(a.Id, a.Settings, options: new S3SyncOptions { ConfirmedDeletionFingerprint = confirmation.DeletionFingerprint });
        var snapshot = Decode(server.Files[a.SnapshotPath]);
        Assert.Empty(snapshot.Books);
        Assert.Equal(12, snapshot.Tombstones.Count(row => row.EntityType == "book"));
    }

    private static S3SyncSnapshot Decode(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var gzip = new GZipStream(memory, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<S3SyncSnapshot>(gzip)!;
    }

    private sealed class Device : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required AppPaths Paths { get; init; }
        public required SqliteBookLibraryService Library { get; init; }
        public required ReaderDataService Reader { get; init; }
        public required S3SyncService Service { get; init; }
        public required S3SyncSettings Settings { get; set; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string SnapshotPath => WebDavTestServer.RootPath + $"/{Settings.Prefix}/devices/{Id}/snapshot.bin";
        public Task<S3SyncResult> SyncAsync() => Service.SyncAsync(Id, Settings);

        public static async Task<Device> CreateAsync(WebDavTestServer server, S3SyncSettings? settings = null, bool realHttp = false)
        {
            var root = TestHelpers.CreateTempDirectory();
            var paths = new AppPaths(root);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            var reader = new ReaderDataService(paths);
            await library.InitializeAsync();
            await reader.InitializeAsync();
            var protector = new TestHelpers.PlaintextSecretProtector();
            var service = realHttp ? new S3SyncService(paths, protector)
                : new S3SyncService(paths, protector, current => new WebDavSyncObjectStore(current, server.Handler()));
            var device = new Device { Root = root, Paths = paths, Library = library, Reader = reader, Service = service, Settings = settings ?? server.Settings() };
            await service.SaveSettingsAsync(device.Id, device.Settings);
            await service.InitializeDeletionTrackingAsync(deviceId: device.Id);
            return device;
        }

        public async Task<(Guid BookId, Guid FileId, byte[] Bytes)> AddBookAsync(bool large = false)
        {
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var bytes = large ? RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 123) : Encoding.UTF8.GetBytes("book " + bookId);
            var relative = Path.Combine("library", bookId.ToString("N"), "书籍.epub");
            var path = Path.Combine(Paths.Data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            await using var connection = new SqliteConnection($"Data Source={Paths.Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                VALUES ($book, $title, 'Author', '', '', 0, 0, $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                VALUES ($file, $book, 'epub', $path, $size, $hash);
                """;
            command.Parameters.AddWithValue("$book", bookId.ToString());
            command.Parameters.AddWithValue("$title", "Book " + bookId);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$file", fileId.ToString());
            command.Parameters.AddWithValue("$path", relative);
            command.Parameters.AddWithValue("$size", bytes.Length);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            await command.ExecuteNonQueryAsync();
            return (bookId, fileId, bytes);
        }

        public async Task DeleteAllBooksAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM BookFiles; DELETE FROM Books;";
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            var root = Path.GetFullPath(Root);
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KkindleTests"));
            if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Test cleanup path is outside the temporary test directory.");
            TestHelpers.TryDelete(root);
            return ValueTask.CompletedTask;
        }
    }
}
