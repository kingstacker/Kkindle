using System.Text.Json;

namespace Kkindle.Infrastructure;

public static class AppRootConfiguration
{
    private const string FileName = "app-root.json";
    private static string ConfigPath(string applicationDirectory) => Path.Combine(applicationDirectory, FileName);

    public static string ResolveRoot(string configurationDirectory, string? fallbackRoot = null)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var fallback = Path.GetFullPath(fallbackRoot ?? configurationRoot);
        var configured = TryReadRoot(configurationRoot);
        if (configured is null) return fallback;
        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch
        {
            return fallback;
        }
    }

    // Uninstall cleanup must be able to inspect the persisted root without
    // creating a directory that is about to be removed. Normal startup uses
    // ResolveRoot, which retains the historical create-on-read behavior.
    public static string? TryReadRoot(string configurationDirectory)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var path = ConfigPath(configurationRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<RootConfig>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(config?.Root)
                ? null
                : Path.GetFullPath(config.Root);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string configurationDirectory, string root)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var target = Path.GetFullPath(root);
        Directory.CreateDirectory(configurationRoot);
        Directory.CreateDirectory(target);
        var temporary = ConfigPath(configurationRoot) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RootConfig { Root = target }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, ConfigPath(configurationRoot), true);
    }

    public static string MigrationBackupPath(string root) => Path.Combine(Path.GetFullPath(root), ".kkindle-migration.kkindle");

    private sealed class RootConfig { public string Root { get; set; } = string.Empty; }
}
