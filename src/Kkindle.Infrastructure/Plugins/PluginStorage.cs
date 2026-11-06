using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>
/// A plugin's private key/value store, persisted as one JSON file under
/// <c>plugin-data/&lt;id&gt;</c>. Values are arbitrary JSON.
/// </summary>
public sealed class PluginStorage
{
    public const int MaximumKeyLength = 128;
    public const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    // Several pages of one plugin may be open at once; they share a file.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    private readonly string _path;

    public PluginStorage(string directory)
    {
        _path = Path.GetFullPath(Path.Combine(directory, "storage.json"));
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<JsonNode?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await LoadAsync(cancellationToken);
            return values.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, JsonNode? value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await LoadAsync(cancellationToken);
            if (value is null) values.Remove(key);
            else values[key] = value.DeepClone();
            var json = values.ToJsonString(JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidOperationException("插件存储空间已满（上限 1 MB）。");
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
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumKeyLength)
            throw new ArgumentException("存储键名无效。");
    }
}
