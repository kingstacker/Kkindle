using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>
/// A plugin's encrypted key/value store for credentials. Values are protected
/// with the platform secret protector (DPAPI / Keychain) and written to
/// <c>plugin-data/&lt;id&gt;/secrets.json</c>, so a settings file copied to
/// another machine cannot be decrypted.
/// </summary>
public sealed class PluginSecrets
{
    public const int MaximumKeyLength = 64;
    public const int MaximumValueLength = 4096;
    public const int MaximumBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _gate;
    private readonly string _path;
    private readonly ISecretProtector _protector;

    public PluginSecrets(string directory, ISecretProtector protector)
    {
        _path = Path.GetFullPath(Path.Combine(directory, "secrets.json"));
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        _protector = protector;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await LoadAsync(cancellationToken);
            if (!values.TryGetPropertyValue(key, out var encoded) || encoded is not JsonValue value
                || !value.TryGetValue<string>(out var text))
                return null;
            try
            {
                return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(text)));
            }
            catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidOperationException("无法解密已保存的账号信息，请重新填写。", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await LoadAsync(cancellationToken);
            if (string.IsNullOrEmpty(value))
            {
                values.Remove(key);
            }
            else
            {
                if (value.Length > MaximumValueLength)
                    throw new ArgumentException("账号信息过长。");
                values[key] = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(value)));
            }
            var json = values.ToJsonString(JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidOperationException("插件可保存的账号信息已满。");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var text = await File.ReadAllTextAsync(_path, cancellationToken);
            return JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Length > MaximumKeyLength
            || key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException("账号信息键名无效。");
    }
}

/// <summary>
/// Remembers HTTPS hosts a plugin was allowed to call at runtime. Manifest
/// origins are static; mirrors and personal domains are approved by the user
/// through a dialog and stored next to the plugin's data.
/// </summary>
public sealed class PluginNetworkGrants
{
    public const int MaximumHosts = 32;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate;
    private readonly string _path;

    public PluginNetworkGrants(string directory)
    {
        _path = Path.GetFullPath(Path.Combine(directory, "network.json"));
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<bool> IsApprovedAsync(string host, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken)).Contains(host, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApproveAsync(string host, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var hosts = await LoadAsync(cancellationToken);
            if (hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return;
            if (hosts.Count >= MaximumHosts)
                throw new InvalidOperationException("插件批准的网站数量已达上限。");
            hosts.Add(host.ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { hosts }, JsonOptions), cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var text = await File.ReadAllTextAsync(_path, cancellationToken);
            var document = JsonNode.Parse(text) as JsonObject;
            return document?["hosts"] is JsonArray array
                ? array.Select(node => node?.GetValue<string>() ?? string.Empty)
                    .Where(host => host.Length > 0)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }
}
