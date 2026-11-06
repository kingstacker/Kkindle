using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle;

/// <summary>
/// Hosts one plugin page in a native web view. The page is composed into a
/// single self-contained document, may not navigate anywhere, and talks to
/// the app only through <see cref="PluginBridge"/>.
/// </summary>
internal sealed class PluginWindow : Window, IPluginUiHost
{
    private readonly InstalledPlugin _plugin;
    private readonly string _pagePath;
    private readonly PluginPageContext _context;
    private PluginTheme _theme;
    private readonly PluginBridge _bridge;
    private readonly Func<bool> _networkEnabled;
    private readonly bool _enableDevTools;
    private readonly string _browserDataDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeWebView _view = new();
    private readonly TextBlock _statusText = new();
    private readonly Border _notice = new();
    private readonly TextBlock _noticeText = new();
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TextBox _logBox = new();
    private readonly StringBuilder _log = new();
    private const int MaximumLogCharacters = 60_000;
    private readonly string _baseTitle;
    private bool _documentLoaded;
    private bool _closed;
    private bool _sessionImportsApproved;
    private bool _confirmingTaskClose;
    private bool _closeApproved;

    public PluginWindow(
        InstalledPlugin plugin,
        string pagePath,
        string? pageTitle,
        int width,
        int height,
        PluginPageContext context,
        PluginTheme theme,
        IBookLibraryService library,
        AppPaths paths,
        PluginStorage storage,
        ISecretProtector secretProtector,
        Func<bool> networkEnabled,
        bool enableDevTools,
        IPluginBookTaskHost? bookTasks = null,
        ReaderDataService? readerData = null)
    {
        _plugin = plugin;
        _pagePath = pagePath;
        _context = context;
        _theme = theme;
        _networkEnabled = networkEnabled;
        _enableDevTools = enableDevTools;
        _browserDataDirectory = Path.Combine(paths.BrowserData, "plugins");
        var dataDirectory = Path.Combine(paths.PluginData, plugin.Id);
        _bridge = new PluginBridge(
            plugin,
            context,
            library,
            paths,
            storage,
            this,
            plugin.Manifest.UsesSecrets ? new PluginSecrets(dataDirectory, secretProtector) : null,
            bookTasks: bookTasks,
            readerData: readerData);

        _baseTitle = string.IsNullOrWhiteSpace(pageTitle) || pageTitle == plugin.Manifest.Name
            ? plugin.Manifest.Name
            : $"{plugin.Manifest.Name} · {pageTitle}";
        Title = _baseTitle;
        Width = width;
        Height = height;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse(theme.Paper));

        _statusText.Text = UiText.Get("正在加载插件…");
        _statusText.HorizontalAlignment = HorizontalAlignment.Center;
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.Margin = new Thickness(24);
        _statusText.Foreground = new SolidColorBrush(Color.Parse(theme.MutedInk));

        _noticeText.TextWrapping = TextWrapping.Wrap;
        _noticeText.Foreground = new SolidColorBrush(Color.Parse(theme.Paper));
        _notice.Child = _noticeText;
        _notice.Background = new SolidColorBrush(Color.Parse(theme.Ink));
        _notice.CornerRadius = new CornerRadius(6);
        _notice.Padding = new Thickness(14, 8);
        _notice.Margin = new Thickness(16);
        _notice.MaxWidth = 520;
        _notice.HorizontalAlignment = HorizontalAlignment.Center;
        _notice.VerticalAlignment = VerticalAlignment.Bottom;
        _notice.IsVisible = false;
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice.IsVisible = false;
        };

        var pageLayer = new Grid { Children = { _statusText, _view } };

        var toolbar = new Border
        {
            IsVisible = _enableDevTools,
            Background = new SolidColorBrush(Color.Parse(theme.Paper)),
            BorderBrush = new SolidColorBrush(Color.Parse(theme.MutedInk)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = BuildDeveloperToolbar()
        };
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(pageLayer, 1);
        Grid.SetRow(_notice, 1);

        _logBox.IsVisible = _enableDevTools;
        _logBox.IsReadOnly = true;
        _logBox.AcceptsReturn = true;
        _logBox.TextWrapping = TextWrapping.NoWrap;
        _logBox.FontFamily = new FontFamily("Menlo, Consolas, monospace");
        _logBox.FontSize = 11;
        _logBox.MinHeight = 80;
        _logBox.MaxHeight = 160;
        _logBox.Margin = new Thickness(0);
        _logBox.Background = Brushes.Transparent;
        _logBox.BorderThickness = new Thickness(0);
        var logPanel = new Border
        {
            IsVisible = _enableDevTools,
            Background = new SolidColorBrush(Color.Parse(theme.Paper)),
            BorderBrush = new SolidColorBrush(Color.Parse(theme.MutedInk)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4),
            Child = _logBox
        };
        Grid.SetRow(logPanel, 2);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { toolbar, pageLayer, logPanel, _notice }
        };
        _view.IsVisible = false;
        _view.EnvironmentRequested += View_EnvironmentRequested;
        _view.NavigationStarted += View_NavigationStarted;
        _view.NavigationCompleted += View_NavigationCompleted;
        _view.WebMessageReceived += View_WebMessageReceived;
        _view.NewWindowRequested += View_NewWindowRequested;
        Opened += (_, _) => Load();
        Closing += (_, e) =>
        {
            if (_closed || _closeApproved || !_bridge.HasRunningTask) return;
            // A running translation/pinyin job would be cancelled with the
            // window; ask first and keep the task alive when the user declines.
            e.Cancel = true;
            _ = ConfirmCloseWithRunningTaskAsync();
        };
    }

    private async Task ConfirmCloseWithRunningTaskAsync()
    {
        if (_confirmingTaskClose) return;
        _confirmingTaskClose = true;
        try
        {
            var confirmed = await ShowPromptAsync(
                UiText.Get("任务正在进行"),
                UiText.Get("任务尚未完成，关闭窗口会取消任务。确定要关闭吗？"),
                UiText.Get("取消任务并关闭"),
                rememberText: null);
            if (!confirmed) return;
            _closeApproved = true;
            Close();
        }
        finally
        {
            _confirmingTaskClose = false;
        }
    }

    /// <summary>Raised on the UI thread after the page changed library books.</summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? BooksUpdated;

    /// <summary>Pushes an app event such as <c>library.changed</c> to the page.</summary>
    internal void RaiseHostEvent(string name, JsonNode payload) =>
        ((IPluginUiHost)this).RaiseEvent(name, payload);

    /// <summary>Re-applies the app palette without reloading the page.</summary>
    internal void ApplyTheme(PluginTheme theme)
    {
        _theme = theme;
        Background = new SolidColorBrush(Color.Parse(theme.Paper));
        var script = "(function () { var style = document.getElementById('kkindle-theme'); "
            + "if (style) style.textContent = " + JsonSerializer.Serialize(PluginPageComposer.BuildThemeCss(theme)) + "; "
            + "document.documentElement.setAttribute('data-kk-theme', "
            + (theme.IsDark ? "'dark'" : "'light'") + "); })();";
        Dispatcher.UIThread.Post(async () =>
        {
            if (_closed) return;
            try { await _view.InvokeScript(script); }
            catch (Exception) { }
        });
    }

    public string PluginId => _plugin.Id;

    bool IPluginUiHost.NetworkEnabled => _networkEnabled();

    void IPluginUiHost.Close() => Dispatcher.UIThread.Post(Close);

    void IPluginUiHost.Notify(string message) => Dispatcher.UIThread.Post(() =>
    {
        _noticeText.Text = message;
        _notice.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    });

    void IPluginUiHost.SetTitle(string title) => Dispatcher.UIThread.Post(() =>
        Title = string.IsNullOrWhiteSpace(title) ? _baseTitle : $"{_plugin.Manifest.Name} · {title}");

    void IPluginUiHost.OpenExternal(Uri uri) => Dispatcher.UIThread.Post(async () =>
    {
        try { await Launcher.LaunchUriAsync(uri); }
        catch { }
    });

    void IPluginUiHost.BooksChanged(IReadOnlyCollection<Guid> bookIds) =>
        Dispatcher.UIThread.Post(() => BooksUpdated?.Invoke(this, bookIds));

    async Task<bool> IPluginUiHost.RequestNetworkAccessAsync(string host)
    {
        return await ShowPromptAsync(
            UiText.Get("插件请求访问网络"),
            UiText.Get("插件“{0}”要访问网站 {1}。允许后，此插件以后访问该网站时不再询问。", _plugin.Manifest.Name, host),
            UiText.Get("允许"),
            rememberText: null);
    }

    async Task<bool> IPluginUiHost.ConfirmImportAsync(string fileName, long sizeBytes)
    {
        if (_sessionImportsApproved) return true;
        var size = sizeBytes > 0
            ? UiText.Get("{0} MB", (sizeBytes / (1024d * 1024d)).ToString("0.#"))
            : UiText.Get("大小未知");
        var remember = false;
        var accepted = await ShowPromptAsync(
            UiText.Get("导入书籍"),
            UiText.Get("插件“{0}”要下载“{1}”（{2}）并加入书库。", _plugin.Manifest.Name, fileName, size),
            UiText.Get("加入书库"),
            UiText.Get("本次窗口内不再询问"),
            value => remember = value);
        if (accepted && remember) _sessionImportsApproved = true;
        return accepted;
    }

    void IPluginUiHost.Log(string level, string message) =>
        Dispatcher.UIThread.Post(() => AppendLog(level, message));

    async Task<IReadOnlyList<PluginPickedFile>> IPluginUiHost.PickFilesAsync(
        bool allowMultiple,
        IReadOnlyList<string> extensions)
    {
        return await RunOnUiThreadAsync(async () =>
        {
            IReadOnlyList<FilePickerFileType>? filters = extensions.Count == 0
                ? null
                : [new FilePickerFileType(UiText.Get("文件")) { Patterns = extensions.Select(extension => "*" + extension).ToArray() }];
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiText.Get("选择文件"),
                AllowMultiple = allowMultiple,
                FileTypeFilter = filters
            });
            return (IReadOnlyList<PluginPickedFile>)files
                .Select(file => new PluginPickedFile(file.Name, file.TryGetLocalPath()))
                .ToArray();
        });
    }

    async Task<PluginPickedFile?> IPluginUiHost.SaveFileAsync(string suggestedName, byte[] content)
    {
        return await RunOnUiThreadAsync(async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiText.Get("保存文件"),
                SuggestedFileName = suggestedName,
                DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
                ShowOverwritePrompt = true
            });
            if (file is null) return null;
            await using (var stream = await file.OpenWriteAsync())
                await stream.WriteAsync(content);
            return new PluginPickedFile(file.Name, file.TryGetLocalPath());
        });
    }

    async Task IPluginUiHost.CopyTextAsync(string text)
    {
        await RunOnUiThreadAsync(async () =>
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException("剪贴板不可用。");
            await clipboard.SetTextAsync(text);
            return true;
        });
    }

    async Task<string?> IPluginUiHost.ReadTextAsync()
    {
        return await RunOnUiThreadAsync(() =>
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException("剪贴板不可用。");
            return clipboard.TryGetTextAsync();
        });
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    void IPluginUiHost.RaiseEvent(string name, JsonNode payload)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_closed) return;
            // Progress text is composed by the book engines, which run on
            // worker threads and only know Chinese. Translate it here, on the
            // UI thread, so the page follows the application language.
            if (name == "task.progress" && payload is JsonObject progress)
            {
                if (progress["stage"]?.GetValue<string>() is { } stage)
                    progress["stage"] = PluginTaskText.Stage(stage);
                if (progress["message"]?.GetValue<string>() is { } message)
                    progress["message"] = PluginTaskText.Detail(message);
            }
            var script = $"window.__kkindleBridge && window.__kkindleBridge.event({JsonSerializer.Serialize(name)}, {payload.ToJsonString()});";
            try { await _view.InvokeScript(script); }
            catch (Exception) { }
        });
    }

    /// <summary>Small modal confirmation owned by the plugin window.</summary>
    private async Task<bool> ShowPromptAsync(
        string title,
        string message,
        string acceptText,
        string? rememberText,
        Action<bool>? rememberChanged = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
                ShowPromptAsync(title, message, acceptText, rememberText, rememberChanged));
        }

        var ink = new SolidColorBrush(Color.Parse(_theme.Ink));
        var paper = new SolidColorBrush(Color.Parse(_theme.Paper));
        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(_theme.Ink))
        };
        var rememberBox = new CheckBox
        {
            Content = rememberText ?? string.Empty,
            IsVisible = !string.IsNullOrEmpty(rememberText),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(_theme.MutedInk))
        };
        var cancelButton = new Button { Content = UiText.Get("取消"), FontSize = 13 };
        var acceptButton = new Button
        {
            Content = acceptText,
            FontSize = 13,
            Background = ink,
            Foreground = paper,
            Padding = new Thickness(14, 6)
        };
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = paper,
            Content = new StackPanel
            {
                Margin = new Thickness(22, 18),
                Spacing = 14,
                Children =
                {
                    messageText,
                    rememberBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, acceptButton }
                    }
                }
            }
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        acceptButton.Click += (_, _) => dialog.Close(true);
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.Close(false);
        };
        dialog.Opened += (_, _) => acceptButton.Focus();
        var accepted = false;
        try
        {
            accepted = await dialog.ShowDialog<bool>(this);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        rememberChanged?.Invoke(rememberBox.IsChecked == true);
        return accepted;
    }

    /// <summary>Developer toolbar: reload without closing the window.</summary>
    private Control BuildDeveloperToolbar()
    {
        var reload = new Button { Content = UiText.Get("重新加载"), FontSize = 12, Padding = new Thickness(10, 4) };
        reload.Click += (_, _) => ReloadPage();
        var clear = new Button { Content = UiText.Get("清空日志"), FontSize = 12, Padding = new Thickness(10, 4) };
        clear.Click += (_, _) =>
        {
            _log.Clear();
            _logBox.Text = string.Empty;
        };
        var hint = new TextBlock
        {
            Text = UiText.Get("开发者模式：在页面内右键可打开开发者工具"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(_theme.MutedInk)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { reload, clear, hint }
        };
    }

    private void ReloadPage()
    {
        if (_closed) return;
        AppendLog("info", "Reloading plugin page.");
        _documentLoaded = false;
        _view.IsVisible = false;
        _statusText.IsVisible = true;
        _statusText.Text = UiText.Get("正在重新加载插件…");
        Load();
    }

    private void AppendLog(string level, string message)
    {
        if (!_enableDevTools || _closed) return;
        _log.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] [")
            .Append(level).Append("] ").Append(message).AppendLine();
        if (_log.Length > MaximumLogCharacters)
            _log.Remove(0, _log.Length - MaximumLogCharacters);
        _logBox.Text = _log.ToString();
        _logBox.CaretIndex = _logBox.Text.Length;
    }

    private void Load()
    {
        string html;
        try
        {
            html = PluginPageComposer.Compose(_plugin, _pagePath, _context, _theme);
        }
        catch (Exception exception) when (exception is PluginPackageException or IOException or UnauthorizedAccessException)
        {
            _statusText.Text = UiText.Get("无法打开插件页面：{0}", UiText.Localize(exception.Message));
            return;
        }

        try
        {
            _view.NavigateToString(html, new Uri($"https://{_plugin.Id}.plugin.kkindle.invalid/"));
        }
        catch (Exception exception)
        {
            _statusText.Text = UiText.Get("无法启动网页组件：{0}", UiText.Localize(exception.Message));
        }
    }

    private void View_EnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = _enableDevTools;
        try { Directory.CreateDirectory(_browserDataDirectory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = Path.Combine(_browserDataDirectory, "webview2");
                windows.ProfileName = "Plugins";
                windows.IsInPrivateModeEnabled = true;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
            case LinuxWpeWebViewEnvironmentRequestedEventArgs linux:
                linux.DataDirectory = Path.Combine(_browserDataDirectory, "wpe-data");
                linux.CacheDirectory = Path.Combine(_browserDataDirectory, "wpe-cache");
                linux.PreferWebKitGtkInstead = !KeepAliveNativeLibrary.TryLoad("libWPEWebKit-2.0.so.1");
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                break;
        }
    }

    private void View_NavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        // Only the composed document itself may load; links and scripted
        // navigation would leave the sandboxed page.
        if (!_documentLoaded) return;
        var request = e.Request;
        if (request is null || request.Scheme is "about" or "data") return;
        e.Cancel = true;
    }

    private void View_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_documentLoaded) return;
        _documentLoaded = true;
        if (e.IsSuccess)
        {
            _view.IsVisible = true;
            _statusText.IsVisible = false;
        }
        else
        {
            _statusText.Text = UiText.Get("插件页面加载失败。");
        }
    }

    private static void View_NewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e) => e.Handled = true;

    private async void View_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_closed) return;
        try
        {
            var reply = await _bridge.HandleAsync(e.Body, _lifetime.Token);
            if (reply is not { } value || _closed) return;
            await _view.InvokeScript($"window.__kkindleBridge && window.__kkindleBridge.reply({value.Id}, {value.Reply});");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closed)
        {
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _noticeTimer.Stop();
        _view.EnvironmentRequested -= View_EnvironmentRequested;
        _view.NavigationStarted -= View_NavigationStarted;
        _view.NavigationCompleted -= View_NavigationCompleted;
        _view.WebMessageReceived -= View_WebMessageReceived;
        _view.NewWindowRequested -= View_NewWindowRequested;
        try { _view.Stop(); } catch { }
        _bridge.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }
}
