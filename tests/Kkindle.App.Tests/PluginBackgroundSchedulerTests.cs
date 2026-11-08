using System.Text.Json.Nodes;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;
using Xunit;

namespace Kkindle.Tests;

public sealed class PluginBackgroundSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kkindle-bg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private InstalledPlugin Install(string version, int interval, bool onStartup)
    {
        var folder = Path.Combine(_root, "source-" + version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
            {
              "id": "com.example.background",
              "name": "后台",
              "version": "{{version}}",
              "apiVersion": 1,
              "contributes": { "background": { "script": "bg.js", "intervalMinutes": {{interval}}, "onStartup": {{(onStartup ? "true" : "false")}} } }
            }
            """);
        File.WriteAllText(Path.Combine(folder, "bg.js"), "kkindle.register.background(function () { return 1; });");
        var paths = new AppPaths(Path.Combine(_root, "app"));
        return new PluginManager(paths).InstallFromDirectory(folder);
    }

    [Fact]
    public async Task RunsOnStartupThenOnTheIntervalAndPersistsState()
    {
        var plugin = Install("1.0.0", 30, onStartup: true);
        var now = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var reasons = new List<string>();
        var statePath = Path.Combine(_root, "state.json");
        PluginBackgroundScheduler Create() => new(
            statePath,
            () => [plugin],
            (_, _, argument, _) =>
            {
                reasons.Add(argument["reason"]!.GetValue<string>());
                return Task.FromResult(new PluginScriptResult(true, JsonValue.Create(1), null));
            },
            () => now);

        var scheduler = Create();
        Assert.Equal(1, await scheduler.RunDueAsync(CancellationToken.None));
        Assert.Equal(0, await scheduler.RunDueAsync(CancellationToken.None));
        now = now.AddMinutes(31);
        Assert.Equal(1, await scheduler.RunDueAsync(CancellationToken.None));
        Assert.Equal(["startup", "interval"], reasons);

        // A new session runs the startup pass again; the interval state survives.
        reasons.Clear();
        var next = Create();
        Assert.Equal(1, await next.RunDueAsync(CancellationToken.None));
        Assert.Equal(0, await next.RunDueAsync(CancellationToken.None));
        Assert.Equal(["startup"], reasons);
    }

    [Fact]
    public async Task RepeatedFailuresParkThePluginUntilItIsUpdated()
    {
        var plugin = Install("1.0.0", 15, onStartup: false);
        var now = DateTimeOffset.UtcNow;
        var runs = 0;
        var statePath = Path.Combine(_root, "state.json");
        PluginBackgroundScheduler Create(InstalledPlugin target) => new(
            statePath,
            () => [target],
            (_, _, _, _) =>
            {
                runs++;
                return Task.FromResult(new PluginScriptResult(false, null, "boom"));
            },
            () => now);

        var scheduler = Create(plugin);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await scheduler.RunDueAsync(CancellationToken.None);
            now = now.AddMinutes(16);
        }
        Assert.Equal(5, runs);

        var updated = Install("1.0.1", 15, onStartup: false);
        await Create(updated).RunDueAsync(CancellationToken.None);
        Assert.Equal(6, runs);
    }
}
