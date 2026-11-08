using Kkindle.Core;

namespace Kkindle.AccountServer;

/// <summary>Each regional server stores objects beneath the authenticated account's directory.</summary>
internal sealed class AccountSyncStorage(string directory)
{
    public const long MaxObjectBytes = 512L * 1024 * 1024;

    public AccountSyncObjectMetadata[] List(Guid account, string prefix)
    {
        ValidateKey(prefix, allowEmpty: true);
        var root = Root(account);
        if (!Directory.Exists(root)) return [];
        var values = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new AccountSyncObjectMetadata(Path.GetRelativePath(root, path).Replace('\\', '/'),
                new DateTimeOffset(File.GetLastWriteTimeUtc(path))))
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Take(200_001).ToArray();
        if (values.Length > 200_000) throw new AccountRequestException(413, "同步对象列表过大。");
        return values;
    }

    public string ExistingPath(Guid account, string key)
    {
        var path = ObjectPath(account, key);
        if (!File.Exists(path)) throw new AccountRequestException(404, "同步对象不存在。");
        return path;
    }

    public async Task PutAsync(Guid account, string key, Stream input, CancellationToken token)
    {
        var path = ObjectPath(account, key);
        var uploads = Path.Combine(directory, account.ToString("N"), "uploads");
        Directory.CreateDirectory(uploads);
        var temporary = Path.Combine(uploads, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += count;
                    if (length > MaxObjectBytes) throw new AccountRequestException(413, "同步文件超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(Guid account, string key) => File.Delete(ObjectPath(account, key));
    private string Root(Guid account) => Path.Combine(directory, account.ToString("N"), "objects");

    private string ObjectPath(Guid account, string key)
    {
        ValidateKey(key, allowEmpty: false);
        var root = Path.GetFullPath(Root(account)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AccountRequestException(400, "无效的同步对象路径。");
        return path;
    }

    private static void ValidateKey(string key, bool allowEmpty)
    {
        if (allowEmpty && key.Length == 0) return;
        var parts = key.TrimEnd('/').Split('/');
        if (key.Length == 0 || key.Length > 1024 || key.StartsWith('/') || key.Contains('\\')
            || key.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 || Path.IsPathRooted(key)
            || key.Any(char.IsControl) || parts.Any(part => part is "" or "." or "..")
            || parts.Any(part => part.EndsWith('.') || part.EndsWith(' '))
            || (!allowEmpty && key.EndsWith('/')))
            throw new AccountRequestException(400, "无效的同步对象路径。");
    }
}
