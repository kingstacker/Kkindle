using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Runtime;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle;

/// <summary>Services a headless plugin script can use; all optional so tests can stub them.</summary>
internal sealed record PluginScriptServices(
    IBookLibraryService Library,
    AppPaths Paths,
    ISecretProtector? SecretProtector,
    IPluginUiHost Ui,
    ReaderDataService? ReaderData = null,
    IPluginContentHost? Content = null,
    IPluginAiHost? Ai = null,
    HttpMessageHandler? HttpHandler = null);

/// <summary>Outcome of one headless call: the handler's JSON result, or an error message.</summary>
internal sealed record PluginScriptResult(bool Succeeded, JsonNode? Value, string? Error);

/// <summary>
/// Runs a plugin's provider or background script in a Jint sandbox. Each
/// call gets a fresh engine with no CLR access, a statement time limit, a
/// memory cap and the same <c>window.kkindle</c> API as plugin pages; host
/// calls go through <see cref="PluginBridge"/>, so the manifest permissions
/// apply unchanged. Calls block on a worker thread, never the UI thread.
/// </summary>
internal sealed class PluginScriptRuntime
{
    public const long MemoryLimitBytes = 64L * 1024 * 1024;
    private const int MaximumScriptBytes = 2 * 1024 * 1024;
    private const int MaximumResultCharacters = 200_000;

    private readonly PluginScriptServices _services;

    public PluginScriptRuntime(PluginScriptServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Loads <paramref name="scriptPath"/>, then calls the handler registered
    /// as <paramref name="kind"/>:<paramref name="id"/> with <paramref name="arguments"/>.
    /// </summary>
    public Task<PluginScriptResult> InvokeAsync(
        InstalledPlugin plugin,
        string scriptPath,
        string kind,
        string id,
        JsonArray arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.Run(() => Invoke(plugin, scriptPath, kind, id, arguments, timeout, cancellationToken), cancellationToken);

    private PluginScriptResult Invoke(
        InstalledPlugin plugin,
        string scriptPath,
        string kind,
        string id,
        JsonArray arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string script;
        try
        {
            var path = plugin.ResolveFile(scriptPath);
            var info = new FileInfo(path);
            if (!info.Exists) return Failure($"插件缺少脚本文件“{scriptPath}”。");
            if (info.Length > MaximumScriptBytes) return Failure("插件脚本超过 2 MB。");
            script = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PluginPackageException)
        {
            return Failure(exception.Message);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        var token = lifetime.Token;
        var context = new PluginPageContext(
            plugin.Id,
            plugin.Manifest.Version,
            $"{kind}:{id}",
            [],
            UiText.CurrentLanguage,
            PluginHostInfo.AppVersion.ToString(3),
            Selection: null,
            // Headless scripts have no window; console output always goes
            // to the host log so failures can be diagnosed.
            DeveloperMode: true)
        {
            Headless = true
        };
        using var bridge = new PluginBridge(
            plugin,
            context,
            _services.Library,
            _services.Paths,
            new PluginStorage(Path.Combine(_services.Paths.PluginData, plugin.Id)),
            _services.Ui,
            plugin.Manifest.UsesSecrets && _services.SecretProtector is not null
                ? new PluginSecrets(Path.Combine(_services.Paths.PluginData, plugin.Id), _services.SecretProtector)
                : null,
            _services.HttpHandler,
            bookTasks: null,
            readerData: _services.ReaderData,
            content: _services.Content,
            ai: _services.Ai,
            reader: null);

        string? completion = null;
        var timers = new PriorityQueue<int, long>();
        var clock = Stopwatch.StartNew();
        var engine = new Engine(options =>
        {
            options.Strict = false;
            options.LimitMemory(MemoryLimitBytes);
            options.LimitRecursion(512);
            // Per-execution budget: each Invoke/Execute below must finish
            // within the remaining time; host calls count toward it.
            options.TimeoutInterval(timeout);
            options.CancellationToken(token);
        });
        engine.SetValue("__kkHostCall", new Func<string, string?>(message =>
        {
            var reply = bridge.HandleAsync(message, token).GetAwaiter().GetResult();
            return reply is { } value
                ? new JsonObject { ["id"] = value.Id, ["payload"] = JsonNode.Parse(value.Reply) }.ToJsonString()
                : null;
        }));
        engine.SetValue("__kkDone", new Action<string>(json => completion = json));
        engine.SetValue("__kkTimer", new Action<int, double>((timerId, delay) =>
            timers.Enqueue(timerId, clock.ElapsedMilliseconds + (long)Math.Clamp(double.IsNaN(delay) ? 0 : delay, 0, 60_000))));
        engine.SetValue("__kkAtob", new Func<string, string>(value =>
            Encoding.Latin1.GetString(Convert.FromBase64String(value))));
        engine.SetValue("__kkBtoa", new Func<string, string>(value =>
        {
            if (value.Any(character => character > 0xFF))
                throw new ArgumentException("btoa: 字符串包含 Latin-1 以外的字符。");
            return Convert.ToBase64String(Encoding.Latin1.GetBytes(value));
        }));

        try
        {
            engine.Execute(Prelude);
            engine.Execute(PluginPageComposer.BuildBridgeScript(context, PluginTheme.Light, plugin.Manifest));
            engine.Execute(script, scriptPath);
            engine.Invoke("__kkRun", $"{kind}:{id}", arguments.ToJsonString());
            while (completion is null)
            {
                token.ThrowIfCancellationRequested();
                engine.Advanced.ProcessTasks();
                if (completion is not null) break;
                if (!timers.TryDequeue(out var timerId, out var due))
                    return Failure("插件脚本没有返回结果（Promise 一直未完成）。");
                var wait = due - clock.ElapsedMilliseconds;
                if (wait > 0) token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(wait));
                token.ThrowIfCancellationRequested();
                engine.Invoke("__kkFire", timerId);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"插件脚本超过 {timeout.TotalSeconds:0} 秒未完成，已停止。");
        }
        catch (ExecutionCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"插件脚本超过 {timeout.TotalSeconds:0} 秒未完成，已停止。");
        }
        catch (TimeoutException)
        {
            return Failure($"插件脚本超过 {timeout.TotalSeconds:0} 秒未完成，已停止。");
        }
        catch (MemoryLimitExceededException)
        {
            return Failure("插件脚本占用内存过多，已停止。");
        }
        catch (RecursionDepthOverflowException)
        {
            return Failure("插件脚本递归过深，已停止。");
        }
        catch (JavaScriptException exception)
        {
            return Failure(Truncate($"插件脚本出错：{exception.Message}", 1000));
        }
        catch (Acornima.ParseErrorException exception)
        {
            return Failure(Truncate($"插件脚本语法错误：{exception.Message}", 1000));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return Failure(Truncate($"插件脚本出错：{exception.Message}", 1000));
        }

        if (completion.Length > MaximumResultCharacters * 2)
            return Failure("插件脚本返回的结果过大。");
        try
        {
            var outcome = JsonNode.Parse(completion) as JsonObject;
            if (outcome?["ok"]?.GetValue<bool>() == true)
                return new PluginScriptResult(true, outcome["value"]?.DeepClone(), null);
            return Failure(Truncate(outcome?["error"]?.GetValue<string>() ?? "插件脚本出错。", 1000));
        }
        catch (JsonException)
        {
            return Failure("插件脚本返回了无法识别的结果。");
        }
    }

    private static PluginScriptResult Failure(string message) => new(false, null, message);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    // Minimal browser-like globals the shared bridge template expects, plus
    // the runner the host calls. Timers are cooperative: callbacks run from
    // the host loop between promise jobs.
    private const string Prelude = """
        var window = globalThis;
        globalThis.window = globalThis;
        globalThis.self = globalThis;
        (function () {
          var timers = Object.create(null);
          var nextTimer = 0;
          globalThis.setTimeout = function (callback, delay) {
            var id = ++nextTimer;
            var args = Array.prototype.slice.call(arguments, 2);
            timers[id] = function () { callback.apply(null, args); };
            __kkTimer(id, Number(delay) || 0);
            return id;
          };
          globalThis.clearTimeout = function (id) { delete timers[id]; };
          globalThis.__kkFire = function (id) {
            var callback = timers[id];
            delete timers[id];
            if (callback) callback();
          };
          globalThis.atob = function (value) { return __kkAtob(String(value)); };
          globalThis.btoa = function (value) { return __kkBtoa(String(value)); };
          globalThis.addEventListener = function () { };
          globalThis.removeEventListener = function () { };
          globalThis.invokeCSharpAction = function (message) {
            var reply = __kkHostCall(message);
            if (!reply) return;
            var parsed = JSON.parse(reply);
            globalThis.__kkindleBridge.reply(parsed.id, parsed.payload);
          };
          var noop = function () { };
          globalThis.console = { log: noop, info: noop, warn: noop, error: noop, debug: noop };
          globalThis.__kkRun = function (key, argsJson) {
            var handler = globalThis.__kkindleBridge.handlers[key];
            if (typeof handler !== 'function') {
              __kkDone(JSON.stringify({ ok: false, error: 'Plugin script did not register "' + key + '".' }));
              return;
            }
            var settled = false;
            function finish(payload) {
              if (settled) return;
              settled = true;
              __kkDone(JSON.stringify(payload));
            }
            try {
              Promise.resolve(handler.apply(null, JSON.parse(argsJson))).then(
                function (value) { finish({ ok: true, value: value === undefined ? null : value }); },
                function (error) { finish({ ok: false, error: String((error && error.message) || error) }); });
            } catch (error) {
              finish({ ok: false, error: String((error && error.message) || error) });
            }
          };
        })();
        """;
}

/// <summary>
/// <see cref="IPluginUiHost"/> for scripts without a window. Anything that
/// would need a dialog is refused; notices and logs are forwarded.
/// </summary>
internal sealed class HeadlessPluginUiHost : IPluginUiHost
{
    private readonly Func<bool> _networkEnabled;
    private readonly Action<string> _notify;
    private readonly Action<IReadOnlyCollection<Guid>> _booksChanged;
    private readonly Action<string, string> _log;

    public HeadlessPluginUiHost(
        Func<bool> networkEnabled,
        Action<string> notify,
        Action<IReadOnlyCollection<Guid>> booksChanged,
        Action<string, string> log)
    {
        _networkEnabled = networkEnabled;
        _notify = notify;
        _booksChanged = booksChanged;
        _log = log;
    }

    public bool NetworkEnabled => _networkEnabled();
    public void Close() { }
    public void Notify(string message) => _notify(message);
    public void SetTitle(string title) { }
    public void OpenExternal(Uri uri) => throw new InvalidOperationException("后台脚本不能打开浏览器。");
    public void BooksChanged(IReadOnlyCollection<Guid> bookIds) => _booksChanged(bookIds);
    public Task<bool> RequestNetworkAccessAsync(string host) => Task.FromResult(false);
    public Task<bool> ConfirmImportAsync(string fileName, long sizeBytes) => Task.FromResult(false);
    public void RaiseEvent(string name, JsonNode payload) { }
    public void Log(string level, string message) => _log(level, message);

    public Task<IReadOnlyList<PluginPickedFile>> PickFilesAsync(bool allowMultiple, IReadOnlyList<string> extensions) =>
        throw new InvalidOperationException("后台脚本不能打开文件选择器。");

    public Task<PluginPickedFile?> SaveFileAsync(string suggestedName, byte[] content) =>
        throw new InvalidOperationException("后台脚本不能保存文件。");

    public Task CopyTextAsync(string text) => throw new InvalidOperationException("后台脚本不能访问剪贴板。");

    public Task<string?> ReadTextAsync() => throw new InvalidOperationException("后台脚本不能访问剪贴板。");
}
