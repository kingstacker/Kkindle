using System.IO.Compression;
using System.Text.Json;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>An installed plugin and whether the user has it switched on.</summary>
public sealed record InstalledPlugin(PluginManifest Manifest, string Directory, bool Enabled)
{
    public string Id => Manifest.Id;

    public string ResolveFile(string relativePath)
    {
        var normalized = PluginPackage.NormalizeEntryPath(relativePath);
        var root = Path.GetFullPath(Directory);
        var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PluginPackageException($"插件文件路径“{relativePath}”无效。");
        return path;
    }
}

/// <summary>Result of inspecting a package before the user confirms installation.</summary>
public sealed record PluginInstallPreview(PluginManifest Manifest, InstalledPlugin? Existing)
{
    public bool IsUpdate => Existing is not null;

    public bool IsDowngrade =>
        Existing is not null
        && PluginManifest.TryParseVersion(Manifest.Version, out var incoming)
        && PluginManifest.TryParseVersion(Existing.Manifest.Version, out var current)
        && incoming < current;
}

/// <summary>
/// Installs plugins under <c>&lt;root&gt;/plugins/&lt;id&gt;</c> and keeps the
/// enabled flags in <c>plugins.json</c> next to them. Plugins belong to this
/// installation: they live outside the synced and backed-up data folder.
/// </summary>
public sealed class PluginManager
{
    private const string StateFileName = "plugins.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _pluginsDirectory;
    private readonly string _dataDirectory;

    public PluginManager(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _pluginsDirectory = paths.Plugins;
        _dataDirectory = paths.PluginData;
    }

    public event EventHandler? Changed;

    public string PluginsDirectory => _pluginsDirectory;

    public string GetDataDirectory(string pluginId) => Path.Combine(_dataDirectory, pluginId);

    /// <summary>Lists installed plugins, skipping folders whose manifest no longer validates.</summary>
    public IReadOnlyList<InstalledPlugin> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_pluginsDirectory)) return [];
            var state = LoadState();
            var plugins = new List<InstalledPlugin>();
            foreach (var directory in Directory.EnumerateDirectories(_pluginsDirectory))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.')) continue;
                try
                {
                    var manifestPath = Path.Combine(directory, PluginManifest.FileName);
                    if (!File.Exists(manifestPath)) continue;
                    var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
                    if (!string.Equals(manifest.Id, name, StringComparison.Ordinal)) continue;
                    plugins.Add(new InstalledPlugin(
                        manifest,
                        directory,
                        !state.TryGetValue(manifest.Id, out var entry) || entry.Enabled));
                }
                catch (Exception exception) when (exception is PluginPackageException or IOException or UnauthorizedAccessException)
                {
                    // A damaged plugin folder must not hide the others.
                }
            }
            return plugins.OrderBy(plugin => plugin.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    public InstalledPlugin? Find(string pluginId) =>
        List().FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

    public PluginInstallPreview Inspect(string packagePath)
    {
        var manifest = PluginPackage.ReadManifest(packagePath);
        return new PluginInstallPreview(manifest, Find(manifest.Id));
    }

    /// <summary>
    /// Reads a plugin straight from a development folder. Only the manifest
    /// and the pages it references are checked; the folder itself is not
    /// modified or moved.
    /// </summary>
    public PluginInstallPreview InspectDirectory(string directory)
    {
        var root = ResolvePluginDirectory(directory);
        var manifestPath = Path.Combine(root, PluginManifest.FileName);
        var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
        foreach (var page in manifest.PagePaths)
        {
            if (!File.Exists(ToDirectoryPath(root, page)))
                throw new PluginPackageException($"插件缺少页面文件“{page}”。");
        }
        if (manifest.Icon is { } icon && !File.Exists(ToDirectoryPath(root, icon)))
            throw new PluginPackageException($"插件缺少图标文件“{icon}”。");
        return new PluginInstallPreview(manifest, Find(manifest.Id));
    }

    /// <summary>
    /// Installs or updates a plugin from a development folder. The folder is
    /// packed into a temporary archive so it passes the same validation and
    /// staging path as a regular package.
    /// </summary>
    public InstalledPlugin InstallFromDirectory(string directory)
    {
        var root = ResolvePluginDirectory(directory);
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"kkindle-plugin-{Guid.NewGuid():N}{PluginPackage.Extension}");
        try
        {
            long total = 0;
            var count = 0;
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in EnumeratePluginFiles(root))
                {
                    var info = new FileInfo(file);
                    total += info.Length;
                    if (total > PluginPackage.MaximumPackageBytes)
                        throw new PluginPackageException("插件文件夹超过 32 MB。");
                    if (++count > PluginPackage.MaximumEntries)
                        throw new PluginPackageException("插件包含的文件过多。");
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }
            return Install(temporary);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string ResolvePluginDirectory(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new PluginPackageException("找不到插件文件夹。");
        if (!File.Exists(Path.Combine(root, PluginManifest.FileName)))
            throw new PluginPackageException("插件文件夹中缺少 manifest.json。");
        return root;
    }

    private static string ToDirectoryPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static IEnumerable<string> EnumeratePluginFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Never follow symlinked directories out of the plugin folder.
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var segments = relative.Split('/');
            // Editor and dependency folders are never part of a plugin.
            if (segments.Any(segment => segment is ".git" or ".DS_Store" or "node_modules" or ".idea" or ".vscode"))
                continue;
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) continue;
            yield return file;
        }
    }

    /// <summary>
    /// Installs or replaces a plugin. The package is extracted into a staging
    /// folder first, so a failed import leaves any existing version intact.
    /// </summary>
    public InstalledPlugin Install(string packagePath)
    {
        using var archive = PluginPackage.Open(packagePath);
        var manifest = PluginPackage.Validate(archive);
        lock (_gate)
        {
            Directory.CreateDirectory(_pluginsDirectory);
            var staging = Path.Combine(_pluginsDirectory, $".staging-{Guid.NewGuid():N}");
            var target = Path.Combine(_pluginsDirectory, manifest.Id);
            var backup = Path.Combine(_pluginsDirectory, $".previous-{Guid.NewGuid():N}");
            try
            {
                PluginPackage.ExtractTo(archive, staging);
                if (Directory.Exists(target)) Directory.Move(target, backup);
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
                    throw;
                }
            }
            finally
            {
                TryDeleteDirectory(staging);
                TryDeleteDirectory(backup);
            }

            var state = LoadState();
            var enabled = !state.TryGetValue(manifest.Id, out var entry) || entry.Enabled;
            state[manifest.Id] = new PluginStateEntry { Enabled = enabled, InstalledAt = DateTimeOffset.UtcNow };
            SaveState(state);
            var installed = new InstalledPlugin(manifest, target, enabled);
            Changed?.Invoke(this, EventArgs.Empty);
            return installed;
        }
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        lock (_gate)
        {
            var state = LoadState();
            var entry = state.TryGetValue(pluginId, out var existing) ? existing : new PluginStateEntry();
            entry.Enabled = enabled;
            state[pluginId] = entry;
            SaveState(state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a plugin and, optionally, the data it stored.</summary>
    public void Uninstall(string pluginId, bool removeData = true)
    {
        lock (_gate)
        {
            var target = Path.Combine(_pluginsDirectory, pluginId);
            if (Directory.Exists(target))
            {
                // Rename first so a partially deleted folder is never listed.
                var doomed = Path.Combine(_pluginsDirectory, $".removed-{Guid.NewGuid():N}");
                Directory.Move(target, doomed);
                TryDeleteDirectory(doomed);
            }
            if (removeData) TryDeleteDirectory(GetDataDirectory(pluginId));
            var state = LoadState();
            if (state.Remove(pluginId)) SaveState(state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, PluginStateEntry> LoadState()
    {
        var path = Path.Combine(_pluginsDirectory, StateFileName);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, PluginStateEntry>>(File.ReadAllText(path), JsonOptions)
                    ?? new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
        }
        return new(StringComparer.Ordinal);
    }

    private void SaveState(Dictionary<string, PluginStateEntry> state)
    {
        Directory.CreateDirectory(_pluginsDirectory);
        var path = Path.Combine(_pluginsDirectory, StateFileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class PluginStateEntry
    {
        public bool Enabled { get; set; } = true;
        public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
