using System.Reflection;
using System.Text.Json;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    private static Dictionary<string, JsonElement> FutureField(string value) =>
        new() { ["FutureFeature"] = JsonSerializer.SerializeToElement(value) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRetainsOldObjectsWhenTheirReferencesAreUnknown(bool unknownData)
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await a.SyncAsync();
        var snapshot = bucket.Snapshot(a.SnapshotKey);
        var key = $"sync/objects/plain/{new string('a', 64)}";
        bucket.Objects[key] = [1, 2, 3];
        bucket.ModifiedTimes[key] = DateTime.UtcNow.AddDays(-400);
        bucket.ModifiedTimes[a.SnapshotKey] = DateTime.UtcNow.AddDays(-400);
        if (unknownData) snapshot.ExtensionData = FutureField(key);
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(snapshot);
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        Assert.Equal(unknownData, bucket.Objects.ContainsKey(key));
        if (unknownData) Assert.True(bucket.Objects.ContainsKey(a.SnapshotKey));
    }

    [Fact]
    public async Task CompatibleFutureSnapshotSurvivesSyncRestartAndLocalEdits()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        var future = bucket.Snapshot(a.SnapshotKey);
        future.Version = 6;
        future.MinimumReaderVersion = 5;
        future.ExtensionData = FutureField("snapshot");
        future.Books.Single().ExtensionData = FutureField("book");
        future.Files.Single().ExtensionData = FutureField("file");
        future.Settings!.App.ExtensionData = FutureField("app");
        future.Settings.App.DefaultReaderLayout = future.Settings.App.DefaultReaderLayout with
        { ExtensionData = FutureField("layout") };
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(future);
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        Assert.Equal(1, await b.BookCountAsync());
        // Only the persisted baseline remains available after restart.
        bucket.Objects.TryRemove(a.SnapshotKey, out _);
        await b.SqlAsync("UPDATE Books SET Title='Local edit',UpdatedAt=$time WHERE Id=$id;",
            ("$time", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")), ("$id", book.BookId.ToString()));
        var restarted = new S3SyncService(b.Paths, new TestHelpers.PlaintextSecretProtector(), _ => new MemoryClient(bucket));
        await restarted.SyncAsync(b.Id, b.Settings);
        var result = bucket.Snapshot(b.SnapshotKey);
        Assert.Equal(5, result.Version);
        Assert.Equal(5, result.MinimumReaderVersion);
        Assert.Equal("snapshot", result.ExtensionData!["FutureFeature"].GetString());
        Assert.Equal("book", result.Books.Single().ExtensionData!["FutureFeature"].GetString());
        Assert.Equal("Local edit", result.Books.Single().Title);
        Assert.Equal("file", result.Files.Single().ExtensionData!["FutureFeature"].GetString());
        Assert.Equal("app", result.Settings!.App.ExtensionData!["FutureFeature"].GetString());
        Assert.Equal("layout", result.Settings.App.DefaultReaderLayout.ExtensionData!["FutureFeature"].GetString());
    }

    [Fact]
    public async Task UnmarkedFutureProtocolStillFailsWithoutWriting()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await a.SyncAsync();
        var future = bucket.Snapshot(a.SnapshotKey);
        future.Version = 6;
        future.MinimumReaderVersion = null;
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(future);
        await using var b = await Device.CreateAsync(bucket);
        var writes = bucket.WriteCount;
        await Assert.ThrowsAsync<InvalidDataException>(() => b.SyncAsync());
        Assert.Equal(writes, bucket.WriteCount);
    }

    [Fact]
    public void OpaqueFieldsKeepOriginalClockAcrossRecaptureAndDoNotResurrectRows()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var old = new S3SyncSnapshot { CreatedAt = now, ExtensionData = FutureField("old") };
        old.Books.Add(new S3SyncBook { Id = id, UpdatedAt = now, ExtensionData = FutureField("deleted") });
        var baseline = new S3SyncSnapshot();
        Preserve(baseline, null, old);
        baseline.CreatedAt = now.AddDays(2); // recapture must not advance unknown field clocks
        var newer = new S3SyncSnapshot { CreatedAt = now.AddDays(1), ExtensionData = FutureField("new") };
        var result = new S3SyncSnapshot();
        Preserve(result, baseline, newer);
        Assert.Equal("new", result.ExtensionData!["FutureFeature"].GetString());
        Assert.Empty(result.Books);
        Assert.True((bool)typeof(S3SyncService).GetMethod("HasForwardCompatibleData", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [result])!);
    }

    private static void Preserve(S3SyncSnapshot target, S3SyncSnapshot? baseline, params S3SyncSnapshot[] remotes) =>
        typeof(S3SyncService).GetMethod("PreserveForwardCompatibleData", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [target, baseline, remotes]);
}
