using System.IO.Compression;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData("progress")]
    [InlineData("bookmark")]
    [InlineData("reading-time")]
    [InlineData("reset")]
    public async Task ReaderWritesWaitForAppDataLease(string operation)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await SetBackupKeyAsync(paths);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            var reader = new ReaderDataService(paths);
            await library.InitializeAsync();
            await reader.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var lease = await AppDataProcessLock.AcquireAsync(paths, CancellationToken.None);
            Task write;
            try
            {
                write = operation switch
                {
                    "progress" => reader.SaveProgressAsync(new ReaderProgressRow(bookId, fileId, "chapter", null, 0, 1, 1, 0, DateTimeOffset.UtcNow)),
                    "bookmark" => reader.SaveBookmarkAsync(new ReaderBookmark { Id = Guid.NewGuid(), BookId = bookId, BookFileId = fileId, ChapterPath = "chapter", Title = "mark", Quote = "" }),
                    "reading-time" => reader.AddReadingTimeAsync(bookId, fileId, 10, 1, 0, 1),
                    _ => reader.ResetReadingDataAsync()
                };
                Assert.False(write.IsCompleted);
            }
            finally { lease.Dispose(); }

            await write.WaitAsync(TimeSpan.FromSeconds(10));
            if (operation == "progress") Assert.NotNull(await reader.GetProgressAsync(fileId));
            if (operation == "bookmark") Assert.Single(await reader.GetBookmarksAsync(fileId));
            if (operation == "reading-time") Assert.Equal(10, (await reader.GetReadingStatsAsync(fileId))!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ExportWaitsForAnInFlightReaderIndexTransaction()
    {
        var root = TestHelpers.CreateTempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await SetBackupKeyAsync(paths);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<BookContentChunkDraft> Chunks()
            {
                started.SetResult();
                await release.Task;
                yield return new BookContentChunkDraft(0, 0, "chapter", "chapter.xhtml", 0, 4, "text");
            }

            var indexing = reader.ReplaceBookChunksStreamingAsync(Guid.NewGuid(), Guid.NewGuid(), "hash", Chunks());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var archivePath = Path.Combine(root, "backup.kkindle");
            var export = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector()).ExportAsync(archivePath);
            Assert.False(export.IsCompleted);
            release.SetResult();
            Assert.Equal(1, await indexing.WaitAsync(TimeSpan.FromSeconds(10)));
            await export.WaitAsync(TimeSpan.FromSeconds(10));

            var snapshot = Path.Combine(root, "snapshot.db");
            using (var archive = await OpenBackupArchiveAsync(archivePath))
                archive.GetEntry("database/kkindle.db")!.ExtractToFile(snapshot);
            await using var connection = new SqliteConnection($"Data Source={snapshot}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM BookContentChunks WHERE Content='text';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        finally { release.TrySetResult(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CancelledReaderWriteDoesNotHoldTheAppDataLease()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await SetBackupKeyAsync(paths);
            var reader = new ReaderDataService(paths);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await reader.InitializeAsync();
            var row = new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "chapter", null, 0, 1, 1, 0, DateTimeOffset.UtcNow);
            using var cancellation = new CancellationTokenSource();
            using (await AppDataProcessLock.AcquireAsync(paths, CancellationToken.None))
            {
                var pending = reader.SaveProgressAsync(row, cancellation.Token);
                Assert.False(pending.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            await reader.SaveProgressAsync(row).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(await reader.GetProgressAsync(row.BookFileId));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StartupRecoversInterruptedImportBeforeDatabaseMigration(bool databaseReplaced, bool legacyJournal)
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
            var retainedCovers = Path.Combine(paths.Data, "reading-material-covers");
            Directory.CreateDirectory(retainedCovers);
            await File.WriteAllTextAsync(Path.Combine(retainedCovers, "previous.jpg"), "previous-cover");
            var originals = new List<string>();
            var managed = new List<string> { "library", "covers", "trash", "ai-settings.json" };
            if (!legacyJournal) managed.Add("reading-material-covers");
            foreach (var name in managed)
            {
                var source = Path.Combine(paths.Data, name);
                if (Directory.Exists(source)) Directory.Move(source, Path.Combine(recovery, name));
                else if (File.Exists(source)) File.Move(source, Path.Combine(recovery, name));
                else continue;
                originals.Add(name);
            }
            if (!legacyJournal)
            {
                Directory.CreateDirectory(retainedCovers);
                await File.WriteAllTextAsync(Path.Combine(retainedCovers, "incoming.jpg"), "incoming-cover");
            }
            var incomingPath = Path.Combine(paths.Data, incoming.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(incomingPath)!);
            await File.WriteAllTextAsync(incomingPath, "replacement file");
            if (databaseReplaced)
            {
                using var zip = await OpenBackupArchiveAsync(archive);
                var snapshot = Path.Combine(staging, "incoming.db");
                zip.GetEntry("database/kkindle.db")!.ExtractToFile(snapshot);
                await CopySnapshot(snapshot, paths.Database);
            }
            await File.WriteAllTextAsync(Path.Combine(recovery, AppBackupService.ImportJournalFileName),
                JsonSerializer.Serialize(new AppBackupService.ImportJournal
                {
                    Phase = AppBackupService.ImportPhase.Replacing,
                    StagingDirectory = Path.GetFileName(staging), OriginalPaths = originals,
                    ReplaceReadingMaterialCovers = !legacyJournal
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
                }));

            await library.InitializeAsync();

            var restored = Assert.Single(await library.SearchAsync());
            Assert.Equal(previous.Id, restored.Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(restored.Files[0])));
            Assert.Equal("previous-key", (await new AiSettingsStore(paths, new TestHelpers.PlaintextSecretProtector()).LoadAsync()).ApiKey);
            Assert.Equal("previous-cover", await File.ReadAllTextAsync(Path.Combine(retainedCovers, "previous.jpg")));
            Assert.False(File.Exists(Path.Combine(retainedCovers, "incoming.jpg")));
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
            await SetBackupKeyAsync(paths);
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
            await Assert.ThrowsAsync<AppBackupRollbackException>(() => new AppBackupService(paths, protector).ImportAsync(archive));
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
            await SetBackupKeyAsync(paths);
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
            using (var zip = await OpenBackupArchiveAsync(archive)) zip.GetEntry("database/kkindle.db")!.ExtractToFile(snapshot);
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
