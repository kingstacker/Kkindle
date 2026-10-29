using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task LocalDeletionRetriesAfterUnlockAndServiceRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        var file = Assert.Single(Assert.Single(await b.Library.SearchAsync()).Files);
        var path = await b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, file);
        await a.Library.DeleteAsync(book.BookId);
        await a.SyncAsync();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True((await b.SyncAsync()).IsPartial);
            Assert.True(File.Exists(path));
            Assert.Equal(1, await b.ScalarAsync("SELECT COUNT(*) FROM LocalFileCleanupQueue;"));
        }

        var restarted = new S3SyncService(b.Paths, new TestHelpers.PlaintextSecretProtector(), _ => new MemoryClient(bucket));
        Assert.False((await restarted.SyncAsync(b.Id, b.Settings)).IsPartial);
        Assert.False(File.Exists(path));
        Assert.Equal(0, await b.ScalarAsync("SELECT COUNT(*) FROM LocalFileCleanupQueue;"));
    }

    [Fact]
    public async Task CleanupDoesNotDeleteAReferencedOrOutOfBoundsFile()
    {
        var bucket = new MemoryBucket();
        await using var device = await Device.CreateAsync(bucket);
        await device.AddBookAsync();
        var file = Assert.Single(Assert.Single(await device.Library.SearchAsync()).Files);
        var outside = Path.Combine(device.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "keep");
        await device.SqlAsync("INSERT INTO LocalFileCleanupQueue VALUES ($path);", ("$path", file.RelativePath));
        await device.SqlAsync("INSERT INTO LocalFileCleanupQueue VALUES ('../outside.txt');");

        Assert.True((await device.SyncAsync()).IsPartial);
        Assert.True(File.Exists(device.Library.GetAbsoluteFilePath(file)));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        Assert.Equal(1, await device.ScalarAsync("SELECT COUNT(*) FROM LocalFileCleanupQueue;"));
    }
}
