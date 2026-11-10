using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed partial class S3SyncIntegrationTests
{
    private static readonly MethodInfo UnprotectSnapshot = typeof(S3SyncService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == "UnprotectPayload" && method.GetParameters() is var parameters
            && parameters.Length == 2 && parameters[1].ParameterType == typeof(string));
    private static readonly MethodInfo ProtectSnapshot = typeof(S3SyncService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == "ProtectPayload" && method.GetParameters().Length == 2);

    private static S3SyncSnapshot EncryptedSnapshot(MemoryBucket bucket, string key, string passphrase) =>
        MemoryBucket.Decode((byte[])UnprotectSnapshot.Invoke(null, [bucket.Objects[key], passphrase])!);

    private static byte[] EncodeEncryptedSnapshot(S3SyncSnapshot snapshot, string passphrase) =>
        (byte[])ProtectSnapshot.Invoke(null, [MemoryBucket.Encode(snapshot), passphrase])!;

    // Independent calculation locks down the naming protocol across clients.
    private static string ExpectedEncryptedBlobKey(S3SyncSettings settings, string hash, S3SyncSnapshot snapshot,
        string? customSalt = null)
    {
        var identity = settings.Provider == SyncProvider.AccountServer
            ? $"KKINDLE-ACCOUNT-OBJECTS2|{AccountRegions.Slug(settings.AccountRegion)}|{settings.AccountUserId:N}"
            : "KKINDLE-CUSTOM-OBJECTS3|" + (customSalt ?? snapshot.BlobNamingSalts!.Order(StringComparer.Ordinal).First());
        var objectNamespace = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("KKINDLE-BLOB-NAMING-SALT1|" + identity));
        var baseKey = Rfc2898DeriveBytes.Pbkdf2(settings.EncryptionKey, salt, 120_000, HashAlgorithmName.SHA256, 32);
        var namingKey = HMACSHA256.HashData(baseKey, "KKINDLE-BLOB-NAMING-KEY1"u8);
        var name = Convert.ToHexString(HMACSHA256.HashData(namingKey, Encoding.ASCII.GetBytes(hash.ToLowerInvariant()))).ToLowerInvariant();
        return $"{settings.Prefix}/objects/encrypted/{objectNamespace}/{name}";
    }

    [Fact]
    public async Task AccountNamesRemainStableWhenDeploymentUrlChanges()
    {
        const string passphrase = "fictional-deployment-independent-key";
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, passphrase);
        a.Settings = a.Settings with { Provider = SyncProvider.AccountServer, AccountUserId = Guid.NewGuid(), AccountServerUrl = "http://127.0.0.1:5088" };
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        var originalKey = Assert.Single(bucket.Objects.Keys, key => key.Contains("/objects/", StringComparison.Ordinal));
        var objectWrites = 0;
        bucket.BeforePut = (key, _) => { if (key.Contains("/objects/", StringComparison.Ordinal)) objectWrites++; return Task.CompletedTask; };
        a.Settings = a.Settings with { AccountServerUrl = "https://accounts-cn.example.invalid" };
        Assert.False((await a.SyncAsync()).IsPartial);
        Assert.Equal(originalKey, ExpectedEncryptedBlobKey(a.Settings, book.Hash, EncryptedSnapshot(bucket, a.SnapshotKey, passphrase)));
        await using var b = await Device.CreateAsync(bucket, passphrase);
        b.Settings = a.Settings;
        await b.SyncAsync();
        var remote = Assert.Single(await b.Library.SearchAsync());
        var downloaded = await b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, Assert.Single(remote.Files));
        Assert.Equal(book.Hash, await Hashing.Sha256Async(downloaded));
        Assert.Equal(0, objectWrites);
    }

    [Theory]
    [InlineData(SyncProvider.S3)]
    [InlineData(SyncProvider.WebDav)]
    [InlineData(SyncProvider.AccountServer)]
    public async Task EncryptedGcListsOpaquePrefixesAndKeepsReferencedFilesCoversAndOldNamespaces(SyncProvider provider)
    {
        const string passphrase = "fictional-opaque-gc-key";
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, passphrase);
        a.Settings = a.Settings with { Provider = provider, WebDavEndpoint = "https://example.invalid/dav", AccountUserId = Guid.NewGuid(), AccountServerUrl = "http://127.0.0.1:5088" };
        var book = await a.AddBookAsync();
        await a.SetCoverAsync(book.BookId, "fictional-gc-cover", DateTimeOffset.UtcNow);
        await a.SyncAsync();
        var referenced = bucket.Objects.Keys.Where(key => key.Contains("/objects/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, referenced.Length);
        foreach (var key in referenced) bucket.ModifiedTimes[key] = DateTime.UtcNow.AddDays(-400);
        var prefix = referenced[0][..(referenced[0].LastIndexOf('/') + 1)];
        var orphan = prefix + new string('f', 64);
        Assert.DoesNotContain(orphan, referenced);
        bucket.Objects[orphan] = [1, 2, 3];
        bucket.ModifiedTimes[orphan] = DateTime.UtcNow.AddDays(-400);
        var legacy = $"sync/objects/encrypted/v2/{book.Hash}";
        bucket.Objects[legacy] = [1, 2, 3];
        bucket.ModifiedTimes[legacy] = DateTime.UtcNow.AddDays(-400);
        await a.SyncAsync();
        Assert.False(bucket.Objects.ContainsKey(orphan));
        Assert.All(referenced, key => Assert.True(bucket.Objects.ContainsKey(key)));
        Assert.True(bucket.Objects.ContainsKey(legacy));
        Assert.DoesNotContain(bucket.ListPrefixes, value => value.Contains(book.Hash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentCustomInitializationsMergeSaltsWithoutOrphaningOrReuploadingObjects()
    {
        const string passphrase = "fictional-concurrent-naming-key";
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, passphrase);
        await using var b = await Device.CreateAsync(bucket, passphrase);
        await a.AddBookAsync(); await b.AddBookAsync();
        var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        bucket.BeforePut = async (key, _) =>
        {
            if (!key.EndsWith("/snapshot.bin", StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref ready) == 2) bothReady.TrySetResult();
            await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        await Task.WhenAll(a.SyncAsync(), b.SyncAsync()).WaitAsync(TimeSpan.FromSeconds(45));
        bucket.BeforePut = null;
        var aSalt = Assert.Single(EncryptedSnapshot(bucket, a.SnapshotKey, passphrase).BlobNamingSalts!);
        var bSalt = Assert.Single(EncryptedSnapshot(bucket, b.SnapshotKey, passphrase).BlobNamingSalts!);
        Assert.NotEqual(aSalt, bSalt);
        await a.SyncAsync(); await b.SyncAsync();
        var merged = EncryptedSnapshot(bucket, b.SnapshotKey, passphrase);
        Assert.Equal(new[] { aSalt, bSalt }.Order(), merged.BlobNamingSalts);
        var originalKeys = bucket.Objects.Keys.Where(key => key.Contains("/objects/", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(2, originalKeys.Length);
        foreach (var device in new[] { a, b })
        {
            foreach (var book in await device.Library.SearchAsync())
            {
                var file = Assert.Single(book.Files);
                var path = await device.Service.EnsureBookFileDownloadedAsync(device.Id, device.Settings, file);
                Assert.Equal(file.Sha256, await Hashing.Sha256Async(path));
            }
            await device.SyncAsync();
        }
        Assert.Equal(originalKeys, bucket.Objects.Keys.Where(key => key.Contains("/objects/", StringComparison.Ordinal)).Order());
        await using var c = await Device.CreateAsync(bucket, passphrase);
        await c.SyncAsync();
        foreach (var book in await c.Library.SearchAsync())
        {
            var file = Assert.Single(book.Files);
            Assert.Equal(file.Sha256, await Hashing.Sha256Async(await c.Service.EnsureBookFileDownloadedAsync(c.Id, c.Settings, file)));
        }
    }

    [Fact]
    public async Task LostBaselineRecoversCustomNamingSaltBeforeOnDemandDownload()
    {
        const string passphrase = "fictional-restored-baseline-key";
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, passphrase);
        var book = await a.AddBookAsync(); await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket, passphrase);
        await b.SyncAsync();
        var remote = Assert.Single(await b.Library.SearchAsync());
        File.Delete(Path.Combine(b.Paths.Data, "s3-sync-state.json"));
        File.Delete(Path.Combine(b.Paths.Data, "s3-sync-baseline.json.gz"));
        var path = await b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, Assert.Single(remote.Files));
        Assert.Equal(book.Hash, await Hashing.Sha256Async(path));
    }

    [Theory]
    [InlineData(SyncProvider.S3, false)]
    [InlineData(SyncProvider.WebDav, true)]
    [InlineData(SyncProvider.AccountServer, false)]
    public async Task OldEncryptedNamesAreReadOnlyAndOnlyRequestedAfterDiscovery(SyncProvider provider, bool releasedLegacy)
    {
        const string passphrase = "fictional-old-object-discovery-key";
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, passphrase);
        a.Settings = a.Settings with { Provider = provider, WebDavEndpoint = "https://example.invalid/dav", AccountUserId = Guid.NewGuid(), AccountServerUrl = "http://127.0.0.1:5088" };
        var book = await a.AddBookAsync(); await a.SyncAsync();
        var modern = Assert.Single(bucket.Objects.Keys, key => key.Contains("/objects/", StringComparison.Ordinal));
        bucket.Objects.TryRemove(modern, out _);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase))).ToLowerInvariant();
        var oldNamespace = provider == SyncProvider.AccountServer
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"KKINDLE-ACCOUNT-OBJECTS1|china|{a.Settings.AccountUserId:N}"))).ToLowerInvariant()
            : releasedLegacy ? fingerprint : "v2";
        var legacy = $"sync/objects/encrypted/{oldNamespace}/{book.Hash}";
        var originalBytes = await File.ReadAllBytesAsync(Path.Combine(a.Paths.Data, Assert.Single(Assert.Single(await a.Library.SearchAsync()).Files).RelativePath));
        bucket.Objects[legacy] = (byte[])ProtectSnapshot.Invoke(null, [originalBytes, passphrase])!;
        bucket.ModifiedTimes[legacy] = DateTime.UtcNow.AddDays(-400);
        var snapshot = EncryptedSnapshot(bucket, a.SnapshotKey, passphrase);
        snapshot.Version = 5; snapshot.MinimumReaderVersion = null; snapshot.BlobNamingSalts = null;
        bucket.Objects[a.SnapshotKey] = EncodeEncryptedSnapshot(snapshot, passphrase);
        bucket.BeforeGet = (key, _) =>
        {
            if (key.Contains(book.Hash, StringComparison.Ordinal))
            {
                Assert.Equal(legacy, key);
                Assert.Contains("sync/objects/encrypted/", bucket.ListPrefixes);
            }
            return Task.CompletedTask;
        };
        bucket.BeforePut = (key, _) =>
        {
            Assert.DoesNotContain(book.Hash, key); Assert.DoesNotContain(fingerprint, key);
            return Task.CompletedTask;
        };
        await using var b = await Device.CreateAsync(bucket, passphrase);
        b.Settings = a.Settings;
        await b.SyncAsync();
        var file = Assert.Single(Assert.Single(await b.Library.SearchAsync()).Files);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(await b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, file)));
        await b.SyncAsync();
        Assert.Contains(ExpectedEncryptedBlobKey(b.Settings, book.Hash, EncryptedSnapshot(bucket, b.SnapshotKey, passphrase)), bucket.Objects.Keys);
        Assert.True(bucket.Objects.ContainsKey(legacy));
        Assert.DoesNotContain(bucket.ListPrefixes, prefix => prefix.Contains(fingerprint, StringComparison.Ordinal) || prefix.Contains(book.Hash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TruncatedPaddedCiphertextDoesNotReplaceAnExistingLocalFile()
    {
        const string passphrase = "fictional-truncated-download-key";
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket, passphrase);
        await a.AddBookAsync(); await a.SyncAsync();
        var key = Assert.Single(bucket.Objects.Keys, value => value.Contains("/objects/", StringComparison.Ordinal));
        bucket.Objects[key] = bucket.Objects[key][..^4]; // remove the required terminator
        await using var b = await Device.CreateAsync(bucket, passphrase);
        await b.SyncAsync();
        var file = Assert.Single(Assert.Single(await b.Library.SearchAsync()).Files);
        var target = Path.Combine(b.Paths.Data, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "keep-existing-local-bytes");
        await Assert.ThrowsAsync<InvalidDataException>(() => b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, file));
        Assert.Equal("keep-existing-local-bytes", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + $".sync-{file.Sha256}.part"));
    }
}
