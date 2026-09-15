using System.IO.Compression;
using Kkindle.Infrastructure;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task ExportsAndRestoresLibraryReaderDataAndSafeSettings()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceBook = Path.Combine(root, "source.epub");
            CreateEpub(sourceBook);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var sourcePaths = new AppPaths(Path.Combine(root, "source-app"));
            var sourceLibrary = new SqliteBookLibraryService(sourcePaths, new BookMetadataService());
            var sourceReaderData = new ReaderDataService(sourcePaths);
            await sourceLibrary.InitializeAsync();
            await sourceReaderData.InitializeAsync();
            await sourceLibrary.ImportAsync([sourceBook]);

            var browserProfile = Path.Combine(sourcePaths.BrowserData, "send-to-kindle");
            Directory.CreateDirectory(browserProfile);
            await File.WriteAllTextAsync(Path.Combine(browserProfile, "session"), "test-browser-session");

            await new AiSettingsStore(sourcePaths, protector).SaveAsync(new AiConnectionSettings
            {
                Provider = "openai",
                BaseUrl = "https://api.example.com/v1",
                Model = "example-model",
                ApiKey = "source-api-key"
            });
            await new KindleEmailSettingsStore(sourcePaths, protector).SaveAsync(new KindleEmailSettings
            {
                KindleEmailAddress = "kindle@example.com",
                SenderEmailAddress = "sender@example.com",
                SmtpHost = "smtp.example.com",
                SmtpPort = 465,
                SmtpUsername = "sender@example.com",
                SmtpPassword = "source-smtp-password",
                EnableSsl = true
            });
            var sourceS3Store = new S3SyncSettingsStore(sourcePaths, protector);
            var sourceS3Stored = await sourceS3Store.LoadAsync();
            await sourceS3Store.SaveAsync(sourceS3Stored.DeviceId, new S3SyncSettings
            {
                Enabled = true,
                AutomaticSyncEnabled = false,
                Endpoint = "https://s3.example.com",
                AccessKey = "source-s3-access-key",
                SecretKey = "source-s3-secret-key",
                Bucket = "source-books",
                Region = "eu-west-1",
                Prefix = "source/kkindle",
                EncryptionKey = "source-s3-encryption-key"
            });

            var backupPath = Path.Combine(root, "Kkindle.kkindle");
            var sourceBackup = new AppBackupService(sourcePaths, protector);
            var export = await sourceBackup.ExportAsync(backupPath);

            Assert.Equal(1, export.BookCount);
            Assert.Equal(1, export.FileCount);
            Assert.True(export.ArchiveSize > 0);
            using (var archive = ZipFile.OpenRead(backupPath))
            {
                Assert.DoesNotContain(archive.Entries, entry =>
                    entry.FullName.Contains("browser-data", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.EndsWith("/session", StringComparison.OrdinalIgnoreCase));
                var settingsEntry = archive.GetEntry("settings/settings.json");
                Assert.NotNull(settingsEntry);
                using var reader = new StreamReader(settingsEntry!.Open());
                var settingsJson = await reader.ReadToEndAsync();
                Assert.DoesNotContain("source-api-key", settingsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("source-smtp-password", settingsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("source-s3-access-key", settingsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("source-s3-secret-key", settingsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("source-s3-encryption-key", settingsJson, StringComparison.Ordinal);
                Assert.Contains("example-model", settingsJson, StringComparison.Ordinal);
                Assert.Contains("s3.example.com", settingsJson, StringComparison.Ordinal);
                Assert.Contains("source-books", settingsJson, StringComparison.Ordinal);
            }

            var targetPaths = new AppPaths(Path.Combine(root, "target-app"));
            var targetLibrary = new SqliteBookLibraryService(targetPaths, new BookMetadataService());
            var targetReaderData = new ReaderDataService(targetPaths);
            await targetLibrary.InitializeAsync();
            await targetReaderData.InitializeAsync();
            await new AiSettingsStore(targetPaths, protector).SaveAsync(new AiConnectionSettings { ApiKey = "target-api-key" });
            await new KindleEmailSettingsStore(targetPaths, protector).SaveAsync(new KindleEmailSettings
            {
                SmtpPassword = "target-smtp-password"
            });
            var targetS3Store = new S3SyncSettingsStore(targetPaths, protector);
            var targetS3Stored = await targetS3Store.LoadAsync();
            await targetS3Store.SaveAsync(targetS3Stored.DeviceId, new S3SyncSettings
            {
                AccessKey = "target-s3-access-key",
                SecretKey = "target-s3-secret-key",
                EncryptionKey = "source-s3-encryption-key"
            });

            var imported = await new AppBackupService(targetPaths, protector).ImportAsync(backupPath);
            await targetLibrary.InitializeAsync();
            await targetReaderData.InitializeAsync();

            var book = Assert.Single(await targetLibrary.SearchAsync());
            Assert.Equal("测试书", book.Title);
            Assert.True(File.Exists(targetLibrary.GetAbsoluteFilePath(book.Files[0])));
            Assert.Equal("openai", imported.AiSettings.Provider);
            Assert.Equal("target-api-key", imported.AiSettings.ApiKey);
            Assert.Equal("target-smtp-password", imported.KindleEmailSettings.SmtpPassword);
            Assert.Equal("smtp.example.com", imported.KindleEmailSettings.SmtpHost);
            Assert.NotNull(imported.S3Settings);
            Assert.True(imported.S3Settings!.Enabled);
            Assert.Equal("https://s3.example.com", imported.S3Settings.Endpoint);
            Assert.Equal("source-books", imported.S3Settings.Bucket);
            Assert.Equal("target-s3-access-key", imported.S3Settings.AccessKey);
            Assert.Equal("target-s3-secret-key", imported.S3Settings.SecretKey);
            Assert.Equal("source-s3-encryption-key", imported.S3Settings.EncryptionKey);
            var restoredS3 = await targetS3Store.LoadAsync();
            Assert.Equal(imported.S3Settings, restoredS3.Settings);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task ExportsAndRestoresTrashItems()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceBook = Path.Combine(root, "source.epub");
            CreateEpub(sourceBook);
            var converted = Path.Combine(root, "source.pdf");
            await File.WriteAllBytesAsync(converted, [1, 2, 3, 4]);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var sourcePaths = new AppPaths(Path.Combine(root, "source-app"));
            var sourceLibrary = new SqliteBookLibraryService(sourcePaths, new BookMetadataService());
            await sourceLibrary.InitializeAsync();
            await sourceLibrary.ImportAsync([sourceBook]);
            var book = Assert.Single(await sourceLibrary.SearchAsync());
            var deletedFile = await sourceLibrary.AddFileToBookAsync(book.Id, converted);
            await sourceLibrary.DeleteFileAsync(book.Id, deletedFile.Id);
            Assert.Single(await sourceLibrary.GetTrashItemsAsync());

            var backupPath = Path.Combine(root, "with-trash.kkindle");
            await new AppBackupService(sourcePaths, protector).ExportAsync(backupPath);
            using (var archive = ZipFile.OpenRead(backupPath))
                Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("trash/", StringComparison.OrdinalIgnoreCase));

            var targetPaths = new AppPaths(Path.Combine(root, "target-app"));
            var targetLibrary = new SqliteBookLibraryService(targetPaths, new BookMetadataService());
            await targetLibrary.InitializeAsync();
            await new AppBackupService(targetPaths, protector).ImportAsync(backupPath);
            await targetLibrary.InitializeAsync();

            var trash = Assert.Single(await targetLibrary.GetTrashItemsAsync());
            Assert.Equal(LibraryTrashItemKind.File, trash.Kind);
            await targetLibrary.RestoreTrashItemAsync(trash.Id);
            Assert.Equal(2, Assert.Single(await targetLibrary.SearchAsync()).Files.Count);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task ExportCreatesStandaloneSnapshotWithOnlyCommittedWalChanges()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.Database, Pooling = false
            }.ToString());
            await connection.OpenAsync();
            using var setup = connection.CreateCommand();
            setup.CommandText = "CREATE TABLE BackupProbe (Value INTEGER); INSERT INTO BackupProbe VALUES (1);";
            await setup.ExecuteNonQueryAsync();
            using var transaction = connection.BeginTransaction();
            using var pending = connection.CreateCommand();
            pending.Transaction = transaction;
            pending.CommandText = "INSERT INTO BackupProbe VALUES (2);";
            await pending.ExecuteNonQueryAsync();

            var backupPath = Path.Combine(root, "snapshot.kkindle");
            await new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector()).ExportAsync(backupPath);
            using var archive = ZipFile.OpenRead(backupPath);
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("-wal") || entry.FullName.EndsWith("-shm"));
            var snapshotPath = Path.Combine(root, "snapshot.db");
            archive.GetEntry("database/kkindle.db")!.ExtractToFile(snapshotPath);
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            await snapshot.OpenAsync();
            using var query = snapshot.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM BackupProbe WHERE Value = 1;";
            Assert.Equal(1L, await query.ExecuteScalarAsync());
            query.CommandText = "SELECT COUNT(*) FROM BackupProbe WHERE Value = 2;";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FailedExportPreservesThePreviousBackup()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            await library.ImportAsync([source]);
            var book = Assert.Single(await library.SearchAsync());
            var backup = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector());
            var backupPath = Path.Combine(root, "backup.kkindle");
            await backup.ExportAsync(backupPath);
            var originalBytes = await File.ReadAllBytesAsync(backupPath);
            using var locked = new FileStream(library.GetAbsoluteFilePath(book.Files[0]), FileMode.Open, FileAccess.Read, FileShare.None);

            await Assert.ThrowsAnyAsync<IOException>(() => backup.ExportAsync(backupPath));

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CancelledImportRestoresPreviousDatabaseFilesAndSettings()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var (paths, library, previousBook, backupPath, _) = await PrepareImportFailureAsync(root);
            using var cancellation = new CancellationTokenSource();
            var protector = new CallbackSecretProtector(() => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new AppBackupService(paths, protector).ImportAsync(backupPath, cancellation.Token));

            var restored = Assert.Single(await library.SearchAsync());
            Assert.Equal(previousBook.Id, restored.Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(restored.Files[0])));
            Assert.Equal("previous-key", (await new AiSettingsStore(paths, new TestHelpers.PlaintextSecretProtector()).LoadAsync()).ApiKey);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FailedRollbackRetainsRecoveryDataAndReportsItsLocation()
    {
        // Windows sharing violations give us a deterministic failure while
        // rolling back directory replacement, without modifying permissions.
        if (!OperatingSystem.IsWindows()) return;
        var root = TestHelpers.CreateTempDirectory();
        FileStream? locked = null;
        try
        {
            var (paths, _, previousBook, backupPath, incomingFile) = await PrepareImportFailureAsync(root);
            var protector = new CallbackSecretProtector(() =>
            {
                locked = new FileStream(Path.Combine(paths.Data, incomingFile.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new InvalidOperationException("Simulated settings write failure.");
            });

            var error = await Assert.ThrowsAnyAsync<Exception>(() => new AppBackupService(paths, protector).ImportAsync(backupPath));

            var recovery = Assert.Single(Directory.GetDirectories(paths.Data, ".kkindle-rollback-*"));
            Assert.Contains(recovery, error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(recovery, "kkindle.db")));
            Assert.True(File.Exists(Path.Combine(recovery, previousBook.Files[0].RelativePath)));
        }
        finally
        {
            locked?.Dispose();
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RestoresLegacyWindowsPathsForBooksCoversAndTrash()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.epub");
            CreateEpub(source);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var paths = new AppPaths(Path.Combine(root, "source"));
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            await library.ImportAsync([source]);
            var book = Assert.Single(await library.SearchAsync());
            await File.WriteAllTextAsync(Path.Combine(paths.Covers, "cover.jpg"), "cover-content");
            book.CoverPath = "covers/cover.jpg";
            await library.UpdateMetadataAsync(book);
            var converted = Path.Combine(root, "book.pdf");
            await File.WriteAllBytesAsync(converted, [1, 2, 3]);
            var removed = await library.AddFileToBookAsync(book.Id, converted);
            await library.DeleteFileAsync(book.Id, removed.Id);
            var backupPath = Path.Combine(root, "legacy.kkindle");
            await new AppBackupService(paths, protector).ExportAsync(backupPath);

            // Recreate a v1 package written on Windows, even when this test
            // itself runs on Linux/macOS or the exporter has been fixed.
            var legacyDatabase = Path.Combine(root, "legacy.db");
            using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update))
            {
                archive.GetEntry("database/kkindle.db")!.ExtractToFile(legacyDatabase);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = legacyDatabase, Pooling = false
                }.ToString()))
                {
                    await connection.OpenAsync();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE Books SET CoverPath = replace(CoverPath, '/', '\');
                        UPDATE BookFiles SET RelativePath = replace(RelativePath, '/', '\');
                        UPDATE LibraryTrash SET TrashPath = replace(TrashPath, '/', '\'),
                            OriginalPath = replace(OriginalPath, '/', '\'),
                            FileJson = json_set(FileJson, '$.RelativePath',
                                replace(json_extract(FileJson, '$.RelativePath'), '/', '\'));
                        """;
                    await command.ExecuteNonQueryAsync();
                }
                archive.GetEntry("database/kkindle.db")!.Delete();
                archive.CreateEntryFromFile(legacyDatabase, "database/kkindle.db");
            }

            var targetPaths = new AppPaths(Path.Combine(root, "target"));
            var target = new SqliteBookLibraryService(targetPaths, new BookMetadataService());
            await target.InitializeAsync();
            await new AppBackupService(targetPaths, protector).ImportAsync(backupPath);
            var restored = Assert.Single(await target.SearchAsync());
            Assert.DoesNotContain('\\', restored.Files[0].RelativePath);
            Assert.Equal("covers/cover.jpg", restored.CoverPath);
            Assert.True(File.Exists(target.GetAbsoluteFilePath(restored.Files[0])));
            Assert.Equal("cover-content", await File.ReadAllTextAsync(Path.Combine(targetPaths.Data, restored.CoverPath!)));
            await target.RestoreTrashItemAsync(Assert.Single(await target.GetTrashItemsAsync()).Id);
            var files = Assert.Single(await target.SearchAsync()).Files;
            Assert.Equal(2, files.Count);
            Assert.All(files, file =>
            {
                Assert.DoesNotContain('\\', file.RelativePath);
                Assert.True(File.Exists(target.GetAbsoluteFilePath(file)));
            });
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task<(AppPaths Paths, SqliteBookLibraryService Library, Book PreviousBook, string BackupPath, BookFile IncomingFile)>
        PrepareImportFailureAsync(string root)
    {
        var source = Path.Combine(root, "book.epub");
        CreateEpub(source);
        var protector = new TestHelpers.PlaintextSecretProtector();
        var sourcePaths = new AppPaths(Path.Combine(root, "source"));
        var sourceLibrary = new SqliteBookLibraryService(sourcePaths, new BookMetadataService());
        await sourceLibrary.InitializeAsync();
        await sourceLibrary.ImportAsync([source]);
        var incoming = Assert.Single(await sourceLibrary.SearchAsync());
        var backupPath = Path.Combine(root, "backup.kkindle");
        await new AppBackupService(sourcePaths, protector).ExportAsync(backupPath);
        var paths = new AppPaths(Path.Combine(root, "target"));
        var library = new SqliteBookLibraryService(paths, new BookMetadataService());
        await library.InitializeAsync();
        await library.ImportAsync([source]);
        await new AiSettingsStore(paths, protector).SaveAsync(new AiConnectionSettings { ApiKey = "previous-key" });
        return (paths, library, Assert.Single(await library.SearchAsync()), backupPath, incoming.Files[0]);
    }

    private sealed class CallbackSecretProtector(Action onProtect) : ISecretProtector
    {
        public byte[] Protect(byte[] value) { onProtect(); return value.ToArray(); }
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private static void CreateEpub(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>测试书</dc:title>
                <dc:creator>测试作者</dc:creator>
              </metadata>
            </package>
            """);
    }
}
