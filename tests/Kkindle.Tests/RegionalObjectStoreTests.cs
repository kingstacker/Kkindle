using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task RegionalNamespacesPreventLibrariesFromMergingInTheSameExternalBucket()
    {
        var bucket = new MemoryBucket();
        await using var china = await Device.CreateAsync(bucket);
        await using var international = await Device.CreateAsync(bucket);
        var protector = new TestHelpers.PlaintextSecretProtector();
        using var chineseAccount = new AccountService(china.Paths, protector, dataRegion: AccountRegion.China);
        using var foreignAccount = new AccountService(international.Paths, protector, dataRegion: AccountRegion.International);
        await foreignAccount.SelectRegionAsync(AccountRegion.International);
        var chineseSync = new S3SyncService(china.Paths, protector, settings =>
            new RegionalSyncObjectStore(new S3SyncObjectStore(settings, new MemoryClient(bucket)), chineseAccount, AccountRegion.China));
        var foreignSync = new S3SyncService(international.Paths, protector, settings =>
            new RegionalSyncObjectStore(new S3SyncObjectStore(settings, new MemoryClient(bucket)), foreignAccount, AccountRegion.International));
        await china.AddBookAsync();
        await chineseSync.SyncAsync(china.Id, china.Settings);
        await foreignSync.SyncAsync(international.Id, international.Settings);
        Assert.Equal(0, await international.BookCountAsync());
        Assert.Contains("regions/china/" + china.SnapshotKey, bucket.Objects.Keys);
        Assert.Contains("regions/international/" + international.SnapshotKey, bucket.Objects.Keys);
        await international.AddBookAsync();
        await foreignSync.SyncAsync(international.Id, international.Settings);
        await chineseSync.SyncAsync(china.Id, china.Settings);
        Assert.Equal(1, await china.BookCountAsync());
        Assert.Equal(1, await international.BookCountAsync());
        await foreignAccount.SelectRegionAsync(AccountRegion.China);
        var writes = bucket.WriteCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => foreignSync.SyncAsync(international.Id, international.Settings));
        Assert.Equal(writes, bucket.WriteCount);
    }
}
