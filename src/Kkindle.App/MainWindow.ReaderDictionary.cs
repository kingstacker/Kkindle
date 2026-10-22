using System.Diagnostics;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private const int MaximumWikipediaSelectionLength = 120;
    private readonly WikipediaLookupService _wikipediaLookupService = new();
    private WikipediaArticle? _readerDictionaryWikipediaArticle;
    private CancellationTokenSource? _readerDictionaryCancellation;
    private int _readerDictionaryRequestSequence;
    private bool _suppressReaderDictionaryPopupClosed;

    private async Task PerformReaderSelectionDictionaryAsync(bool preservePopup = false)
    {
        var term = (_readerPendingSelection ?? string.Empty).Trim();
        if (term.Length == 0) return;

        if (!preservePopup) HideReaderSelectionPopup();
        _readerDictionaryWikipediaArticle = null;
        ReaderDictionaryTermText.Text = term;
        ReaderDictionaryLocalStatusText.Text = T("正在查本地词典…");
        ReaderDictionaryLocalStatusText.IsVisible = true;
        ReaderDictionaryEntries.SetEntries([]);
        ReaderDictionaryEntries.TermRequested -= ReaderDictionaryTermRequested;
        ReaderDictionaryEntries.TermRequested += ReaderDictionaryTermRequested;
        ReaderDictionaryWikipediaExtractText.Text = string.Empty;
        ReaderDictionaryWikipediaStatusText.IsVisible = true;
        ReaderDictionaryWikipediaResultPanel.IsVisible = false;
        ReaderDictionaryWikipediaOpenButton.IsEnabled = false;

        var canSearchWikipedia = _appSettings.NetworkEnabled
            && term.Length <= MaximumWikipediaSelectionLength;
        ReaderDictionaryWikipediaStatusText.Text = !_appSettings.NetworkEnabled
            ? T("网络功能已关闭；本地词典仍可用。")
            : !canSearchWikipedia
                ? T("选中的内容太长，请只选一个词或短语查询维基百科。")
                : T("正在查询维基百科…");

        ReaderDictionaryPopup.MaxHeight = Math.Min(500, Math.Max(1, ReaderWebViewHost.Bounds.Height - 16));
        ShowReaderPopupNearSelection(
            ReaderDictionaryHostPopup,
            ReaderDictionaryPopup,
            fallbackWidth: 440,
            fallbackHeight: 140,
            _readerLastSelectionPopupAnchor,
            _readerLastSelectionPopupBottom,
            preferBelow: true);

        var requestSequence = ++_readerDictionaryRequestSequence;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerDictionaryCancellation = cancellation;
        try
        {
            var localTask = _dictionaryService.LookupAsync(term, cancellation.Token);
            var wikipediaTask = canSearchWikipedia
                ? _wikipediaLookupService.LookupAsync(term, UiText.CurrentLanguage, cancellation.Token)
                : null;

            try
            {
                var entries = await localTask;
                if (!IsCurrentReaderDictionaryRequest(requestSequence, cancellation)) return;
                ReaderDictionaryLocalStatusText.Text = entries.Count == 0
                    ? T("没有找到本地释义。请先在字典管理中导入本地词典。")
                    : string.Empty;
                ReaderDictionaryLocalStatusText.IsVisible = entries.Count == 0;
                ReaderDictionaryEntries.SetEntries(entries);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (!IsCurrentReaderDictionaryRequest(requestSequence, cancellation)) return;
                ReaderDictionaryLocalStatusText.Text = T(
                    "本地词典查询失败：{0}",
                    UiText.Localize(exception.Message));
                ReaderDictionaryLocalStatusText.IsVisible = true;
            }

            if (wikipediaTask is null) return;
            try
            {
                var article = await wikipediaTask;
                if (!IsCurrentReaderDictionaryRequest(requestSequence, cancellation)) return;
                if (article is null)
                {
                    ReaderDictionaryWikipediaStatusText.Text = T("没有找到同名维基百科条目。普通词义请查本地词典。");
                    return;
                }

                _readerDictionaryWikipediaArticle = article;
                ReaderDictionaryWikipediaExtractText.Text = article.Extract;
                ReaderDictionaryWikipediaStatusText.Text = string.Empty;
                ReaderDictionaryWikipediaStatusText.IsVisible = false;
                ReaderDictionaryWikipediaOpenButton.IsEnabled = true;
                ReaderDictionaryWikipediaResultPanel.IsVisible = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!IsCurrentReaderDictionaryRequest(requestSequence, cancellation)) return;
                ReaderDictionaryWikipediaStatusText.Text = T(
                    "维基百科查询失败：{0}",
                    UiText.Localize(exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_readerDictionaryCancellation, cancellation))
                _readerDictionaryCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool IsCurrentReaderDictionaryRequest(
        int requestSequence,
        CancellationTokenSource cancellation) =>
        requestSequence == _readerDictionaryRequestSequence
        && ReferenceEquals(_readerDictionaryCancellation, cancellation)
        && !cancellation.IsCancellationRequested;

    private async void ReaderDictionaryTermRequested(object? sender, string term)
    {
        if (!ReaderDictionaryHostPopup.IsOpen) return;
        CancelReaderDictionaryRequest();
        _readerPendingSelection = term;
        await PerformReaderSelectionDictionaryAsync(preservePopup: true);
    }

    private void ReaderDictionaryPopup_SizeChanged(object? sender, Avalonia.Controls.SizeChangedEventArgs e)
    {
        if (!ReaderDictionaryHostPopup.IsOpen) return;
        // The fixed tab viewport keeps async results and tab switches at the same anchor.
        ShowReaderPopupNearSelection(
            ReaderDictionaryHostPopup, ReaderDictionaryPopup, 440, 140,
            _readerLastSelectionPopupAnchor, _readerLastSelectionPopupBottom,
            preferBelow: true);
    }

    private void ReaderDictionaryCloseButton_Click(object? sender, RoutedEventArgs e)
        => HideReaderDictionaryPopup(clearSelection: true);

    private void ReaderDictionaryWikipediaOpenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerDictionaryWikipediaArticle is not { } article) return;
        try
        {
            Process.Start(new ProcessStartInfo(article.Url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ReaderDictionaryWikipediaStatusText.Text = UiText.Localize(exception.Message);
        }
    }

    private void ReaderDictionaryHostPopup_Closed(object? sender, EventArgs e)
    {
        if (_suppressReaderDictionaryPopupClosed) return;
        CancelReaderDictionaryRequest();
        ClearReaderSelectionAfterDictionary();
    }

    private void HideReaderDictionaryPopup(bool clearSelection)
    {
        CancelReaderDictionaryRequest();
        if (ReaderDictionaryHostPopup is { IsOpen: true })
        {
            _suppressReaderDictionaryPopupClosed = true;
            try
            {
                ReaderDictionaryHostPopup.IsOpen = false;
            }
            finally
            {
                _suppressReaderDictionaryPopupClosed = false;
            }
        }

        if (clearSelection)
            ClearReaderSelectionAfterDictionary();
    }

    private void CancelReaderDictionaryRequest()
    {
        _readerDictionaryRequestSequence++;
        _readerDictionaryCancellation?.Cancel();
        _readerDictionaryCancellation = null;
    }

    private void ClearReaderSelectionAfterDictionary()
    {
        if (IsLinuxReaderTextFallbackActive())
            ClearLinuxReaderTextFallbackVisualSelection();

        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _selectedReaderAnnotation = null;
        _readerDictionaryWikipediaArticle = null;
        if (!_readerIsPdf && CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }
}
