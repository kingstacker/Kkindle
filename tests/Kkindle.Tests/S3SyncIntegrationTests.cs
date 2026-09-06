using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Tests;

public sealed class S3SyncIntegrationTests
{
    [Fact]
    public async Task MissingEncryptionKey_IsRejectedBeforeAnyUpload()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, "test-encryption-key");
        await a.AddBookAsync();
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.AddBookAsync();
        var writes = bucket.WriteCount;

        await Assert.ThrowsAsync<InvalidDataException>(() => b.SyncAsync());

        Assert.Equal(writes, bucket.WriteCount);
        Assert.DoesNotContain(bucket.Objects.Keys, key => key.Contains("/objects/plain/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectionTest_ChecksEncryptionWithoutWriting()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, "encrypted");
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        var writes = bucket.WriteCount;
        await Assert.ThrowsAsync<InvalidDataException>(() => b.Service.TestConnectionAsync(b.Settings));
        Assert.Equal(writes, bucket.WriteCount);
    }

    [Fact]
    public async Task KeyChange_PreservesWorkingSettingsAndSupportsANewPrefix()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, "original");
        await a.AddBookAsync();
        await a.SyncAsync();
        var changed = a.Settings with { EncryptionKey = "replacement" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Service.SaveSettingsAsync(a.Id, changed));
        Assert.Equal("original", (await a.Service.LoadSettingsAsync()).Settings.EncryptionKey);

        a.Settings = changed with { Prefix = "replacement-library" };
        await a.Service.SaveSettingsAsync(a.Id, a.Settings);
        Assert.False((await a.SyncAsync()).IsPartial);
        await using var b = await Device.CreateAsync(bucket, "replacement");
        b.Settings = b.Settings with { Prefix = "replacement-library" };
        await b.SyncAsync();
        Assert.Equal(1, await b.BookCountAsync());
    }

    [Fact]
    public async Task DeleteWhileReadingRemoteSnapshot_IsNotResurrected()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        var deleted = false;
        bucket.BeforeGet = async (key, _) =>
        {
            if (key == b.SnapshotKey && !deleted)
            {
                deleted = true;
                await a.DeleteBookAsync(book.BookId);
            }
        };

        await a.SyncAsync();
        bucket.BeforeGet = null;
        Assert.Equal(0, await a.BookCountAsync());
        Assert.Contains(bucket.Snapshot(a.SnapshotKey).Tombstones, row => row.EntityType == "book" && row.Key == book.BookId.ToString("N"));
        await b.SyncAsync();
        Assert.Equal(0, await b.BookCountAsync());
        Assert.Equal(0, await b.ScalarAsync("SELECT COUNT(*) FROM S3SyncDeletionLog;"));
    }

    [Fact]
    public async Task DeleteBeforeFinalCapture_IsPublishedInTheSameSync()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        var progress = new InlineProgress(message =>
        {
            if (message == UiText.Get("正在合并同步设置…"))
                a.DeleteBookAsync(book.BookId).GetAwaiter().GetResult();
        });

        await a.Service.SyncAsync(a.Id, a.Settings, progress);

        var snapshot = bucket.Snapshot(a.SnapshotKey);
        Assert.Empty(snapshot.Books);
        Assert.Contains(snapshot.Tombstones, row => row.EntityType == "book" && row.Key == book.BookId.ToString("N"));
        await a.SyncAsync();
        Assert.Empty(bucket.Snapshot(a.SnapshotKey).Books);
    }

    [Fact]
    public async Task DeleteAfterFinalCapture_RemainsPendingForNextSync()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        bucket.BeforePut = async (key, _) =>
        {
            if (key == a.SnapshotKey) await a.DeleteBookAsync(book.BookId);
        };
        await a.SyncAsync();
        bucket.BeforePut = null;

        await a.SyncAsync();

        var snapshot = bucket.Snapshot(a.SnapshotKey);
        Assert.Empty(snapshot.Books);
        Assert.Contains(snapshot.Tombstones, row => row.Key == book.BookId.ToString("N"));
    }

    [Fact]
    public async Task BookAddedDuringNetworkWork_HasItsObjectUploadedBeforeSnapshot()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        var added = false;
        bucket.BeforeGet = async (key, _) =>
        {
            if (key == b.SnapshotKey && !added)
            {
                added = true;
                await a.AddBookAsync();
            }
        };
        bucket.BeforePut = (key, bytes) =>
        {
            if (key == a.SnapshotKey)
                foreach (var file in MemoryBucket.Decode(bytes).Files)
                    Assert.True(bucket.Objects.ContainsKey($"{a.Settings.Prefix}/objects/plain/{file.Sha256}"));
            return Task.CompletedTask;
        };

        await a.SyncAsync();

        Assert.Single(bucket.Snapshot(a.SnapshotKey).Files);
    }

    [Fact]
    public async Task OldTombstones_BlockDormantDeviceSnapshotsAfterNinetyDays()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync(DateTimeOffset.UtcNow.AddDays(-100));
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.DeleteBookAsync(book.BookId);
        await a.SyncAsync();
        var oldTime = DateTimeOffset.UtcNow.AddDays(-91);
        var statePath = Path.Combine(a.Paths.Data, "s3-sync-state.json");
        var state = JsonSerializer.Deserialize<S3SyncState>(await File.ReadAllTextAsync(statePath))!;
        foreach (var row in state.Tombstones) row.DeletedAt = oldTime;
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state));
        await a.SqlAsync("UPDATE S3SyncDeletionLog SET DeletedAt = $time;", ("$time", oldTime.ToString("O")));

        await a.SyncAsync();

        Assert.Equal(0, await a.BookCountAsync());
        Assert.Contains(bucket.Snapshot(a.SnapshotKey).Tombstones, row => row.Key == book.BookId.ToString("N"));
    }

    [Fact]
    public async Task LargeLocalDeletion_RequiresScopedConfirmationButRemoteDeletionDoesNot()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var books = new List<Guid>();
        for (var i = 0; i < 10; i++) books.Add((await a.AddBookAsync()).BookId);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        foreach (var id in books.Take(6)) await a.DeleteBookAsync(id);
        var writes = bucket.WriteCount;

        var confirmation = await Assert.ThrowsAsync<S3SyncDeletionConfirmationRequiredException>(() => a.SyncAsync());
        Assert.Equal(writes, bucket.WriteCount);
        await Assert.ThrowsAsync<S3SyncDeletionConfirmationRequiredException>(() => a.Service.SyncAsync(a.Id, a.Settings,
            options: new S3SyncOptions { ConfirmedDeletionFingerprint = "different-deletions" }));
        await a.Service.SyncAsync(a.Id, a.Settings,
            options: new S3SyncOptions { ConfirmedDeletionFingerprint = confirmation.DeletionFingerprint });
        await b.SyncAsync();

        Assert.Equal(4, await b.BookCountAsync());
        Assert.Equal(0, await b.ScalarAsync("SELECT COUNT(*) FROM S3SyncDeletionLog;"));
    }

    [Fact]
    public async Task OfflineReadingTime_IsAdditiveAndRepeatedSyncIsIdempotent()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 100, 10, 1, 10);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 600, 60, 6, 10);
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 300, 30, 3, 10);

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.Equal(1000, (await a.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(1000, (await b.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
    }

    [Fact]
    public async Task SettingsChangedDuringDownload_AreNotOverwritten()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await a.SaveAppSettingsAsync(10, DateTime.UtcNow.AddMinutes(-10));
        await b.SaveAppSettingsAsync(20, DateTime.UtcNow.AddMinutes(-5));
        await b.SyncAsync();
        bucket.BeforeGet = async (key, _) =>
        {
            if (key == b.SnapshotKey) await a.SaveAppSettingsAsync(30);
        };

        await a.SyncAsync();

        Assert.Equal(30, (await new AppSettingsStore(a.Paths).LoadAsync()).AutoBackupRetention);
    }

    [Fact]
    public async Task AppliedSettings_KeepTheirVersionAndDoNotPingPong()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await a.SaveAppSettingsAsync(20, DateTime.UtcNow.AddMinutes(-5));
        await a.SyncAsync();
        var version = bucket.Snapshot(a.SnapshotKey).Settings!.UpdatedAt;

        Assert.Equal(1, (await b.SyncAsync()).SettingsApplied);
        Assert.Equal(version, bucket.Snapshot(b.SnapshotKey).Settings!.UpdatedAt);
        Assert.Equal(0, (await a.SyncAsync()).SettingsApplied);
        Assert.Equal(0, (await b.SyncAsync()).SettingsApplied);
    }

    [Fact]
    public async Task MissingBlob_IsReportedAsPartialAndCanBeRetried()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        bucket.Objects.TryRemove($"{a.Settings.Prefix}/objects/plain/{book.Hash}", out _);
        await using var b = await Device.CreateAsync(bucket);

        var partial = await b.SyncAsync();
        Assert.True(partial.IsPartial);
        Assert.NotNull(partial.Warning);
        Assert.Equal(0, partial.FilesDownloaded);

        await a.SyncAsync();
        Assert.False((await b.SyncAsync()).IsPartial);
        Assert.Single(bucket.Snapshot(b.SnapshotKey).Files);
    }

    [Fact]
    public async Task Cancellation_StopsNetworkWorkAndReleasesSyncGate()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        bucket.BeforeGet = (_, token) => Task.Delay(Timeout.Infinite, token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            a.Service.SyncAsync(a.Id, a.Settings, cancellationToken: cancellation.Token));
        bucket.BeforeGet = null;

        Assert.False((await a.SyncAsync()).IsPartial);
    }

    [Fact]
    public async Task CoverChangedDuringDownload_KeepsTheNewLocalCover()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SetCoverAsync(book.BookId, "original-cover", DateTimeOffset.UtcNow.AddMinutes(-15));
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        var remoteHash = await a.SetCoverAsync(book.BookId, "remote-cover", DateTimeOffset.UtcNow.AddMinutes(-5));
        await a.SyncAsync();
        string? latestHash = null;
        bucket.BeforeGet = async (key, _) =>
        {
            if (key.EndsWith("/" + remoteHash, StringComparison.Ordinal))
                latestHash = await b.SetCoverAsync(book.BookId, "my-new-cover", DateTimeOffset.UtcNow);
        };

        await b.SyncAsync();

        Assert.NotNull(latestHash);
        Assert.Equal("my-new-cover", await File.ReadAllTextAsync(Path.Combine(b.Paths.Covers, book.BookId.ToString("N") + ".jpg")));
        Assert.Equal(latestHash, Assert.Single(bucket.Snapshot(b.SnapshotKey).Books).CoverHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("encrypted")]
    public async Task CorruptLocalFile_IsPartialAndDoesNotPoisonTheSharedObject(string encryptionKey)
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, encryptionKey);
        var book = await a.AddBookAsync();
        var path = Path.Combine(a.Paths.Library, book.BookId.ToString("N"), "book.epub");
        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(path, "changed after import");

        var result = await a.SyncAsync();

        Assert.True(result.IsPartial);
        Assert.DoesNotContain(bucket.Objects.Keys, key => key.Contains("/objects/", StringComparison.Ordinal));
        await File.WriteAllBytesAsync(path, original);
        Assert.False((await a.SyncAsync()).IsPartial);
        await using var b = await Device.CreateAsync(bucket, encryptionKey);
        Assert.False((await b.SyncAsync()).IsPartial);
        Assert.Equal(1, await b.BookCountAsync());
    }

    [Fact]
    public async Task CorruptRemoteFile_IsReportedAsPartial()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        bucket.Objects[$"{a.Settings.Prefix}/objects/plain/{book.Hash}"] = Encoding.UTF8.GetBytes("corrupt remote bytes");
        await using var b = await Device.CreateAsync(bucket);

        var result = await b.SyncAsync();

        Assert.True(result.IsPartial);
        Assert.NotNull(result.Warning);
        Assert.Empty(bucket.Snapshot(b.SnapshotKey).Files);
    }

    [Fact]
    public async Task LegacyReadingTime_IsMigratedOnceWithoutDoublingTheSharedHistory()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 100, 10, 1, 10);
        await a.SyncAsync();
        var legacySnapshot = bucket.Snapshot(a.SnapshotKey);
        legacySnapshot.Version = 1;
        foreach (var stats in legacySnapshot.ReadingStats) stats.SecondsByDevice.Clear();
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(legacySnapshot);
        await a.SqlAsync("DELETE FROM S3SyncReadingTimeCounters; DELETE FROM S3SyncLocalMetadata WHERE Key = 'reading-time-v1';");
        await a.Service.InitializeDeletionTrackingAsync(deviceId: a.Id);
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        var staleStats = (await a.Reader.GetReadingStatsAsync(book.FileId))!;
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 600, 60, 6, 10);
        await a.Reader.SaveReadingStatsAsync(staleStats);
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 300, 30, 3, 10);

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.Equal(1000, (await a.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(1000, (await b.Reader.GetReadingStatsAsync(book.FileId))!.CumulativeSeconds);
        Assert.Equal(100, Assert.Single(bucket.Snapshot(a.SnapshotKey).ReadingStats).SecondsByDevice["legacy"]);
    }

    [Fact]
    public async Task IndependentSettingsChanges_AreMergedPerSettingsFile()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var protector = new TestHelpers.PlaintextSecretProtector();
        await new AiSettingsStore(b.Paths, protector).SaveAsync(new AiConnectionSettings { Model = "remote-model" });
        File.SetLastWriteTimeUtc(Path.Combine(b.Paths.Data, "ai-settings.json"), DateTime.UtcNow.AddMinutes(-5));
        await b.SyncAsync();
        await a.SaveAppSettingsAsync(30);

        await a.SyncAsync();
        await b.SyncAsync();

        Assert.Equal("remote-model", (await new AiSettingsStore(a.Paths, protector).LoadAsync()).Model);
        Assert.Equal(30, (await new AppSettingsStore(b.Paths).LoadAsync()).AutoBackupRetention);
        Assert.Equal(0, (await a.SyncAsync()).SettingsApplied);
    }

    [Fact]
    public async Task SettingsApplyInterruptedAfterOneFile_ResumesTheRemainingFiles()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await a.SaveAppSettingsAsync(30);
        var protector = new TestHelpers.PlaintextSecretProtector();
        await new AiSettingsStore(a.Paths, protector).SaveAsync(new AiConnectionSettings { Model = "remote-model" });
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        var blockedPath = Path.Combine(b.Paths.Data, "ai-settings.json");
        Directory.CreateDirectory(blockedPath);

        var failure = await Record.ExceptionAsync(() => b.SyncAsync());
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(30, (await new AppSettingsStore(b.Paths).LoadAsync()).AutoBackupRetention);
        Directory.Delete(blockedPath);
        await b.SyncAsync();

        Assert.Equal("remote-model", (await new AiSettingsStore(b.Paths, protector).LoadAsync()).Model);
        Assert.Equal(0, (await b.SyncAsync()).SettingsApplied);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class Device : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required AppPaths Paths { get; init; }
        public required S3SyncService Service { get; init; }
        public required ReaderDataService Reader { get; init; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public S3SyncSettings Settings { get; set; } = new()
        {
            Enabled = true, Endpoint = "http://localhost", Bucket = "test-books", Prefix = "sync",
            AccessKey = "test-access", SecretKey = "test-secret", PathStyle = true
        };
        public string SnapshotKey => $"{Settings.Prefix}/devices/{Id}/snapshot.bin";

        public static async Task<Device> CreateAsync(MemoryBucket bucket, string encryptionKey = "")
        {
            var root = TestHelpers.CreateTempDirectory();
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var device = new Device
            {
                Root = root, Paths = paths, Reader = reader,
                Service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector(), _ => new MemoryClient(bucket))
            };
            device.Settings = device.Settings with { EncryptionKey = encryptionKey };
            await device.Service.SaveSettingsAsync(device.Id, device.Settings);
            await device.Service.InitializeDeletionTrackingAsync(deviceId: device.Id);
            return device;
        }

        public Task<S3SyncResult> SyncAsync() => Service.SyncAsync(Id, Settings);
        public Task<long> BookCountAsync() => ScalarAsync("SELECT COUNT(*) FROM Books;");

        public async Task<(Guid BookId, Guid FileId, string Hash)> AddBookAsync(DateTimeOffset? updatedAt = null)
        {
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes("Book content " + bookId);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var relativePath = Path.Combine("library", bookId.ToString("N"), "book.epub");
            var path = Path.Combine(Paths.Data, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            await SqlAsync("""
                INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                VALUES ($book, $title, 'Author', '', '', 0, 0, $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                VALUES ($file, $book, 'epub', $path, $size, $hash);
                """, ("$book", bookId.ToString()), ("$file", fileId.ToString()), ("$title", "Book " + bookId),
                ("$time", (updatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1)).ToString("O")),
                ("$path", relativePath), ("$size", bytes.Length), ("$hash", hash));
            return (bookId, fileId, hash);
        }

        public Task DeleteBookAsync(Guid id) => SqlAsync("""
            DELETE FROM BookFiles WHERE BookId = $id;
            DELETE FROM Books WHERE Id = $id;
            """, ("$id", id.ToString()));

        public async Task<string> SetCoverAsync(Guid bookId, string content, DateTimeOffset updatedAt)
        {
            var relative = Path.Combine("covers", bookId.ToString("N") + ".jpg");
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(Paths.Data, relative), bytes);
            await SqlAsync("UPDATE Books SET CoverPath = $path, UpdatedAt = $time WHERE Id = $book;",
                ("$path", relative), ("$time", updatedAt.ToString("O")), ("$book", bookId.ToString()));
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        public async Task SaveAppSettingsAsync(int retention, DateTime? timestamp = null)
        {
            await new AppSettingsStore(Paths).SaveAsync(new AppSettings { AutoBackupRetention = retention });
            if (timestamp is { } time) File.SetLastWriteTimeUtc(Paths.Settings, time);
        }

        public async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public ValueTask DisposeAsync()
        {
            var root = Path.GetFullPath(Root);
            var testDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KkindleTests"));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(root), testDirectory, comparison)
                || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Refusing to clean up a path outside the temporary test directory.");
            TestHelpers.TryDelete(root);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryBucket
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public int WriteCount;
        public Func<string, CancellationToken, Task>? BeforeGet;
        public Func<string, byte[], Task>? BeforePut;
        public S3SyncSnapshot Snapshot(string key) => Decode(Objects[key]);
        public static byte[] Encode(S3SyncSnapshot snapshot)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
                JsonSerializer.Serialize(gzip, snapshot);
            return output.ToArray();
        }
        public static S3SyncSnapshot Decode(byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<S3SyncSnapshot>(gzip)!;
        }
    }

    private sealed class MemoryClient(MemoryBucket bucket)
        : AmazonS3Client("test-access", "test-secret", new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ListObjectsV2Response
            {
                IsTruncated = false,
                S3Objects = bucket.Objects.Keys.Where(key => key.StartsWith(request.Prefix, StringComparison.Ordinal))
                    .Select(key => new S3Object { Key = key }).ToList()
            });
        }

        public override async Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (bucket.BeforeGet is { } before) await before(request.Key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bucket.Objects.TryGetValue(request.Key, out var bytes))
                throw new AmazonS3Exception("Missing test object") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };
            return new GetObjectResponse { ContentLength = bytes.Length, ResponseStream = new MemoryStream(bytes, false), HttpStatusCode = HttpStatusCode.OK };
        }

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            byte[] bytes;
            if (request.FilePath is not null) bytes = await File.ReadAllBytesAsync(request.FilePath, cancellationToken);
            else
            {
                using var memory = new MemoryStream();
                await request.InputStream.CopyToAsync(memory, cancellationToken);
                bytes = memory.ToArray();
            }
            if (bucket.BeforePut is { } before) await before(request.Key, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            bucket.Objects[request.Key] = bytes;
            Interlocked.Increment(ref bucket.WriteCount);
            return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK, ETag = "test-etag" };
        }
    }
}
