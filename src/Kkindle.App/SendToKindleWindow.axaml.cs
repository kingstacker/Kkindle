using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class SendToKindleWindow : Window, IKindleWebPage, IKindleWebAuthentication
{
    private readonly string _profileDirectory;
    private readonly IKindleWebFileInput? _fileInput;
    private readonly Func<bool> _networkAllowed;
    private readonly IKindleWebPage _page;
    private readonly bool _useNativeBrowser;
    private readonly ObservableCollection<KindleWebQueueItem> _files = [];
    private readonly ObservableCollection<KindleWebRecentItem> _recentStatusItems = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _openedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _sendCancellation;
    private bool _closed;
    private bool _forceClose;
    private bool _busy;
    private bool _batchFinished;
    private bool _sessionReady;
    private bool _probeRunning;
    private bool _manualWeb;
    private bool _signInDisplayed;
    private bool _unknownPageShown;
    private string _status = "正在检查亚马逊登录状态…";
    private object?[] _statusArguments = [];
    private KindleWebPageSnapshot? _lastSnapshot;
    private string? _accountDisplay;
    private bool _recentStatusExpanded = true;
    private bool _lastBatchWasOneClick;

    public SendToKindleWindow() : this(new AppPaths()) { }

    public SendToKindleWindow(AppPaths paths, IKindleWebFileInput? fileInput = null,
        Func<bool>? networkAllowed = null, IKindleWebPage? page = null)
    {
        _profileDirectory = Path.Combine(paths.BrowserData, "send-to-kindle");
        _fileInput = fileInput;
        _networkAllowed = networkAllowed ?? (() => true);
        _page = page ?? this;
        _useNativeBrowser = page is null;
        _authentication = page is null ? this : page as IKindleWebAuthentication;
        InitializeComponent();
        FileList.ItemsSource = _files;
        RecentStatusList.ItemsSource = _recentStatusItems;
        QueuePanel.AddHandler(DragDrop.DragEnterEvent, Queue_DragOver, RoutingStrategies.Bubble, true);
        QueuePanel.AddHandler(DragDrop.DragOverEvent, Queue_DragOver, RoutingStrategies.Bubble, true);
        QueuePanel.AddHandler(DragDrop.DragLeaveEvent, Queue_DragLeave, RoutingStrategies.Bubble, true);
        QueuePanel.AddHandler(DragDrop.DropEvent, Queue_Drop, RoutingStrategies.Bubble, true);
        _probeTimer.Tick += ProbeTimer_Tick;
        _loadingTimer.Tick += LoadingTimer_Tick;
        UiText.LanguageChanged += LanguageChanged;
        Opened += (_, _) =>
        {
            if (_useNativeBrowser) CreateBrowser();
            _probeTimer.Start();
            _openedCompletion.TrySetResult(true);
        };
        Closing += (_, e) =>
        {
            if (!_busy || _forceClose) return;
            e.Cancel = true;
            SetStatus("正在发送，请等待结果或先停止等待。");
        };
        Closed += Window_Closed;
        UpdateText();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (_closed) return;
        if (_busy || _batchFinished)
        {
            ShowNotice(UiText.Get(_busy ? "正在发送，请稍后添加文件。" : "请先清空本次结果，再添加下一批文件。"));
            return;
        }
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var file = KindleWebFilePolicy.Inspect(path);
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (_files.Any(item => item.FullPath.Equals(file.FullPath, pathComparison))) continue;
                if (_files.Any(item => item.Name.Equals(file.Name, StringComparison.Ordinal)))
                {
                    errors.Add(UiText.Get("同名文件请分批发送：{0}", file.Name));
                    continue;
                }
                _files.Add(new KindleWebQueueItem(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                errors.Add($"{Path.GetFileName(path)}：{UiText.Localize(exception.Message)}");
            }
        }
        if (errors.Count > 0) ShowNotice(string.Join(Environment.NewLine, errors.Take(5)));
        UpdateText();
    }

    public bool CanAcceptOneClickSend => !_closed && !_busy
        && ((_files.Count == 0 && !_batchFinished)
            || (_batchFinished && _lastBatchWasOneClick
                && _files.All(item => item.State is "submitted" or "failed")));

    public async Task<bool> SendOneFileImmediatelyAsync(string path, CancellationToken cancellationToken)
    {
        if (!CanAcceptOneClickSend)
        {
            ShowNotice(UiText.Get("请先完成或清空当前 Send to Kindle Web 文件列表，再一键发送。"));
            return false;
        }

        await _openedCompletion.Task.WaitAsync(cancellationToken);
        if (!CanAcceptOneClickSend) return false;
        if (_batchFinished)
        {
            _files.Clear();
            _batchFinished = false;
            _lastBatchWasOneClick = false;
            _lastSnapshot = null;
            ShowNotice("");
            SetStatus(_sessionReady ? "登录状态有效，检查文件列表后点击发送。" : "正在检查亚马逊登录状态…");
            UpdateText();
        }

        AddFiles([path]);
        if (_files.Count != 1) return false;
        return await SendQueuedFilesAsync(
            requireSessionReady: false,
            oneClick: true,
            cancellationToken: cancellationToken);
    }

    public void ShowNotice(string message)
    {
        if (_closed) return;
        QueueNoticeText.Text = message;
        QueueNoticeText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    public void CloseForShutdown()
    {
        _forceClose = true;
        Close();
    }

    private async void ChooseFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _batchFinished) return;
        try
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiText.Get("选择要发送的文件"),
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Send to Kindle")
                {
                    Patterns = ["*.epub", "*.pdf", "*.doc", "*.docx", "*.txt", "*.rtf", "*.htm", "*.html",
                        "*.png", "*.gif", "*.jpg", "*.jpeg", "*.bmp"]
                }]
            });
            AddFiles(selected.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception exception) { ShowNotice(UiText.Localize(exception.Message)); }
    }

    private void Queue_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_busy && !_batchFinished && LibraryDropImportPolicy.CanAccept(e.DataTransfer)
            ? DragDropEffects.Copy : DragDropEffects.None;
        DropArea.Classes.Set("dragOver", e.DragEffects == DragDropEffects.Copy);
        e.Handled = true;
    }

    private void Queue_DragLeave(object? sender, DragEventArgs e) => DropArea.Classes.Remove("dragOver");

    private void Queue_Drop(object? sender, DragEventArgs e)
    {
        DropArea.Classes.Remove("dragOver");
        e.Handled = true;
        if (_busy || _batchFinished) return;
        try { AddFiles(LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer)); }
        catch (Exception exception) { ShowNotice(UiText.Localize(exception.Message)); }
    }

    private void RemoveFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_busy && !_batchFinished && sender is Control { DataContext: KindleWebQueueItem item })
            _files.Remove(item);
        UpdateText();
    }

    private void ClearFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _files.Clear();
        _batchFinished = false;
        _lastBatchWasOneClick = false;
        _lastSnapshot = null;
        ShowNotice("");
        SetStatus(_sessionReady ? "登录状态有效，检查文件列表后点击发送。" : "正在检查亚马逊登录状态…");
    }

    private async void SendFilesButton_Click(object? sender, RoutedEventArgs e) =>
        await SendQueuedFilesAsync(requireSessionReady: true, oneClick: false, cancellationToken: _lifetime.Token);

    private async Task<bool> SendQueuedFilesAsync(bool requireSessionReady, bool oneClick, CancellationToken cancellationToken)
    {
        if (_busy || _batchFinished || (requireSessionReady && !_sessionReady) || _files.Count == 0) return false;
        var workflow = new KindleWebSendWorkflow(_page);
        _lastBatchWasOneClick = oneClick;
        _busy = true;
        _manualWeb = false;
        ShowBrowser(false);
        ShowNotice("");
        _sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        foreach (var item in _files) item.State = "sending";
        UpdateText();
        var fullySubmitted = false;
        try
        {
            EnsureNetworkAllowed();
            var batch = _files.Select(item => KindleWebFilePolicy.Inspect(item.FullPath)).ToArray();
            var results = await workflow.SendAsync(batch, ReportProgress, _sendCancellation.Token);
            ApplyResults(results);
            var submitted = results.Count(file => file.Status == "submitted");
            fullySubmitted = submitted == results.Length;
            if (submitted == results.Length)
                SetStatus("已提交 {0} 个文件。亚马逊完成处理后，Kindle 联网即可同步。", submitted);
            else
            {
                SetStatus("已提交 {0} 个文件，其余文件请在亚马逊网页中核实。", submitted);
                ShowNotice(UiText.Get("结果未确认的文件不会自动重发，请先查看网页。"));
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                ApplyResults(_lastSnapshot?.Files ?? [], workflow.SubmissionAttempted ? "unknown" : "pending");
                SetStatus(workflow.SubmissionAttempted
                    ? "已停止等待，亚马逊可能仍在处理，请在网页中核实结果。"
                    : "已停止，尚未提交发送。");
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                ApplyResults(_lastSnapshot?.Files ?? [], workflow.SubmissionAttempted ? "unknown" : "pending");
                if (workflow.SubmissionAttempted)
                    SetStatus("提交结果未确认，请在亚马逊网页中核实，避免重复发送。");
                else
                    SetStatus("发送未开始，请检查网络或打开网页重试。");
                ShowNotice(UiText.Localize(exception.Message));
            }
        }
        finally
        {
            _busy = false;
            _batchFinished = workflow.SubmissionAttempted;
            _sendCancellation.Dispose();
            _sendCancellation = null;
            if (!_closed)
            {
                LoadingProgress.IsVisible = false;
                UpdateText();
            }
        }
        return fullySubmitted;
    }

    private void ReportProgress(string phase, KindleWebPageSnapshot? snapshot)
    {
        if (_closed) return;
        if (snapshot is not null)
        {
            _lastSnapshot = snapshot;
            ApplyAccount(snapshot);
            if (snapshot.Page is "ready" or "unknown") ApplyRecentFiles(snapshot.RecentFiles);
        }
        LoadingProgress.IsVisible = true;
        LoadingProgress.IsIndeterminate = phase != "sending" || snapshot is null || snapshot.Percentage <= 0;
        if (!LoadingProgress.IsIndeterminate) LoadingProgress.Value = Math.Clamp(snapshot!.Percentage, 0, 100);
        if (phase == "signin" || snapshot?.Page == "signin")
        {
            PresentAuthentication(snapshot ?? new() { Page = "signin" }, resumesSend: phase == "signin");
            if (phase == "sending") SetStatus("登录状态已失效，请登录后到网页核实发送结果。");
        }
        else
        {
            if (snapshot?.Page == "ready")
            {
                _sessionReady = true;
                CompleteAuthentication();
                if (!_manualWeb) ShowBrowser(false);
            }
            if (phase == "sending")
            {
                ApplyResults(snapshot?.Files ?? [], "sending");
                SetStatus("正在发送 {0} 个文件，请等待亚马逊返回结果…", _files.Count);
            }
            else SetStatus("正在准备文件…");
        }
    }

    private void ApplyResults(IEnumerable<KindleWebPageFile> results, string fallback = "unknown")
    {
        var byName = results.GroupBy(file => file.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Status, StringComparer.Ordinal);
        foreach (var item in _files)
            item.State = byName.TryGetValue(item.Name, out var state) && state is "submitted" or "failed"
                ? state : fallback;
    }

    private void ApplyAccount(KindleWebPageSnapshot snapshot)
    {
        var account = snapshot.Account?.Trim();
        if (snapshot.Page == "ready" && !string.IsNullOrWhiteSpace(account) && account.Length <= 320)
            _accountDisplay = account;
    }

    private void StopWaitingButton_Click(object? sender, RoutedEventArgs e) => _sendCancellation?.Cancel();

    private void AccountButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_authenticationBusy) return;
        if (!_sessionReady && _authentication is not null && _authenticationSnapshot?.Step != "web")
        {
            _manualWeb = false;
            _signInDisplayed = true;
            ShowAuthentication();
            if (_authenticationSnapshot?.Step == "start" && !_signInNavigationRequested)
                _ = OpenAuthenticationAsync();
            return;
        }
        _manualWeb = true;
        ShowBrowser(true);
    }

    private void ReturnToQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        _manualWeb = false;
        _signInDisplayed = true;
        ShowBrowser(false);
    }

    private void ShowBrowser(bool visible)
    {
        ClearNativeAuthenticationSecrets();
        AuthenticationPanel.IsVisible = false;
        BrowserPanel.IsVisible = visible;
        QueuePanel.IsVisible = !visible;
        ReturnToQueueButton.IsVisible = visible;
    }

    private async void ProbeTimer_Tick(object? sender, EventArgs e)
    {
        if (_closed || _busy || _probeRunning || _authenticationBusy) return;
        _probeRunning = true;
        try
        {
            var snapshot = await _page.ReadAsync(_lifetime.Token);
            if (_closed || _busy) return;
            ApplyAccount(snapshot);
            if (snapshot.Page is "ready" or "unknown") ApplyRecentFiles(snapshot.RecentFiles);
            _sessionReady = snapshot.Page == "ready";
            if (snapshot.Page == "signin")
            {
                PresentAuthentication(snapshot);
            }
            else if (_sessionReady)
            {
                CompleteAuthentication();
                _unknownPageShown = false;
                if (!_manualWeb && (_fileInput is not null || !_useNativeBrowser)) ShowBrowser(false);
                if (_useNativeBrowser && _fileInput is null)
                {
                    ShowBrowser(true);
                    SetStatus("此平台请在亚马逊网页中选择文件并发送。");
                }
                else if (!_batchFinished) SetStatus("登录状态有效，检查文件列表后点击发送。");
                LoadingProgress.IsVisible = false;
            }
            else if (snapshot.Page == "unknown" && !_unknownPageShown
                && DateTimeOffset.UtcNow - _pageLoadedAt > TimeSpan.FromSeconds(12))
            {
                _unknownPageShown = true;
                ShowBrowser(true);
                SetStatus("页面需要手动处理，请在网页中完成提示或刷新重试。");
            }
            UpdateText();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_closed && !_busy)
            {
                _sessionReady = false;
                SetStatus("无法检查登录状态，请检查网络或打开网页重试。");
            }
        }
        finally { _probeRunning = false; }
    }

    private void EnsureNetworkAllowed()
    {
        if (!_networkAllowed())
            throw new InvalidOperationException(UiText.Get("请在应用设置中允许网络功能后再使用 Send to Kindle。"));
    }

    private void SetStatus(string source, params object?[] args)
    {
        _status = source;
        _statusArguments = args;
        UpdateText();
    }

    private void RecentStatusHeaderButton_Click(object? sender, RoutedEventArgs e)
    {
        _recentStatusExpanded = !_recentStatusExpanded;
        UpdateRecentStatusVisibility();
    }

    private void ApplyRecentFiles(IEnumerable<KindleWebRecentFile>? recentFiles)
    {
        if (_closed) return;
        var latest = (recentFiles ?? [])
            .Where(file => file is not null && !string.IsNullOrWhiteSpace(file.Title))
            .Take(20)
            .ToArray();
        for (var index = 0; index < latest.Length; index++)
        {
            if (index < _recentStatusItems.Count)
                _recentStatusItems[index].Update(latest[index]);
            else
                _recentStatusItems.Add(new KindleWebRecentItem(latest[index]));
        }
        while (_recentStatusItems.Count > latest.Length)
            _recentStatusItems.RemoveAt(_recentStatusItems.Count - 1);
        UpdateRecentStatusVisibility();
    }

    private void UpdateRecentStatusVisibility()
    {
        var hasRecent = _recentStatusItems.Count > 0;
        RecentStatusPanel.IsVisible = _sessionReady || hasRecent;
        RecentStatusBody.IsVisible = _recentStatusExpanded;
        RecentStatusChevronDown.IsVisible = _recentStatusExpanded;
        RecentStatusChevronRight.IsVisible = !_recentStatusExpanded;
        RecentStatusColumnHeader.IsVisible = _recentStatusExpanded && hasRecent;
        RecentStatusRows.IsVisible = _recentStatusExpanded && hasRecent;
        RecentStatusEmptyText.IsVisible = _recentStatusExpanded && _sessionReady && !hasRecent;
    }

    private void UpdateText()
    {
        StatusText.Text = UiText.Get(_status, _statusArguments);
        QueueSummaryText.Text = UiText.Get("文件列表 · {0} 个", _files.Count);
        EmptyQueuePanel.IsVisible = _files.Count == 0;
        QueueFilesPanel.IsVisible = _files.Count > 0;
        ChooseFilesButton.IsEnabled = !_busy && !_batchFinished;
        ClearFilesButton.IsEnabled = !_busy && _files.Count > 0;
        SendFilesButton.IsEnabled = !_busy && !_batchFinished && _sessionReady && _files.Count > 0
            && (_fileInput is not null || !_useNativeBrowser);
        StopWaitingButton.IsVisible = _busy;
        BrowserNavigationButtons.IsEnabled = !_busy;
        AccountButton.Content = _sessionReady
            ? string.IsNullOrWhiteSpace(_accountDisplay) ? UiText.Get("已登录") : _accountDisplay
            : UiText.Get("未登录");
        ToolTip.SetTip(AccountButton, AccountButton.Content);
        AccountButton.IsEnabled = !_authenticationBusy;
        UpdateWindowChrome();
        UpdateRecentStatusVisibility();
        UpdateAuthenticationText();
        foreach (var item in _files)
        {
            item.CanRemove = !_busy && !_batchFinished;
            item.RefreshText();
        }
        foreach (var item in _recentStatusItems) item.RefreshText();
    }

    private void LanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed) UpdateText();
    });

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _openedCompletion.TrySetCanceled();
        ClearNativeAuthenticationSecrets(clearAccount: true);
        _lifetime.Cancel();
        _probeTimer.Stop();
        _probeTimer.Tick -= ProbeTimer_Tick;
        _loadingTimer.Stop();
        _loadingTimer.Tick -= LoadingTimer_Tick;
        UiText.LanguageChanged -= LanguageChanged;
        _navigationCompleted?.TrySetCanceled();
        ReleaseBrowser();
        _lifetime.Dispose();
    }
}

public sealed class KindleWebQueueItem(KindleWebFile file) : ObservableObject
{
    private string _state = "pending";
    private bool _canRemove = true;
    public string FullPath => file.FullPath;
    public string Name => file.Name;
    public string SizeText => file.Length < 1024 ? $"{file.Length} B"
        : file.Length < 1024 * 1024 ? $"{file.Length / 1024d:0.#} KB" : $"{file.Length / (1024d * 1024d):0.#} MB";
    public bool CanRemove { get => _canRemove; set => SetProperty(ref _canRemove, value); }
    public string State
    {
        get => _state;
        set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(StatusText)); }
    }
    public string StatusText => UiText.Get(_state switch
    {
        "sending" => "发送中…",
        "submitted" => "已提交亚马逊",
        "failed" => "发送失败",
        "unknown" => "结果待核实",
        _ => "待发送"
    });
    public void RefreshText() => OnPropertyChanged(nameof(StatusText));
}

public sealed class KindleWebRecentItem : ObservableObject
{
    private KindleWebRecentFile _file;

    public KindleWebRecentItem(KindleWebRecentFile file) => _file = file;

    public string SentText => LocalizeSent(_file.Sent);
    public string Title => string.IsNullOrWhiteSpace(_file.Title) ? "—" : _file.Title;
    public string FromText => string.IsNullOrWhiteSpace(_file.From) ? "—" : _file.From;
    public string StatusText => LocalizeStatus(_file.Status);
    public string StatusGlyph => StatusKind(_file.Status) switch
    {
        "success" => "✓",
        "processing" => "…",
        "failed" => "!",
        _ => "·"
    };

    public void Update(KindleWebRecentFile file)
    {
        if (_file == file) return;
        _file = file;
        RefreshText();
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(SentText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FromText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusGlyph));
    }

    private static string LocalizeSent(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Equals("Just now", StringComparison.OrdinalIgnoreCase)
            || text.Equals("刚刚", StringComparison.Ordinal))
            return UiText.IsEnglish ? "Just now" : "刚刚";
        return string.IsNullOrWhiteSpace(text) ? "—" : text;
    }

    private static string LocalizeStatus(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Contains("in library", StringComparison.OrdinalIgnoreCase)
            || text.Contains("在资料库", StringComparison.Ordinal)
            || text.Contains("已在资料库", StringComparison.Ordinal))
            return UiText.IsEnglish ? "In library" : "在资料库";
        if (text.Contains("processing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || text.Contains("处理中", StringComparison.Ordinal))
            return UiText.IsEnglish ? "Processing" : "处理中";
        if (text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("失败", StringComparison.Ordinal))
            return UiText.IsEnglish ? "Failed" : "失败";
        if (text.Contains("sent", StringComparison.OrdinalIgnoreCase)
            || text.Contains("success", StringComparison.OrdinalIgnoreCase)
            || text.Contains("已发送", StringComparison.Ordinal))
            return UiText.IsEnglish ? "Sent" : "已发送";
        return string.IsNullOrWhiteSpace(text)
            ? UiText.IsEnglish ? "Unknown" : "未知"
            : text;
    }

    private static string StatusKind(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Contains("in library", StringComparison.OrdinalIgnoreCase)
            || text.Contains("在资料库", StringComparison.Ordinal)
            || text.Contains("已在资料库", StringComparison.Ordinal)
            || text.Contains("sent", StringComparison.OrdinalIgnoreCase)
            || text.Contains("success", StringComparison.OrdinalIgnoreCase)
            || text.Contains("已发送", StringComparison.Ordinal))
            return "success";
        if (text.Contains("processing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || text.Contains("处理中", StringComparison.Ordinal))
            return "processing";
        if (text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("失败", StringComparison.Ordinal))
            return "failed";
        return "unknown";
    }
}
