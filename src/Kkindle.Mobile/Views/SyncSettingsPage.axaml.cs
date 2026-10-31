using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
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
public partial class SyncSettingsPage : UserControl
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private S3SyncStoredSettings? _stored;
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
        _inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_inputPane is not null) _inputPane.StateChanged += OnInputPaneChanged;
        try
        {
            await _modules!.Library.Books.InitializeAsync(_lifetime.Token);
            await _shell!.EnsureReaderDataAsync();
            _stored = await _modules.Sync.Service.LoadSettingsAsync(_lifetime.Token);
            Present(_stored.Settings);
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

    /// <summary>Fields without a control here (timeouts, WebDAV) keep their stored values.</summary>
    private S3SyncSettings ReadSettings() => S3SyncSettings.Normalize(_stored!.Settings with
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
        await _modules!.Sync.Service.SaveSettingsAsync(_stored!.DeviceId, settings, _lifetime.Token);
        _stored = new S3SyncStoredSettings(_stored.DeviceId, settings);
        return true;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _busy) return;
        var settings = ReadSettings();
        // Like the desktop app, a disabled draft may be saved incomplete.
        if (settings.Enabled && settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }

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

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (_stored is null || _busy) return;
        var settings = ReadSettings();
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
        var settings = ReadSettings() with { Enabled = true };
        if (settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }

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
                _lifetime.Token);

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
        ProviderS3.Classes.Set("on", !webDav);
        ProviderWebDav.Classes.Set("on", webDav);
        S3Fields.IsVisible = !webDav;
        WebDavFields.IsVisible = webDav;
        PathStyleRow.IsVisible = !webDav;
    }

    private void OnProviderClick(object? sender, RoutedEventArgs e)
    {
        _provider = sender is Button { Name: nameof(ProviderWebDav) } ? SyncProvider.WebDav : SyncProvider.S3;
        PresentProvider();
    }

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
        // Leaving cancels a running sync; the next sync resumes from the saved baseline.
        _lifetime.Cancel();
        base.OnDetachedFromVisualTree(e);
    }
}
