using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private string GetSelectedPinyinEngineId()
    {
        var id = PinyinEngineSelector?.SelectedItem is ComboBoxItem { Tag: not null } item
            ? item.Tag.ToString()
            : _appSettings.PinyinEngineId;
        return PinyinBookEngineCatalog.NormalizeId(id);
    }

    private void ApplyPinyinEngineSelection()
    {
        if (PinyinEngineSelector is null) return;
        var selectedId = PinyinBookEngineCatalog.NormalizeId(_appSettings.PinyinEngineId);
        _updatingPinyinEngineSelector = true;
        try
        {
            PinyinEngineSelector.SelectedIndex = selectedId == PinyinBookEngineCatalog.G2PWId ? 1 : 0;
        }
        finally
        {
            _updatingPinyinEngineSelector = false;
        }
        RefreshPinyinEngineStatus();
    }

    private void PinyinEngineSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingPinyinEngineSelector || _g2pwModelDownloader is null) return;
        RefreshPinyinEngineStatus();
        ScheduleAppSettingsAutoSave();
    }

    private void RefreshPinyinEngineStatus()
    {
        if (PinyinEngineStatusText is null
            || _g2pwModelDownloader is null
            || _g2pwModelDownloadBusy)
            return;

        var selectedId = GetSelectedPinyinEngineId();
        var isG2pw = selectedId == PinyinBookEngineCatalog.G2PWId;
        var networkEnabled = NetworkEnabledCheck?.IsChecked != false;
        PinyinEngineDownloadButton.IsVisible = isG2pw;
        PinyinEngineDownloadButton.IsEnabled = isG2pw && networkEnabled;
        PinyinEngineDownloadProgressBar.IsVisible = false;
        PinyinEngineDownloadProgressBar.IsIndeterminate = false;
        PinyinEngineDownloadProgressBar.Value = 0;
        PinyinEngineCancelButton.IsVisible = false;
        PinyinEngineCancelButton.IsEnabled = false;

        if (!isG2pw)
        {
            PinyinEngineStatusText.Text = T("内置拼音模型已就绪，可直接使用。");
            PinyinEngineDownloadButton.Content = T("下载模型");
            return;
        }

        var installed = false;
        try
        {
            installed = _g2pwModelDownloader.IsInstalled();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Pinyin] Failed to inspect g2pW model: {exception.Message}");
        }

        PinyinEngineStatusText.Text = installed
            ? T("g2pW 已安装，可直接用于书籍注音。")
            : networkEnabled
                ? T("g2pW 尚未安装，点击下载后即可使用。")
                : T("g2pW 尚未安装；请先开启网络访问后下载。");
        PinyinEngineDownloadButton.Content = installed ? T("重新下载") : T("下载模型");
    }

    private void SetPinyinEngineDownloadBusy(bool busy)
    {
        PinyinEngineSelector.IsEnabled = !busy;
        PinyinEngineDownloadButton.IsVisible = !busy;
        PinyinEngineDownloadButton.IsEnabled = !busy && NetworkEnabledCheck?.IsChecked != false;
        PinyinEngineCancelButton.IsVisible = busy;
        PinyinEngineCancelButton.IsEnabled = busy;
        PinyinEngineDownloadProgressBar.IsVisible = busy;
        PinyinEngineDownloadProgressBar.IsIndeterminate = busy;
        if (!busy) PinyinEngineDownloadProgressBar.Value = 0;
    }

    private void UpdatePinyinEngineDownloadProgress(G2PWModelDownloadProgress progress)
    {
        var percentage = progress.OverallPercentage ?? progress.FilePercentage;
        var status = percentage is { } value
            ? T("{0} · {1:0}%", progress.Stage, value)
            : T("{0} · {1}", progress.Stage, FormatPinyinBytes(progress.BytesReceived));
        if (progress.FileTotalBytes is > 0)
        {
            status += T(
                "（{0}/{1}）",
                FormatPinyinBytes(progress.BytesReceived),
                FormatPinyinBytes(progress.FileTotalBytes.Value));
        }

        PinyinEngineDownloadProgressBar.IsVisible = true;
        PinyinEngineDownloadProgressBar.IsIndeterminate = percentage is null;
        if (percentage is { } progressValue)
            PinyinEngineDownloadProgressBar.Value = progressValue;
        PinyinEngineStatusText.Text = status;
    }

    private async Task HandlePinyinEngineDownloadRequestAsync()
    {
        if (_g2pwModelDownloadBusy || GetSelectedPinyinEngineId() != PinyinBookEngineCatalog.G2PWId)
            return;

        var downloadTask = DownloadG2PWModelAsync();
        _g2pwModelDownloadTask = downloadTask;
        try
        {
            await downloadTask;
        }
        finally
        {
            if (ReferenceEquals(_g2pwModelDownloadTask, downloadTask))
                _g2pwModelDownloadTask = null;
        }
    }

    private async Task DownloadG2PWModelAsync()
    {
        if (NetworkEnabledCheck?.IsChecked == false)
        {
            PinyinEngineStatusText.Text = T("请先在应用设置中开启网络访问，再下载 g2pW 模型。");
            return;
        }

        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _g2pwModelDownloadCancellation = downloadCancellation;
        _g2pwModelDownloadBusy = true;
        SetPinyinEngineDownloadBusy(true);
        PinyinEngineStatusText.Text = T("准备下载 g2pW 模型…");

        var canceled = false;
        string? error = null;
        try
        {
            var progress = new Progress<G2PWModelDownloadProgress>(
                UpdatePinyinEngineDownloadProgress);
            await _g2pwModelDownloader.DownloadAsync(
                force: true,
                progress,
                downloadCancellation.Token);
            canceled = downloadCancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (Exception exception)
        {
            error = T("模型下载失败：{0}", UiText.Localize(exception.Message));
            Debug.WriteLine($"[Pinyin] g2pW model download failed: {exception}");
        }
        finally
        {
            if (ReferenceEquals(_g2pwModelDownloadCancellation, downloadCancellation))
                _g2pwModelDownloadCancellation = null;
            _g2pwModelDownloadBusy = false;
            SetPinyinEngineDownloadBusy(false);

            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                RefreshPinyinEngineStatus();
                if (canceled)
                    PinyinEngineStatusText.Text = T("模型下载已取消。");
                else if (error is not null)
                    PinyinEngineStatusText.Text = error;
            }
        }
    }

    private void PinyinEngineDownloadButton_Click(object? sender, RoutedEventArgs e) =>
        _ = HandlePinyinEngineDownloadRequestAsync();

    private void PinyinEngineCancelButton_Click(object? sender, RoutedEventArgs e) =>
        _g2pwModelDownloadCancellation?.Cancel();

    private static string FormatPinyinBytes(long bytes)
    {
        const long Kilobyte = 1024;
        const long Megabyte = Kilobyte * 1024;
        return bytes >= Megabyte
            ? $"{bytes / (double)Megabyte:0.0} MB"
            : bytes >= Kilobyte
                ? $"{bytes / (double)Kilobyte:0} KB"
                : $"{bytes} B";
    }
}
