using System.Runtime.ExceptionServices;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private string? _lastKindleAnnotationSyncPromptSignature;
    private string? _kindleAnnotationAutoCheckedIdentity;
    private CancellationTokenSource? _kindleAnnotationAnalysisCancellation;
    private bool _kindleAnnotationSyncPromptBusy;

    /// <summary>
    /// Kindle annotation work is reported in the task record instead of an
    /// inline panel. Writes are display-only so they cannot be cancelled
    /// between backing up and replacing the device database.
    /// </summary>
    private async Task<T> RunKindleAnnotationTaskAsync<T>(
        string title,
        bool cancellable,
        Func<IProgress<KindleAnnotationSyncProgress>, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var handle = BackgroundTasks.Start(title, BackgroundTaskKind.Other, async (context, token) =>
        {
            context.ReportProgress(DescribeKindleAnnotationStage(KindleAnnotationSyncStage.ReadingDevice));
            var progress = new Progress<KindleAnnotationSyncProgress>(value =>
            {
                var stage = DescribeKindleAnnotationStage(value.Stage);
                if (!string.IsNullOrWhiteSpace(value.BookTitle)) stage += " · 《" + value.BookTitle + "》";
                if (value.Total <= 0)
                    context.ReportProgress(stage);
                else if (value.Stage is KindleAnnotationSyncStage.ProcessingBooks
                         or KindleAnnotationSyncStage.ImportingToComputer
                         && value.Total <= int.MaxValue)
                    context.ReportProgress(stage, (int)Math.Min(value.Completed, value.Total), (int)value.Total);
                else
                    context.ReportProgress(stage, (int)Math.Round(value.Percentage ?? 0), 100);
            });
            try { result = await operation(progress, token); }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }, cancellationToken, allowRetry: false);
        if (!cancellable) _displayOnlyTaskIds.Add(handle.Id);
        await handle.Completion;
        failure?.Throw();
        return result;
    }

    private static string DescribeKindleAnnotationStage(KindleAnnotationSyncStage stage) => stage switch
    {
        KindleAnnotationSyncStage.ReadingDevice => T("正在读取 Kindle 批注"),
        KindleAnnotationSyncStage.MatchingBooks => T("正在匹配共同书籍"),
        KindleAnnotationSyncStage.ProcessingBooks => T("正在检查书籍批注"),
        KindleAnnotationSyncStage.ReadingBook => T("正在读取划线原文"),
        KindleAnnotationSyncStage.ImportingToComputer => T("正在更新电脑批注"),
        KindleAnnotationSyncStage.PreparingWrite => T("正在准备同步内容"),
        KindleAnnotationSyncStage.BackingUp => T("正在备份批注"),
        KindleAnnotationSyncStage.WritingToDevice => T("正在写入 Kindle 批注"),
        KindleAnnotationSyncStage.Verifying => T("正在读回校验"),
        KindleAnnotationSyncStage.Ready => T("批注检查完成"),
        _ => T("批注同步完成")
    };

    private void ResetKindleAnnotationSyncPrompt()
    {
        _lastKindleAnnotationSyncPromptSignature = null;
        _kindleAnnotationAutoCheckedIdentity = null;
    }

    /// <summary>
    /// Analysis only reads the device, so disconnecting or ejecting cancels it
    /// instead of waiting for every shared book to be copied.
    /// </summary>
    private void CancelKindleAnnotationAnalysis()
    {
        try { _kindleAnnotationAnalysisCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void UpdateKindleAnnotationSyncButtonVisibility()
    {
        SyncKindleAnnotationsButton.IsVisible = ReadingMaterialsPage.IsVisible
            && CurrentDevice is { CanReadNotes: true } device
            && device.Profile.Family == ReaderDeviceFamily.Kindle
            && _kindle is not null;
        SyncKindleAnnotationsButton.IsEnabled = !_kindleAnnotationSyncPromptBusy;
    }

    private async void SyncKindleAnnotationsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is not { CanReadNotes: true } device || _kindle is null) return;
        ResetKindleAnnotationSyncPrompt();
        await PromptKindleAnnotationSyncIfNeededAsync(device, manual: true);
    }

    private async Task PromptKindleAnnotationSyncIfNeededAsync(
        KindleDevice device,
        bool manual = false)
    {
        if (_kindle is null
            || _kindleAnnotationSyncPromptBusy
            || !ReadingMaterialsPage.IsVisible
            || device.Profile.Family != ReaderDeviceFamily.Kindle
            || !_deviceBooksLoaded
            || _deviceBooksShowingCache
            || _deviceBookScanFailed
            || DeviceBooks.Count == 0
            || HasActiveDeviceOperations
            || _confirmationCompletion is not null
            || _bookRemovalCompletion is not null
            || _deviceDuplicateCompletion is not null
            || _messageCompletion is not null)
            return;
        // Automatic checks run once per connection; the toolbar button can
        // always re-check. Every check reads the whole shared library.
        if (!manual && string.Equals(
                _kindleAnnotationAutoCheckedIdentity, device.Identity, StringComparison.Ordinal))
            return;
        _kindleAnnotationAutoCheckedIdentity = device.Identity;

        _kindleAnnotationSyncPromptBusy = true;
        UpdateKindleAnnotationSyncButtonVisibility();
        var analysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _kindleAnnotationAnalysisCancellation = analysisCancellation;
        try
        {
            var booksToCheck = DeviceBooks.Select(card => card.Book).ToArray();
            KindleAnnotationSyncPreview? preview = null;
            // Analysis copies books from the device, so it must be visible to
            // eject and other device work like any other transfer. It only
            // reads, so it can be cancelled from the task record.
            await TrackDeviceOperationAsync(async () =>
                preview = await RunKindleAnnotationTaskAsync(
                    T("检查 Kindle 批注差异"),
                    cancellable: true,
                    (progress, token) => Task.Run(() => _kindleAnnotationSync.AnalyzeAsync(
                        device, booksToCheck, _kindle, token, progress), token),
                    analysisCancellation.Token));
            if (preview is null || !IsCurrentDevice(device)) return;
            if (!preview.HasChanges)
            {
                _lastKindleAnnotationSyncPromptSignature = null;
                if (manual)
                {
                    var message = preview.UnsupportedCount > 0
                        ? T("没有发现可安全同步的差异；{0} 项未处理（位置无法确认、两端时间冲突，或电脑上已清空的笔记不会删除 Kindle 上的内容）。", preview.UnsupportedCount)
                        : T("电脑书库与 Kindle 的批注已经一致。");
                    ReadingMaterialsStatusText.Text = message;
                    await ShowMessageAsync(T("Kindle 批注"), message);
                }
                return;
            }
            if (!manual && string.Equals(
                    preview.Signature,
                    _lastKindleAnnotationSyncPromptSignature,
                    StringComparison.Ordinal))
                return;

            _lastKindleAnnotationSyncPromptSignature = preview.Signature;
            var details = string.Join(
                Environment.NewLine,
                preview.Books
                    .Where(book => book.ChangesToComputer + book.ChangesToDevice > 0)
                    .Take(6)
                    .Select(book => T(
                        "《{0}》：电脑 {1} 条，Kindle {2} 条；待同步 {3} 项",
                        book.LocalTitle,
                        book.LocalCount,
                        book.DeviceCount,
                        book.ChangesToComputer + book.ChangesToDevice)));
            var more = preview.BookCount > 6
                ? Environment.NewLine + T("另有 {0} 本书存在差异。", preview.BookCount - 6)
                : string.Empty;
            var unsupported = preview.UnsupportedCount > 0
                ? Environment.NewLine + Environment.NewLine
                    + T("其中 {0} 项暂不处理（位置无法确认、两端时间冲突，或电脑上已清空的笔记），将保持原样。", preview.UnsupportedCount)
                : string.Empty;
            var confirmed = await ConfirmAsync(
                T("发现 Kindle 批注差异"),
                T(
                    "检测到 {0} 本共同书籍的批注不一致。同步会合并两端记录；同一条划线存在不同笔记时，以更新时间较新的内容为准，不会自动删除任一端独有记录。",
                    preview.BookCount)
                + Environment.NewLine + Environment.NewLine
                + details
                + more
                + unsupported
                + Environment.NewLine + Environment.NewLine
                + T("同步期间请保持 Kindle 解锁，并暂时不要操作设备。"),
                T("立即同步"),
                _lifetimeCancellation.Token,
                allowDiagnosticAutoConfirm: false,
                cancelText: T("稍后"));
            if (!confirmed || !IsCurrentDevice(device)) return;

            var booksToSync = DeviceBooks.Select(card => card.Book).ToArray();

            KindleAnnotationSyncResult? result = null;
            Exception? failure = null;
            await TrackDeviceOperationAsync(async () =>
            {
                try
                {
                    result = await RunKindleAnnotationTaskAsync(
                        T("同步 Kindle 批注"),
                        cancellable: false,
                        (progress, _) => Task.Run(() => _kindleAnnotationSync.SynchronizeAsync(
                            device, booksToSync, _kindle, _lifetimeCancellation.Token, progress),
                            _lifetimeCancellation.Token),
                        _lifetimeCancellation.Token);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
            if (failure is not null)
            {
                ReadingMaterialsStatusText.Text = T(
                    "Kindle 批注同步失败：{0}",
                    UiText.Localize(failure.Message));
                await ShowMessageAsync(
                    T("批注同步失败"),
                    UiText.Localize(failure.Message));
                _lastKindleAnnotationSyncPromptSignature = null;
                return;
            }
            if (result is null) return;

            _deviceClippingCache.Remove(device.Identity);
            MarkReadingMaterialsDirty();
            _lastKindleAnnotationSyncPromptSignature = null;
            await RefreshReadingMaterialsAsync();
            var completion = T(
                "批注同步完成：导入电脑 {0} 项，写入 Kindle {1} 项。",
                result.ChangesToComputer,
                result.ChangesToDevice);
            if (result.UnsupportedCount > 0)
                completion += Environment.NewLine
                    + T("{0} 项未同步（位置无法确认、两端时间冲突，或电脑上已清空的笔记）。", result.UnsupportedCount);
            if (!string.IsNullOrWhiteSpace(result.LocalBackupPath))
                completion += Environment.NewLine
                    + T("本机备份：{0}", result.LocalBackupPath);
            // In MTP mode the Kindle keeps running and may hold the database
            // in memory; a restart makes it reload the replaced file.
            if (result.ChangesToDevice > 0 && device.Transport == KindleTransport.Wpd)
                completion += Environment.NewLine
                    + T("如果 Kindle 上暂未显示新批注，请断开连接后重启 Kindle 再查看。");
            ReadingMaterialsStatusText.Text = completion.ReplaceLineEndings(" · ");
            await ShowMessageAsync(T("批注同步完成"), completion);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            // The read-only check was cancelled from the task record, or by a
            // disconnect or eject; the record already shows it as cancelled.
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = T(
                "检查 Kindle 批注差异失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_kindleAnnotationAnalysisCancellation, analysisCancellation))
                _kindleAnnotationAnalysisCancellation = null;
            analysisCancellation.Dispose();
            _kindleAnnotationSyncPromptBusy = false;
            UpdateKindleAnnotationSyncButtonVisibility();
        }
    }
}
