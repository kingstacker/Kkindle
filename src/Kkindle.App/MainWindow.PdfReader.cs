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
        _readerPdfPaperAnalysisTask = BuildReaderPdfPaperAnalysisAsync(host, fileId, token);
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
            _readerPdfEmbeddedOutline = info.Outline;
            RebuildReaderPdfNavigationItems();
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

    private async Task BuildReaderPdfPaperAnalysisAsync(
        NativePdfReaderHost host,
        Guid fileId,
        CancellationToken token)
    {
        try
        {
            await _readerPdfIndexTask.WaitAsync(token);
            if (!IsCurrentPdfSession(host, fileId, token)) return;
            _readerPdfPaperAnalysis ??= PdfPaperAnalysisService.Analyze(_readerPdfPages);
            RefreshReaderPdfPaperNavigation();
            RebuildReaderPdfNavigationItems();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrentPdfSession(host, fileId, token))
                ReaderPdfPaperStatusText.Text = T("论文结构识别失败：{0}", UiText.Localize(exception.Message));
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
            : _readerPdfPaperAnalysis?.Sections.Count > 0
                ? _readerPdfPaperAnalysis.Sections.Select(item => new EpubReaderNavigationItem(
                    item.Title,
                    source + $"#page={item.PageNumber}&offset={item.StartOffset}",
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
        if (ReaderPdfPaperItems.Count > 0)
            ReaderPdfPaperStatusText.Text = T("已识别 {0} 个结构入口", ReaderPdfPaperItems.Count);
        SyncReaderPdfTocSelection();
    }

    private void RefreshReaderPdfPaperNavigation()
    {
        ReaderPdfPaperItems.Clear();
        if (_readerPdfPaperAnalysis is null)
        {
            ReaderPdfPaperStatusText.Text = T("正在识别论文结构…");
            return;
        }

        foreach (var item in _readerPdfPaperAnalysis.Sections
                     .Concat(_readerPdfPaperAnalysis.Elements.Take(200))
                     .OrderBy(item => item.PageNumber)
                     .ThenBy(item => item.StartOffset)
                     .ThenBy(item => item.Kind))
            ReaderPdfPaperItems.Add(new ReaderPdfPaperNavigationViewModel(item));
        ReaderPdfPaperStatusText.Text = ReaderPdfPaperItems.Count == 0
            ? T("未识别到论文结构，可使用目录或全文搜索。")
            : T("已识别 {0} 个结构入口 · {1} 个文字页面",
                ReaderPdfPaperItems.Count,
                _readerPdfPaperAnalysis.TextPageCount);
        ReaderPdfPaperButton.IsVisible = _readerIsPdf && ReaderPdfPaperItems.Count > 0;
    }

    private async void ReaderPdfPaperNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderPdfPaperNavigationViewModel item }
            || !_readerIsPdf
            || string.IsNullOrWhiteSpace(_readerPdfSourcePath))
            return;

        var target = new Uri(_readerPdfSourcePath).AbsoluteUri
            + $"#page={item.Item.PageNumber}&offset={item.Item.StartOffset}";
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(
                item.Title,
                target,
                item.Item.PageNumber - 1,
                item.Item.Level),
            ReaderToken,
            ReaderNavigationIntent.Toc);
    }

    private void ReaderPdfPointNoteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        var enabled = !pdf.IsPointAnnotationMode;
        pdf.SetPointAnnotationMode(enabled);
        ReaderPdfPointNoteButton.Content = enabled ? "取消页注" : "页注";
        ShowReaderTransientStatus(enabled
            ? T("请点击或右键点击 PDF 任意位置添加页面批注。")
            : T("已取消页面批注模式。"));
    }

    private void ReaderPdfRegionAiButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        var enabled = !pdf.IsRegionSelectionMode;
        pdf.SetRegionSelectionMode(enabled);
        ReaderPdfRegionAiButton.Content = enabled ? "取消框选" : "区域 AI";
        ShowReaderTransientStatus(enabled
            ? T("请在 PDF 页面上拖拽框选图、表或公式区域。")
            : T("已取消区域 AI 框选。"));
    }

    private async Task HandleReaderPdfRegionSelectionAsync(PdfRegionSelection selection)
    {
        if (!_readerIsPdf || CurrentReaderHost is not NativePdfReaderHost pdf)
            return;
        selection = selection.Normalize();
        if (selection.PageNumber != pdf.PageNumber)
            await NavigatePdfPageAsync(selection.PageNumber, ReaderToken, saveProgress: false);
        if (CurrentReaderHost is not NativePdfReaderHost currentPdf
            || currentPdf.PageNumber != selection.PageNumber)
            return;

        _readerPendingPdfRegion = selection;
        try
        {
            ReaderStatusText.Text = T("正在准备 PDF 区域图像…");
            var png = await currentPdf.CaptureRegionPngAsync(
                selection.PageNumber,
                new PdfPageCrop(selection.X, selection.Y, selection.Width, selection.Height),
                ReaderToken);
            if (png is null || png.Length == 0)
            {
                ReaderStatusText.Text = T("PDF 区域图像准备失败。");
                return;
            }

            if (!_readerZenMode)
            {
                ReaderAssistantPanel.IsVisible = true;
                SetReaderAiPanelWidth(_readerAiPanelWidth);
            }
            ShowReaderAiTab();
            await SendReaderAiQuestionAsync(
                T("请解释 PDF 第 {0} 页框选区域中的内容：如果是图表，请说明坐标轴、趋势和结论；如果是公式，请解释变量与含义；如果是表格，请概括关键数据。请区分图像中直接可见的事实和你的推断。", selection.PageNumber),
                ReaderAiRequestKind.RegionExplain,
                image: new AiImageAttachment("image/png", png));
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("PDF 区域 AI 失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _readerPendingPdfRegion = null;
            if (CurrentReaderHost is NativePdfReaderHost activePdf)
                activePdf.ClearRegionSelection();
            UpdateReaderToolbar();
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
        if (CurrentReaderHost is NativePdfReaderHost { DisplayMode: PdfReaderDisplayMode.PaperColumns } pdf)
        {
            var column = pdf.PaperColumnIndex == 0 ? T("左栏") : T("右栏");
            return T("第 {0} 页 · {1} / {2}", _readerPdfPage, column, total);
        }
        if (CurrentReaderHost is NativePdfReaderHost { DisplayMode: PdfReaderDisplayMode.TwoPage })
        {
            var first = (_readerPdfPage - 1) / 2 * 2 + 1;
            var last = Math.Min(total, first + 1);
            if (last > first) return $"{first}–{last} / {total}";
        }
        return T("{0} / {1}", _readerPdfPage, total);
    }

    private async Task MoveReaderPdfPositionAsync(int direction)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        direction = Math.Sign(direction);
        if (direction == 0) return;
        if (pdf.DisplayMode == PdfReaderDisplayMode.PaperColumns)
        {
            if (!await pdf.TurnPaperColumnAsync(direction, ReaderToken))
            {
                ReaderStatusText.Text = direction < 0
                    ? T("已经是 PDF 第一页。")
                    : T("已经是 PDF 最后一页。");
                return;
            }
            _readerPdfPage = pdf.PageNumber;
            _readerChapterIndex = _readerPdfPage - 1;
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            UpdateReaderToolbar();
            await SaveReaderProgressAsync(ReaderToken);
            return;
        }

        var page = pdf.GetAdjacentPage(direction);
        if (page != _readerPdfPage)
            await NavigatePdfPageAsync(page, ReaderToken);
    }
}
