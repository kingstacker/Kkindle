using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle;

/// <summary>
/// Lends the user's configured AI connection to plugins with the <c>ai</c>
/// permission. The API key never reaches the plugin; it only sees the reply.
/// </summary>
internal sealed class PluginAiHost : IPluginAiHost
{
    private readonly AiSettingsStore _settings;
    private readonly AiChatClient _client;
    private readonly Func<AppSettings> _appSettings;

    public PluginAiHost(AiSettingsStore settings, AiChatClient client, Func<AppSettings> appSettings)
    {
        _settings = settings;
        _client = client;
        _appSettings = appSettings;
    }

    public async Task<PluginAiStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (settings, reason) = await LoadAsync(cancellationToken);
        return new PluginAiStatus(
            reason is null,
            settings?.ProviderDisplayName,
            reason is null ? settings?.Model : null,
            reason);
    }

    public async Task<string> CompleteAsync(PluginAiRequest request, CancellationToken cancellationToken)
    {
        var (settings, reason) = await LoadAsync(cancellationToken);
        if (reason is not null) throw new InvalidOperationException(reason);
        var history = request.History
            .Select(turn => new AiConversationTurn(turn.Role, turn.Content))
            .ToArray();
        return await _client.CompleteAsync(
            settings!,
            string.IsNullOrWhiteSpace(request.System) ? "You are a helpful assistant inside the Kkindle reading app." : request.System,
            request.Prompt,
            history,
            cancellationToken);
    }

    private async Task<(AiConnectionSettings? Settings, string? Reason)> LoadAsync(CancellationToken cancellationToken)
    {
        var app = _appSettings();
        if (!app.AiEnabled) return (null, "AI 已在应用设置中关闭。");
        var settings = await _settings.LoadAsync(cancellationToken);
        if (!settings.IsConfigured) return (settings, "AI 还没有配置服务、模型和 API Key。");
        // A local endpoint (Ollama and friends) keeps working offline.
        if (!app.NetworkEnabled
            && !(Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback))
            return (settings, "网络功能已关闭，无法使用 AI。");
        return (settings, null);
    }
}

/// <summary>Reader navigation for plugin pages, backed by a main-window callback.</summary>
internal sealed class DelegatePluginReaderHost : IPluginReaderHost
{
    private readonly Func<Guid, Guid?, string?, int?, CancellationToken, Task> _open;

    public DelegatePluginReaderHost(Func<Guid, Guid?, string?, int?, CancellationToken, Task> open)
    {
        _open = open;
    }

    public Task OpenAsync(Guid bookId, Guid? fileId, string? chapterPath, int? offset, CancellationToken cancellationToken) =>
        _open(bookId, fileId, chapterPath, offset, cancellationToken);
}

/// <summary>
/// Runs plugins' <c>contributes.background</c> scripts: once shortly after
/// startup (unless the plugin opts out) and then every
/// <c>intervalMinutes</c>. Runs are sequential, one plugin at a time, and
/// the last run of each plugin is persisted so intervals survive restarts.
/// </summary>
internal sealed class PluginBackgroundScheduler
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumConsecutiveFailures = 5;
    private readonly string _statePath;
    private readonly Func<IReadOnlyList<InstalledPlugin>> _plugins;
    private readonly Func<InstalledPlugin, PluginBackgroundTask, JsonObject, CancellationToken, Task<PluginScriptResult>> _run;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _startedThisSession = new(StringComparer.Ordinal);
    private Dictionary<string, BackgroundState>? _state;

    public PluginBackgroundScheduler(
        string statePath,
        Func<IReadOnlyList<InstalledPlugin>> plugins,
        Func<InstalledPlugin, PluginBackgroundTask, JsonObject, CancellationToken, Task<PluginScriptResult>> run,
        Func<DateTimeOffset>? clock = null)
    {
        _statePath = statePath;
        _plugins = plugins;
        _run = run;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised after every run with the plugin and its outcome.</summary>
    public event Action<InstalledPlugin, PluginScriptResult>? Completed;

    /// <summary>Runs every task that is due; returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            var state = LoadState();
            var ran = 0;
            foreach (var plugin in _plugins())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!plugin.Enabled || plugin.Manifest.Background is not { } task) continue;
                state.TryGetValue(plugin.Id, out var entry);
                var now = _clock();
                var reason = Due(plugin.Id, task, entry, now);
                if (reason is null) continue;

                _startedThisSession.Add(plugin.Id);
                var argument = new JsonObject
                {
                    ["reason"] = reason,
                    ["lastRun"] = entry?.LastRunAt?.ToString("O"),
                    ["lastSucceeded"] = entry?.LastSucceeded
                };
                PluginScriptResult result;
                try
                {
                    result = await _run(plugin, task, argument, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    result = new PluginScriptResult(false, null, exception.Message);
                }
                var failures = result.Succeeded ? 0 : (entry?.ConsecutiveFailures ?? 0) + 1;
                state[plugin.Id] = new BackgroundState(
                    now,
                    result.Succeeded,
                    result.Error,
                    failures,
                    plugin.Manifest.Version);
                SaveState(state);
                ran++;
                Completed?.Invoke(plugin, result);
            }
            return ran;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Due(string pluginId, PluginBackgroundTask task, BackgroundState? entry, DateTimeOffset now)
    {
        // A plugin that keeps failing is parked until it is updated, so a
        // broken script does not burn CPU every interval forever.
        if (entry is { ConsecutiveFailures: >= MaximumConsecutiveFailures }) return null;
        if (!_startedThisSession.Contains(pluginId) && task.RunOnStartup) return "startup";
        if (task.IntervalMinutes <= 0) return null;
        var last = entry?.LastRunAt;
        return last is null || now - last.Value >= TimeSpan.FromMinutes(task.IntervalMinutes) ? "interval" : null;
    }

    private Dictionary<string, BackgroundState> LoadState()
    {
        if (_state is not null) return _state;
        try
        {
            if (File.Exists(_statePath))
                _state = JsonSerializer.Deserialize<Dictionary<string, BackgroundState>>(File.ReadAllText(_statePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        _state ??= new Dictionary<string, BackgroundState>(StringComparer.Ordinal);
        // An updated plugin gets a fresh start after earlier failures.
        foreach (var plugin in _plugins())
        {
            if (_state.TryGetValue(plugin.Id, out var entry) && entry.Version != plugin.Manifest.Version)
                _state[plugin.Id] = entry with { ConsecutiveFailures = 0, Version = plugin.Manifest.Version };
        }
        return _state;
    }

    private void SaveState(Dictionary<string, BackgroundState> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed record BackgroundState(
        DateTimeOffset? LastRunAt,
        bool LastSucceeded,
        string? LastError,
        int ConsecutiveFailures,
        string? Version);
}
