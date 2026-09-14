using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private bool _readingDataResetBusy;

    private async void ResetReadingDataButton_Click(object? sender, RoutedEventArgs e) =>
        await ResetReadingDataFromSettingsAsync();

    private string? ReadingDataResetBlockReason()
    {
        if (!_stage3Ready) return T("正在准备阅读数据，请稍后重试。");
        if (_readerDocument is not null || _readerIsPdf || _bookOpenInProgress > 0 || _readerCloseInProgress != 0)
            return T("请先关闭阅读器并返回书库，再重置阅读数据。");
        if (_s3SyncBusy || _backupBusy || _s3SyncExitInProgress)
            return T("请等待同步或备份任务完成后，再重置阅读数据。");
        return null;
    }

    private async Task ResetReadingDataFromSettingsAsync()
    {
        if (_readingDataResetBusy) return;
        if (ReadingDataResetBlockReason() is { } blocked)
        {
            SettingsReadingDataStatusText.Text = blocked;
            return;
        }

        _readingDataResetBusy = true;
        ResetReadingDataButton.IsEnabled = false;
        _s3LocalChangeSyncTimer.Stop();
        SettingsReadingDataStatusText.Text = string.Empty;
        try
        {
            if (!await ConfirmAsync(
                    T("重置阅读数据"),
                    T("确定清除所有阅读统计和进度吗？包括累计时长、每日统计和最近阅读记录。书籍、书签、划线与批注将保留。此操作无法撤销，开启云端同步后也会重置其他设备上的阅读统计和进度。"),
                    T("确认重置"), _lifetimeCancellation.Token, allowDiagnosticAutoConfirm: false))
                return;

            // The confirmation can stay open while an earlier operation is
            // completing. Recheck before changing any persisted data.
            if (ReadingDataResetBlockReason() is { } changed)
            {
                SettingsReadingDataStatusText.Text = changed;
                return;
            }

            SettingsReadingDataStatusText.Text = T("正在重置阅读数据…");
            await _readerData.ResetReadingDataAsync(_lifetimeCancellation.Token);
            await RefreshReadingDashboardAsync();
            SettingsReadingDataStatusText.Text = T("阅读统计和进度已重置。书籍、书签、划线与批注已保留。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SettingsReadingDataStatusText.Text = T("重置阅读数据失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _readingDataResetBusy = false;
            ResetReadingDataButton.IsEnabled = true;
            if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
                ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce);
        }
    }
}
