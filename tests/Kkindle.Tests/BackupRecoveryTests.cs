using System.IO.Compression;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupRecoversInterruptedImportBeforeDatabaseMigration(bool databaseReplaced)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var (paths, library, previous, archive, incoming) = await PrepareImportFailureAsync(root);
            var recovery = Path.Combine(paths.Data, ".kkindle-rollback-" + Guid.NewGuid().ToString("N"));
            var staging = Path.Combine(paths.Data, ".kkindle-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery);
            Directory.CreateDirectory(staging);
            await CopySnapshot(paths.Database, Path.Combine(recovery, "kkindle.db"));
            var originals = new List<string>();
            foreach (var name in new[] { "library", "covers", "trash", "ai-settings.json" })
            {
                var source = Path.Combine(paths.Data, name);
                if (Directory.Exists(source)) Directory.Move(source, Path.Combine(recovery, name));
                else if (File.Exists(source)) File.Move(source, Path.Combine(recovery, name));
                else continue;
                originals.Add(name);
            }
            var incomingPath = Path.Combine(paths.Data, incoming.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(incomingPath)!);
            await File.WriteAllTextAsync(incomingPath, "replacement file");
            if (databaseReplaced)
            {
                using var zip = ZipFile.OpenRead(archive);
                var snapshot = Path.Combine(staging, "incoming.db");
                zip.GetEntry("database/kkindle.db")!.ExtractToFile(snapshot);
                await CopySnapshot(snapshot, paths.Database);
            }
            await File.WriteAllTextAsync(Path.Combine(recovery, AppBackupService.ImportJournalFileName),
                JsonSerializer.Serialize(new AppBackupService.ImportJournal
                {
                    Phase = AppBackupService.ImportPhase.Replacing,
                    StagingDirectory = Path.GetFileName(staging), OriginalPaths = originals
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            await library.InitializeAsync();

            var restored = Assert.Single(await library.SearchAsync());
            Assert.Equal(previous.Id, restored.Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(restored.Files[0])));
            Assert.Equal("previous-key", (await new AiSettingsStore(paths, new TestHelpers.PlaintextSecretProtector()).LoadAsync()).ApiKey);
            Assert.False(Directory.Exists(recovery));
            Assert.False(Directory.Exists(staging));
            await library.InitializeAsync();
            Assert.Equal(previous.Id, Assert.Single(await library.SearchAsync()).Id);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedBackupInvalidatesProgressRecoveryInMemoryAndOnDisk(bool restart)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            var reader = new ReaderDataService(paths);
            await library.InitializeAsync();
            await reader.InitializeAsync();
            var row = new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "chapter.xhtml", null, 0, 100, 10, 0,
                DateTimeOffset.UtcNow.AddMinutes(-5));
            await reader.SaveProgressAsync(row);
            var backup = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector());
            var archive = Path.Combine(root, "checkpoint.kkindle");
            await backup.ExportAsync(archive);
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            await recovery.SaveAsync(row with { ScrollPosition = 900, UpdatedAt = DateTimeOffset.UtcNow }, new CancellationToken(true));

            await backup.ImportAsync(archive);
            await reader.InitializeAsync();
            if (restart) recovery = new ReaderProgressRecoveryService(paths, reader);
            await recovery.InitializeAsync();

            Assert.Equal(100, (await reader.GetProgressAsync(row.BookFileId))!.ScrollPosition);
            Assert.False(File.Exists(Path.Combine(paths.Data, ReaderProgressRecoveryService.RecoveryFileName)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FailedRollbackCanFinishAtStartupAfterTheFileIsUnlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = TestHelpers.CreateTempDirectory();
        FileStream? locked = null;
        try
        {
            var (paths, library, previous, archive, incoming) = await PrepareImportFailureAsync(root);
            var protector = new CallbackSecretProtector(() =>
            {
                locked = new FileStream(Path.Combine(paths.Data, incoming.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("Simulated settings failure.");
            });
            await Assert.ThrowsAsync<IOException>(() => new AppBackupService(paths, protector).ImportAsync(archive));
            Assert.NotNull(locked);
            locked.Dispose();
            locked = null;

            await library.InitializeAsync();

            var restored = Assert.Single(await library.SearchAsync());
            Assert.Equal(previous.Id, restored.Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(restored.Files[0])));
            Assert.Empty(Directory.GetDirectories(paths.Data, ".kkindle-rollback-*"));
        }
        finally { locked?.Dispose(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CommittedImportJournalOnlyCleansRecoveryCopies()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var (paths, library, previous, _, _) = await PrepareImportFailureAsync(root);
            var recovery = Path.Combine(paths.Data, ".kkindle-rollback-" + Guid.NewGuid().ToString("N"));
            var staging = Path.Combine(paths.Data, ".kkindle-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery);
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(recovery, AppBackupService.ImportJournalFileName), JsonSerializer.Serialize(
                new AppBackupService.ImportJournal { Phase = AppBackupService.ImportPhase.Committed, StagingDirectory = Path.GetFileName(staging) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            await library.InitializeAsync();

            Assert.Equal(previous.Id, Assert.Single(await library.SearchAsync()).Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(previous.Files[0])));
            Assert.False(Directory.Exists(recovery));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RetainedNoteCoverPathsArePortableInExportedSnapshots()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await File.WriteAllTextAsync(Path.Combine(paths.Covers, "retained.jpg"), "cover");
            await using (var db = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO ReadingMaterialBooks (Id,Title,CoverPath) VALUES ($id,'Retained','covers\\retained.jpg');";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                await command.ExecuteNonQueryAsync();
            }
            var archive = Path.Combine(root, "portable.kkindle");
            await new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector()).ExportAsync(archive);
            var snapshot = Path.Combine(root, "snapshot.db");
            using (var zip = ZipFile.OpenRead(archive)) zip.GetEntry("database/kkindle.db")!.ExtractToFile(snapshot);
            await using var source = new SqliteConnection($"Data Source={snapshot};Pooling=False");
            await source.OpenAsync();
            using var read = source.CreateCommand();
            read.CommandText = "SELECT CoverPath FROM ReadingMaterialBooks;";
            Assert.Equal("covers/retained.jpg", await read.ExecuteScalarAsync());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task CopySnapshot(string sourcePath, string destinationPath)
    {
        await using var source = new SqliteConnection($"Data Source={sourcePath};Pooling=False");
        await using var target = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        await source.OpenAsync();
        await target.OpenAsync();
        source.BackupDatabase(target);
    }
}
