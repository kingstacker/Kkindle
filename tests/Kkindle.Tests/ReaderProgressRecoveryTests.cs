using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReaderProgressRecoveryTests
{
    [Fact]
    public async Task FailedDatabaseWriteQueuesProgressForRecovery()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            await CreateBlockingTriggerAsync(paths);
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var progress = NewProgress(scrollPosition: 120);

            await recovery.SaveAsync(progress);

            Assert.Null(await reader.GetProgressAsync(progress.BookFileId));
            var recoveryPath = RecoveryPath(paths);
            Assert.True(File.Exists(recoveryPath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(recoveryPath));
            var queued = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(progress.BookFileId.ToString(), queued.GetProperty("bookFileId").GetString());
            Assert.Equal(120, queued.GetProperty("scrollPosition").GetInt32());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task StartupRetryRestoresQueuedProgressAndRemovesRecoveryFile()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            await CreateBlockingTriggerAsync(paths);
            var progress = NewProgress(scrollPosition: 240);
            await new ReaderProgressRecoveryService(paths, reader).SaveAsync(progress);
            Assert.True(File.Exists(RecoveryPath(paths)));
            await DropBlockingTriggerAsync(paths);

            var restartedReader = new ReaderDataService(paths);
            await restartedReader.InitializeAsync();
            await new ReaderProgressRecoveryService(paths, restartedReader).InitializeAsync();

            var restored = Assert.IsType<ReaderProgressRow>(
                await restartedReader.GetProgressAsync(progress.BookFileId));
            Assert.Equal(240, restored.ScrollPosition);
            Assert.Equal(progress.UpdatedAt, restored.UpdatedAt);
            Assert.False(File.Exists(RecoveryPath(paths)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RepeatedFailuresForOneBookOnlyKeepTheLatestPosition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            await CreateBlockingTriggerAsync(paths);
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var older = NewProgress(scrollPosition: 100, updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

            await recovery.SaveAsync(older);
            await recovery.SaveAsync(older with { ScrollPosition = 900, UpdatedAt = DateTimeOffset.UtcNow });

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(RecoveryPath(paths)));
            var queued = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(900, queued.GetProperty("scrollPosition").GetInt32());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CanceledSaveStillPersistsTheLatestPosition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var progress = NewProgress(scrollPosition: 360);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await recovery.SaveAsync(progress, cancellation.Token);

            Assert.True(File.Exists(RecoveryPath(paths)));
            Assert.Null(await reader.GetProgressAsync(progress.BookFileId));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(RecoveryPath(paths)));
            var queued = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(360, queued.GetProperty("scrollPosition").GetInt32());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SuccessfulRetryClearsAnOlderQueuedPosition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            await CreateBlockingTriggerAsync(paths);
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var progress = NewProgress(scrollPosition: 180);
            await recovery.SaveAsync(progress);
            Assert.True(File.Exists(RecoveryPath(paths)));
            await DropBlockingTriggerAsync(paths);

            await recovery.SaveAsync(progress with
            {
                ScrollPosition = 720,
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
            });

            var saved = Assert.IsType<ReaderProgressRow>(
                await reader.GetProgressAsync(progress.BookFileId));
            Assert.Equal(720, saved.ScrollPosition);
            Assert.False(File.Exists(RecoveryPath(paths)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CorruptRecoveryFileIsIsolatedInsteadOfBlockingStartup()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var recoveryPath = RecoveryPath(paths);
            await File.WriteAllTextAsync(recoveryPath, "{not-valid-json");
            var recovery = new ReaderProgressRecoveryService(paths, reader);

            await recovery.InitializeAsync();

            Assert.False(File.Exists(recoveryPath));
            var corrupt = Assert.Single(Directory.GetFiles(
                paths.Data,
                ReaderProgressRecoveryService.RecoveryFileName + ".corrupt-*"));
            Assert.Equal("{not-valid-json", await File.ReadAllTextAsync(corrupt));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDiscardsPendingProgressInExistingAndRestartedRecoveryServices(bool restart)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var progress = NewProgress(500);
            await recovery.SaveAsync(progress, new CancellationToken(true));
            await reader.ResetReadingDataAsync();
            if (restart) recovery = new ReaderProgressRecoveryService(paths, reader);

            await recovery.InitializeAsync();

            Assert.Null(await reader.GetProgressAsync(progress.BookFileId));
            Assert.False(File.Exists(RecoveryPath(paths)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PendingProgressCreatedAfterResetStillRecovers()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            await reader.ResetReadingDataAsync();
            var progress = NewProgress(700);
            await new ReaderProgressRecoveryService(paths, reader).SaveAsync(progress, new CancellationToken(true));

            await new ReaderProgressRecoveryService(paths, reader).InitializeAsync();

            Assert.Equal(700, (await reader.GetProgressAsync(progress.BookFileId))!.ScrollPosition);
            Assert.False(File.Exists(RecoveryPath(paths)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OlderDirectSaveDoesNotReplaceNewerProgress()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var newer = NewProgress(900);
            await reader.SaveProgressAsync(newer);

            await reader.SaveProgressAsync(newer with { ScrollPosition = 100, UpdatedAt = newer.UpdatedAt.AddTicks(-1) });

            Assert.Equal(900, (await reader.GetProgressAsync(newer.BookFileId))!.ScrollPosition);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRecoveryFileIsReplayedOnlyBeforeTheFirstReset(bool reset)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var progress = NewProgress(550);
            await File.WriteAllTextAsync(RecoveryPath(paths), JsonSerializer.Serialize(
                new { Version = 1, Items = new[] { progress } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (reset) await reader.ResetReadingDataAsync();

            await new ReaderProgressRecoveryService(paths, reader).InitializeAsync();

            if (reset) Assert.Null(await reader.GetProgressAsync(progress.BookFileId));
            else Assert.Equal(550, (await reader.GetProgressAsync(progress.BookFileId))!.ScrollPosition);
            Assert.False(File.Exists(RecoveryPath(paths)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task MixedRecoveryGenerationsOnlyReplayCurrentReading()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var obsolete = NewProgress(100);
            await recovery.SaveAsync(obsolete, new CancellationToken(true));
            await reader.ResetReadingDataAsync();
            var current = NewProgress(800);
            await recovery.SaveAsync(current, new CancellationToken(true));

            await new ReaderProgressRecoveryService(paths, reader).InitializeAsync();

            Assert.Null(await reader.GetProgressAsync(obsolete.BookFileId));
            Assert.Equal(800, (await reader.GetProgressAsync(current.BookFileId))!.ScrollPosition);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OutOfOrderCanceledSavesKeepTheNewestQueuedPosition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var recovery = new ReaderProgressRecoveryService(paths, reader);
            var newer = NewProgress(900);
            await recovery.SaveAsync(newer, new CancellationToken(true));
            await recovery.SaveAsync(newer with { ScrollPosition = 100, UpdatedAt = newer.UpdatedAt.AddMinutes(-1) }, new CancellationToken(true));

            await new ReaderProgressRecoveryService(paths, reader).InitializeAsync();

            Assert.Equal(900, (await reader.GetProgressAsync(newer.BookFileId))!.ScrollPosition);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static ReaderProgressRow NewProgress(
        int scrollPosition,
        DateTimeOffset? updatedAt = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "chapter.xhtml",
            "anchor",
            2,
            scrollPosition,
            42,
            0,
            updatedAt ?? DateTimeOffset.UtcNow);

    private static string RecoveryPath(AppPaths paths) =>
        Path.Combine(paths.Data, ReaderProgressRecoveryService.RecoveryFileName);

    private static async Task CreateBlockingTriggerAsync(AppPaths paths)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER BlockReaderProgress
            BEFORE INSERT ON ReaderProgress
            BEGIN
                SELECT RAISE(FAIL, 'reader progress blocked');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropBlockingTriggerAsync(AppPaths paths)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS BlockReaderProgress;";
        await command.ExecuteNonQueryAsync();
    }
}
