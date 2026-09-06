using System.Security.Cryptography;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace Kkindle.Tests;

public sealed class S3SyncTests
{
    [Fact]
    public void DefaultsToVirtualHostedStyleAddressing()
    {
        Assert.False(new S3SyncSettings().PathStyle);
    }

    [Fact]
    public void Normalize_ClampsValuesAndSanitizesPrefix()
    {
        var normalized = S3SyncSettings.Normalize(new S3SyncSettings
        {
            Endpoint = " https://minio.example.test/// ",
            IntervalMinutes = 1,
            TimeoutSeconds = 9999,
            ConcurrentRequests = 999,
            Prefix = "/team\\reader/./books/../sync/"
        });

        Assert.Equal("https://minio.example.test", normalized.Endpoint);
        Assert.Equal("team/reader/books/sync", normalized.Prefix);
        Assert.Equal(5, normalized.IntervalMinutes);
        Assert.Equal(600, normalized.TimeoutSeconds);
        Assert.Equal(32, normalized.ConcurrentRequests);
    }

    [Fact]
    public void Validate_RequiresCredentialsAndAcceptsAwsDefaultEndpoint()
    {
        var incomplete = new S3SyncSettings();
        Assert.Contains("Access Key", incomplete.Validate());

        var configured = new S3SyncSettings
        {
            AccessKey = "access",
            SecretKey = "secret",
            Bucket = "books",
            Endpoint = "https://s3.amazonaws.com"
        };

        Assert.Null(configured.Validate());
        Assert.Equal(string.Empty, S3SyncSettings.Normalize(configured).Endpoint);
    }

    [Fact]
    public async Task SettingsStore_PreservesDeviceAndProtectedValues()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var store = new S3SyncSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            var deviceId = Guid.NewGuid().ToString("N");
            var settings = new S3SyncSettings
            {
                Enabled = true,
                AutomaticSyncEnabled = false,
                Endpoint = "https://s3.example.test",
                AccessKey = "access-value",
                SecretKey = "secret-value",
                Bucket = "books",
                Region = "eu-west-1",
                Prefix = "kkindle/books",
                EncryptionKey = "local-encryption-passphrase"
            };

            await store.SaveAsync(deviceId, settings);
            var loaded = await store.LoadAsync();

            Assert.Equal(deviceId, loaded.DeviceId);
            Assert.Equal(settings with { IntervalMinutes = 30, TimeoutSeconds = 60, ConcurrentRequests = 4 }, loaded.Settings);
            var persisted = await File.ReadAllTextAsync(store.SettingsPath);
            Assert.DoesNotContain(settings.AccessKey, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(settings.SecretKey, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(settings.EncryptionKey, persisted, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task ConnectionProfile_RoundTripsConnectionFieldsAndPreservesLocalOnlySettings()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var profilePath = Path.Combine(root, "connection" + S3ConnectionProfileService.FileExtension);
            var source = new S3SyncSettings
            {
                Enabled = true,
                AutomaticSyncEnabled = false,
                IntervalMinutes = 45,
                Endpoint = "https://s3.example.test",
                AccessKey = "profile-access",
                SecretKey = "profile-secret",
                Bucket = "books",
                Region = "eu-west-1",
                Prefix = "reader/sync",
                PathStyle = true,
                SkipTlsVerify = true,
                TimeoutSeconds = 120,
                ConcurrentRequests = 8,
                EncryptionKey = "keep-local-only"
            };

            await S3ConnectionProfileService.ExportAsync(profilePath, source);
            var imported = await S3ConnectionProfileService.ImportAsync(
                profilePath,
                new S3SyncSettings
                {
                    Enabled = false,
                    AutomaticSyncEnabled = true,
                    IntervalMinutes = 15,
                    EncryptionKey = "target-local-only"
                });

            Assert.Equal(source.Endpoint, imported.Endpoint);
            Assert.Equal(source.AccessKey, imported.AccessKey);
            Assert.Equal(source.SecretKey, imported.SecretKey);
            Assert.Equal(source.Bucket, imported.Bucket);
            Assert.Equal(source.Region, imported.Region);
            Assert.Equal(source.Prefix, imported.Prefix);
            Assert.Equal(source.PathStyle, imported.PathStyle);
            Assert.Equal(source.SkipTlsVerify, imported.SkipTlsVerify);
            Assert.Equal(source.TimeoutSeconds, imported.TimeoutSeconds);
            Assert.Equal(source.ConcurrentRequests, imported.ConcurrentRequests);
            Assert.False(imported.Enabled);
            Assert.True(imported.AutomaticSyncEnabled);
            Assert.Equal(15, imported.IntervalMinutes);
            Assert.Equal("target-local-only", imported.EncryptionKey);

            var json = await File.ReadAllTextAsync(profilePath);
            Assert.Contains("profile-access", json, StringComparison.Ordinal);
            Assert.Contains("profile-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("keep-local-only", json, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task ResetLocalBaseline_DoesNotKeepPreviousSnapshot()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector());
            var deviceId = Guid.NewGuid().ToString("N");

            await service.ResetLocalBaselineAsync(deviceId);

            var statePath = Path.Combine(paths.Data, "s3-sync-state.json");
            var state = await File.ReadAllTextAsync(statePath);
            Assert.Contains(deviceId, state, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LastUploadedSnapshot", state, StringComparison.Ordinal);
            Assert.Contains("Tombstones", state, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task Consolidation_KeepsFileBearingBookAndMergesDuplicateMetadata()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await new ReaderDataService(paths).InitializeAsync();

            var canonicalId = Guid.NewGuid();
            var duplicateId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            var duplicateUpdatedAt = DateTimeOffset.UtcNow;
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Books (
                        Id, Title, Authors, Series, SeriesIndex, Description, Publisher,
                        PublishDate, Isbn, PageCount, Binding, DoubanRating,
                        DoubanRatingCount, Tags, Category, IsFavorite, ReadingStatus,
                        CoverPath, CreatedAt, UpdatedAt)
                    VALUES
                        ($canonical, '同一本书', '同一作者', NULL, NULL, NULL, NULL,
                         NULL, NULL, NULL, NULL, NULL, NULL, '', '', 0, 0,
                         'covers/canonical.jpg', $created, $created),
                        ($duplicate, '同一本书', '同一作者', NULL, NULL, '重复记录的简介', NULL,
                         NULL, NULL, NULL, NULL, NULL, NULL, '', '', 1, 0,
                         'covers/duplicate.jpg', $created, $updated);
                    INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($file, $canonical, 'azw3', 'library/canonical/book.azw3', 4,
                            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
                    """;
                command.Parameters.AddWithValue("$canonical", canonicalId.ToString());
                command.Parameters.AddWithValue("$duplicate", duplicateId.ToString());
                command.Parameters.AddWithValue("$file", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
                command.Parameters.AddWithValue("$updated", duplicateUpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector());
            var method = typeof(S3SyncService).GetMethod(
                "ConsolidateLocalDuplicateBooksAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var pathsToDelete = new List<string>();
            var mergeTask = (Task<int>)method!.Invoke(
                service,
                [pathsToDelete, CancellationToken.None])!;

            Assert.Equal(1, await mergeTask);
            await using var verify = new SqliteConnection($"Data Source={paths.Database}");
            await verify.OpenAsync();
            var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Books WHERE Title = '同一本书';";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

            var metadata = verify.CreateCommand();
            metadata.CommandText = "SELECT Id, Description, IsFavorite FROM Books WHERE Title = '同一本书';";
            await using var reader = await metadata.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(canonicalId.ToString(), reader.GetString(0));
            Assert.Equal("重复记录的简介", reader.GetString(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Single(pathsToDelete, path => path.EndsWith("duplicate.jpg", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void SnapshotLocalPaths_AreNotSerialized()
    {
        var snapshot = new S3SyncSnapshot();
        var fileId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        snapshot.LocalFilePaths[fileId] = "library/book/book.epub";
        snapshot.LocalCoverPaths[bookId] = "covers/book.jpg";
        snapshot.Books.Add(new S3SyncBook { Id = bookId, LocalCoverPath = "covers/book.jpg" });

        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("LocalFilePaths", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalCoverPaths", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalCoverPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("library/book/book.epub", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteParameterReuse_UpdatesExistingNamedParameter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT $value;";
        command.Parameters.AddWithValue("$value", 1);
        Assert.Equal(0, command.Parameters.IndexOf("$value"));
        command.Parameters[command.Parameters.IndexOf("$value")].Value = 2;
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void MergeTombstones_RetainsOldDeletionsAndKeepsNewestVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var dormantId = Guid.NewGuid();
        var method = typeof(S3SyncService).GetMethod(
            "MergeTombstones",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (List<S3SyncTombstone>)method!.Invoke(
            null,
            [
                new List<S3SyncTombstone>
                {
                    new() { EntityType = "Book", Key = id.ToString(), DeletedAt = now.AddDays(-91) },
                    new() { EntityType = "book", Key = id.ToString("N"), DeletedAt = now.AddDays(-2) },
                    new() { EntityType = "book", Key = dormantId.ToString("N"), DeletedAt = now.AddDays(-91) }
                },
                Array.Empty<S3SyncTombstone>(),
                now
            ])!;

        Assert.Equal(2, result.Count);
        Assert.Contains(result, row => row.Key == dormantId.ToString("N") && row.DeletedAt == now.AddDays(-91));
        var tombstone = Assert.Single(result, row => row.Key == id.ToString("N"));
        Assert.Equal("book", tombstone.EntityType);
        Assert.Equal(id.ToString("N"), tombstone.Key);
        Assert.Equal(now.AddDays(-2), tombstone.DeletedAt);
    }

    [Fact]
    public void DetectDeletedEntities_UsesPreviousEntityVersion()
    {
        var id = Guid.NewGuid();
        var deletedVersion = DateTimeOffset.UtcNow.AddDays(-3);
        var previous = new S3SyncSnapshot
        {
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            Books =
            [
                new S3SyncBook { Id = id, UpdatedAt = deletedVersion }
            ]
        };
        var current = new S3SyncSnapshot();
        var method = typeof(S3SyncService).GetMethod(
            "DetectDeletedEntities",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (List<S3SyncTombstone>)method!.Invoke(null, [previous, current])!;

        var tombstone = Assert.Single(result);
        Assert.Equal("book", tombstone.EntityType);
        Assert.Equal(id.ToString("N"), tombstone.Key);
        Assert.Equal(deletedVersion, tombstone.DeletedAt);
    }

    [Fact]
    public async Task EncryptedBlob_RoundTripsStreamingAndLegacyFormats()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector());
            var sourcePath = Path.Combine(root, "source.bin");
            var encryptedPath = Path.Combine(root, "encrypted.bin");
            var decryptedPath = Path.Combine(root, "decrypted.bin");
            var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 137);
            await File.WriteAllBytesAsync(sourcePath, bytes);

            var encrypt = typeof(S3SyncService).GetMethod(
                "EncryptFileToPathAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(encrypt);
            await (Task)encrypt!.Invoke(
                service,
                [sourcePath, encryptedPath, "test-passphrase", CancellationToken.None])!;

            var decrypt = typeof(S3SyncService).GetMethod(
                "DecryptBlobToPathAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(decrypt);
            await using (var encrypted = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await (Task)decrypt!.Invoke(
                    service,
                    [encrypted, decryptedPath, "test-passphrase", CancellationToken.None])!;
            }
            Assert.Equal(bytes, await File.ReadAllBytesAsync(decryptedPath));

            var protect = typeof(S3SyncService)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(item => item.Name == "ProtectPayload" && item.GetParameters().Length == 2);
            var legacy = (byte[])protect.Invoke(null, [bytes, "test-passphrase"])!;
            var legacyPath = Path.Combine(root, "legacy.bin");
            await File.WriteAllBytesAsync(legacyPath, legacy);
            var legacyDecryptedPath = Path.Combine(root, "legacy-decrypted.bin");
            await using (var legacyStream = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await (Task)decrypt!.Invoke(
                    service,
                    [legacyStream, legacyDecryptedPath, "test-passphrase", CancellationToken.None])!;
            }
            Assert.Equal(bytes, await File.ReadAllBytesAsync(legacyDecryptedPath));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task TombstonedRemoteBook_IsFilteredBeforeDownloadAndCleanup()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await new ReaderDataService(paths).InitializeAsync();

            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var relativePath = Path.Combine("library", bookId.ToString("N"), "book.epub");
            var absolutePath = Path.Combine(paths.Data, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, [1, 2, 3]);

            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus,
                                       CreatedAt, UpdatedAt)
                    VALUES ($bookId, '待删除', '作者', '', '', 0, 0, $updatedAt, $updatedAt);
                    INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($fileId, $bookId, 'epub', $relativePath, 3,
                            '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81');
                    """;
                command.Parameters.AddWithValue("$bookId", bookId.ToString());
                command.Parameters.AddWithValue("$fileId", fileId.ToString());
                command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
                command.Parameters.AddWithValue("$relativePath", relativePath);
                await command.ExecuteNonQueryAsync();
            }

            var local = new S3SyncSnapshot
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                Tombstones =
                [
                    new S3SyncTombstone
                    {
                        EntityType = "book",
                        Key = bookId.ToString("N"),
                        DeletedAt = updatedAt
                    }
                ],
                Books =
                [
                    new S3SyncBook
                    {
                        Id = bookId,
                        Title = "待删除",
                        Authors = "作者",
                        CreatedAt = updatedAt,
                        UpdatedAt = updatedAt
                    }
                ],
                Files =
                [
                    new S3SyncBookFile
                    {
                        Id = fileId,
                        BookId = bookId,
                        FileName = "book.epub",
                        Format = "epub",
                        Size = 3,
                        Sha256 = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
                        ModifiedAt = updatedAt
                    }
                ]
            };
            var remote = new S3SyncSnapshot
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                Books = local.Books.ToList(),
                Files = local.Files.ToList()
            };

            var service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector());
            var method = typeof(S3SyncService).GetMethod(
                "ApplyRemoteSnapshotsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var settings = new S3SyncSettings();
            var mergeTask = (Task)method!.Invoke(
                service,
                [null!, settings, local, new[] { remote }, null, CancellationToken.None])!;
            await mergeTask;

            await using var verify = new SqliteConnection($"Data Source={paths.Database}");
            await verify.OpenAsync();
            using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Books;";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            Assert.False(File.Exists(absolutePath));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task DeletionTracking_RecordsActualDeleteTime()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await new ReaderDataService(paths).InitializeAsync();

            var service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector());
            await service.InitializeDeletionTrackingAsync();

            var bookId = Guid.NewGuid();
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus,
                                       CreatedAt, UpdatedAt)
                    VALUES ($id, '删除时间测试', '', '', '', 0, 0, $now, $now);
                    DELETE FROM Books WHERE Id = $id;
                    """;
                insert.Parameters.AddWithValue("$id", bookId.ToString());
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            await using var verify = new SqliteConnection($"Data Source={paths.Database}");
            await verify.OpenAsync();
            using var query = verify.CreateCommand();
            query.CommandText = """
                SELECT EntityType, EntityKey, DeletedAt
                FROM S3SyncDeletionLog
                WHERE EntityType = 'book' AND EntityKey = $id;
                """;
            query.Parameters.AddWithValue("$id", bookId.ToString());
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("book", reader.GetString(0));
            Assert.Equal(bookId.ToString(), reader.GetString(1));
            Assert.True(DateTimeOffset.TryParse(reader.GetString(2), out var deletedAt));
            Assert.True(deletedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }
}
