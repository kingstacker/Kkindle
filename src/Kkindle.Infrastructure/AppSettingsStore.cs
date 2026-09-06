using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    public AppSettingsStore(AppPaths paths) => _paths = paths;

    // The first window surface must be selected before Avalonia presents the
    // desktop window. Keep this small synchronous bootstrap read separate from
    // the full async startup load used by MainWindow.
    public AppSettings LoadSynchronously()
    {
        try
        {
            _paths.EnsureDirectories();
            if (!File.Exists(_paths.Settings)) return new AppSettings();
            using var stream = new FileStream(_paths.Settings, FileMode.Open, FileAccess.Read, FileShare.Read);
            return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions));
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.Settings)) return new AppSettings();
        try
        {
            await using var stream = new FileStream(_paths.Settings, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return AppSettings.Normalize(await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken));
        }
        catch (JsonException) { return new AppSettings(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        await SaveUnderLockAsync(settings, cancellationToken);
    }

    internal async Task SaveUnderLockAsync(AppSettings settings, CancellationToken cancellationToken, DateTimeOffset? syncedAt = null)
    {
        _paths.EnsureDirectories();
        var temporary = _paths.Settings + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, AppSettings.Normalize(settings), JsonOptions, cancellationToken);
        if (syncedAt is { } timestamp) File.SetLastWriteTimeUtc(temporary, timestamp.UtcDateTime);
        File.Move(temporary, _paths.Settings, true);
    }
}
