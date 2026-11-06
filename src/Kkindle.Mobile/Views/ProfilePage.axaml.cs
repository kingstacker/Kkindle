using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Kkindle.Infrastructure;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public partial class ProfilePage : UserControl
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private const int DeveloperModeClicks = 5;
    private static readonly TimeSpan DeveloperModeClickWindow = TimeSpan.FromSeconds(3);
    private int _versionClicks;
    private DateTimeOffset _firstVersionClick;
    private bool _backupBusy;
    private bool _restartRequired;

    /// <summary>Design-time only.</summary>
    public ProfilePage() => InitializeComponent();

    public ProfilePage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        VersionText.Text = ApplicationVersion.GetDisplayVersion(typeof(MobileApp).Assembly);
        PresentPreferences();
        Loaded += OnLoaded;
    }

    private void PresentPreferences()
    {
        var preferences = _shell!.Preferences.Current;
        ThemeSystem.Classes.Set("on", preferences.ThemeMode == MobileThemeMode.System);
        ThemeLight.Classes.Set("on", preferences.ThemeMode == MobileThemeMode.Light);
        ThemeDark.Classes.Set("on", preferences.ThemeMode == MobileThemeMode.Dark);
    }

    // Also runs when a pushed page is popped, so both summaries stay current.
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules!.Library.Books.InitializeAsync();
            var books = await _modules.Library.Books.SearchAsync();
            var sync = await _modules.Sync.Service.LoadSettingsAsync();

            SyncSummaryText.Text = sync.Settings is { Enabled: true, IsConfigured: true } settings
                ? $"{settings.ProviderName} · 已开启"
                : "未开启";
            var ai = await _modules.Ai.Settings.LoadAsync();
            AiSummaryText.Text = ai.IsConfigured ? ai.ProviderDisplayName : "未设置";
            var kindle = await _modules.Integrations.KindleEmailSettings.LoadAsync();
            KindleSummaryText.Text = kindle.IsConfigured ? "已设置" : "未设置";
            StorageText.Text = MobileFormat.Size(books.Sum(book => book.Files.Sum(file => file.Size)));
        }
        catch (Exception exception)
        {
            _shell?.ShowToast("读取数据失败：" + exception.Message);
        }
    }

    /// <summary>Five quick taps on the version toggle developer mode, as on desktop.</summary>
    private void OnVersionClick(object? sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_versionClicks == 0 || now - _firstVersionClick > DeveloperModeClickWindow)
        {
            _versionClicks = 0;
            _firstVersionClick = now;
        }
        if (++_versionClicks < DeveloperModeClicks) return;
        _versionClicks = 0;
        _shell?.ToggleDeveloperMode();
    }

    private void OnSyncClick(object? sender, RoutedEventArgs e) => _shell?.OpenSyncSettings();

    private void OnAiClick(object? sender, RoutedEventArgs e) => _shell?.OpenAiSettings();

    private void OnKindleClick(object? sender, RoutedEventArgs e) => _shell?.OpenKindleSettings();

    private void SetBackupBusy(bool busy)
    {
        _backupBusy = busy;
        ExportBackupButton.IsEnabled = !busy && !_restartRequired;
        RestoreBackupButton.IsEnabled = !busy && !_restartRequired;
    }

    private void SetBackupStatus(string text)
    {
        BackupStatusText.Text = text;
        BackupStatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async void OnExportBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null || _backupBusy || _restartRequired) return;
        SetBackupBusy(true);
        string? directory = null;
        try
        {
            var fileName = $"Kkindle-备份-{DateTime.Now:yyyyMMdd-HHmmss}{AppBackupService.FileExtension}";
            var share = _shell.Services.FileShare;
            IStorageFile? destination = null;
            if (share is null)
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage is null || !storage.CanSave)
                    throw new InvalidOperationException("此设备不支持保存备份文件。");
                destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "导出 Kkindle 备份",
                    SuggestedFileName = fileName,
                    FileTypeChoices = [new FilePickerFileType("Kkindle 备份")
                    {
                        Patterns = [$"*{AppBackupService.FileExtension}"]
                    }]
                });
                if (destination is null)
                {
                    SetBackupStatus("已取消导出备份。");
                    return;
                }
            }

            SetBackupStatus("正在导出备份，请稍候…");
            directory = Path.Combine(Path.GetTempPath(), "kkindle-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            await _shell.ExportBackupAsync(path);
            if (share is not null)
            {
                SetBackupStatus("请选择备份的保存或分享位置…");
                var shared = await share.ShareFileAsync(path);
                SetBackupStatus(shared ? "备份已分享。" : "已取消分享，备份未保存。");
            }
            else
            {
                await using var input = File.OpenRead(path);
                await using var output = await destination!.OpenWriteAsync();
                await input.CopyToAsync(output);
                SetBackupStatus("备份已导出。");
            }
        }
        catch (OperationCanceledException)
        {
            SetBackupStatus("已取消导出备份。");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetBackupStatus("备份导出失败：" + exception.Message);
        }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            SetBackupBusy(false);
        }
    }

    private async void OnRestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null || _backupBusy || _restartRequired) return;
        SetBackupBusy(true);
        IReadOnlyList<string> files = [];
        try
        {
            files = await _shell.Services.ImportSource.PickFilesAsync();
            if (files.Count == 0)
            {
                SetBackupStatus("已取消选择备份。");
                return;
            }
            if (files.Count != 1)
            {
                SetBackupStatus("请选择一个 .kkindle 备份文件。");
                return;
            }
            var confirmed = await _shell.ConfirmAsync(
                "恢复 Kkindle 备份",
                "恢复将覆盖当前整个书库，包括书籍、封面和阅读记录。建议先导出当前书库备份。恢复成功后必须完全关闭并重新打开应用，才能继续使用。确定恢复吗？",
                "覆盖并恢复");
            if (!confirmed)
            {
                SetBackupStatus("已取消恢复备份。");
                return;
            }

            SetBackupStatus("正在恢复备份，请稍候…");
            await _shell.RestoreBackupAsync(files[0]);
            _restartRequired = true;
            SetBackupStatus("备份已恢复。请完全关闭并重新打开应用后继续使用。");
        }
        catch (OperationCanceledException)
        {
            SetBackupStatus("已取消恢复备份。");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetBackupStatus("备份恢复失败：" + exception.Message);
        }
        finally
        {
            try { _shell.Services.ImportSource.ReleaseFiles(files); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SetBackupStatus(BackupStatusText.Text + " 临时文件清理失败：" + exception.Message);
            }
            SetBackupBusy(false);
        }
    }

    private void OnTrashClick(object? sender, RoutedEventArgs e)
    {
        if (_modules is not null && _shell is not null)
            _shell.ShowSheet("回收站", new TrashSheet(_modules.Library.Books, _shell));
    }

    private async void OnDeveloperClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(new Uri("https://github.com/kingstacker")))
                _shell?.ShowToast("无法打开开发者主页");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell?.ShowToast("无法打开开发者主页");
        }
    }

    private async void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { _shell?.ShowToast("剪贴板不可用"); return; }
            await clipboard.SetTextAsync("1109898894");
            _shell?.ShowToast("群号已复制到剪贴板");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell?.ShowToast("复制群号失败");
        }
    }

    private void OnSupportClick(object? sender, RoutedEventArgs e) =>
        _shell?.ShowSheet("请我喝杯咖啡", new SupportSheet());

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        var mode = sender switch
        {
            Button { Name: nameof(ThemeLight) } => MobileThemeMode.Light,
            Button { Name: nameof(ThemeDark) } => MobileThemeMode.Dark,
            _ => MobileThemeMode.System
        };
        _shell!.Preferences.Update(value => value with { ThemeMode = mode });
        _shell.ApplyThemeMode();
        PresentPreferences();
    }

}
