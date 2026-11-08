using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;
using Xunit;

namespace Kkindle.Tests;

public sealed class PluginScriptRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kkindle-script-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private async Task<(PluginScriptRuntime Runtime, InstalledPlugin Plugin, List<(string Level, string Message)> Logs)> CreateAsync(
        string script,
        string permissions = "\"storage\": true")
    {
        var folder = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(folder, "providers"));
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), $$"""
            {
              "id": "com.example.script",
              "name": "脚本测试",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": { {{permissions}} },
              "contributes": {
                "dictionaries": [{ "id": "demo", "title": "示例词典", "script": "providers/main.js" }],
                "translators": [{ "id": "upper", "title": "大写", "script": "providers/main.js" }],
                "background": { "script": "providers/main.js", "intervalMinutes": 30 }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(folder, "providers", "main.js"), script);
        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureDirectories();
        var plugin = new PluginManager(paths).InstallFromDirectory(folder);
        var library = new SqliteBookLibraryService(paths, new BookMetadataService());
        await library.InitializeAsync();
        var logs = new List<(string, string)>();
        var ui = new HeadlessPluginUiHost(() => true, _ => { }, _ => { }, (level, message) => logs.Add((level, message)));
        return (new PluginScriptRuntime(new PluginScriptServices(library, paths, null, ui)), plugin, logs);
    }

    [Fact]
    public async Task DictionaryHandlerRunsAsyncCodeTimersAndStorage()
    {
        var (runtime, plugin, logs) = await CreateAsync("""
            kkindle.register.dictionary('demo', async function (term) {
              await kkindle.storage.set('last', term);
              await new Promise(function (resolve) { setTimeout(resolve, 5); });
              console.log('looked up', term);
              var saved = await kkindle.storage.get('last');
              return { entries: [{ headword: saved, senses: ['释义 ' + btoa('ab')] }],
                       headless: kkindle.context.headless,
                       hasAi: kkindle.has('ai.complete') };
            });
            """);

        var result = await runtime.InvokeAsync(
            plugin, "providers/main.js", "dictionary", "demo", new JsonArray("书"),
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("书", result.Value!["entries"]![0]!["headword"]!.GetValue<string>());
        Assert.Equal("释义 YWI=", result.Value["entries"]![0]!["senses"]![0]!.GetValue<string>());
        Assert.True(result.Value["headless"]!.GetValue<bool>());
        Assert.True(result.Value["hasAi"]!.GetValue<bool>());
        Assert.Contains(logs, log => log.Message.Contains("looked up 书"));
    }

    [Fact]
    public async Task PermissionsStillApplyAndErrorsAreReported()
    {
        var (runtime, plugin, _) = await CreateAsync("""
            kkindle.register.translator('upper', async function (text) {
              await kkindle.library.search({ query: text });
              return text.toUpperCase();
            });
            kkindle.register.background(function () { throw new Error('boom'); });
            """);

        var denied = await runtime.InvokeAsync(
            plugin, "providers/main.js", "translator", "upper", new JsonArray("abc", new JsonObject()),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(denied.Succeeded);
        Assert.Contains("整个书库", denied.Error);

        var thrown = await runtime.InvokeAsync(
            plugin, "providers/main.js", "background", "", new JsonArray(new JsonObject()),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(thrown.Succeeded);
        Assert.Contains("boom", thrown.Error);

        var missing = await runtime.InvokeAsync(
            plugin, "providers/main.js", "dictionary", "demo", new JsonArray("x"),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(missing.Succeeded);
        Assert.Contains("did not register", missing.Error);
    }

    [Fact]
    public async Task RunawayScriptsAreStopped()
    {
        var (runtime, plugin, _) = await CreateAsync("""
            kkindle.register.dictionary('demo', function () { while (true) { } });
            kkindle.register.translator('upper', function () { return new Promise(function () { }); });
            """);

        var loop = await runtime.InvokeAsync(
            plugin, "providers/main.js", "dictionary", "demo", new JsonArray("x"),
            TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.False(loop.Succeeded);
        Assert.Contains("秒未完成", loop.Error);

        var pending = await runtime.InvokeAsync(
            plugin, "providers/main.js", "translator", "upper", new JsonArray("x", new JsonObject()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(pending.Succeeded);
        Assert.Contains("没有返回结果", pending.Error);
    }

    [Fact]
    public async Task ScriptsCannotReachTheClr()
    {
        var (runtime, plugin, _) = await CreateAsync("""
            kkindle.register.dictionary('demo', function () {
              return [typeof System, typeof importNamespace, typeof require, typeof fetch, typeof XMLHttpRequest].join(',');
            });
            """);

        var result = await runtime.InvokeAsync(
            plugin, "providers/main.js", "dictionary", "demo", new JsonArray("x"),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("undefined,undefined,undefined,undefined,undefined", result.Value!.GetValue<string>());
    }
}
