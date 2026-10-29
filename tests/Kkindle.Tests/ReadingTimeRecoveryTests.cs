using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReadingTimeRecoveryTests
{
    [Fact]
    public async Task UnwritableSpoolReportsFailureAndRetainsTimeForRetry()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var file = Guid.NewGuid();
            var obstruction = Path.Combine(paths.Data, "reader-reading-time-pending");
            await File.WriteAllTextAsync(obstruction, "blocked");
            var recovery = new ReaderReadingTimeRecoveryService(paths, reader);
            await Assert.ThrowsAnyAsync<IOException>(() => recovery.SaveAsync(Guid.NewGuid(), file, 30, 25, 1, 4));
            Assert.Null(await reader.GetReadingStatsAsync(file));
            File.Delete(obstruction);
            await recovery.InitializeAsync();
            Assert.Equal(30, (await reader.GetReadingStatsAsync(file))!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task LockedLegacyQueueAndDatabaseFailureStillRecoverAfterRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var fileId = Guid.NewGuid();
            await Execute(paths, "CREATE TRIGGER BlockTime BEFORE INSERT ON ReaderReadingStats BEGIN SELECT RAISE(FAIL, 'blocked'); END;");
            using (var locked = new FileStream(Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName),
                FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                await new ReaderReadingTimeRecoveryService(paths, reader).SaveAsync(Guid.NewGuid(), fileId, 30, 25, 1, 4);
            await Execute(paths, "DROP TRIGGER BlockTime;");
            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            Assert.Equal(30, (await reader.GetReadingStatsAsync(fileId))!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FailedOldBookTimeIsRetriedForItsOriginalBookWithoutDoubleCounting()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await Execute(paths, $"CREATE TRIGGER BlockTime BEFORE INSERT ON ReaderReadingStats WHEN NEW.BookFileId='{a}' BEGIN SELECT RAISE(FAIL, 'blocked'); END;");
            var service = new ReaderReadingTimeRecoveryService(paths, reader);
            await service.SaveAsync(Guid.NewGuid(), a, 30, 25, 1, 4);
            var queued = await File.ReadAllTextAsync(Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName));
            await service.SaveAsync(Guid.NewGuid(), b, 5, 50, 2, 4);
            Assert.Null(await reader.GetReadingStatsAsync(a));
            Assert.Equal(5, (await reader.GetReadingStatsAsync(b))!.CumulativeSeconds);
            await Execute(paths, "DROP TRIGGER BlockTime;");

            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            Assert.Equal(30, (await reader.GetReadingStatsAsync(a))!.CumulativeSeconds);
            await File.WriteAllTextAsync(Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName), queued);
            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            Assert.Equal(30, (await reader.GetReadingStatsAsync(a))!.CumulativeSeconds);
            Assert.Equal(5, (await reader.GetReadingStatsAsync(b))!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ResetInvalidatesOldTimeButNewReadingStillRecovers()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var file = Guid.NewGuid();
            await new ReaderReadingTimeRecoveryService(paths, reader).SaveAsync(Guid.NewGuid(), file, 30, 25, 1, 4, new CancellationToken(true));
            await reader.ResetReadingDataAsync();
            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            Assert.Null(await reader.GetReadingStatsAsync(file));
            await new ReaderReadingTimeRecoveryService(paths, reader).SaveAsync(Guid.NewGuid(), file, 7, 30, 1, 4, new CancellationToken(true));
            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            Assert.Equal(7, (await reader.GetReadingStatsAsync(file))!.CumulativeSeconds);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OldRecoveredTimeAddsSecondsWithoutRegressingNewerProgress()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var file = Guid.NewGuid();
            var book = Guid.NewGuid();
            await new ReaderReadingTimeRecoveryService(paths, reader).SaveAsync(book, file, 30, 25, 1, 4, new CancellationToken(true));
            await reader.AddReadingTimeAsync(book, file, 5, 75, 3, 4);

            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();

            var stats = (await reader.GetReadingStatsAsync(file))!;
            Assert.Equal(35, stats.CumulativeSeconds);
            Assert.Equal(75, stats.ProgressPercent);
            Assert.Equal(3, stats.CompletedChapters);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BackupImportInvalidatesPendingReadingTime()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            var reader = new ReaderDataService(paths);
            await library.InitializeAsync();
            await reader.InitializeAsync();
            var file = Guid.NewGuid();
            var backup = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector());
            var archive = Path.Combine(root, "checkpoint.kkindle");
            await backup.ExportAsync(archive);
            var recovery = new ReaderReadingTimeRecoveryService(paths, reader);
            await recovery.SaveAsync(Guid.NewGuid(), file, 30, 25, 1, 4, new CancellationToken(true));

            await backup.ImportAsync(archive);
            await reader.InitializeAsync();
            await recovery.InitializeAsync();

            Assert.Null(await reader.GetReadingStatsAsync(file));
            Assert.False(File.Exists(Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task Execute(AppPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
