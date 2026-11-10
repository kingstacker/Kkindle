using System.Collections.Concurrent;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

/// <summary>Idle syncs avoid metered traffic; a lost cloud entitlement stops the entire sync.</summary>
public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task IdleSyncsNeitherRepublishNorRedownloadUnchangedSnapshots()
    {
        var bucket = new MemoryBucket { TrackModified = true };
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await a.AddBookAsync();
        // Let both devices converge; merged views can change for a couple of rounds.
        for (var round = 0; round < 3; round++) { await a.SyncAsync(); await b.SyncAsync(); }

        var writes = bucket.WriteCount;
        var reads = bucket.GetCounts.GetValueOrDefault(a.SnapshotKey);
        await a.SyncAsync(); await b.SyncAsync(); await a.SyncAsync();
        Assert.Equal(writes, bucket.WriteCount);
        Assert.Equal(reads, bucket.GetCounts.GetValueOrDefault(a.SnapshotKey));

        var added = await a.AddBookAsync();
        await a.SyncAsync();
        Assert.True(bucket.WriteCount > writes);
        await b.SyncAsync();
        Assert.Equal(reads + 1, bucket.GetCounts.GetValueOrDefault(a.SnapshotKey));
        Assert.Contains(await b.Library.SearchAsync(), book => book.Id == added.BookId);
    }

    [Fact]
    public async Task LostCloudEntitlementStopsTheSyncBeforePublishingReadingData()
    {
        var bucket = new MemoryBucket();
        var refused = new ConcurrentBag<string>();
        ISyncObjectStore NoBooks(S3SyncSettings settings) => new BooklessStore(new S3SyncObjectStore(settings, new MemoryClient(bucket)), refused);
        await using var a = await Device.CreateAsync(bucket, store: NoBooks);
        await a.AddBookAsync();
        for (var i = 0; i < 11; i++) await a.AddBookAsync();
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => a.SyncAsync());
        Assert.Empty(bucket.Objects);
        // An entitlement lost during transfer also cancels other uploads in flight.
        Assert.InRange(refused.Count, 1, a.Settings.ConcurrentRequests);
    }

    private sealed class BooklessStore(ISyncObjectStore inner, ConcurrentBag<string> refused) : ISyncObjectStore
    {
        private static bool IsBook(string key) => key.Contains("/objects/", StringComparison.Ordinal);
        private void Refuse(string key) { refused.Add(key); throw new DeviceFeatureRequiredException(MemberEntitlements.AccountCloudRequirement); }
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken token) => inner.ListKeysAsync(prefix, token);
        public Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken token) => inner.ListObjectsAsync(prefix, token);
        public Task<bool> ExistsAsync(string key, CancellationToken token) => inner.ExistsAsync(key, token);
        public Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken token, long rangeStart = 0)
        { if (IsBook(key)) Refuse(key); return inner.OpenReadAsync(key, token, rangeStart); }
        public Task PutAsync(string key, Stream input, string contentType, CancellationToken token, string? knownSha256 = null)
        { if (IsBook(key)) Refuse(key); return inner.PutAsync(key, input, contentType, token, knownSha256); }
        public Task DeleteAsync(string key, CancellationToken token) => inner.DeleteAsync(key, token);
        public Task TestWriteAsync(string prefix, CancellationToken token) => inner.TestWriteAsync(prefix, token);
        public void Dispose() => inner.Dispose();
    }
}
