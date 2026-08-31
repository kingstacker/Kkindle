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
    private bool _allowWindowCloseForPendingUpdate;
    private bool _pendingUpdateExitPromptInProgress;

    // Display-only record of an available update. It drives the title-bar badge;
    // the actual download always starts from a fresh release lookup so package
    // URLs are never reused across versions.
    private string? _pendingUpdateVersion;

    private void StartAutomaticUpdateCheck()
    {
        if (!_appSettings.OnboardingCompleted
            || _automaticUpdateCheckStarted
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
        if (string.IsNullOrWhiteSpace(storedVersion))
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

        if (TryGetPendingUpdatePackage(out _, out _))
        {
            _pendingUpdateVersion = storedVersion;
            ShowUpdateBadge(
                storedVersion,
                _appSettings.PendingUpdateReleaseNotes,
                packageReady: true);
            AboutUpdateStatusText.Text = T("更新包已下载，退出应用后安装 {0}", storedVersion);
            return;
        }

        if (!_appSettings.AutoUpdateCheckEnabled || !_appSettings.NetworkEnabled)
            return;

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
        if (TryGetPendingUpdatePackage(out var packagePath, out var packageVersion))
        {
            await PromptPendingUpdateInstallAsync(packagePath, packageVersion);
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请先在常规设置中允许网络功能，再检查应用更新。"));
            return;
        }
        // A fresh lookup keeps package URLs and release notes accurate, then the
        // shared flow asks for confirmation and either downloads or installs the
        // already-downloaded package.
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
        TaskProgressPopupBar.IsIndeterminate = false;
        TaskProgressPopupBar.Value = 100;
        TaskProgressPopupText.Text = T("下载完成，等待退出应用…");
        await MarkPendingUpdateReadyAsync(update, packagePath);
        HideTaskProgressPopup();
        await ShowMessageAsync(
            T("更新已下载"),
            T("Kkindle {0} 更新包已下载完成。当前窗口保持打开；退出应用时会提示并完成安装。", update.Version));
    }

    private async Task ApplyUpdateCheckResultAsync(AppUpdateInfo? update)
    {
        _pendingUpdateVersion = update?.Version;
        if (update is null) HideUpdateBadge();
        else
        {
            var packageReady = string.Equals(
                _appSettings.PendingUpdateVersion,
                update.Version,
                StringComparison.OrdinalIgnoreCase)
                && TryGetPendingUpdatePackage(out _, out _);
            ShowUpdateBadge(update.Version, update.ReleaseNotes, packageReady);
        }

        try
        {
            var samePendingPackage = update is not null
                && string.Equals(
                    _appSettings.PendingUpdateVersion,
                    update.Version,
                    StringComparison.OrdinalIgnoreCase);
            _appSettings = AppSettings.Normalize(_appSettings with
            {
                LastAutoUpdateCheckAt = DateTimeOffset.Now,
                PendingUpdateVersion = update?.Version,
                PendingUpdateReleaseNotes =
                    update is null ? null : TruncateNotes(update.ReleaseNotes, 2000),
                PendingUpdatePackagePath = samePendingPackage
                    ? _appSettings.PendingUpdatePackagePath
                    : null,
                PendingUpdateDownloadedAt = samePendingPackage
                    ? _appSettings.PendingUpdateDownloadedAt
                    : null
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
                PendingUpdateReleaseNotes = null,
                PendingUpdatePackagePath = null,
                PendingUpdateDownloadedAt = null
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

    private async Task MarkPendingUpdateReadyAsync(AppUpdateInfo update, string packagePath)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        _pendingUpdateVersion = update.Version;
        _appSettings = AppSettings.Normalize(_appSettings with
        {
            PendingUpdateVersion = update.Version,
            PendingUpdateReleaseNotes = TruncateNotes(update.ReleaseNotes, 2000),
            PendingUpdatePackagePath = fullPackagePath,
            PendingUpdateDownloadedAt = DateTimeOffset.Now
        });
        ShowUpdateBadge(update.Version, update.ReleaseNotes, packageReady: true);
        AboutUpdateStatusText.Text = T("更新包已下载，退出应用后安装 {0}", update.Version);
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool TryGetPendingUpdatePackage(out string packagePath, out string version)
    {
        packagePath = _appSettings.PendingUpdatePackagePath?.Trim() ?? string.Empty;
        version = _appSettings.PendingUpdateVersion?.Trim() ?? string.Empty;
        return _updateService?.CanInstall == true
            && version.Length > 0
            && packagePath.Length > 0
            && File.Exists(packagePath);
    }

    private async Task PromptPendingUpdateInstallAsync(string packagePath, string version)
    {
        if (_pendingUpdateExitPromptInProgress) return;
        _pendingUpdateExitPromptInProgress = true;
        try
        {
            if (!File.Exists(packagePath))
            {
                await ClearPendingUpdateStateAsync();
                HideUpdateBadge();
                AboutUpdateStatusText.Text = T("更新包已失效，请重新检查更新");
                return;
            }

            if (!await ConfirmAsync(
                    T("更新已就绪"),
                    T("Kkindle {0} 更新包已下载完成。退出应用后将启动安装程序并完成更新。\n\n现在退出并安装吗？", version),
                    T("退出并安装")))
            {
                return;
            }

            try
            {
                _allowWindowCloseForPendingUpdate = true;
                _updateService!.LaunchInstaller(packagePath);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Close();
            }
            catch (Exception exception)
            {
                _allowWindowCloseForPendingUpdate = false;
                await ShowMessageAsync(T("更新失败"), UiText.Localize(exception.Message));
            }
        }
        finally
        {
            _pendingUpdateExitPromptInProgress = false;
        }
    }

    private void ShowUpdateBadge(string version, string? releaseNotes, bool packageReady = false)
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
            Text = packageReady
                ? T("更新包已下载，点击此处或退出应用即可完成安装")
                : T("点击黄点即可下载并安装最新版"),
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
