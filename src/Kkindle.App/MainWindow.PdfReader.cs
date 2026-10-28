using Avalonia.Controls;
using Avalonia.Automation;
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

    // The rail is cheap (the host already parsed the outline while opening the
    // document) so it starts before navigation. Full-text indexing stays after
    // the first paint: it walks every page and must never hold up the reader.
    private void StartReaderPdfOutlineWork(NativePdfReaderHost host, Guid fileId, CancellationToken token) =>
        _readerPdfOutlineTask = ApplyReaderPdfOutlineWhenReadyAsync(host, fileId, token);

    private void StartReaderPdfTextIndexWork(NativePdfReaderHost host, Guid fileId, CancellationToken token) =>
        _readerPdfIndexTask = BuildReaderPdfTextIndexAsync(host, fileId, token);

    private bool IsCurrentPdfSession(NativePdfReaderHost host, Guid fileId, CancellationToken token) =>
        !token.IsCancellationRequested && _readerIsPdf && _readerBookFile?.Id == fileId && ReferenceEquals(CurrentReaderHost, host);

    // The host parses the outline while it opens the document, so the rail can
    // be filled before the first page finishes rendering. Waiting for
    // ReadDocumentInfoAsync instead would queue behind that render (PDFium is
    // serialised process-wide) and the table of contents would only appear once
    // the page was ready.
    private async Task ApplyReaderPdfOutlineWhenReadyAsync(NativePdfReaderHost host, Guid fileId, CancellationToken token)
    {
        try
        {
            var waited = 0;
            while (host.CachedDocumentInfo is null && !token.IsCancellationRequested && waited < 3000)
            {
                await Task.Delay(15, token);
                waited += 15;
            }
        }
        catch (OperationCanceledException) { return; }
        await LoadReaderPdfOutlineAsync(host, fileId, token);
    }

    private async Task LoadReaderPdfOutlineAsync(NativePdfReaderHost host, Guid fileId, CancellationToken token)
    {
        try
        {
            // The outline is tiny next to a page raster (tens of ms) and it is
            // what the reader shows first, so let it take the PDFium lock right
            // away. Only the text index below waits for the first paint.
            await Task.Delay(1, token);
            var outlineWatch = System.Diagnostics.Stopwatch.StartNew();
            // The host parses the outline while opening the document; taking that
            // snapshot avoids waiting for the PDFium lock behind page rendering.
            var info = host.CachedDocumentInfo ?? await host.ReadDocumentInfoAsync(token);
            if (!IsCurrentPdfSession(host, fileId, token)) return;
            PdfAnnotTrace.Write($"pdf outline read: {outlineWatch.ElapsedMilliseconds}ms for {info.Outline.Count} items");
            _readerPdfEmbeddedOutline = info.Outline;
            outlineWatch.Restart();
            RebuildReaderPdfNavigationItems();
            PdfAnnotTrace.Write($"pdf toc rebuild: {outlineWatch.ElapsedMilliseconds}ms for {_readerTocItems.Count} items");
            ReaderTocEmptyText.IsVisible = _readerTocItems.Count == 0;
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

    private void RebuildReaderPdfNavigationItems()
    {
        if (!_readerIsPdf || string.IsNullOrWhiteSpace(_readerPdfSourcePath)) return;
        var source = new Uri(_readerPdfSourcePath).AbsoluteUri;
        _readerTocItems = _readerPdfEmbeddedOutline.Count > 0
            ? _readerPdfEmbeddedOutline.Select((item, index) => new EpubReaderNavigationItem(
                item.Title,
                source + $"#page={item.PageNumber}&top={(item.Top ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}&outline={index}",
                item.PageNumber - 1,
                item.Level)).ToArray()
            : _readerPdfPages.Select(page => new EpubReaderNavigationItem(
                T("第 {0} 页", page.PageNumber),
                source + $"#page={page.PageNumber}",
                page.PageNumber - 1)).ToArray();
        BuildReaderTocRows();
        CollapseReaderTocToCurrentChapter(_readerChapterIndex);
        SetReaderCompactNavigationItems(_readerTocItems);
        ReaderTocEmptyText.IsVisible = _readerTocItems.Count == 0;
        SyncReaderPdfTocSelection();
    }

    private void ReaderPdfPointNoteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        var enabled = !pdf.IsPointAnnotationMode;
        pdf.SetPointAnnotationMode(enabled);
        UpdateReaderPdfPointNoteButton(enabled);
        UpdateReaderPdfHandToolButton(pdf.IsHandToolMode);
        ShowReaderTransientStatus(enabled
            ? T("请点击 PDF 任意位置添加页面批注。")
            : T("已取消页面批注模式。"));
    }

    private void UpdateReaderPdfPointNoteButton(bool enabled)
    {
        var label = enabled ? T("取消页面批注") : T("添加页面批注");
        ToolTip.SetTip(ReaderPdfPointNoteButton, label);
        AutomationProperties.SetName(ReaderPdfPointNoteButton, label);
        ReaderPdfPointNoteButton.Classes.Set("active", enabled);
    }

    private void ReaderPdfHandToolButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        var enabled = !pdf.IsHandToolMode;
        pdf.SetHandToolMode(enabled);
        UpdateReaderPdfHandToolButton(enabled);
        UpdateReaderPdfPointNoteButton(pdf.IsPointAnnotationMode);
        ShowReaderTransientStatus(enabled
            ? T("抓手工具已启用，按住鼠标左键拖动页面即可移动。")
            : T("已退出抓手工具。"));
    }

    private void UpdateReaderPdfHandToolButton(bool enabled)
    {
        var label = enabled ? T("退出抓手工具") : T("抓手移动页面");
        ToolTip.SetTip(ReaderPdfHandToolButton, label);
        AutomationProperties.SetName(ReaderPdfHandToolButton, label);
        ReaderPdfHandToolButton.Classes.Set("active", enabled);
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
        var pdf = CurrentReaderHost as NativePdfReaderHost;
        string Label(int first, int last)
        {
            var physical = first == last ? $"{first} / {total}" : $"{first}–{last} / {total}";
            var a = pdf?.GetPageLabel(first) ?? first.ToString();
            var b = pdf?.GetPageLabel(last) ?? last.ToString();
            return a == first.ToString() && b == last.ToString() ? physical
                : T("书页 {0} · {1}", first == last ? a : $"{a}–{b}", physical);
        }
        if (CurrentReaderHost is NativePdfReaderHost { DisplayMode: PdfReaderDisplayMode.TwoPage })
        {
            var first = (_readerPdfPage - 1) / 2 * 2 + 1;
            var last = Math.Min(total, first + 1);
            if (last > first) return Label(first, last);
        }
        return Label(_readerPdfPage, _readerPdfPage);
    }

    private async Task MoveReaderPdfPositionAsync(int direction)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        direction = Math.Sign(direction);
        if (direction == 0) return;
        var page = pdf.GetAdjacentPage(direction);
        if (page != _readerPdfPage)
        {
            var token = ReaderToken;
            try
            {
                await NavigatePdfPageCoreAsync(page, token, saveProgress: true, syncTocSelection: true,
                    preserveVerticalPosition: pdf.DisplayMode == PdfReaderDisplayMode.TwoPage);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Closing or replacing the reader cancels the in-flight page
                // animation and is a normal navigation outcome.
            }
            catch (ObjectDisposedException) when (!ReferenceEquals(pdf, CurrentReaderHost))
            {
                // The outgoing PDF host can be disposed while its transition
                // callback is still completing.
            }
        }
    }
}
