using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class SendToKindleWindow
{
    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromSeconds(40) };
    private NativeWebView? _webView;
    private nint _webViewHandle;
    private bool _adapterReady;
    private bool _pageLoading = true;
    private Uri? _currentUri;
    private DateTimeOffset _pageLoadedAt = DateTimeOffset.UtcNow;
    private TaskCompletionSource<bool>? _navigationCompleted;
    private string? _batchId;
    private int _navigationGeneration;
    private bool _nativeAuthenticationAvailable = !OperatingSystem.IsWindows();

    private void CreateBrowser(Uri? initialUri = null)
    {
        if (_closed) return;
        ReleaseBrowser();
        try
        {
            EnsureNetworkAllowed();
            Directory.CreateDirectory(_profileDirectory);
            var view = new NativeWebView();
            view.EnvironmentRequested += Browser_EnvironmentRequested;
            view.AdapterCreated += Browser_AdapterCreated;
            view.NavigationStarted += Browser_NavigationStarted;
            view.NavigationCompleted += Browser_NavigationCompleted;
            view.NewWindowRequested += Browser_NewWindowRequested;
            _webView = view;
            BrowserHost.Child = view;
            SetLoading();
            view.Navigate(initialUri ?? KindleWebNavigationPolicy.HomeUri);
        }
        catch
        {
            ReleaseBrowser();
            _navigationCompleted?.TrySetResult(false);
            ShowError("无法启动网页组件，请检查设置中的诊断页面，或使用系统浏览器。");
        }
    }

    private void Browser_EnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = Path.Combine(_profileDirectory, "webview2");
                windows.ProfileName = "SendToKindle";
                windows.IsInPrivateModeEnabled = false;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = false;
                apple.DataStoreIdentifier = new Guid(SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(_profileDirectory))).AsSpan(0, 16));
                break;
            case LinuxWpeWebViewEnvironmentRequestedEventArgs linux:
                linux.DataDirectory = Path.Combine(_profileDirectory, "wpe-data");
                linux.CacheDirectory = Path.Combine(_profileDirectory, "wpe-cache");
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = false;
                gtk.BaseDataDirectory = Path.Combine(_profileDirectory, "gtk-data");
                gtk.BaseCacheDirectory = Path.Combine(_profileDirectory, "gtk-cache");
                break;
        }
    }

    private void Browser_AdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        _adapterReady = true;
        if (e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle windows)
        {
            _webViewHandle = windows.CoreWebView2;
            try
            {
                _nativeAuthenticationAvailable = _fileInput is IKindleWebBrowserSettings settings
                    && settings.DisableCredentialSaving(_webViewHandle);
            }
            catch { _nativeAuthenticationAvailable = false; }
        }
    }

    private void Browser_NavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is null || e.Request.AbsoluteUri == "about:blank") return;
        if (!KindleWebNavigationPolicy.IsAllowed(e.Request) || !_networkAllowed())
        {
            e.Cancel = true;
            _navigationCompleted?.TrySetResult(false);
            SetStatus("此链接无法在发送窗口中打开，请使用系统浏览器。");
            return;
        }
        _currentUri = e.Request;
        _navigationGeneration++;
        _authenticationSnapshot = null;
        ClearNativeAuthenticationSecrets();
        // Authentication parameters must never enter application text or logs.
        AddressText.Text = e.Request.GetLeftPart(UriPartial.Path);
        SetLoading();
    }

    private void Browser_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_closed || e.Request?.AbsoluteUri == "about:blank") return;
        _loadingTimer.Stop();
        _pageLoading = false;
        _pageLoadedAt = DateTimeOffset.UtcNow;
        _unknownPageShown = false;
        _currentUri = e.Request ?? _currentUri;
        _navigationCompleted?.TrySetResult(e.IsSuccess);
        BackButton.IsEnabled = _webView?.CanGoBack == true;
        if (!e.IsSuccess)
        {
            ShowError("页面加载失败，请检查网络后重试，或在系统浏览器中打开。");
            return;
        }
        ErrorPanel.IsVisible = false;
        BrowserHost.IsVisible = true;
        if (!_busy) LoadingProgress.IsVisible = false;
        // The DOM probe chooses a native login step or the official fallback.
        // Navigation itself must not reveal the shopping/login page briefly.
        UpdateAuthenticationText();
    }

    private static bool IsSignInPage(Uri? uri) => uri?.AbsolutePath.Contains("/ap/", StringComparison.OrdinalIgnoreCase) == true
        || uri?.AbsolutePath.Contains("/ax/", StringComparison.OrdinalIgnoreCase) == true
        || uri?.AbsolutePath.Contains("validateCaptcha", StringComparison.OrdinalIgnoreCase) == true;

    private void Browser_NewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } uri) Dispatcher.UIThread.Post(() => Navigate(uri));
    }

    private void Navigate(Uri uri)
    {
        if (_closed) return;
        if (!KindleWebNavigationPolicy.IsAllowed(uri))
        {
            SetStatus("此链接无法在发送窗口中打开，请使用系统浏览器。");
            return;
        }
        try
        {
            EnsureNetworkAllowed();
            if (_webView is null) { CreateBrowser(uri); return; }
            SetLoading();
            _webView.Navigate(uri);
        }
        catch
        {
            _navigationCompleted?.TrySetResult(false);
            ShowError("页面加载失败，请检查网络后重试，或在系统浏览器中打开。");
        }
    }

    async Task IKindleWebPage.ResetAsync(CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed();
        _batchId = null;
        _lastSnapshot = null;
        _navigationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Navigate(KindleWebNavigationPolicy.HomeUri);
        if (!await _navigationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken))
            throw new IOException(UiText.Get("页面加载失败，请检查网络后重试，或在系统浏览器中打开。"));
    }

    async Task<KindleWebPageSnapshot> IKindleWebPage.ReadAsync(CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed();
        if (!_adapterReady || _pageLoading || _webView is null) return new();
        var generation = _navigationGeneration;
        if (KindleWebNavigationPolicy.IsAuthenticationPage(_currentUri))
        {
            if (!_nativeAuthenticationAvailable) return new() { Page = "signin", Authentication = new() };
            var authenticationJson = await _webView.InvokeScript(KindleWebAuthenticationScripts.Read)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (_closed || _pageLoading || generation != _navigationGeneration) return new();
            var authentication = JsonSerializer.Deserialize<KindleWebAuthenticationSnapshot>(authenticationJson ?? "{}", JsonSerializerOptions.Web);
            return new() { Page = "signin", Authentication = authentication ?? new() };
        }
        if (IsSignInPage(_currentUri)) return new() { Page = "signin", Authentication = new() };
        if (!KindleWebNavigationPolicy.IsUploadPage(_currentUri)) return new() { Page = "unknown" };
        var json = await _webView.InvokeScript(KindleWebPageScripts.Read)
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        if (_closed || _pageLoading || generation != _navigationGeneration) return new();
        var snapshot = JsonSerializer.Deserialize<KindleWebPageSnapshot>(json ?? "{}", JsonSerializerOptions.Web) ?? new();
        return snapshot.Page == "signin" ? snapshot with { Authentication = new() { Step = "start" } } : snapshot;
    }

    Task IKindleWebAuthentication.OpenSignInAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNetworkAllowed();
        Navigate(KindleWebNavigationPolicy.SignInUri);
        return Task.CompletedTask;
    }

    async Task<bool> IKindleWebAuthentication.ApplyAuthenticationAsync(KindleWebAuthenticationSnapshot snapshot,
        KindleWebAuthenticationAction action, string account, string secret, CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed || !_nativeAuthenticationAvailable || _webView is null || _pageLoading
            || !KindleWebNavigationPolicy.IsAuthenticationPage(_currentUri)) return false;
        var result = await _webView.InvokeScript(KindleWebAuthenticationScripts.Apply(snapshot, action, account, secret))
            .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        return string.Equals(result?.Trim(), "true", StringComparison.Ordinal);
    }

    async Task IKindleWebAuthentication.ClearAuthenticationFieldsAsync(CancellationToken cancellationToken)
    {
        if (_closed || _webView is null || _pageLoading || !KindleWebNavigationPolicy.IsAuthenticationPage(_currentUri)) return;
        await _webView.InvokeScript(KindleWebAuthenticationScripts.ClearSecrets)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    async Task IKindleWebPage.StageAsync(IReadOnlyList<KindleWebFile> files, CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed();
        if (_fileInput is null || _webViewHandle == nint.Zero)
            throw new InvalidOperationException(UiText.Get("此平台请在亚马逊网页中选择文件并发送。"));
        _batchId = Guid.NewGuid().ToString("N");
        if (!await ExecuteBooleanAsync(KindleWebPageScripts.Prepare(_batchId, files), cancellationToken))
            throw new InvalidOperationException(UiText.Get("网页未接受文件列表，请打开网页检查后重试。"));
        await _fileInput.SetFilesAsync(_webViewHandle, "kkindle-send-files",
            files.Select(file => file.FullPath).ToArray(), cancellationToken);
        if (!await ExecuteBooleanAsync(KindleWebPageScripts.Drop(_batchId), cancellationToken))
            throw new InvalidOperationException(UiText.Get("网页未接受文件列表，请打开网页检查后重试。"));
    }

    Task<bool> IKindleWebPage.SubmitAsync(CancellationToken cancellationToken) => _batchId is null
        ? Task.FromResult(false)
        : ExecuteBooleanAsync(KindleWebPageScripts.Submit(_batchId), cancellationToken);

    private async Task<bool> ExecuteBooleanAsync(string script, CancellationToken cancellationToken)
    {
        EnsureNetworkAllowed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed || _webView is null || _pageLoading || !KindleWebNavigationPolicy.IsUploadPage(_currentUri)) return false;
        var result = await _webView.InvokeScript(script).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        return string.Equals(result?.Trim(), "true", StringComparison.Ordinal);
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { EnsureNetworkAllowed(); _webView?.GoBack(); }
        catch { ShowError("页面加载失败，请检查网络后重试，或在系统浏览器中打开。"); }
    }

    private void HomeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_busy) Navigate(KindleWebNavigationPolicy.HomeUri);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_webView is null || !_adapterReady) { CreateBrowser(); return; }
        try { EnsureNetworkAllowed(); SetLoading(); _webView.Refresh(); }
        catch { ShowError("页面加载失败，请检查网络后重试，或在系统浏览器中打开。"); }
    }

    private void OpenInBrowserButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            EnsureNetworkAllowed();
            Process.Start(new ProcessStartInfo(KindleWebNavigationPolicy.HomeUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { SetStatus("无法打开系统浏览器，请手动访问 https://www.amazon.com/sendtokindle。"); }
    }

    private void SetLoading()
    {
        _pageLoading = true;
        _sessionReady = false;
        ErrorPanel.IsVisible = false;
        BrowserHost.IsVisible = true;
        LoadingProgress.IsVisible = true;
        LoadingProgress.IsIndeterminate = true;
        if (!_busy && !_batchFinished) SetStatus("正在检查亚马逊登录状态…");
        _loadingTimer.Stop();
        _loadingTimer.Start();
    }

    private void LoadingTimer_Tick(object? sender, EventArgs e)
    {
        _loadingTimer.Stop();
        if (!_adapterReady) ShowError("无法启动网页组件，请检查设置中的诊断页面，或使用系统浏览器。");
        else SetStatus("页面加载时间较长，可以刷新重试，或在系统浏览器中打开。");
    }

    private void ShowError(string message)
    {
        _sessionReady = false;
        _loadingTimer.Stop();
        LoadingProgress.IsVisible = false;
        BrowserHost.IsVisible = false;
        ErrorPanel.IsVisible = true;
        ErrorText.Text = UiText.Get(message);
        if (!_manualWeb && _authentication is not null)
        {
            _authenticationMessage = message;
            _authenticationSnapshot = null;
            ShowAuthentication();
        }
        else ShowBrowser(true);
        SetStatus(message);
    }

    private void ReleaseBrowser()
    {
        _adapterReady = false;
        _webViewHandle = nint.Zero;
        if (_webView is not { } view) return;
        view.EnvironmentRequested -= Browser_EnvironmentRequested;
        view.AdapterCreated -= Browser_AdapterCreated;
        view.NavigationStarted -= Browser_NavigationStarted;
        view.NavigationCompleted -= Browser_NavigationCompleted;
        view.NewWindowRequested -= Browser_NewWindowRequested;
        try { view.Stop(); } catch { }
        BrowserHost.Child = null;
        _webView = null;
    }
}
