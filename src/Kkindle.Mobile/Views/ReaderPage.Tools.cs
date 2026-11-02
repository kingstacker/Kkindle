using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Kkindle.ApplicationServices;
using Kkindle.ApplicationServices.Tts;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

/// <summary>Search, reading aloud, the AI assistant, translation and pinch zoom.</summary>
public partial class ReaderPage
{
    private const int MaxSearchResults = 200;
    private const int ExcerptPadding = 24;
    private const int ExcerptLength = 90;
    private const double MaxPdfZoom = 4;
    private const double ZoomStep = 0.04;
    private const int MaxContextCharacters = 4000;

    private TtsService? _tts;
    private IReadOnlyList<PdfPageText>? _pdfText;
    private string? _lastSearch;
    private bool _pinching;
    private double _pinchStartZoom = 1;
    private double _pinchRequestedZoom = 1;

    // ---------- search

    private void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowSearch();

    public void ShowSearch()
    {
        HideSelectionBar();
        var sheet = new SearchSheet(SearchBookAsync, hit => _ = JumpToSearchHitAsync(hit), _lastSearch);
        _shell.ShowSheet("搜索", sheet, ThemeScope.RequestedThemeVariant, () => _lastSearch = sheet.Query);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchBookAsync(
        string query, IProgress<string> progress, CancellationToken token)
    {
        if (_bookFile is null) return [];
        _lastSearch = query;
        if (_isPdf)
        {
            if (_pdfText is null)
            {
                progress.Report("正在读取 PDF 文字…");
                _pdfText = await new PdfTextService().ExtractAsync(
                    _modules.Library.Books.GetAbsoluteFilePath(_bookFile), token);
            }
            return PdfTextService.Search(_pdfText, query, MaxSearchResults, token)
                .Select(result => new SearchHit($"第 {result.PageNumber} 页", result.Excerpt, query, result))
                .ToList();
        }

        if (_epubDocument is not { } document) return [];
        progress.Report("正在搜索全书，第一次需要建立索引…");
        await _modules.Reader.Content.EnsureIndexedAsync(_book, _bookFile, document, token);
        var chunks = await _modules.Reader.Data.SearchBookAsync(
            _book.Id, query, MaxSearchResults, token, exactPhraseOnly: true, bookFileId: _bookFile.Id);
        return chunks
            .OrderBy(chunk => chunk.ChapterIndex)
            .ThenBy(chunk => chunk.StartOffset)
            .Select(chunk => new SearchHit(
                string.IsNullOrWhiteSpace(chunk.ChapterTitle) ? $"第 {chunk.ChapterIndex + 1} 章" : chunk.ChapterTitle,
                Excerpt(chunk.Content, query),
                query,
                chunk))
            .ToList();
    }

    private static string Excerpt(string content, string query)
    {
        var index = Math.Max(0, content.IndexOf(query, StringComparison.CurrentCultureIgnoreCase));
        var start = Math.Max(0, index - ExcerptPadding);
        var length = Math.Min(content.Length - start, ExcerptLength);
        return (start > 0 ? "…" : string.Empty) + content.Substring(start, length).ReplaceLineEndings(" ").Trim();
    }

    private async Task JumpToSearchHitAsync(SearchHit hit)
    {
        _shell.HideSheet();
        SetChromeVisible(false);
        try
        {
            if (hit.Source is PdfSearchResult pdfHit)
            {
                await GoToPdfPageAsync(pdfHit.PageNumber);
                (_readerHost as IPdfSearchHost)?.Find(hit.Query, pdfHit.MatchIndex);
                return;
            }

            if (hit.Source is not BookContentChunk chunk
                || _readerHost is not NativeReaderHost host
                || _epubDocument is not { } document)
            {
                return;
            }

            var chapter = Math.Clamp(chunk.ChapterIndex, 0, document.Chapters.Count - 1);
            if (chapter != _chapterIndex) await GoToChapterAsync(host, chapter, fromEnd: false, direction: 1);

            // Index offsets come from plain text, the host's from its own layout;
            // the nearest rendered match to the indexed one is the hit.
            var matches = host.FindSearchMatches(hit.Query);
            if (matches.Count == 0)
            {
                host.ScrollToOffset(chunk.StartOffset);
            }
            else
            {
                var target = chunk.StartOffset
                    + Math.Max(0, chunk.Content.IndexOf(hit.Query, StringComparison.CurrentCultureIgnoreCase));
                var focus = Enumerable.Range(0, matches.Count)
                    .MinBy(index => Math.Abs(matches[index].Start - target));
                host.SetSearchHighlights(matches.Select(match => (match.Start, match.Length)).ToList(), focus);
                host.ScrollToSearchHit(focus);
            }
            UpdateEpubLabels();
            _ = SaveProgressSoonAsync(force: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    // ---------- reading aloud

    private void OnListenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = ToggleListeningAsync();

    private async Task ToggleListeningAsync()
    {
        HideSelectionBar();
        if (_tts is { State: not (TtsPlaybackState.Stopped or TtsPlaybackState.Error) } running)
        {
            await running.StopAsync();
            return;
        }
        if (_shell.Services is not { CreateTtsEngine: { } createEngine, CreateTtsPlayer: { } createPlayer })
        {
            _shell.ShowToast("这台设备不支持朗读");
            return;
        }

        _tts ??= CreateSpeech(createEngine(), createPlayer());
        SetChromeVisible(false);
        SpeechBar.IsVisible = true;
        SpeechStatusText.Text = "正在准备朗读…";
        try
        {
            var settings = await _modules.Reader.TtsSettings.LoadAsync(_lifetime.Token);
            settings.AudioFormat = "wav";
            await _tts.StartAsync(settings, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            SpeechBar.IsVisible = false;
            _shell.ShowToast("朗读失败：" + UiText.Localize(exception.Message));
        }
    }

    private TtsService CreateSpeech(ITtsEngine engine, ITtsAudioPlayer player)
    {
        var tts = new TtsService(
            engine,
            _modules.Reader.TtsCache,
            player,
            () => Dispatcher.UIThread.CheckAccess()
                ? BuildSpeechDocument()
                : Dispatcher.UIThread.Invoke(BuildSpeechDocument),
            token => Dispatcher.UIThread.InvokeAsync(AdvanceForSpeechAsync).WaitAsync(token));
        tts.StateChanged += (_, e) => Dispatcher.UIThread.Post(() => PresentSpeech(e));
        return tts;
    }

    /// <summary>The text from the current reading position to the end of the chapter or page.</summary>
    private ReaderTtsDocument? BuildSpeechDocument()
    {
        if (_disposed || _bookFile is null) return null;
        var fileKey = _bookFile.Id.ToString("N");
        if (_readerHost is NativePdfReaderHost pdf)
        {
            var page = pdf.PageNumber;
            var text = pdf.GetPageContent(page)?.Text;
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new ReaderTtsDocument(
                $"{fileKey}:pdf:{page}",
                text,
                0,
                (start, length) => Dispatcher.UIThread.InvokeAsync(() => pdf.SetSpeechHighlight(start, length)).GetTask(),
                () => Dispatcher.UIThread.Post(pdf.ClearSpeechHighlight),
                null,
                fileKey,
                $"pdf:{page}");
        }

        if (_readerHost is not IReaderSpeechHost host
            || host.GetSpeechText() is not { } speech
            || string.IsNullOrWhiteSpace(speech.Text))
        {
            return null;
        }
        var chapter = _chapterIndex.ToString();
        return new ReaderTtsDocument(
            $"{fileKey}:{chapter}",
            speech.Text,
            speech.StartOffset,
            (start, length) => Dispatcher.UIThread.InvokeAsync(() => host.SetSpeechHighlight(start, length)).GetTask(),
            () => Dispatcher.UIThread.Post(host.ClearSpeechHighlight),
            speech.MapToSource,
            fileKey,
            chapter,
            speech.PageBreakOffsets);
    }

    private async Task<bool> AdvanceForSpeechAsync()
    {
        if (_isPdf)
        {
            if (_pdfPage >= _pdfPageCount) return false;
            await GoToPdfPageAsync(_pdfPage + 1);
            return true;
        }
        if (_readerHost is not NativeReaderHost host || _epubDocument is null
            || _chapterIndex + 1 >= _epubDocument.Chapters.Count)
        {
            return false;
        }
        var next = _chapterIndex + 1;
        await GoToChapterAsync(host, next, fromEnd: false, direction: 1);
        return _chapterIndex == next;
    }

    private void PresentSpeech(TtsStateChangedEventArgs e)
    {
        if (_disposed) return;
        SpeechBar.IsVisible = e.State is not (TtsPlaybackState.Stopped or TtsPlaybackState.Error);
        ListenLabel.Text = SpeechBar.IsVisible ? "停止" : "听书";
        if (this.TryFindResource(e.State == TtsPlaybackState.Paused ? "IconPlay" : "IconPause", out var icon)
            && icon is Avalonia.Media.Geometry geometry)
        {
            SpeechPauseIcon.Data = geometry;
        }
        SpeechStatusText.Text = e.State switch
        {
            TtsPlaybackState.Generating => "正在合成…",
            TtsPlaybackState.Playing => e.SegmentCount > 0 ? $"朗读中 {e.SegmentIndex}/{e.SegmentCount}" : "朗读中",
            TtsPlaybackState.Paused => "已暂停",
            TtsPlaybackState.AdvancingChapter => _isPdf ? "翻到下一页…" : "翻到下一章…",
            _ => string.Empty
        };
        if (e.State == TtsPlaybackState.Error && !string.IsNullOrWhiteSpace(e.Message))
            _shell.ShowToast("朗读失败：" + UiText.Localize(e.Message));
    }

    private void OnSpeechPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _tts?.PauseOrResume();

    private void OnSpeechStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = _tts?.StopAsync();

    // ---------- AI and translation

    private void OnAiClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowAssistant(null);

    private void ShowAssistant(string? selection)
    {
        HideSelectionBar();
        _shell.ShowSheet("AI 助手", new AiSheet(_modules, _shell, _book, CurrentPageText, selection),
            ThemeScope.RequestedThemeVariant);
    }

    private string CurrentPageText()
    {
        string? text = _readerHost switch
        {
            NativePdfReaderHost pdf => pdf.GetPageContent(pdf.PageNumber)?.Text,
            IReaderSpeechHost host when host.GetSpeechText() is { } speech => speech.Text[speech.StartOffset..],
            _ => null
        };
        text ??= string.Empty;
        return text.Length > MaxContextCharacters ? text[..MaxContextCharacters] : text;
    }

    /// <summary>Uses the reader's own AI service when configured, otherwise a free online engine.</summary>
    private async Task TranslateAsync(string text)
    {
        HideSelectionBar();
        var sheet = new InfoSheet("正在翻译…", text);
        _shell.ShowSheet("翻译", sheet, ThemeScope.RequestedThemeVariant);
        try
        {
            var chinese = text.Count(character => character is >= '一' and <= '鿿');
            var target = chinese * 3 > text.Length ? "en" : "zh-CN";
            var ai = await _modules.Ai.Settings.LoadAsync(_lifetime.Token);
            var provider = ai.IsConfigured ? ReaderTranslationProvider.Ai : ReaderTranslationProvider.Bing;
            var translated = await _modules.Ai.Translation.TranslateAsync(text, provider, target, ai, _lifetime.Token);
            sheet.Present(translated, text);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            sheet.Present("翻译失败：" + UiText.Localize(exception.Message), text);
        }
    }

    // ---------- pinch zoom

    // Pinches are recognized here, ahead of the PDF surface, which otherwise
    // takes each finger as a pan or selection.
    private readonly Dictionary<int, Point> _touches = [];
    private double _pinchStartDistance;

    private void TrackTouchPressed(PointerPressedEventArgs e)
    {
        _touches[e.Pointer.Id] = e.GetPosition(ReaderHostSlot);
        if (_touches.Count == 2 && _readerHost is NativePdfReaderHost pdf)
        {
            _pinching = true;
            _pinchStartDistance = Math.Max(1, TouchDistance());
            _pinchStartZoom = _pinchRequestedZoom = pdf.Zoom;
        }
    }

    /// <returns>True while a pinch owns the gesture.</returns>
    private bool TrackTouchMoved(PointerEventArgs e)
    {
        if (!_touches.ContainsKey(e.Pointer.Id)) return false;
        _touches[e.Pointer.Id] = e.GetPosition(ReaderHostSlot);
        if (!_pinching || _touches.Count < 2 || _readerHost is not NativePdfReaderHost pdf) return _pinching;

        var zoom = Math.Clamp(_pinchStartZoom * TouchDistance() / _pinchStartDistance, 1, MaxPdfZoom);
        if (Math.Abs(zoom - _pinchRequestedZoom) >= ZoomStep)
        {
            _pinchRequestedZoom = zoom;
            _ = pdf.SetZoomAsync(zoom);
        }
        return true;
    }

    /// <returns>True when this release ended a pinch.</returns>
    private bool TrackTouchReleased(PointerReleasedEventArgs e)
    {
        _touches.Remove(e.Pointer.Id);
        if (!_pinching) return false;
        if (_touches.Count > 0) return true;
        _pinching = false;
        // A pinch that ends near 1 snaps back to the whole page.
        var zoom = _pinchRequestedZoom < 1.1 ? 1 : Math.Round(_pinchRequestedZoom, 2);
        _shell.Preferences.Update(value => value with { PdfZoom = zoom });
        if (_readerHost is NativePdfReaderHost pdf) _ = ApplyPdfZoomAsync(pdf);
        return true;
    }

    private double TouchDistance()
    {
        var points = _touches.Values.Take(2).ToArray();
        var delta = points[0] - points[1];
        return Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
    }
}
