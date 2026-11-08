using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>How far the app trusts where an installed plugin came from.</summary>
public enum PluginTrustLevel
{
    /// <summary>No signature; the user only has the file's origin to go by.</summary>
    Unsigned = 0,

    /// <summary>Signed with a key the app does not know. The signature still pins updates to that key.</summary>
    Signed = 1,

    /// <summary>Signed with one of the app's official keys.</summary>
    Official = 2,

    /// <summary>Shipped inside the application package.</summary>
    Bundled = 3
}

/// <summary>Result of checking a package's <c>signature.json</c>.</summary>
public sealed record PluginSignatureInfo(PluginTrustLevel Trust, string? KeyId, string? Publisher)
{
    public static PluginSignatureInfo Unsigned { get; } = new(PluginTrustLevel.Unsigned, null, null);
}

/// <summary>
/// Signs and verifies plugin packages. The signature covers the plugin id,
/// version and the SHA-256 of every file except <c>signature.json</c>, so any
/// changed, added or removed file invalidates it. Keys are ECDSA P-256
/// (ES256), available on every platform .NET supports without native extras.
/// </summary>
public static class PluginSignature
{
    public const string FileName = "signature.json";
    public const string Algorithm = "ES256";
    private const string PayloadHeader = "kkindle-plugin-signature-v1";
    private const long MaximumSignatureBytes = 16 * 1024;

    /// <summary>
    /// Public keys (SubjectPublicKeyInfo, base64) whose signatures count as
    /// official. The private half stays outside the repository; see
    /// docs/plugins.md, "签名".
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> OfficialKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Kkindle"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6KXHN7c2luYWZI2ZvDrc/bt/GfRd+O1d1cIXnK+V7nhO4YsMah/g0E2E69KZbOaWokRETDCeVdFfhkGgc/4x+Q=="
    };

    /// <summary>Identifier of a public key: the first 16 hex digits of its SHA-256.</summary>
    public static string GetKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo))[..16].ToLowerInvariant();

    /// <summary>Creates a new signing key pair as PEM text (private) and base64 SPKI (public).</summary>
    public static (string PrivateKeyPem, string PublicKey, string KeyId) CreateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(publicKey), GetKeyId(publicKey));
    }

    /// <summary>
    /// Verifies the archive's signature. Returns <see cref="PluginSignatureInfo.Unsigned"/>
    /// when there is none and throws when one is present but does not match.
    /// </summary>
    public static PluginSignatureInfo Verify(ZipArchive archive, PluginManifest manifest)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            !candidate.FullName.EndsWith('/')
            && string.Equals(SafeNormalize(candidate.FullName), FileName, StringComparison.Ordinal));
        if (entry is null) return PluginSignatureInfo.Unsigned;
        if (entry.Length > MaximumSignatureBytes)
            throw new PluginPackageException("插件签名文件过大。");

        JsonObject document;
        try
        {
            using var stream = entry.Open();
            document = JsonNode.Parse(stream) as JsonObject
                ?? throw new PluginPackageException("插件签名文件格式无效。");
        }
        catch (JsonException)
        {
            throw new PluginPackageException("插件签名文件格式无效。");
        }

        if (!string.Equals(document["algorithm"]?.GetValue<string>(), Algorithm, StringComparison.Ordinal))
            throw new PluginPackageException("插件签名使用了不支持的算法。");
        byte[] publicKey;
        byte[] signature;
        try
        {
            publicKey = Convert.FromBase64String(document["publicKey"]?.GetValue<string>() ?? string.Empty);
            signature = Convert.FromBase64String(document["signature"]?.GetValue<string>() ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new PluginPackageException("插件签名文件格式无效。");
        }
        catch (InvalidOperationException)
        {
            throw new PluginPackageException("插件签名文件格式无效。");
        }
        if (publicKey.Length == 0 || signature.Length == 0)
            throw new PluginPackageException("插件签名文件格式无效。");

        var payload = BuildPayload(archive, manifest);
        bool valid;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (key.KeySize != 256)
                throw new PluginPackageException("插件签名使用了不支持的密钥。");
            valid = key.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            valid = false;
        }
        if (!valid)
            throw new PluginPackageException("插件签名校验失败：文件可能被修改过，已拒绝安装。");

        var keyId = GetKeyId(publicKey);
        var publicKeyText = Convert.ToBase64String(publicKey);
        var official = OfficialKeys.FirstOrDefault(pair => string.Equals(pair.Value, publicKeyText, StringComparison.Ordinal));
        if (official.Key is not null)
            return new PluginSignatureInfo(PluginTrustLevel.Official, keyId, official.Key);
        var publisher = document["publisher"]?.GetValue<string>()?.Trim();
        return new PluginSignatureInfo(
            PluginTrustLevel.Signed,
            keyId,
            string.IsNullOrWhiteSpace(publisher) ? null : publisher[..Math.Min(publisher.Length, 60)]);
    }

    /// <summary>
    /// Adds or replaces <c>signature.json</c> in a package, signing it with
    /// the PEM-encoded private key.
    /// </summary>
    public static PluginSignatureInfo Sign(string packagePath, string privateKeyPem, string? publisher = null)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256)
            throw new PluginPackageException("签名密钥必须是 ECDSA P-256。");
        var publicKey = key.ExportSubjectPublicKeyInfo();

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        foreach (var existing in archive.Entries
                     .Where(entry => string.Equals(SafeNormalize(entry.FullName), FileName, StringComparison.Ordinal))
                     .ToArray())
            existing.Delete();
        var manifest = PluginPackage.Validate(archive);
        var signature = key.SignData(BuildPayload(archive, manifest), HashAlgorithmName.SHA256);
        var document = new JsonObject
        {
            ["format"] = 1,
            ["algorithm"] = Algorithm,
            ["keyId"] = GetKeyId(publicKey),
            ["publisher"] = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            ["publicKey"] = Convert.ToBase64String(publicKey),
            ["signature"] = Convert.ToBase64String(signature)
        };
        var entry = archive.CreateEntry(FileName, CompressionLevel.Optimal);
        using (var stream = entry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var publicKeyText = Convert.ToBase64String(publicKey);
        var official = OfficialKeys.FirstOrDefault(pair => string.Equals(pair.Value, publicKeyText, StringComparison.Ordinal));
        return new PluginSignatureInfo(
            official.Key is null ? PluginTrustLevel.Signed : PluginTrustLevel.Official,
            GetKeyId(publicKey),
            official.Key ?? publisher);
    }

    /// <summary>The exact bytes that are signed: header, id, version, then one "path\nsha256" pair per file.</summary>
    internal static byte[] BuildPayload(ZipArchive archive, PluginManifest manifest)
    {
        var files = new List<(string Path, string Hash)>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var path = PluginPackage.NormalizeEntryPath(entry.FullName);
            if (string.Equals(path, FileName, StringComparison.Ordinal)) continue;
            using var stream = entry.Open();
            files.Add((path, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
        }
        var builder = new StringBuilder()
            .Append(PayloadHeader).Append('\n')
            .Append(manifest.Id).Append('\n')
            .Append(manifest.Version).Append('\n');
        foreach (var (path, hash) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            builder.Append(path).Append('\n').Append(hash).Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string SafeNormalize(string path)
    {
        try { return PluginPackage.NormalizeEntryPath(path); }
        catch (PluginPackageException) { return path; }
    }
}

/// <summary>The application version plugins are checked against (<c>minAppVersion</c>).</summary>
public static class PluginHostInfo
{
    private static Version? _appVersion;

    /// <summary>
    /// Defaults to the entry assembly's informational version with any
    /// prerelease suffix dropped, so 1.2.0-dev.3 satisfies minAppVersion 1.2.0.
    /// </summary>
    public static Version AppVersion
    {
        get => _appVersion ??= DetectAppVersion();
        set => _appVersion = value;
    }

    private static Version DetectAppVersion()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly();
        var text = assembly is null ? null : Core.ApplicationVersion.GetDisplayVersion(assembly);
        return text is not null && PluginManifest.TryParseVersion(text, out var version)
            ? version
            : new Version(0, 0, 0);
    }

    /// <summary>Throws when the plugin needs a newer app than this one.</summary>
    public static void EnsureCompatible(PluginManifest manifest)
    {
        if (manifest.MinAppVersion is { } minimum && Normalize(AppVersion) < Normalize(minimum))
            throw new PluginPackageException(
                $"插件需要 Kkindle {minimum.ToString(3)} 或更高版本，当前版本 {AppVersion.ToString(3)}。");
    }

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
}
