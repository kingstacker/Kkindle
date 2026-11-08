using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>One plugin listed in a catalog index.</summary>
public sealed record PluginCatalogEntry(
    string Id,
    string Name,
    string Version,
    string Description,
    string Author,
    string? Homepage,
    Version? MinAppVersion,
    Uri Download,
    string Sha256,
    long Size,
    IReadOnlyDictionary<string, PluginLocalization> Localizations)
{
    public string GetName(string? language) => Find(language)?.Name ?? Name;

    public string GetDescription(string? language) => Find(language)?.Description ?? Description;

    /// <summary>False when this app is older than the plugin's <c>minAppVersion</c>.</summary>
    public bool IsCompatible =>
        MinAppVersion is null || new Version(
            PluginHostInfo.AppVersion.Major,
            Math.Max(0, PluginHostInfo.AppVersion.Minor),
            Math.Max(0, PluginHostInfo.AppVersion.Build)) >= MinAppVersion;

    private PluginLocalization? Find(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        if (Localizations.TryGetValue(language, out var exact)) return exact;
        var prefix = language.Split('-')[0];
        return Localizations
            .Where(pair => pair.Key.Split('-')[0].Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }
}

/// <summary>An installed plugin with a newer compatible version in the catalog.</summary>
public sealed record PluginUpdate(InstalledPlugin Installed, PluginCatalogEntry Available);

/// <summary>
/// Reads a static plugin index (<c>plugins/index.json</c>) and downloads
/// packages from it. Downloads must match the index's SHA-256 and size; the
/// package then goes through the normal inspect/confirm/install path, so the
/// catalog never bypasses signature or permission checks.
/// </summary>
public sealed class PluginCatalogService : IDisposable
{
    public const string DefaultIndexUrl = "https://kkindle.stacker.beauty/plugins/index.json";
    public const string IndexUrlEnvironmentVariable = "KKINDLE_PLUGIN_CATALOG";
    private const int MaximumIndexBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PluginCatalogService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kkindle", PluginHostInfo.AppVersion.ToString(3)));
        _ownsClient = true;
    }

    /// <summary>The index in use: the environment override for development, otherwise the official one.</summary>
    public static Uri IndexUri
    {
        get
        {
            var overrideValue = Environment.GetEnvironmentVariable(IndexUrlEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(overrideValue)
                && Uri.TryCreate(overrideValue, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback || uri.IsFile)
                    ? uri
                    : new Uri(DefaultIndexUrl);
        }
    }

    public async Task<IReadOnlyList<PluginCatalogEntry>> GetEntriesAsync(
        Uri? indexUri = null,
        CancellationToken cancellationToken = default)
    {
        var uri = indexUri ?? IndexUri;
        string json;
        if (uri.IsFile)
        {
            json = await File.ReadAllTextAsync(uri.LocalPath, cancellationToken);
        }
        else
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumIndexBytes)
                throw new InvalidDataException("插件索引过大。");
            json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (json.Length > MaximumIndexBytes)
                throw new InvalidDataException("插件索引过大。");
        }
        return Parse(json, uri);
    }

    /// <summary>Parses an index document; entries with invalid fields are skipped.</summary>
    public static IReadOnlyList<PluginCatalogEntry> Parse(string json, Uri baseUri)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("插件索引格式无效。");
        if (root.TryGetProperty("format", out var format) && format.TryGetInt32(out var formatVersion) && formatVersion > 1)
            throw new InvalidDataException("插件索引需要更新版本的 Kkindle。");

        var entries = new List<PluginCatalogEntry>();
        foreach (var element in plugins.EnumerateArray())
        {
            if (TryParseEntry(element, baseUri) is { } entry
                && entries.All(existing => !string.Equals(existing.Id, entry.Id, StringComparison.Ordinal)))
                entries.Add(entry);
        }
        return entries;
    }

    private static PluginCatalogEntry? TryParseEntry(JsonElement element, Uri baseUri)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

        var id = Text("id");
        var name = Text("name");
        var version = Text("version");
        var download = Text("download");
        var sha256 = Text("sha256")?.ToLowerInvariant();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)
            || string.IsNullOrEmpty(download) || sha256 is not { Length: 64 }
            || !sha256.All(Uri.IsHexDigit)
            || !PluginManifest.TryParseVersion(version, out _))
            return null;
        if (!Uri.TryCreate(baseUri, download, out var downloadUri)
            || !(downloadUri.Scheme == Uri.UriSchemeHttps || downloadUri.IsLoopback || downloadUri.IsFile)
            || downloadUri.IsFile != baseUri.IsFile)
            return null;
        Version? minAppVersion = null;
        if (Text("minAppVersion") is { Length: > 0 } minimum)
        {
            if (!PluginManifest.TryParseVersion(minimum, out var parsed)) return null;
            minAppVersion = parsed;
        }
        var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes)
            ? bytes
            : 0;
        if (size < 0 || size > PluginPackage.MaximumPackageBytes) return null;

        var localizations = new Dictionary<string, PluginLocalization>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("i18n", out var i18n) && i18n.ValueKind == JsonValueKind.Object)
        {
            foreach (var language in i18n.EnumerateObject())
            {
                if (language.Value.ValueKind != JsonValueKind.Object) continue;
                string? Localized(string field) =>
                    language.Value.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                localizations[language.Name] = new PluginLocalization(
                    Localized("name"),
                    Localized("description"),
                    new Dictionary<string, string>());
            }
        }
        var homepage = Text("homepage");
        return new PluginCatalogEntry(
            id,
            name,
            version,
            Text("description") ?? string.Empty,
            Text("author") ?? string.Empty,
            homepage is not null && Uri.TryCreate(homepage, UriKind.Absolute, out var homepageUri)
                && homepageUri.Scheme == Uri.UriSchemeHttps
                ? homepage
                : null,
            minAppVersion,
            downloadUri,
            sha256,
            size,
            localizations);
    }

    /// <summary>Installed plugins that have a newer compatible version in <paramref name="entries"/>.</summary>
    public static IReadOnlyList<PluginUpdate> FindUpdates(
        IEnumerable<InstalledPlugin> installed,
        IEnumerable<PluginCatalogEntry> entries)
    {
        var byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var updates = new List<PluginUpdate>();
        foreach (var plugin in installed)
        {
            if (!byId.TryGetValue(plugin.Id, out var entry) || !entry.IsCompatible) continue;
            if (PluginManifest.TryParseVersion(entry.Version, out var available)
                && PluginManifest.TryParseVersion(plugin.Manifest.Version, out var current)
                && available > current)
                updates.Add(new PluginUpdate(plugin, entry));
        }
        return updates;
    }

    /// <summary>
    /// Downloads a package to a temporary file and checks it against the index.
    /// The caller installs it and deletes the file afterwards.
    /// </summary>
    public async Task<string> DownloadAsync(
        PluginCatalogEntry entry,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"kkindle-catalog-{Guid.NewGuid():N}{PluginPackage.Extension}");
        try
        {
            if (entry.Download.IsFile)
            {
                File.Copy(entry.Download.LocalPath, temporary);
            }
            else
            {
                using var response = await _http.GetAsync(entry.Download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? entry.Size;
                if (total > PluginPackage.MaximumPackageBytes)
                    throw new InvalidDataException("插件文件超过 32 MB。");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(temporary);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > PluginPackage.MaximumPackageBytes)
                        throw new InvalidDataException("插件文件超过 32 MB。");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (total > 0) progress?.Report(Math.Min(1, copied / (double)total));
                }
            }

            var length = new FileInfo(temporary).Length;
            if (entry.Size > 0 && length != entry.Size)
                throw new InvalidDataException("下载的插件大小与索引不一致，已停止安装。");
            await using (var stream = File.OpenRead(temporary))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("下载的插件校验值与索引不一致，已停止安装。");
            }
            return temporary;
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
