using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

/// <summary>
/// S3 or WebDAV connection settings. It edits the same protected settings file as the
/// desktop app and runs the same sync, including deletion confirmation, so a
/// phone joins an existing bucket as just another device.
/// </summary>
public partial class SyncSettingsPage : UserControl, IShellPage
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private S3SyncStoredSettings? _stored;
    // Includes imported fields that have no mobile form control, without saving the draft.
    private S3SyncSettings? _draftSettings;
    private SyncProvider _provider;
    private IInputPane? _inputPane;
    private bool _busy;

    /// <summary>Design-time only.</summary>
    public SyncSettingsPage() => InitializeComponent();

    public SyncSettingsPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        Focusable = true;
        AddHandler(GotFocusEvent, OnFieldFocused);
        AddHandler(PointerPressedEvent, OnPagePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        _inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_inputPane is not null) _inputPane.StateChanged += OnInputPaneChanged;
        try
        {
            await _modules!.Library.Books.InitializeAsync(_lifetime.Token);
            await _shell!.EnsureReaderDataAsync();
            _stored = await _modules.Sync.Service.LoadSettingsAsync(_lifetime.Token);
            _draftSettings = await ApplyAccountCloudSyncDefaultAsync(_stored.Settings);
            Present(_draftSettings);
            await PresentAccountAsync();
            await PresentLastSyncAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("读取同步设置失败：" + UiText.Localize(exception.Message));
        }
    }

    private void Present(S3SyncSettings settings)
    {
        _provider = settings.Provider;
        PresentProvider();
        WebDavEndpointBox.Text = settings.WebDavEndpoint;
        WebDavUserBox.Text = settings.WebDavUsername;
        WebDavPasswordBox.Text = settings.WebDavPassword;
        EnabledSwitch.IsChecked = settings.Enabled;
        AutomaticSwitch.IsChecked = settings.AutomaticSyncEnabled;
        EndpointBox.Text = settings.Endpoint;
        BucketBox.Text = settings.Bucket;
        RegionBox.Text = settings.Region;
        AccessKeyBox.Text = settings.AccessKey;
        SecretKeyBox.Text = settings.SecretKey;
        PrefixBox.Text = settings.Prefix;
        PathStyleSwitch.IsChecked = settings.PathStyle;
        DownloadFilesSwitch.IsChecked = settings.DownloadBookFilesOnSync;
        SkipTlsSwitch.IsChecked = settings.SkipTlsVerify;
        EncryptionKeyBox.Text = settings.EncryptionKey;
    }

    /// <summary>Fields without a control retain their current draft values.</summary>
    private S3SyncSettings ReadSettings() => S3SyncSettings.Normalize(_draftSettings! with
    {
        Provider = _provider,
        WebDavEndpoint = WebDavEndpointBox.Text ?? string.Empty,
        WebDavUsername = WebDavUserBox.Text ?? string.Empty,
        WebDavPassword = WebDavPasswordBox.Text ?? string.Empty,
        Enabled = EnabledSwitch.IsChecked == true,
        AutomaticSyncEnabled = AutomaticSwitch.IsChecked == true,
        Endpoint = EndpointBox.Text ?? string.Empty,
        Bucket = BucketBox.Text ?? string.Empty,
        Region = RegionBox.Text ?? string.Empty,
        AccessKey = AccessKeyBox.Text ?? string.Empty,
        SecretKey = SecretKeyBox.Text ?? string.Empty,
        Prefix = PrefixBox.Text ?? string.Empty,
        PathStyle = PathStyleSwitch.IsChecked == true,
        DownloadBookFilesOnSync = DownloadFilesSwitch.IsChecked == true,
        SkipTlsVerify = SkipTlsSwitch.IsChecked == true,
        EncryptionKey = EncryptionKeyBox.Text ?? string.Empty
    });

    private async Task<S3SyncSettings?> PrepareSettingsAsync()
    {
        try
        {
            var settings = ReadSettings() with { AccountRegion = _modules!.DataRegion };
            return settings.Provider == SyncProvider.AccountServer
                ? await _modules.Accounts.Service.CreateSyncSettingsAsync(settings, _lifetime.Token)
                : settings;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ShowStatus(UiText.Localize(exception.Message)); return null; }
    }

    private async Task PresentAccountAsync()
    {
        var account = await _modules!.Accounts.Service.GetStatusAsync(cancellationToken: _lifetime.Token);
        AccountSummaryText.Text = AccountRegions.DisplayName(account.Region) + " · " + (account.User?.Username ?? "请先登录")
            + (account.RequiresDataRestart ? "\n区域已切换，请重新打开应用后同步。" : "");
    }

    private async Task PresentLastSyncAsync()
    {
        if (_stored is not { } stored) return;
        if (!stored.Settings.IsConfigured)
        {
            LastSyncText.Text = "填写连接信息后可与其他设备同步";
            return;
        }
        var summary = await _modules!.Sync.Service.GetStatusAsync(stored.DeviceId, stored.Settings, _lifetime.Token);
        if (summary.LastSyncAt is not { } last)
        {
            LastSyncText.Text = "尚未同步";
            return;
        }
        var pending = summary.PendingFileDownloadCount > 0
            ? $" · {summary.PendingFileDownloadCount} 个文件待下载"
            : string.Empty;
        LastSyncText.Text = $"上次同步 {last.LocalDateTime:yyyy/MM/dd HH:mm} · {summary.SnapshotBookCount} 本{pending}";
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = !string.IsNullOrEmpty(message);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SyncNowButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        FormPanel.IsHitTestVisible = !busy;
    }

    private async Task<bool> SaveAsync(S3SyncSettings settings)
    {
        // Saving this form records the user's explicit provider choice.
        settings = settings with { ProviderChosenByUser = true };
        await _modules!.Sync.Service.SaveSettingsAsync(_stored!.DeviceId, settings, _lifetime.Token);
        _stored = new S3SyncStoredSettings(_stored.DeviceId, settings);
        _draftSettings = settings;
        return true;
    }

    /// <summary>
    /// A Plus account defaults to Kkindle cloud sync until the user saves a
    /// provider of their own. Only the form is prefilled; the saved settings
    /// change when the user saves or syncs.
    /// </summary>
    private async Task<S3SyncSettings> ApplyAccountCloudSyncDefaultAsync(S3SyncSettings settings)
    {
        if (settings.Provider == SyncProvider.AccountServer || settings.ProviderChosenByUser) return settings;
        try
        {
            var account = await _modules!.Accounts.Service.GetStatusAsync(validateSession: true, _lifetime.Token);
            if (!account.IsLoggedIn || account.RequiresDataRestart || account.Region != _modules.DataRegion)
                return settings;
            var member = await _modules.Accounts.Membership.RefreshAsync(_lifetime.Token);
            return member.CanUseAccountCloudSync ? settings with { Provider = SyncProvider.AccountServer } : settings;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Offline or membership unavailable: keep the saved provider.
            return settings;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _busy) return;
        SetBusy(true);
        var settings = await PrepareSettingsAsync();
        SetBusy(false);
        if (settings is null) return;
        // Like the desktop app, a disabled draft may be saved incomplete.
        if (settings.Enabled && settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }

        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) { ShowStatus("正在恢复数据，请稍后重试。"); return; }
        SetBusy(true);
        try
        {
            await SaveAsync(settings);
            ShowStatus(null);
            _shell?.ShowToast("已保存");
            await PresentLastSyncAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("保存失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnImportProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _shell is null || _busy) return;
        SetBusy(true);
        IReadOnlyList<string> files = [];
        try
        {
            files = await _shell.Services.ImportSource.PickFilesAsync(_lifetime.Token);
            if (files.Count == 0) return;
            if (files.Count != 1)
            {
                ShowStatus("请选择一个同步参数文件。");
                return;
            }

            var imported = await SyncConnectionProfileService.ImportAsync(files[0], ReadSettings(), _lifetime.Token);
            _draftSettings = imported;
            Present(imported);
            ShowStatus("参数已导入，请检查后点击保存。同步加密密钥保持本机原值。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("导入参数失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            try { _shell.Services.ImportSource.ReleaseFiles(files); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ShowStatus("清理临时文件失败：" + UiText.Localize(exception.Message));
            }
            SetBusy(false);
        }
    }

    private async void OnExportProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _shell is null || _busy) return;
        var settings = ReadSettings();
        SetBusy(true);
        string? directory = null;
        try
        {
            if (!await _shell.ConfirmAsync("导出连接参数", "参数文件会明文包含当前服务的账号和密码，请只保存或分享给信任的位置。同步加密密钥不会导出。", "继续导出"))
                return;
            _lifetime.Token.ThrowIfCancellationRequested();

            var extension = SyncConnectionProfileService.FileExtension(settings.Provider);
            var name = $"Kkindle-{(settings.Provider == SyncProvider.WebDav ? "WebDAV" : "S3")}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
            var share = _shell.Services.FileShare;
            IStorageFile? destination = null;
            if (share is null)
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage is null || !storage.CanSave)
                    throw new InvalidOperationException("此设备不支持保存参数文件。");
                destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "导出同步参数",
                    SuggestedFileName = name,
                    FileTypeChoices = [new FilePickerFileType("Kkindle 同步参数") { Patterns = [$"*{extension}"] }]
                });
                if (destination is null) return;
            }

            directory = Path.Combine(Path.GetTempPath(), "kkindle-sync-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            await SyncConnectionProfileService.ExportAsync(path, settings, _lifetime.Token);
            if (share is not null)
                ShowStatus(await share.ShareFileAsync(path) ? "参数文件已导出。" : "已取消导出参数。");
            else
            {
                await using var input = File.OpenRead(path);
                await using var output = await destination!.OpenWriteAsync();
                await input.CopyToAsync(output, _lifetime.Token);
                ShowStatus("参数文件已导出。");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("导出参数失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    ShowStatus("含凭据的临时文件清理失败：" + UiText.Localize(exception.Message));
                }
            }
            SetBusy(false);
        }
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _busy) return;
        SetBusy(true);
        var settings = await PrepareSettingsAsync();
        SetBusy(false);
        if (settings is null) return;
        if (settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }

        SetBusy(true);
        ShowStatus("正在测试连接…");
        try
        {
            await _modules!.Sync.Service.TestConnectionAsync(settings, _lifetime.Token);
            ShowStatus("连接成功");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("连接失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSyncNowClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _busy) return;
        SetBusy(true);
        var settings = await PrepareSettingsAsync();
        SetBusy(false);
        if (settings is null) return;
        settings = settings with { Enabled = true };
        if (settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }

        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) { ShowStatus("正在恢复数据，请稍后重试。"); return; }
        SetBusy(true);
        ShowStatus("正在同步…");
        try
        {
            await SaveAsync(settings);
            EnabledSwitch.IsChecked = true;
            var progress = new Progress<string>(message => ShowStatus(UiText.Localize(message)));
            var result = await _shell!.Sync.RunAsync(
                _stored!,
                progress,
                (exception, _) => _shell.ConfirmAsync(
                    "确认同步删除",
                    UiText.Localize(exception.Message) + "\n\n继续后，这些删除会同步到其他设备。",
                    "同步这些删除"),
                _lifetime.Token,
                operation);

            ShowStatus(result is null ? "已取消，未同步删除。" : DescribeResult(result));
            await PresentLastSyncAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("同步失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string DescribeResult(S3SyncResult result)
    {
        var status = result.IsPartial
            ? $"同步未完成：新增 {result.BooksAdded} 本书，下载 {result.FilesDownloaded} 个文件；部分数据未能同步，请重试。"
            : result.Changed
                ? $"同步完成：新增 {result.BooksAdded} 本书，下载 {result.FilesDownloaded} 个文件，应用 {result.AnnotationsApplied} 条批注。"
                : "同步完成，当前已是最新。";
        return string.IsNullOrWhiteSpace(result.Warning) ? status : $"{status} {UiText.Localize(result.Warning)}";
    }

    private void PresentProvider()
    {
        var webDav = _provider == SyncProvider.WebDav;
        var account = _provider == SyncProvider.AccountServer;
        ProviderS3.Classes.Set("on", _provider == SyncProvider.S3);
        ProviderWebDav.Classes.Set("on", webDav);
        ProviderAccount.Classes.Set("on", account);
        S3Fields.IsVisible = _provider == SyncProvider.S3;
        WebDavFields.IsVisible = webDav;
        AccountFields.IsVisible = account;
        PathStyleRow.IsVisible = _provider == SyncProvider.S3;
        ExportProfileButton.IsEnabled = !account;
    }

    private async void OnProviderClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _provider = sender is Button { Name: nameof(ProviderAccount) } ? SyncProvider.AccountServer
            : sender is Button { Name: nameof(ProviderWebDav) } ? SyncProvider.WebDav : SyncProvider.S3;
        PresentProvider();
        if (_modules is not null)
        {
            try { await PresentAccountAsync(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        }
    }

    private void OnAccountClick(object? sender, RoutedEventArgs e) => _shell?.OpenAccount();

    private void OnAdvancedClick(object? sender, RoutedEventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
        AdvancedButton.Content = AdvancedPanel.IsVisible ? "收起高级选项" : "高级选项";
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();

    /// <summary>
    /// The viewport itself must end at the keyboard: with only extra content
    /// padding, a covered field still counts as in view and nothing scrolls.
    /// </summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var open = e.NewState == InputPaneState.Open;
        var covered = open ? Math.Max(0, e.EndRect.Height - ActionBar.Bounds.Height) : 0;
        FormScroller.Margin = new Thickness(0, 0, 0, covered);
        if (open) RevealFocusedField();
    }

    private void OnFieldFocused(object? sender, FocusChangedEventArgs e) => RevealFocusedField();

    private void RevealFocusedField()
    {
        // After the layout pass that applies the new viewport.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { Parent: Control field })
                field.BringIntoView();
        }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Otherwise a field stays focused and any tap reopens the keyboard.</summary>
    private void OnPagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is null)
            Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_inputPane is not null) _inputPane.StateChanged -= OnInputPaneChanged;
        _inputPane = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Leaving cancels a running sync; the next sync resumes from the saved baseline.</summary>
    public void OnClosed() => _lifetime.Cancel();
}
