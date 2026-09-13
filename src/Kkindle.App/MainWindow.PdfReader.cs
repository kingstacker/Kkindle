using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private Task _readerPdfIndexTask = Task.CompletedTask;
    private Task _readerPdfOutlineTask = Task.CompletedTask;
    private bool _readerPdfIndexReady;
    private string? _readerPdfIndexError;

    private void StartReaderPdfBackgroundWork(NativePdfReaderHost host, Guid fileId, CancellationToken token)
    {
        _readerPdfOutlineTask = LoadReaderPdfOutlineAsync(host, fileId, token);
        _readerPdfIndexTask = BuildReaderPdfTextIndexAsync(host, fileId, token);
    }

    private bool IsCurrentPdfSession(NativePdfReaderHost host, Guid fileId, CancellationToken token) =>
        !token.IsCancellationRequested && _readerIsPdf && _readerBookFile?.Id == fileId && ReferenceEquals(CurrentReaderHost, host);

    private async Task LoadReaderPdfOutlineAsync(NativePdfReaderHost host, Guid fileId, CancellationToken token)
    {
        try
        {
            // First paint must not wait for thousands of bookmarks or text pages.
            await Task.Delay(60, token);
            var info = await host.ReadDocumentInfoAsync(token);
            if (!IsCurrentPdfSession(host, fileId, token)) return;
            var source = new Uri(_readerPdfSourcePath!).AbsoluteUri;
            _readerTocItems = info.Outline.Count > 0
                ? info.Outline.Select((item, index) => new EpubReaderNavigationItem(item.Title,
                    source + $"#page={item.PageNumber}&top={(item.Top ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}&outline={index}",
                    item.PageNumber - 1, item.Level)).ToArray()
                : _readerPdfPages.Select(page => new EpubReaderNavigationItem(T("第 {0} 页", page.PageNumber),
                    source + $"#page={page.PageNumber}", page.PageNumber - 1)).ToArray();
            BuildReaderTocRows();
            CollapseReaderTocToCurrentChapter(_readerChapterIndex);
            SetReaderCompactNavigationItems(_readerTocItems);
            ReaderTocEmptyText.IsVisible = false;
            SyncReaderPdfTocSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrentPdfSession(host, fileId, token)) ReaderTocEmptyText.Text = UiText.Localize(exception.Message);
        }
    }

    private async Task BuildReaderPdfTextIndexAsync(NativePdfReaderHost host, Guid fileId, CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token);
            var pages = await host.BuildTextIndexAsync(token);
            if (!IsCurrentPdfSession(host, fileId, token)) return;
            _readerPdfPages = pages;
            _readerPdfIndexReady = true;
            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrentPdfSession(host, fileId, token)) _readerPdfIndexError = exception.Message;
        }
    }

    private async Task EnsureReaderPdfTextIndexAsync(CancellationToken token)
    {
        if (!_readerPdfIndexReady) await _readerPdfIndexTask.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (_readerPdfIndexError is { } error) throw new IOException(error);
    }

    private void RememberCurrentPdfPageText()
    {
        if (CurrentReaderHost is not NativePdfReaderHost host || _readerPdfPages is not PdfPageText[] pages) return;
        foreach (var page in host.VisiblePageNumbers.Append(host.PageNumber).Distinct())
            if (page >= 1 && page <= pages.Length && host.GetPageContent(page) is { } content)
                pages[page - 1] = new(page, content.Text);
    }

    private async Task SetReaderPdfDisplayModeAsync(PdfReaderDisplayMode mode)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        await _readerTts.StopAsync();
        _selectedReaderAnnotation = null;
        HideReaderSelectionPopup();
        HideReaderAnnotationInputPopup();
        await pdf.SetDisplayModeAsync(mode);
        if (!ReferenceEquals(pdf, CurrentReaderHost)) return;
        UpdateReaderToolbar();
        ReaderChapterText.Text = GetReaderChapterPositionLabel();
        SyncReaderPdfTocSelection();
        await SaveReaderProgressAsync(ReaderToken);
    }

    private async void ReaderPdfRotateButton_Click(object? sender, RoutedEventArgs e) => await RotateReaderPdfAsync();

    private async Task RotateReaderPdfAsync()
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        await _readerTts.StopAsync();
        _selectedReaderAnnotation = null;
        HideReaderSelectionPopup();
        HideReaderAnnotationInputPopup();
        await pdf.RotateClockwiseAsync();
        if (!ReferenceEquals(pdf, CurrentReaderHost)) return;
        SyncReaderPdfTocSelection();
        await SaveReaderProgressAsync(ReaderToken);
    }

    private async void ReaderPdfFitItem_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf || sender is not MenuItem { Tag: string tag }) return;
        ReaderPdfFitButton.Flyout?.Hide();
        await pdf.SetFitModeAsync(tag == "page" ? PdfReaderFitMode.Page : PdfReaderFitMode.Width);
        if (!ReferenceEquals(pdf, CurrentReaderHost)) return;
        UpdateReaderToolbar();
        await SaveReaderProgressAsync(ReaderToken);
    }

    private string GetPdfPagePositionLabel()
    {
        var total = Math.Max(1, _readerPdfPages.Count);
        if (CurrentReaderHost is NativePdfReaderHost { DisplayMode: PdfReaderDisplayMode.TwoPage })
        {
            var first = (_readerPdfPage - 1) / 2 * 2 + 1;
            var last = Math.Min(total, first + 1);
            if (last > first) return $"{first}–{last} / {total}";
        }
        return T("{0} / {1}", _readerPdfPage, total);
    }
}
