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

    private async Task PerformReaderSelectionDictionaryAsync()
    {
        var term = (_readerPendingSelection ?? string.Empty).Trim();
        if (term.Length == 0) return;

        HideReaderSelectionPopup();
        _readerDictionaryWikipediaArticle = null;
        ReaderDictionaryTermText.Text = term;
        ReaderDictionaryLocalStatusText.Text = T("正在查本地词典…");
        ReaderDictionaryLocalResultText.Text = string.Empty;
        ReaderDictionaryWikipediaTitleText.Text = string.Empty;
        ReaderDictionaryWikipediaExtractText.Text = string.Empty;
        ReaderDictionaryWikipediaResultPanel.IsVisible = false;
        ReaderDictionaryWikipediaOpenButton.IsEnabled = false;

        var canSearchWikipedia = _appSettings.NetworkEnabled
            && term.Length <= MaximumWikipediaSelectionLength;
        ReaderDictionaryWikipediaPrivacyText.IsVisible = canSearchWikipedia;
        ReaderDictionaryWikipediaStatusText.Text = !_appSettings.NetworkEnabled
            ? T("网络功能已关闭；本地词典仍可用。")
            : !canSearchWikipedia
                ? T("选中的内容太长，请只选一个词或短语查询维基百科。")
                : T("正在查询维基百科…");

        ShowReaderPopupNearSelection(
            ReaderDictionaryHostPopup,
            ReaderDictionaryPopup,
            fallbackWidth: 440,
            fallbackHeight: 430,
            _readerLastSelectionPopupAnchor,
            _readerLastSelectionPopupBottom);

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
                ReaderDictionaryLocalResultText.Text = string.Join(
                    $"{Environment.NewLine}{Environment.NewLine}",
                    entries.Select(entry => $"[{entry.DictionaryName}] {entry.Definition}"));
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
                ReaderDictionaryWikipediaTitleText.Text = article.Title;
                ReaderDictionaryWikipediaExtractText.Text = article.Extract;
                ReaderDictionaryWikipediaStatusText.Text = string.Empty;
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
