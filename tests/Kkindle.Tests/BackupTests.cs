using System.IO.Compression;
using Kkindle.Infrastructure;
using Kkindle.Core;

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
