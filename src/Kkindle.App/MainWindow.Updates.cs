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
    private bool _pendingUpdatePromptInProgress;

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
        if (!_appSettings.DevelopmentUpdateCheckEnabled
            && UpdateService.IsDevelopmentVersion(storedVersion))
        {
            _pendingUpdateVersion = null;
            _ = ClearPendingUpdateStateAsync();
            return;
        }
        try
        {
            var currentVersion = ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly);
            if (UpdateService.CompareVersions(storedVersion, currentVersion) <= 0)
            {
                if (_appSettings.PendingUpdateDownloadedAt is not null)
                {
                    AboutUpdateStatusText.Text = T("更新成功，当前版本 {0}", currentVersion);
                    _ = ShowMessageAsync(T("更新成功"), T("已更新至 {0}。可在“关于”中检查是否有更新版本。", currentVersion));
                }
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
                _appSettings.PendingUpdateReleaseNotesEnglish,
                packageReady: true);
            AboutUpdateStatusText.Text = _appSettings.PendingUpdateInstallStartedAt is not null
                ? T("上次安装未完成，点击更新按钮重试 {0}", storedVersion)
                : T("更新包已下载，点击更新按钮安装 {0}", storedVersion);
            return;
        }

        if (!_appSettings.AutoUpdateCheckEnabled || !_appSettings.NetworkEnabled)
            return;

        _pendingUpdateVersion = storedVersion;
        ShowUpdateBadge(
            storedVersion,
            _appSettings.PendingUpdateReleaseNotes,
            _appSettings.PendingUpdateReleaseNotesEnglish);
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

    private async void CheckForUpdatesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateCheckInProgress)
        {
            ShowUpdateTaskHistory();
            return;
        }
        if (TryGetPendingUpdatePackage(out var packagePath, out var version))
            await PromptPendingUpdateInstallAsync(packagePath, version);
        else
            await CheckForUpdatesAsync(userInitiated: true);
    }

    private async void UpdateBadgeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateCheckInProgress)
        {
            ShowUpdateTaskHistory();
            return;
        }
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
        // Keep the action responsive so it can reopen progress. Disabling it
        // during Click leaves the pressed/disabled rectangle on screen.
        CheckForUpdatesButton.Classes.Add("updating");
        ToolTip.SetIsOpen(CheckForUpdatesButton, false);
        AboutUpdateStatusText.Text = T("正在检查更新…");
        try
        {
            var currentVersion = ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly);
            var channel = DevelopmentUpdateCheck.IsChecked == true
                ? AppUpdateChannel.Development
                : AppUpdateChannel.Stable;
            _updateService.ConfigureProxy(TranslationGoogleProxyBox.Text);
            var update = await RunUpdateTaskAsync(
                T("检查更新"),
                (_, token) => _updateService.CheckForUpdateAsync(currentVersion, channel, token),
                showProgress: userInitiated);

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
            if (TryGetPendingUpdatePackage(out var readyPath, out var readyVersion))
            {
                await PromptPendingUpdateInstallAsync(readyPath, readyVersion);
                return;
            }

            // On install-capable platforms, clicking update starts the download
            // immediately. The install confirmation is shown after the package
            // has been downloaded and verified. Platforms without an in-app
            // installer still need confirmation before opening the release page.
            if (!_updateService.CanInstall
                && !await ConfirmUpdateInstallAsync(currentVersion, update))
                return;
            await DownloadAndInstallAsync(update);
        }
        catch (OperationCanceledException)
        {
            AboutUpdateStatusText.Text = T("已取消");
        }
        catch (Exception exception)
        {
            AboutUpdateStatusText.Text = T("检查更新失败：{0}", UiText.Localize(exception.Message));
            if (userInitiated)
                await ShowMessageAsync(T("更新失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            _updateCheckInProgress = false;
            CheckForUpdatesButton.Classes.Remove("updating");
            CheckForUpdatesButton.IsEnabled = _updateService is not null;
        }
    }

    private async Task<T> RunUpdateTaskAsync<T>(string title,
        Func<BackgroundTaskContext, CancellationToken, Task<T>> operation, bool showProgress = true)
    {
        var task = RunTrackedTaskAsync(title, BackgroundTaskKind.Other, operation,
            _lifetimeCancellation.Token, allowRetry: false);
        if (showProgress) ShowUpdateTaskHistory();
        return await task;
    }

    private void ShowUpdateTaskHistory()
    {
        ToolTip.SetIsOpen(CheckForUpdatesButton, false);
        ToolTip.SetIsOpen(BackgroundTaskHistoryButton, false);
        RefreshBackgroundTaskStatus();
        BackgroundTaskHistoryButton.Focus();
        // Explicitly requested progress stays open when the task finishes.
        _backgroundTaskFlyoutAutoOpened = false;
        ShowBackgroundTaskFlyout(autoOpened: false);
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

        var packagePath = await RunUpdateTaskAsync(
            T("正在下载 Kkindle {0}…", update.Version), async (context, token) =>
            {
                context.ReportProgress(T("正在下载 Kkindle {0}…", update.Version), 0, 100);
                var progress = new Progress<AppUpdateDownloadProgress>(value =>
                    context.ReportProgress(value.TotalBytes is > 0
                            ? T("正在下载 Kkindle {0} · {1:0}%", update.Version, value.Percentage)
                            : T("正在下载 Kkindle {0}…", update.Version),
                        value.TotalBytes is > 0 ? (int)value.Percentage : null,
                        value.TotalBytes is > 0 ? 100 : null));
                var path = await _updateService.DownloadAsync(update, progress, token);
                await MarkPendingUpdateReadyAsync(update, path);
                context.ReportProgress(T("下载完成"), 100, 100);
                return path;
            });
        await PromptPendingUpdateInstallAsync(packagePath, update.Version);
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
            ShowUpdateBadge(
                update.Version,
                update.ReleaseNotes,
                update.ReleaseNotesEnglish,
                packageReady);
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
                PendingUpdateReleaseNotesEnglish =
                    update is null || string.IsNullOrWhiteSpace(update.ReleaseNotesEnglish)
                        ? null
                        : TruncateNotes(update.ReleaseNotesEnglish, 2000),
                PendingUpdatePackagePath = samePendingPackage
                    ? _appSettings.PendingUpdatePackagePath
                    : null,
                PendingUpdateDownloadedAt = samePendingPackage
                    ? _appSettings.PendingUpdateDownloadedAt
                    : null,
                PendingUpdateInstallStartedAt = samePendingPackage
                    ? _appSettings.PendingUpdateInstallStartedAt
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
                PendingUpdateReleaseNotesEnglish = null,
                PendingUpdatePackagePath = null,
                PendingUpdateDownloadedAt = null,
                PendingUpdateInstallStartedAt = null
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
            PendingUpdateReleaseNotesEnglish = string.IsNullOrWhiteSpace(update.ReleaseNotesEnglish)
                ? null
                : TruncateNotes(update.ReleaseNotesEnglish, 2000),
            PendingUpdatePackagePath = fullPackagePath,
            PendingUpdateDownloadedAt = DateTimeOffset.Now,
            PendingUpdateInstallStartedAt = null
        });
        ShowUpdateBadge(
            update.Version,
            update.ReleaseNotes,
            update.ReleaseNotesEnglish,
            packageReady: true);
        AboutUpdateStatusText.Text = T("更新包已下载，点击更新按钮安装 {0}", update.Version);
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

    private async Task PromptPendingUpdateInstallAsync(string packagePath, string version, bool exitRequested = false)
    {
        if (_pendingUpdatePromptInProgress) return;
        _pendingUpdatePromptInProgress = true;
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
                    T("当前版本：{0}\n目标版本：{1}\n\n更新包已下载并校验。点击后将退出 Kkindle，显示安装进度，完成后自动打开新版本。\n如安装失败，可重新打开 Kkindle 重试。\n\n现在安装吗？", ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly), version),
                    T("退出并安装"), allowDiagnosticAutoConfirm: false,
                    cancelText: exitRequested ? T("稍后安装并退出") : T("稍后安装")))
            {
                if (exitRequested && !_s3SyncExitInProgress)
                {
                    _skipS3SyncOnExit = false;
                    _s3SyncExitInProgress = true;
                    await CompleteWindowCloseAfterS3SyncAsync();
                }
                return;
            }

            try
            {
                if (!await PrepareSettingsForExitAsync()) return;
                _appSettings = _appSettings with { PendingUpdateInstallStartedAt = DateTimeOffset.Now };
                await _appSettingsStore.SaveAsync(_appSettings);
                AboutUpdateStatusText.Text = T("正在启动安装程序…");
                _updateService!.LaunchInstaller(packagePath);
                _allowWindowCloseForPendingUpdate = true;
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Close();
            }
            catch (Exception exception)
            {
                _allowWindowCloseForPendingUpdate = false;
                AboutUpdateStatusText.Text = T("安装未启动，更新包已保留，可点击更新按钮重试");
                await ShowMessageAsync(T("更新失败"), T("{0}\n\n更新包已保留，可重试安装。安装日志：{1}", UiText.Localize(exception.Message), packagePath + ".install.log"));
            }
        }
        finally
        {
            _pendingUpdatePromptInProgress = false;
        }
    }

    private void ShowUpdateBadge(
        string version,
        string? releaseNotes,
        string? releaseNotesEnglish,
        bool packageReady = false)
    {
        UpdateBadgeButton.IsVisible = true;
        CheckForUpdatesButton.Content = packageReady ? T("安装更新") : T("检查更新");
        var tip = new StackPanel { Spacing = 6, MaxWidth = 380 };
        tip.Children.Add(new TextBlock
        {
            Text = T("发现新版本 v{0}", version),
            FontWeight = FontWeight.Bold
        });
        tip.Children.Add(new TextBlock
        {
            Text = TruncateNotes(
                GetLocalizedReleaseNotes(releaseNotes, releaseNotesEnglish),
                600) is { Length: > 0 } notes
                ? notes
                : T("本次更新未提供说明。"),
            TextWrapping = TextWrapping.Wrap
        });
        tip.Children.Add(new TextBlock
        {
            Text = packageReady
                ? T("更新包已下载，点击此处安装；完成后自动打开新版本")
                : T("点击黄点即可下载并安装最新版"),
            FontSize = 11,
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        ToolTip.SetTip(UpdateBadgeButton, tip);
    }

    private void HideUpdateBadge()
    {
        UpdateBadgeButton.IsVisible = false;
        CheckForUpdatesButton.Content = T("检查更新");
        ToolTip.SetTip(UpdateBadgeButton, null);
    }

    private static string TruncateNotes(string? notes, int maxLength)
    {
        var normalized = (notes ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length <= maxLength) return normalized;
        return normalized[..maxLength].TrimEnd() + "…";
    }

    private static string GetLocalizedReleaseNotes(string? releaseNotes, string? releaseNotesEnglish)
    {
        if (UiText.IsEnglish)
        {
            var english = ExtractReleaseNotesLanguage(releaseNotesEnglish, "en-US")
                ?? ExtractReleaseNotesLanguage(releaseNotes, "en-US");
            if (!string.IsNullOrWhiteSpace(english)) return english;
        }

        var chinese = ExtractReleaseNotesLanguage(releaseNotes, "zh-CN");
        if (!string.IsNullOrWhiteSpace(chinese)) return chinese;
        return ExtractReleaseNotesLanguage(releaseNotesEnglish, "en-US") ?? string.Empty;
    }

    private static string? ExtractReleaseNotesLanguage(string? notes, string language)
    {
        var normalized = (notes ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length == 0) return null;

        var marker = $"<!-- kkindle-release-notes:{language} -->";
        if (!normalized.Contains("<!-- kkindle-release-notes:", StringComparison.Ordinal))
            return normalized;

        var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;
        var contentStart = markerIndex + marker.Length;
        var nextMarker = normalized.IndexOf("<!-- kkindle-release-notes:", contentStart, StringComparison.Ordinal);
        var content = (nextMarker >= 0
                ? normalized[contentStart..nextMarker]
                : normalized[contentStart..])
            .Trim();

        if (content.StartsWith("## 中文", StringComparison.Ordinal)
            || content.StartsWith("## English", StringComparison.Ordinal))
        {
            content = content[(content.IndexOf('\n') + 1)..].Trim();
        }

        return content.Length == 0 ? null : content;
    }

    private string BuildUpdatePrompt(string currentVersion, AppUpdateInfo update)
    {
        var notes = TruncateNotes(
            GetLocalizedReleaseNotes(update.ReleaseNotes, update.ReleaseNotesEnglish),
            900);
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
