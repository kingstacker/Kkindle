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
}
