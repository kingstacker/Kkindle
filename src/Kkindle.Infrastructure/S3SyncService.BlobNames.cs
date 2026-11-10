using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private const int MaximumBlobNamingSalts = 64;
    private BlobNamingSession? _blobNamingSession;

    private sealed record BlobNamingMaterial(string Namespace, byte[] Key);
    private sealed record BlobNamingSession(IReadOnlyList<string> Salts, IReadOnlyList<BlobNamingMaterial> Materials);

    private void InitializeBlobNamingSession(S3SyncSettings settings, S3SyncSnapshot? baseline,
        IReadOnlyList<S3SyncSnapshot> remotes, bool createIfMissing)
    {
        if (settings.EncryptionKey.Length == 0) return;
        if (settings.Provider == SyncProvider.AccountServer)
        {
            // Public account identity isolates dictionaries between users/regions.
            // Do not include the URL: changing a deployment domain must not change
            // object names. The v2 namespace is disjoint from old dev clients' GC.
            var identity = $"KKINDLE-ACCOUNT-OBJECTS2|{AccountRegions.Slug(settings.AccountRegion)}|{settings.AccountUserId:N}";
            _blobNamingSession = new([], [CreateBlobNamingMaterial(settings.EncryptionKey, identity)]);
            return;
        }

        // A custom store has no account ID. Keep its random salt in encrypted
        // snapshots, not in a key-derived public path or an overwriteable singleton
        // salt file. Union handles simultaneous first-device syncs without losing
        // either namespace; all later devices select the same canonical salt.
        var sources = remotes.Concat(baseline is null ? [] : new[] { baseline });
        var salts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var salt in sources.SelectMany(snapshot => snapshot.BlobNamingSalts ?? []))
        {
            if (!IsSha256(salt)) throw new InvalidDataException("云端同步对象命名盐无效。请检查同步数据。");
            salts.Add(salt.ToLowerInvariant());
            if (salts.Count > MaximumBlobNamingSalts)
                throw new InvalidDataException("云端同步对象命名盐过多。请检查同步数据。");
        }
        if (salts.Count == 0 && createIfMissing)
            salts.Add(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _blobNamingSession = new(salts.ToArray(), salts.Select(salt =>
            CreateBlobNamingMaterial(settings.EncryptionKey, "KKINDLE-CUSTOM-OBJECTS3|" + salt)).ToArray());
    }

    private BlobNamingMaterial CreateBlobNamingMaterial(string passphrase, string identity)
    {
        var encodedIdentity = Encoding.UTF8.GetBytes(identity);
        var objectNamespace = Convert.ToHexString(SHA256.HashData(encodedIdentity)).ToLowerInvariant();
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("KKINDLE-BLOB-NAMING-SALT1|" + identity));
        // Keep the same slow password derivation as ciphertext decryption, with
        // domain separation. A name is not a fast passphrase-verification oracle.
        var baseKey = GetOrDeriveEncryptionKey(passphrase, salt);
        var namingKey = HMACSHA256.HashData(baseKey, "KKINDLE-BLOB-NAMING-KEY1"u8);
        return new("encrypted/" + objectNamespace, namingKey);
    }

    private async Task InitializeBlobNamingForDownloadAsync(ISyncObjectStore client, S3SyncSettings settings,
        string deviceId, string storageIdentity, S3SyncState state, CancellationToken token)
    {
        IReadOnlyList<S3SyncSnapshot> remotes = [];
        if (settings.EncryptionKey.Length > 0 && settings.Provider != SyncProvider.AccountServer
            && state.LastUploadedSnapshot?.BlobNamingSalts is not { Length: > 0 })
        {
            // Normal on-demand downloads reuse the persisted baseline and need
            // no snapshot traffic. Recover naming salts after baseline loss or a
            // portable restore by reading authenticated snapshots before blobs.
            var objects = await ListSnapshotObjectsAsync(client, settings, token);
            remotes = await DownloadRemoteSnapshotsAsync(client, settings, objects, storageIdentity,
                deviceId, skipOwnSnapshot: false, progress: null, token);
        }
        InitializeBlobNamingSession(settings, state.LastUploadedSnapshot, remotes, createIfMissing: false);
    }

    private string BlobPrefix(S3SyncSettings settings) =>
        settings.EncryptionKey.Length == 0 ? $"{settings.Prefix}/objects/plain/"
            : $"{settings.Prefix}/objects/{RequiredBlobNamingMaterial().Namespace}/";

    private IEnumerable<string> BlobPrefixes(S3SyncSettings settings)
    {
        if (settings.EncryptionKey.Length == 0)
        {
            yield return BlobPrefix(settings);
            yield break;
        }
        foreach (var material in _blobNamingSession?.Materials ?? [])
            yield return $"{settings.Prefix}/objects/{material.Namespace}/";
    }

    private string BlobKey(S3SyncSettings settings, string hash)
    {
        if (!IsSha256(hash)) throw new InvalidDataException("同步对象缺少有效的 SHA-256 校验值。");
        var name = settings.EncryptionKey.Length == 0 ? hash.ToLowerInvariant()
            : KeyedBlobName(RequiredBlobNamingMaterial(), hash);
        return BlobPrefix(settings) + name;
    }

    private IEnumerable<string> BlobKeys(S3SyncSettings settings, string hash)
    {
        if (!IsSha256(hash)) throw new InvalidDataException("同步对象缺少有效的 SHA-256 校验值。");
        if (settings.EncryptionKey.Length == 0)
        {
            yield return BlobKey(settings, hash);
            yield break;
        }
        foreach (var material in _blobNamingSession?.Materials ?? [])
            yield return $"{settings.Prefix}/objects/{material.Namespace}/{KeyedBlobName(material, hash)}";
    }

    private BlobNamingMaterial RequiredBlobNamingMaterial() =>
        _blobNamingSession?.Materials.FirstOrDefault()
        ?? throw new InvalidOperationException("同步对象命名密钥尚未初始化。");

    private static string KeyedBlobName(BlobNamingMaterial material, string hash) =>
        Convert.ToHexString(HMACSHA256.HashData(material.Key, Encoding.ASCII.GetBytes(hash.ToLowerInvariant()))).ToLowerInvariant();

    private async Task<SyncObjectRead> OpenBlobReadAsync(ISyncObjectStore client, S3SyncSettings settings,
        string hash, CancellationToken token, long rangeStart)
    {
        var first = true;
        foreach (var key in BlobKeys(settings, hash))
        {
            try
            {
                // A partial file may belong to a different salt/legacy ciphertext.
                // Alternate candidates restart rather than mixing ciphertexts.
                return await client.OpenReadAsync(key, token, first ? rangeStart : 0);
            }
            catch (SyncObjectNotFoundException) { first = false; }
        }
        if (settings.EncryptionKey.Length == 0)
            throw new SyncObjectNotFoundException("远端同步对象不存在。");

        // Read old released and dev names only if the store already listed them.
        // Never probe a guessed plaintext-hash or passphrase-fingerprint path.
        await _legacyBlobGate.WaitAsync(token);
        string? legacyKey;
        try
        {
            if (_legacyBlobKeys is null)
            {
                var root = $"{settings.Prefix}/objects/encrypted/";
                IReadOnlyList<string> listed;
                try { listed = await client.ListKeysAsync(root, token); }
                catch (SyncListingUnavailableException)
                {
                    throw new InvalidDataException("读取旧版加密对象需要目录列表权限，请允许列出加密对象目录后重试。");
                }
                var prefixes = settings.Provider == SyncProvider.AccountServer
                    ? new[] { root + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"KKINDLE-ACCOUNT-OBJECTS1|{AccountRegions.Slug(settings.AccountRegion)}|{settings.AccountUserId:N}"))).ToLowerInvariant() + "/" }
                    : new[] { root + "v2/", root + EncryptionKeyFingerprint(settings.EncryptionKey) + "/" };
                var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prefix in prefixes)
                    foreach (var key in listed.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && IsSha256(key[prefix.Length..])))
                        discovered.TryAdd(key[prefix.Length..], key);
                _legacyBlobKeys = discovered;
            }
            legacyKey = _legacyBlobKeys.GetValueOrDefault(hash);
        }
        finally { _legacyBlobGate.Release(); }
        if (legacyKey is null) throw new SyncObjectNotFoundException("远端同步对象不存在。");
        return await client.OpenReadAsync(legacyKey, token, rangeStart: 0);
    }

    private void ResetEncryptionSession()
    {
        foreach (var material in _blobNamingSession?.Materials ?? [])
            CryptographicOperations.ZeroMemory(material.Key);
        _blobNamingSession = null;
        _encryptionSession = null;
        _legacyBlobKeys = null;
        foreach (var key in _derivedEncryptionKeys.Values) CryptographicOperations.ZeroMemory(key);
        _derivedEncryptionKeys.Clear();
    }
}
