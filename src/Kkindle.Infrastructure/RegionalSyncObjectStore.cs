using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>All application sync providers use a fixed namespace for the open library's region.</summary>
internal sealed class RegionalSyncObjectStore(ISyncObjectStore inner, AccountService accounts, AccountRegion region) : ISyncObjectStore
{
    private readonly string _root = "regions/" + AccountRegions.Slug(region) + "/";

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        var keys = await inner.ListKeysAsync(Map(prefix), cancellationToken);
        return keys.Select(Unmap).ToArray();
    }

    public async Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        var items = await inner.ListObjectsAsync(Map(prefix), cancellationToken);
        return items.Select(item => new SyncObjectMetadata(Unmap(item.Key), item.LastModified)).ToArray();
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        return await inner.ExistsAsync(Map(key), cancellationToken);
    }

    public async Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken, long rangeStart = 0)
    {
        await CheckAsync(cancellationToken);
        return await inner.OpenReadAsync(Map(key), cancellationToken, rangeStart);
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken, string? knownSha256 = null)
    {
        await CheckAsync(cancellationToken);
        await inner.PutAsync(Map(key), input, contentType, cancellationToken, knownSha256);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        await inner.DeleteAsync(Map(key), cancellationToken);
    }

    public async Task TestWriteAsync(string prefix, CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        await inner.TestWriteAsync(Map(prefix), cancellationToken);
    }

    private Task CheckAsync(CancellationToken token) => accounts.EnsureSyncRegionAsync(region, token);

    private string Map(string key)
    {
        if (key.StartsWith('/') || key.Contains('\\') || key.Any(char.IsControl)
            || key.Split('/').Any(part => part is "." or "..") || key.Contains("//", StringComparison.Ordinal))
            throw new InvalidDataException("无效的同步对象路径。");
        return _root + key;
    }

    private string Unmap(string key)
    {
        if (!key.StartsWith(_root, StringComparison.Ordinal))
            throw new InvalidDataException("服务器返回了其他区域的同步对象，已停止同步。");
        var relative = key[_root.Length..];
        _ = Map(relative);
        return relative;
    }

    public void Dispose() => inner.Dispose();
}
