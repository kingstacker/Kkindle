using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private bool _automaticUpdateCheckStarted;
    private bool _updateCheckInProgress;

    private void StartAutomaticUpdateCheck()
    {
        if (_automaticUpdateCheckStarted
            || _updateService is null
            || !_appSettings.AutoUpdateCheckEnabled
            || !_appSettings.NetworkEnabled)
            return;
        _automaticUpdateCheckStarted = true;
        _ = CheckForUpdatesAfterStartupAsync(_lifetimeCancellation.Token);
    }

    private async Task CheckForUpdatesAfterStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (!_appSettings.AutoUpdateCheckEnabled || !_appSettings.NetworkEnabled) return;
            await CheckForUpdatesAsync(userInitiated: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async void CheckForUpdatesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await CheckForUpdatesAsync(userInitiated: true);

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckInProgress) return;
        if (_updateService is null)
        {
            if (userInitiated)
                await ShowMessageAsync("检查更新", "当前平台暂不支持应用内更新。");
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            if (userInitiated)
                await ShowMessageAsync("网络功能已关闭", "请先在常规设置中允许网络功能，再检查应用更新。");
            return;
        }

        _updateCheckInProgress = true;
        var updateProgressVisible = false;
        CheckForUpdatesButton.IsEnabled = false;
        AboutUpdateStatusText.Text = "正在检查更新…";
        try
        {
            var currentVersion = ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly);
            var update = await _updateService.CheckForUpdateAsync(
                currentVersion,
                _lifetimeCancellation.Token);
            if (update is null)
            {
                AboutUpdateStatusText.Text = $"当前 {currentVersion} 已是最新版本";
                if (userInitiated)
                    await ShowMessageAsync("检查更新", $"当前版本 {currentVersion} 已是最新版本。");
                return;
            }

            AboutUpdateStatusText.Text = $"发现新版本 {update.Version}";
            var actionText = _updateService.CanInstall ? "下载并安装" : "打开下载页";
            var confirmed = await ConfirmAsync(
                "发现新版本",
                BuildUpdatePrompt(currentVersion, update),
                actionText);
            if (!confirmed) return;

            if (!_updateService.CanInstall)
            {
                OpenReleasePage(update.ReleasePage);
                AboutUpdateStatusText.Text = _updateService.UnavailableReason;
                return;
            }

            TaskProgressPopupBar.IsIndeterminate = false;
            TaskProgressPopupBar.Minimum = 0;
            TaskProgressPopupBar.Maximum = 100;
            TaskProgressPopupBar.Value = 0;
            TaskProgressPopupText.Text = $"正在下载 Kkindle {update.Version}…";
            ShowTaskProgressPopup();
            updateProgressVisible = true;
            var progress = new Progress<AppUpdateDownloadProgress>(value =>
            {
                TaskProgressPopupBar.IsIndeterminate = value.TotalBytes is null;
                if (value.TotalBytes is not null)
                    TaskProgressPopupBar.Value = value.Percentage;
                TaskProgressPopupText.Text = value.TotalBytes is > 0
                    ? $"正在下载 Kkindle {update.Version} · {value.Percentage:0}%"
                    : $"正在下载 Kkindle {update.Version}…";
            });
            var packagePath = await _updateService.DownloadAsync(
                update,
                progress,
                _lifetimeCancellation.Token);
            TaskProgressPopupBar.IsIndeterminate = true;
            TaskProgressPopupText.Text = "校验完成，正在启动安装程序…";
            AboutUpdateStatusText.Text = $"正在安装 {update.Version}";
            _updateService.LaunchInstaller(packagePath);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AboutUpdateStatusText.Text = $"检查更新失败：{exception.Message}";
            if (userInitiated)
                await ShowMessageAsync("更新失败", exception.Message);
        }
        finally
        {
            if (updateProgressVisible)
            {
                TaskProgressPopupBar.IsIndeterminate = false;
                HideTaskProgressPopup();
            }
            _updateCheckInProgress = false;
            CheckForUpdatesButton.IsEnabled = _updateService is not null;
        }
    }

    private static string BuildUpdatePrompt(string currentVersion, AppUpdateInfo update)
    {
        var notes = update.ReleaseNotes.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        if (notes.Length > 900) notes = notes[..900].TrimEnd() + "…";
        if (notes.Length == 0) notes = "本次 Release 未提供更新说明。";
        return $"当前版本：{currentVersion}\n最新版本：{update.Version}\n\n{notes}";
    }

    private static void OpenReleasePage(Uri releasePage)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = releasePage.AbsoluteUri,
            UseShellExecute = true
        });
    }
}
