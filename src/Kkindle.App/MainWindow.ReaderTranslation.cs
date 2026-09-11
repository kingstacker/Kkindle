using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private string _readerTranslationTargetLanguage = "zh-CN";
    private ReaderTranslationProvider _readerTranslationProvider = ReaderTranslationProvider.Google;
    private string _readerTranslationSource = string.Empty;
    private int _readerTranslationRequestSequence;
    private CancellationTokenSource? _readerTranslationCancellation;
    private bool _readerTranslationBusy;
    private bool _suppressReaderTranslationPopupClosed;

    private async void ReaderSelectionTranslationButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ReadBookTranslationSettingsFromControls();
        _readerTranslationProvider = settings.Provider switch
        {
            BookTranslationProvider.BingFree => ReaderTranslationProvider.Bing,
            BookTranslationProvider.GoogleFree => ReaderTranslationProvider.Google,
            _ => ReaderTranslationProvider.Ai
        };
        _readerTranslationTargetLanguage = TranslationLanguageCatalog.NormalizeTarget(settings.TargetLanguage);
        await TranslateReaderSelectionAsync(
            _readerTranslationProvider,
            settings.AiRequestsPerMinute,
            settings.GoogleProxyAddress);
    }

    private async Task TranslateReaderSelectionAsync(
        ReaderTranslationProvider provider,
        int aiRequestsPerMinute,
        string googleProxyAddress)
    {
        var source = (_readerPendingSelection ?? string.Empty).Trim();
        if (source.Length == 0) return;

        // Keep the selection alive while replacing an earlier translation
        // result. The next selection event will replace it naturally.
        HideReaderSelectionPopup();
        _readerTranslationProvider = provider;
        _readerTranslationSource = source;
        ReaderTranslationResultText.Text = string.Empty;
        ReaderTranslationSourceText.Text = source;
        ReaderTranslationProviderText.Text = GetReaderTranslationProviderLabel(provider);
        ReaderTranslationCopyButton.IsEnabled = false;
        ShowReaderTranslationPopup();

        if (!_appSettings.NetworkEnabled)
        {
            ReaderTranslationStatusText.Text = T("网络访问已关闭，无法使用翻译。");
            return;
        }

        if (provider == ReaderTranslationProvider.Ai)
        {
            if (!_appSettings.AiEnabled)
            {
                ReaderTranslationStatusText.Text = T("AI 已在应用设置中关闭。");
                return;
            }

            if (!_readerAiSettings.IsConfigured)
            {
                ReaderTranslationStatusText.Text = T("AI 翻译需要先配置 AI 服务、模型和 API Key。");
                return;
            }
        }

        _readerTranslationBusy = true;
        ReaderTranslationStatusText.Text = T("正在翻译…");
        var requestSequence = ++_readerTranslationRequestSequence;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerTranslationCancellation = cancellation;
        try
        {
            var result = await _translationService.TranslateAsync(
                source,
                provider,
                _readerTranslationTargetLanguage,
                provider == ReaderTranslationProvider.Ai ? _readerAiSettings : null,
                cancellation.Token,
                aiRequestsPerMinute,
                googleProxyAddress);
            if (requestSequence != _readerTranslationRequestSequence
                || !ReferenceEquals(_readerTranslationCancellation, cancellation))
                return;

            ReaderTranslationResultText.Text = result;
            ReaderTranslationStatusText.Text = T("翻译已完成");
            ReaderTranslationCopyButton.IsEnabled = result.Length > 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (requestSequence != _readerTranslationRequestSequence
                || !ReferenceEquals(_readerTranslationCancellation, cancellation))
                return;
            ReaderTranslationResultText.Text = string.Empty;
            ReaderTranslationStatusText.Text = T("翻译失败：{0}", UiText.Localize(exception.Message));
            ReaderTranslationCopyButton.IsEnabled = false;
        }
        finally
        {
            if (ReferenceEquals(_readerTranslationCancellation, cancellation))
            {
                _readerTranslationCancellation = null;
                _readerTranslationBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private void ShowReaderTranslationPopup()
    {
        ShowReaderPopupNearSelection(
            ReaderTranslationHostPopup,
            ReaderTranslationPopup,
            fallbackWidth: 420,
            fallbackHeight: 300,
            _readerLastSelectionPopupAnchor,
            _readerLastSelectionPopupBottom);
    }

    private async void ReaderTranslationCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var result = ReaderTranslationResultText.Text?.Trim() ?? string.Empty;
        if (result.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(result);
            ReaderTranslationStatusText.Text = T("译文已复制");
        }
    }

    private void ReaderTranslationCloseButton_Click(object? sender, RoutedEventArgs e)
        => HideReaderTranslationPopup(clearSelection: true);

    private void ReaderTranslationHostPopup_Closed(object? sender, EventArgs e)
    {
        if (_suppressReaderTranslationPopupClosed) return;
        CancelReaderTranslationRequest();
        if (!string.IsNullOrWhiteSpace(_readerTranslationSource))
            ClearReaderSelectionAfterTranslation();
    }

    private void HideReaderTranslationPopup(bool clearSelection)
    {
        CancelReaderTranslationRequest();
        if (ReaderTranslationHostPopup.IsOpen)
        {
            _suppressReaderTranslationPopupClosed = true;
            try
            {
                ReaderTranslationHostPopup.IsOpen = false;
            }
            finally
            {
                _suppressReaderTranslationPopupClosed = false;
            }
        }

        if (clearSelection)
            ClearReaderSelectionAfterTranslation();
    }

    private void CancelReaderTranslationRequest()
    {
        _readerTranslationRequestSequence++;
        _readerTranslationBusy = false;
        _readerTranslationCancellation?.Cancel();
        _readerTranslationCancellation = null;
    }

    private void ClearReaderSelectionAfterTranslation()
    {
        if (IsLinuxReaderTextFallbackActive())
            ClearLinuxReaderTextFallbackVisualSelection();

        _readerTranslationSource = string.Empty;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _selectedReaderAnnotation = null;
        if (!_readerIsPdf && CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }

    private string GetReaderTranslationProviderLabel(ReaderTranslationProvider provider) => provider switch
    {
        ReaderTranslationProvider.Google => T("Google 翻译"),
        ReaderTranslationProvider.Bing => T("必应翻译（免费）"),
        ReaderTranslationProvider.Ai => T("AI 翻译"),
        _ => T("翻译")
    };

    private void RefreshReaderTranslationLocalizedText()
    {
        if (ReaderTranslationHostPopup is null || !ReaderTranslationHostPopup.IsOpen) return;
        ReaderTranslationProviderText.Text = GetReaderTranslationProviderLabel(_readerTranslationProvider);
        if (_readerTranslationBusy)
            ReaderTranslationStatusText.Text = T("正在翻译…");
    }
}
