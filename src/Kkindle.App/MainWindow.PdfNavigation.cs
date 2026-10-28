using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private NativePdfReaderHost? _readerThumbnailHost;
    private bool _syncingPdfThumbnails;

    private bool TryHandleReaderPdfHistoryKey(KeyEventArgs e)
    {
        if (!ReaderRoot.IsVisible || !_readerIsPdf || IsReaderTextInputFocused()
            || !(e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right
                || e.KeyModifiers == KeyModifiers.None && e.Key == Key.Back)) return false;
        // Handle before Avalonia's Alt-key focus navigation consumes the arrow.
        e.Handled = true;
        FocusCurrentReaderHost();
        _ = ObserveReaderTaskAsync(NavigateReaderPdfHistoryAsync(e.Key == Key.Right ? 1 : -1));
        return true;
    }

    private void UpdateReaderPdfNavigationUi()
    {
        if (ReaderPdfBackButton is null) return;
        var pdf = _readerIsPdf ? CurrentReaderHost as NativePdfReaderHost : null;
        ReaderPdfBackButton.IsVisible = ReaderPdfForwardButton.IsVisible = ReaderPdfThumbnailsButton.IsVisible = pdf is not null;
        ReaderPdfThumbnailsTabButton.IsVisible = pdf is not null;
        ReaderAssistantTabsGrid.ColumnDefinitions[2].Width = pdf is not null
            ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ReaderPdfBackButton.IsEnabled = pdf?.CanGoPrevious == true;
        ReaderPdfForwardButton.IsEnabled = pdf?.CanGoNext == true;
        ReaderPagePositionButton.IsEnabled = pdf is not null;
        if (!ReferenceEquals(pdf, _readerThumbnailHost))
        {
            ReaderPagePositionButton.Flyout?.Hide();
            ReaderPdfThumbnailsList.ItemsSource = null;
            _readerThumbnailHost = pdf;
        }
        if (pdf is null)
        {
            if (ReaderPdfThumbnailsView.IsVisible)
            {
                ReaderPdfThumbnailsView.IsVisible = false;
                ReaderAiView.IsVisible = ReaderAiComposer.IsVisible = true;
                SetReaderAssistantTabState(ReaderAiTabButton, selected: true);
                SetReaderAssistantTabState(ReaderPdfThumbnailsTabButton, selected: false);
            }
            return;
        }
        ToolTip.SetTip(ReaderPagePositionButton, pdf.GetPageDescription(pdf.PageNumber) + T(" · 点击跳页或校正页码"));
        if (ReaderPdfThumbnailsView.IsVisible || ReaderPdfThumbnailsList.ItemsSource is not null)
            SyncReaderPdfThumbnails(pdf);
    }

    private void SyncReaderPdfThumbnails(NativePdfReaderHost pdf, bool revealCurrentPage = false)
    {
        var pageIndex = pdf.PageNumber - 1;
        var selectionChanged = ReaderPdfThumbnailsList.SelectedIndex != pageIndex;
        var rebuild = ReaderPdfThumbnailsList.ItemsSource is not PdfThumbnailItem[] items
            || items.Length != pdf.PageCount
            || items[pageIndex].Label != pdf.GetPageDescription(pdf.PageNumber);
        _syncingPdfThumbnails = true;
        try
        {
            if (rebuild)
                ReaderPdfThumbnailsList.ItemsSource = Enumerable.Range(1, pdf.PageCount)
                    .Select(page => new PdfThumbnailItem(pdf, page, pdf.GetPageDescription(page))).ToArray();
            ReaderPdfThumbnailsList.SelectedIndex = pageIndex;
        }
        finally { _syncingPdfThumbnails = false; }
        if (ReaderAssistantPanel.IsVisible && ReaderPdfThumbnailsView.IsVisible && (selectionChanged || revealCurrentPage || rebuild))
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(pdf, CurrentReaderHost) && ReaderPdfThumbnailsView.IsEffectivelyVisible)
                    ReaderPdfThumbnailsList.ScrollIntoView(pdf.PageNumber - 1);
            });
    }

    private async void ReaderPdfPreviousPageButton_Click(object? sender, RoutedEventArgs e)
    {
        await ObserveReaderTaskAsync(MoveReaderPdfPositionAsync(-1));
    }

    private async void ReaderPdfNextPageButton_Click(object? sender, RoutedEventArgs e)
    {
        await ObserveReaderTaskAsync(MoveReaderPdfPositionAsync(1));
    }

    private async Task NavigateReaderPdfHistoryAsync(int direction)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        CancelReaderPdfNavigation();
        await _readerTts.StopAsync();
        HideReaderSelectionPopup();
        HideReaderAnnotationInputPopup();
        HideReaderAnnotationHoverPopup();
        if (await pdf.NavigateHistoryAsync(direction, ReaderToken) && ReferenceEquals(pdf, CurrentReaderHost))
        {
            SyncReaderPdfTocSelection();
            UpdateReaderToolbar();
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(ReaderToken);
        }
    }

    private async Task FollowReaderPdfLinkAsync(JsonElement message)
    {
        // JsonElement belongs to the bridge's short-lived document. Read all
        // values before awaiting navigation or the first render.
        var uri = ReadString(message, "uri");
        var page = message.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var number) ? number : 0;
        double? Coordinate(string key) => message.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
        var top = Coordinate("top");
        var left = Coordinate("left");
        var zoom = Coordinate("zoom");
        if (Uri.TryCreate(uri, UriKind.Absolute, out var external) && external.Scheme is "http" or "https" or "mailto")
        {
            Process.Start(new ProcessStartInfo(external.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        if (CurrentReaderHost is not NativePdfReaderHost pdf || page < 1 || page > pdf.PageCount) return;
        if (!await NavigatePdfPageCoreAsync(page, ReaderToken, saveProgress: false, syncTocSelection: true)) return;
        if (!ReferenceEquals(pdf, CurrentReaderHost)) return;
        // /XYZ zoom is an absolute PDF scale; the reader's zoom is relative to
        // its chosen fit scale. Preserve the reader's scale for link jumps.
        _ = zoom;
        if (top is { } y) pdf.ScrollToTop(y);
        if (left is { } x && top is { } pointY) pdf.ScrollToPdfPoint(x, pointY);
        await pdf.RefreshViewportAsync(ReaderToken);
        SyncReaderPdfTocSelection();
        await SaveReaderProgressAsync(ReaderToken);
    }

    private void ReaderPdfThumbnailsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        if (_readerZenMode) ExitReaderZenMode();
        ReaderAssistantPanel.IsVisible = true;
        ReaderAiView.IsVisible = false;
        ReaderNotesView.IsVisible = false;
        ReaderPdfThumbnailsView.IsVisible = true;
        SetReaderAiPanelWidth(_readerPdfThumbnailsPanelWidth);
        ReaderAiComposer.IsVisible = false;
        ReaderNotesExportBar.IsVisible = false;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: false);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: false);
        SetReaderAssistantTabState(ReaderPdfThumbnailsTabButton, selected: true);
        SyncReaderPdfThumbnails(pdf, revealCurrentPage: true);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }

    private async void ReaderPdfThumbnailsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPdfThumbnails || ReaderPdfThumbnailsList.SelectedItem is not PdfThumbnailItem item
            || !ReferenceEquals(item.Host, CurrentReaderHost)) return;
        await ObserveReaderTaskAsync(NavigatePdfPageAsync(item.PageNumber, ReaderToken));
    }

    private void ReaderPagePositionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        ReaderPdfPageJumpBox.Text = pdf.GetPageLabel(pdf.PageNumber);
        ReaderPdfPrintedPageBox.Text = pdf.GetPageLabel(pdf.PageNumber);
        ReaderPdfPageJumpKind.SelectedIndex = 0;
        ReaderPdfPageJumpStatus.Text = pdf.GetPageDescription(pdf.PageNumber);
    }
    private async void ReaderPdfPageJumpButton_Click(object? sender, RoutedEventArgs e) => await ObserveReaderTaskAsync(JumpReaderPdfPageAsync());
    private async void ReaderPdfPageJumpBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ObserveReaderTaskAsync(JumpReaderPdfPageAsync());
    }
    private async Task JumpReaderPdfPageAsync()
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        var label = ReaderPdfPageJumpBox.Text?.Trim() ?? "";
        var page = ReaderPdfPageJumpKind.SelectedIndex == 1
            ? int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1
            : pdf.FindLabeledPage(label);
        if (page < 1 || page > pdf.PageCount)
        {
            ReaderPdfPageJumpStatus.Text = T("找不到该页，请检查页码或切换为文件页码。");
            return;
        }
        ReaderPagePositionButton.Flyout?.Hide();
        await NavigatePdfPageAsync(page, ReaderToken);
    }
    private async void ReaderPdfPageMappingButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        if (!int.TryParse(ReaderPdfPrintedPageBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var printed)
            || printed is < 1 or > 1_000_000)
        {
            ReaderPdfPageJumpStatus.Text = T("请输入当前页面上印刷的数字页码（例如 1）。");
            return;
        }
        pdf.SetPageNumberOffset(printed - pdf.PageNumber);
        ReaderPdfPageJumpStatus.Text = T("已校正：{0}", pdf.GetPageDescription(pdf.PageNumber));
        await ObserveReaderTaskAsync(SaveReaderProgressAsync(ReaderToken));
    }
    private async void ReaderPdfResetPageMappingButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        pdf.SetPageNumberOffset(null);
        ReaderPdfPageJumpStatus.Text = T("已恢复 PDF 自带页码。");
        await ObserveReaderTaskAsync(SaveReaderProgressAsync(ReaderToken));
    }
}
