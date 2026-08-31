using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private bool _automaticUpdateCheckStarted;
    private bool _updateCheckInProgress;

    // Display-only record of an available update. It drives the title-bar badge;
    // the actual download always starts from a fresh release lookup so package
    // URLs are never reused across versions.
    private string? _pendingUpdateVersion;

    private void StartAutomaticUpdateCheck()
    {
        if (_automaticUpdateCheckStarted
            || _updateService is null)
            return;
        _automaticUpdateCheckStarted = true;

        // A badge discovered earlier today stays visible across restarts
        // without spending another network call.
        RestorePendingUpdateBadge();

        if (!_appSettings.AutoUpdateCheckEnabled || !_appSettings.NetworkEnabled) return;
        if (_appSettings.LastAutoUpdateCheckAt?.LocalDateTime.Date == DateTime.Today) return;
        _ = CheckForUpdatesAfterStartupAsync(_lifetimeCancellation.Token);
    }

    private void RestorePendingUpdateBadge()
    {
        var storedVersion = _appSettings.PendingUpdateVersion;
        if (!_appSettings.AutoUpdateCheckEnabled
            || !_appSettings.NetworkEnabled
            || string.IsNullOrWhiteSpace(storedVersion))
            return;
        try
        {
            var currentVersion = ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly);
            if (UpdateService.CompareVersions(storedVersion, currentVersion) <= 0)
            {
                // The stored release is already installed (or older): drop it quietly.
                _pendingUpdateVersion = null;
                _ = ClearPendingUpdateStateAsync();
                return;
            }
        }
        catch (InvalidDataException)
        {
            return;
        }

        _pendingUpdateVersion = storedVersion;
        ShowUpdateBadge(storedVersion, _appSettings.PendingUpdateReleaseNotes);
        AboutUpdateStatusText.Text = T("发现新版本 {0}", storedVersion);
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

    private async void UpdateBadgeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            await ShowMessageAsync(T("检查更新"), T("当前平台暂不支持应用内更新。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请先在常规设置中允许网络功能，再检查应用更新。"));
            return;
        }
        // A fresh lookup keeps package URLs and release notes accurate, then the
        // shared flow asks for confirmation and installs directly.
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckInProgress) return;
        if (_updateService is null)
        {
            if (userInitiated)
                await ShowMessageAsync(T("检查更新"), T("当前平台暂不支持应用内更新。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            if (userInitiated)
                await ShowMessageAsync(T("网络功能已关闭"), T("请先在常规设置中允许网络功能，再检查应用更新。"));
            return;
        }

        _updateCheckInProgress = true;
        var updateProgressVisible = false;
        CheckForUpdatesButton.IsEnabled = false;
        AboutUpdateStatusText.Text = T("正在检查更新…");
        try
        {
            var currentVersion = ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly);
            var update = await _updateService.CheckForUpdateAsync(
                currentVersion,
                _lifetimeCancellation.Token);

            // Successful lookups stamp today's date and refresh the persisted
            // badge state; failures leave both untouched so the next launch
            // retries instead of waiting until tomorrow.
            await ApplyUpdateCheckResultAsync(update);

            if (update is null)
            {
                AboutUpdateStatusText.Text = T("当前 {0} 已是最新版本", currentVersion);
                if (userInitiated)
                    await ShowMessageAsync(T("检查更新"), T("当前版本 {0} 已是最新版本。", currentVersion));
                return;
            }

            AboutUpdateStatusText.Text = T("发现新版本 {0}", update.Version);
            if (!userInitiated) return;

            if (!await ConfirmUpdateInstallAsync(currentVersion, update)) return;
            if (_updateService.CanInstall) updateProgressVisible = true;
            await DownloadAndInstallAsync(update);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AboutUpdateStatusText.Text = T("检查更新失败：{0}", UiText.Localize(exception.Message));
            if (userInitiated)
                await ShowMessageAsync(T("更新失败"), UiText.Localize(exception.Message));
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

    private async Task<bool> ConfirmUpdateInstallAsync(string currentVersion, AppUpdateInfo update)
    {
        var actionText = _updateService!.CanInstall ? T("下载并安装") : T("打开下载页");
        return await ConfirmAsync(
            T("发现新版本"),
            BuildUpdatePrompt(currentVersion, update),
            actionText);
    }

    private async Task DownloadAndInstallAsync(AppUpdateInfo update)
    {
        if (!_updateService!.CanInstall)
        {
            OpenReleasePage(update.ReleasePage);
            AboutUpdateStatusText.Text = _updateService.UnavailableReason;
            return;
        }

        TaskProgressPopupBar.IsIndeterminate = false;
        TaskProgressPopupBar.Minimum = 0;
        TaskProgressPopupBar.Maximum = 100;
        TaskProgressPopupBar.Value = 0;
        TaskProgressPopupText.Text = T("正在下载 Kkindle {0}…", update.Version);
        ShowTaskProgressPopup();
        var progress = new Progress<AppUpdateDownloadProgress>(value =>
        {
            TaskProgressPopupBar.IsIndeterminate = value.TotalBytes is null;
            if (value.TotalBytes is not null)
                TaskProgressPopupBar.Value = value.Percentage;
            TaskProgressPopupText.Text = value.TotalBytes is > 0
                ? T("正在下载 Kkindle {0} · {1:0}%", update.Version, value.Percentage)
                : T("正在下载 Kkindle {0}…", update.Version);
        });
        var packagePath = await _updateService.DownloadAsync(
            update,
            progress,
            _lifetimeCancellation.Token);
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("校验完成，正在启动安装程序…");
        AboutUpdateStatusText.Text = T("正在安装 {0}", update.Version);
        _updateService.LaunchInstaller(packagePath);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private async Task ApplyUpdateCheckResultAsync(AppUpdateInfo? update)
    {
        _pendingUpdateVersion = update?.Version;
        if (update is null) HideUpdateBadge();
        else ShowUpdateBadge(update.Version, update.ReleaseNotes);

        try
        {
            _appSettings = AppSettings.Normalize(_appSettings with
            {
                LastAutoUpdateCheckAt = DateTimeOffset.Now,
                PendingUpdateVersion = update?.Version,
                PendingUpdateReleaseNotes =
                    update is null ? null : TruncateNotes(update.ReleaseNotes, 2000)
            });
            await _appSettingsStore.SaveAsync(_appSettings);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task ClearPendingUpdateStateAsync()
    {
        try
        {
            _appSettings = AppSettings.Normalize(_appSettings with
            {
                PendingUpdateVersion = null,
                PendingUpdateReleaseNotes = null
            });
            await _appSettingsStore.SaveAsync(_appSettings);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ShowUpdateBadge(string version, string? releaseNotes)
    {
        UpdateBadgeButton.IsVisible = true;
        var tip = new StackPanel { Spacing = 6, MaxWidth = 380 };
        tip.Children.Add(new TextBlock
        {
            Text = T("发现新版本 v{0}", version),
            FontWeight = FontWeight.Bold
        });
        tip.Children.Add(new TextBlock
        {
            Text = TruncateNotes(releaseNotes, 600) is { Length: > 0 } notes
                ? notes
                : T("本次更新未提供说明。"),
            TextWrapping = TextWrapping.Wrap
        });
        tip.Children.Add(new TextBlock
        {
            Text = T("点击黄点即可下载并安装最新版"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            TextWrapping = TextWrapping.Wrap
        });
        ToolTip.SetTip(UpdateBadgeButton, tip);
    }

    private void HideUpdateBadge()
    {
        UpdateBadgeButton.IsVisible = false;
        ToolTip.SetTip(UpdateBadgeButton, null);
    }

    private static string TruncateNotes(string? notes, int maxLength)
    {
        var normalized = (notes ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length <= maxLength) return normalized;
        return normalized[..maxLength].TrimEnd() + "…";
    }

    private static string BuildUpdatePrompt(string currentVersion, AppUpdateInfo update)
    {
        var notes = TruncateNotes(update.ReleaseNotes, 900);
        if (notes.Length == 0) notes = T("本次 Release 未提供更新说明。");
        return T("当前版本：{0}\n最新版本：{1}\n\n{2}", currentVersion, update.Version, notes);
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
