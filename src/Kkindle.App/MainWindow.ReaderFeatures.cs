using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;
using System.Text.RegularExpressions;

namespace Kkindle;

public partial class MainWindow
{
    private int _readerWholeSearchSequence;
    private int _readerPdfSearchSequence;
    private bool _readerSearchVisible;
    private bool _readerSearchLayoutCaptured;
    private bool _readerSearchPreviousTocExpanded;
    private bool _readerSearchPreviousTocMinimal;
    private bool _readerSearchPreviousBookmarkTabActive;
    private string _readerSearchQuery = string.Empty;
    private ReaderSearchResultViewModel? _selectedReaderSearchResult;

    private sealed record ReaderBookmarkLocation(
        string ChapterPath,
        int ScrollPosition,
        int FlowMode,
        string? Fragment);

    private CancellationToken ReaderToken =>
        _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token;

    private bool IsNativeReaderPaginated =>
        !_readerIsPdf && CurrentReaderHost is NativeReaderHost { IsPaginated: true };

    private bool IsReaderPaginated =>
        _readerIsPdf || _readerLayout.FlowMode == 1 || IsNativeReaderPaginated;

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReaderRoot.IsVisible) return;
        // F11 toggles zen mode (enter/exit), matching the common fullscreen
        // convention used by browsers and readers; Esc is an additional way to
        // leave zen mode (both from the WinUI reference's global hook).
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleReaderZenMode();
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleReaderEscapeShortcut();
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.F)
        {
            e.Handled = true;
            OpenReaderSearchShortcut();
            return;
        }

        if (IsLinuxReaderTextFallbackActive()
            && TryHandleLinuxReaderTextFallbackKeyDown(e))
        {
            return;
        }

        if (IsReaderPaginated
            && CurrentReaderHost is NativeReaderHost nativeReader
            && !IsReaderTextInputFocused()
            && e.Key is Key.Home or Key.End)
        {
            e.Handled = true;
            nativeReader.SeekToBoundary(e.Key == Key.End);
            _ = ObserveReaderTaskAsync(UpdateReaderScrollStateAsync(nativeReader));
            return;
        }

        // Native WebViews are swapped at chapter boundaries. If Windows sends
        // the next arrow to the Avalonia window before the newly visible HWND
        // has accepted focus, keep paginated navigation responsive here. Keys
        // delivered to the native WebView never enter Avalonia's routed input,
        // so any arrow reaching this handler still needs to be handled.
        if (IsReaderPaginated
            && !IsReaderTextInputFocused()
            && CurrentReaderHost?.View is Control)
        {
            var chapterDirection = !_readerIsPdf
                ? e.Key == Key.Up
                    ? -1
                    : e.Key == Key.Down
                        ? 1
                        : 0
                : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(
                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return;
            }
            var verticalPageOrder = !_readerIsPdf && _readerLayout.VerticalWriting;
            var direction = e.Key == Key.Left
                ? (verticalPageOrder ? 1 : -1)
                : e.Key == Key.Right
                    ? (verticalPageOrder ? -1 : 1)
                    : e.Key is Key.Up or Key.PageUp
                        ? -1
                        : e.Key is Key.Down or Key.PageDown
                            ? 1
                            : 0;
            if (direction != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                return;
            }
        }

        if (!_readerIsPdf
            && _readerLayout.FlowMode == 0
            && !IsNativeReaderPaginated
            && !IsReaderTextInputFocused()
            && CurrentReaderHost?.View is Control)
        {
            var chapterDirection = e.Key == Key.Left
                ? -1
                : e.Key == Key.Right
                    ? 1
                    : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(
                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return;
            }

            var scrollDirection = e.Key == Key.Up
                ? -1
                : e.Key == Key.Down
                    ? 1
                    : 0;
            if (scrollDirection != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                return;
            }
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.B
            && !IsReaderTextInputFocused())
        {
            e.Handled = true;
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
        }
    }

    private bool IsReaderTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox;
    }

    private bool HandleReaderEscapeShortcut()
    {
        if (ReaderInPageSearchBar.IsVisible)
        {
            _ = ObserveReaderTaskAsync(ReaderInPageSearchCloseAsync());
            return true;
        }
        // Esc closes reader overlays in priority order, matching the WinUI
        // reference's RootGrid_KeyDown: whole-book search panel first, then
        // the layout settings overlay, then zen mode.
        if (_readerSearchVisible)
        {
            HideReaderSearchPanel();
            return true;
        }
        if (ReaderLayoutSettingsPopup.IsOpen)
        {
            ReaderLayoutSettingsPopup.IsOpen = false;
            return true;
        }
        if (_readerZenMode)
        {
            ToggleReaderZenMode();
            return true;
        }
        return false;
    }

    private void OpenReaderSearchShortcut()
    {
        if (_readerIsPdf)
        {
            ShowReaderSearchPanel();
            return;
        }

        ReaderInPageSearchBar.IsVisible = true;
        ReaderInPageSearchBox.Focus();
        ReaderInPageSearchBox.SelectAll();
    }

    private async Task OpenPdfReaderAsync(
        BookCardViewModel card,
        BookFile file,
        string path)
    {
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var token = _readerSessionCancellation.Token;

        try
        {
            SetTaskStatus($"正在准备《{card.Title}》的 PDF 阅读器…");
            var pages = await _pdfTextService.ExtractAsync(path, token);
            if (pages.Count == 0)
                throw new InvalidDataException("PDF 没有可读取的页面文本。");

            _readerBookCard = card;
            _readerBookFile = file;
            _readerDocument = null;
            _readerIsPdf = true;
            _readerPdfSourcePath = path;
            _readerPdfPages = pages;
            _readerPdfPage = 1;
            _readerChapterIndex = 0;
            _readerScrollRatio = 0;
            _readerScrollPosition = 0;
            // A PDF session always renders in the active host slot; clear any
            // preload flag left over from a previous EPUB session so the layer
            // swap below shows the right slot.
            _readerShowingPreload = false;

            // The PDF surface is rendered by WebView2's built-in PDF viewer
            // (file:// URL + #page=N fragment), exactly like the WinUI
            // reference. The extracted page texts stay as the local search /
            // progress / bookmark / AI context index underneath it.
            await InitializeReaderInteractionAsync(
                new EpubReaderDocument(Path.GetDirectoryName(path) ?? string.Empty, [], [], []),
                file,
                token);
            _readerIsPdf = true;
            _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
            {
                FlowMode = 0,
                TwoPageMode = false
            });
            UpdateReaderBookmarkCornerSurface();

            var progress = await _readerData.GetProgressAsync(file.Id, token);
            if (progress is not null)
                _readerPdfPage = Math.Clamp(progress.ChapterIndex + 1, 1, pages.Count);
            _readerChapterIndex = _readerPdfPage - 1;

            ReaderBookInfoText.Text = $"{card.Title} · PDF";
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            ReaderStatusText.Text = $"PDF · {pages.Count} 页";
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            // The WinUI reference keeps the TOC panel open for PDF with an
            // explanatory empty state; bookmarks still work per page.
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            ReaderTocEmptyText.Text = "PDF 使用内置查看器；Kkindle 已启用本地搜索、页码进度、书签和页面笔记。";
            ReaderTocEmptyText.IsVisible = true;
            ApplyReaderPanelLayout();
            ShowReaderTocTab();
            ReaderAiView.IsVisible = true;
            ReaderNotesView.IsVisible = false;
            ReaderAiComposer.IsVisible = true;
            ReaderAssistantPanel.IsVisible = false;
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);

            await EnsureReaderHostsAsync();
            // PDF renders in the webview's own viewer, so the Linux plain-text
            // surface never applies here. Drop any overlay left by a previous
            // EPUB session before revealing the host.
            HideLinuxReaderTextFallback();
            SetReaderHostLayer();
            FocusCurrentReaderHost();
            var pdfSource = new Uri(path).AbsoluteUri + $"#page={_readerPdfPage}";
            if (CurrentReaderHost is not { } host
                || !await NavigateReaderHostAndWaitAsync(host, new Uri(pdfSource), token))
            {
                throw new InvalidOperationException("PDF 阅读器页面加载失败。");
            }

            ReaderStatusText.Text = $"PDF · {pages.Count} 页 · 可搜索文本已加载";
            UpdateReaderToolbar();
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await CloseReaderAsync();
            SetTaskStatus($"打开 PDF 阅读器失败：{exception.Message}");
        }
    }

    private async Task NavigatePdfPageAsync(
        int page,
        CancellationToken cancellationToken,
        bool saveProgress = true)
    {
        if (!_readerIsPdf || _readerPdfPages.Count == 0 || CurrentReaderHost is not { } host) return;
        if (string.IsNullOrWhiteSpace(_readerPdfSourcePath)) return;
        _readerPdfPage = Math.Clamp(page, 1, _readerPdfPages.Count);
        _readerChapterIndex = _readerPdfPage - 1;
        // Load the real PDF page through WebView2's built-in viewer (the
        // WinUI reference navigates the same file:// + #page=N URL). Page
        // turns are fire-and-forget like the reference: the viewer replaces
        // the pending navigation and the host state is already final.
        var source = new Uri(_readerPdfSourcePath).AbsoluteUri + $"#page={_readerPdfPage}";
        host.Navigate(new Uri(source));
        ReaderChapterText.Text = GetReaderChapterPositionLabel();
        UpdateReaderToolbar();
        await UpdateReaderBookmarkIndicatorAsync();
        if (saveProgress) await SaveReaderProgressAsync(cancellationToken);
    }

    // PDF annotations cannot render inside WebView2's built-in PDF viewer
    // (no DOM to inject into), exactly like the WinUI reference: they live in
    // the notes list and jump to their page on click.
    private async Task ApplySavedReaderPdfAnnotationsAsync(CancellationToken cancellationToken)
        => await Task.CompletedTask;

    private async Task RefreshReaderBookmarksAsync(CancellationToken cancellationToken)
    {
        ReaderBookmarks.Clear();
        if (_readerBookFile is null) return;
        var bookmarks = await _readerData.GetBookmarksAsync(_readerBookFile.Id, cancellationToken);
        foreach (var bookmark in bookmarks
                     .OrderBy(item => item.ChapterIndex)
                     .ThenBy(item => item.CreatedAt))
            ReaderBookmarks.Add(bookmark);
        ReaderBookmarkList.IsVisible = ReaderBookmarks.Count > 0;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
    }

    private async Task RefreshReaderAnnotationsAsync(CancellationToken cancellationToken)
    {
        ReaderAnnotations.Clear();
        _selectedReaderAnnotation = null;
        ReaderDeleteAnnotationButton.IsEnabled = false;
        if (_readerBookFile is null) return;
        var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
        foreach (var annotation in annotations) ReaderAnnotations.Add(annotation);
    }

    private void ShowReaderTocTab()
    {
        ReaderTocView.IsVisible = true;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = false;
        ReaderReadingInfoPanel.IsVisible = true;
        ReaderTocEmptyText.IsVisible = _readerTocItems.Count == 0;
    }

    private void ShowReaderBookmarkTab()
    {
        // The toolbar bookmark command opens this page in the same left rail
        // as the TOC. Re-expand the rail when it had been collapsed or reduced
        // to the compact marker strip so the destination is always visible.
        _readerTocExpanded = true;
        _readerTocMinimal = false;
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = true;
        ReaderSearchPanel.IsVisible = false;
        ReaderReadingInfoPanel.IsVisible = true;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
        ApplyReaderPanelLayout();
    }

    private void ShowReaderSearchTab()
    {
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = true;
        ReaderReadingInfoPanel.IsVisible = false;
    }

    private void ShowReaderSearchStatus(string? message)
    {
        ReaderSearchStatusText.Text = message ?? string.Empty;
        ReaderSearchStatusText.IsVisible = message is not null;
        ReaderSearchResultList.IsVisible = message is null;
    }

    private void ReaderSearchToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerSearchVisible)
            HideReaderSearchPanel();
        else
            ShowReaderSearchPanel();
    }

    private void ShowReaderSearchPanel()
    {
        if (!_readerSearchVisible)
        {
            _readerSearchPreviousTocExpanded = _readerTocExpanded;
            _readerSearchPreviousTocMinimal = _readerTocMinimal;
            _readerSearchPreviousBookmarkTabActive = ReaderBookmarkPane.IsVisible;
            _readerSearchLayoutCaptured = true;
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            _readerSearchVisible = true;
        }

        ApplyReaderPanelLayout();
        ShowReaderSearchTab();
        ReaderTocSearchBox.PlaceholderText = "搜索整本书…";
        ReaderTocSearchBox.Text = _readerSearchQuery;
        ShowReaderSearchStatus(string.IsNullOrWhiteSpace(_readerSearchQuery)
            ? "输入关键词，实时搜索整本书。"
            : ReaderSearchResults.Count > 0 ? null : "正在本地搜索…");
        ReaderTocSearchBox.Focus();
        if (!string.IsNullOrWhiteSpace(_readerSearchQuery) && ReaderSearchResults.Count == 0)
        {
            var sequence = ++_readerWholeSearchSequence;
            _ = RefreshReaderWholeSearchAsync(_readerSearchQuery, sequence);
        }
    }

    private void HideReaderSearchPanel(bool restorePreviousLayout = true)
    {
        if (_readerSearchVisible)
            _readerSearchQuery = ReaderTocSearchBox.Text?.Trim() ?? string.Empty;
        _readerSearchVisible = false;
        _readerWholeSearchSequence++;
        ReaderSearchPanel.IsVisible = false;
        ReaderTocSearchBox.Text = string.Empty;
        if (_readerSearchPreviousBookmarkTabActive && restorePreviousLayout)
            ShowReaderBookmarkTab();
        else
            ShowReaderTocTab();

        if (_readerSearchLayoutCaptured)
        {
            _readerTocExpanded = restorePreviousLayout && _readerSearchPreviousTocExpanded;
            _readerTocMinimal = restorePreviousLayout && _readerSearchPreviousTocMinimal;
            _readerSearchLayoutCaptured = false;
            ApplyReaderPanelLayout();
        }
    }

    private async void ReaderBookmarkList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ReaderBookmark bookmark)
        {
            try
            {
                await NavigateToReaderBookmarkAsync(bookmark);
            }
            catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ReaderStatusText.Text = $"书签定位失败：{exception.Message}";
            }
            finally
            {
                // ListBox does not raise SelectionChanged when the user
                // chooses the same item twice. Clear the transient selection
                // after every jump so a bookmark can be opened repeatedly.
                ReaderBookmarkList.SelectedIndex = -1;
            }
        }
    }

    private async void ReaderBookmarkDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderBookmark bookmark } || _readerBookFile is null) return;
        try
        {
            await _readerData.DeleteBookmarkAsync(bookmark.Id, ReaderToken);
            await RefreshReaderBookmarksAsync(ReaderToken);
            await UpdateReaderBookmarkIndicatorAsync();
            ShowReaderTransientStatus("书签已删除");
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"删除书签失败：{exception.Message}";
        }
    }

    private async Task ToggleReaderBookmarkAsync()
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var location = await CaptureCurrentReaderBookmarkLocationAsync();
        var currentPath = location?.ChapterPath ?? (_readerIsPdf
            ? $"pdf:{_readerPdfPage}"
            : GetReaderChapterPath());
        if (string.IsNullOrWhiteSpace(currentPath)) return;

        var fragment = _readerIsPdf
            ? null
            : location?.Fragment ?? _readerCurrentFragment;
        var quote = _readerIsPdf
            ? $"PDF 第 {_readerPdfPage} 页"
            : await CaptureCurrentPageQuoteAsync();
        var currentPosition = location?.ScrollPosition;
        var currentFlowMode = location?.FlowMode ?? _readerLayout.FlowMode;
        var existing = ReaderBookmarks.FirstOrDefault(bookmark =>
            AreReaderBookmarkChapterPathsEqual(bookmark.ChapterPath, currentPath)
            && string.Equals(bookmark.Fragment, fragment, StringComparison.OrdinalIgnoreCase)
            && (bookmark.ScrollPosition is int savedPosition
                && currentPosition is int position
                ? Math.Abs(savedPosition - position) <= 4
                : string.IsNullOrWhiteSpace(bookmark.Quote)
                  || string.IsNullOrWhiteSpace(quote)
                  || string.Equals(bookmark.Quote, quote, StringComparison.OrdinalIgnoreCase)));

        try
        {
            if (existing is not null)
            {
                await _readerData.DeleteBookmarkAsync(existing.Id, ReaderToken);
                ShowReaderTransientStatus("已取消书签");
                ShowReaderBookmarkFeedback("已取消书签");
            }
            else
            {
                await _readerData.SaveBookmarkAsync(new ReaderBookmark
                {
                    BookId = _readerBookCard.Book.Id,
                    BookFileId = _readerBookFile.Id,
                    ChapterPath = currentPath,
                    Fragment = fragment,
                    ChapterIndex = _readerChapterIndex,
                    ScrollPosition = currentPosition,
                    FlowMode = currentFlowMode,
                    Title = GetReaderChapterLabel(),
                    Quote = quote ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow
                }, ReaderToken);
                ShowReaderTransientStatus("已添加书签");
                ShowReaderBookmarkFeedback("已添加书签");
            }
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"书签保存失败：{exception.Message}";
        }
        await RefreshReaderBookmarksAsync(ReaderToken);
        await UpdateReaderBookmarkIndicatorAsync();
    }

    private async Task<string?> CaptureCurrentPageQuoteAsync()
    {
        if (CurrentReaderHost is not { } host) return null;
        if (host is NativeReaderHost nativeReader)
            return nativeReader.GetCurrentPageQuote();

        try
        {
            var result = await host.InvokeScriptAsync(
                """
                (() => {
                  const body = document.body;
                  if (!body) return '';
                  const viewportWidth = window.visualViewport?.width
                    || window.innerWidth
                    || document.documentElement.clientWidth;
                  const viewportHeight = window.visualViewport?.height
                    || window.innerHeight
                    || document.documentElement.clientHeight;
                  const vertical = (getComputedStyle(body).writingMode || '').startsWith('vertical');
                  const twoPage = window.__kkindleReaderFlowMode === 1
                    && window.__kkindleReaderTwoPage === true;
                  const ignoredSelector = [
                    'script', 'style', 'noscript',
                    '#kkindle-selection-bar', '#kkindle-bookmark-corner',
                    '#kk-slide', '#kk-wave', '.kkindle-wave-sweep'
                  ].join(',');
                  const intersectsViewport = rect => rect
                    && rect.width > 0
                    && rect.height > 0
                    && (rect.left + rect.right) / 2 >= 0
                    && (rect.left + rect.right) / 2 <= viewportWidth
                    && (rect.top + rect.bottom) / 2 >= 0
                    && (rect.top + rect.bottom) / 2 <= viewportHeight;
                  const caretAt = (x, y) => {
                    if (typeof document.caretRangeFromPoint === 'function') {
                      const range = document.caretRangeFromPoint(x, y);
                      if (range) return { node: range.startContainer, offset: range.startOffset };
                    }
                    if (typeof document.caretPositionFromPoint === 'function') {
                      const position = document.caretPositionFromPoint(x, y);
                      if (position) return { node: position.offsetNode, offset: position.offset };
                    }
                    return null;
                  };
                  const visibleGlyphRect = (node, offset, rect) => {
                    if (!intersectsViewport(rect)) return false;
                    const caret = caretAt(
                      (rect.left + rect.right) / 2,
                      (rect.top + rect.bottom) / 2);
                    return caret?.node === node && Math.abs(caret.offset - offset) <= 1;
                  };
                  const glyphs = [];
                  let domOrder = 0;
                  const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
                  while (walker.nextNode()) {
                    const node = walker.currentNode;
                    const parent = node.parentElement;
                    const value = node.data || '';
                    if (!parent || !value.trim() || parent.closest?.(ignoredSelector)) continue;

                    const nodeRange = document.createRange();
                    nodeRange.selectNodeContents(node);
                    if (!Array.from(nodeRange.getClientRects()).some(intersectsViewport)) continue;

                    const characterRange = document.createRange();
                    for (let offset = 0; offset < value.length; offset++) {
                      characterRange.setStart(node, offset);
                      characterRange.setEnd(node, offset + 1);
                      const rect = Array.from(characterRange.getClientRects())
                        .find(candidate => visibleGlyphRect(node, offset, candidate));
                      if (!rect) continue;
                      glyphs.push({
                        value: value[offset],
                        left: rect.left,
                        right: rect.right,
                        top: rect.top,
                        bottom: rect.bottom,
                        domOrder: domOrder++
                      });
                    }
                  }

                  if (!glyphs.length) return '';
                  const pages = new Map();
                  for (const glyph of glyphs) {
                    const center = (glyph.left + glyph.right) / 2;
                    const page = twoPage ? (center < viewportWidth / 2 ? 0 : 1) : 0;
                    if (!pages.has(page)) pages.set(page, []);
                    pages.get(page).push(glyph);
                  }

                  const pageNumbers = Array.from(pages.keys()).sort((a, b) => a - b);
                  const visualLines = [];
                  for (const pageNumber of pageNumbers) {
                    const pageGlyphs = pages.get(pageNumber);
                    pageGlyphs.sort(vertical
                      ? (a, b) => b.right - a.right || a.top - b.top || a.domOrder - b.domOrder
                      : (a, b) => a.top - b.top || a.left - b.left || a.domOrder - b.domOrder);
                    const lines = [];
                    for (const glyph of pageGlyphs) {
                      const start = vertical ? glyph.left : glyph.top;
                      const end = vertical ? glyph.right : glyph.bottom;
                      const line = lines.find(candidate =>
                        Math.min(candidate.end, end) >= Math.max(candidate.start, start) - 1);
                      if (line) {
                        line.glyphs.push(glyph);
                        line.start = Math.min(line.start, start);
                        line.end = Math.max(line.end, end);
                      } else {
                        lines.push({ start, end, glyphs: [glyph] });
                      }
                    }
                    lines.sort(vertical
                      ? (a, b) => b.end - a.end
                      : (a, b) => a.start - b.start);
                    for (const line of lines) {
                      line.glyphs.sort(vertical
                        ? (a, b) => a.top - b.top || a.domOrder - b.domOrder
                        : (a, b) => a.left - b.left || a.domOrder - b.domOrder);
                      const text = line.glyphs.map(glyph => glyph.value).join('').trim();
                      if (text) visualLines.push(text);
                    }
                  }
                  return visualLines.slice(0, 3).join('\n');
                })();
                """);
            var normalized = NormalizeReaderBookmarkQuote(DecodeReaderScriptString(result));
            return normalized.Length <= 72 ? normalized : normalized[..72];
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeReaderBookmarkQuote(string? text) =>
        string.Join(
            " ",
            (text ?? string.Empty).Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries));

    private async Task<ReaderBookmarkLocation?> CaptureCurrentReaderBookmarkLocationAsync()
    {
        if (_readerBookFile is null) return null;
        var chapterPath = _readerIsPdf
            ? $"pdf:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(chapterPath)) return null;

        var flowMode = _readerIsPdf ? 0 : _readerLayout.FlowMode;
        if (_readerIsPdf)
        {
            return new ReaderBookmarkLocation(
                chapterPath,
                0,
                flowMode,
                null);
        }

        if (CurrentReaderHost is not { } host) return null;
        if (host is NativeReaderHost nativeReader)
        {
            var nativeState = nativeReader.GetScrollState();
            var nativePosition = Math.Max(0, (int)Math.Round(nativeState.Position));
            return new ReaderBookmarkLocation(
                chapterPath,
                nativePosition,
                Math.Max(1, flowMode),
                _readerCurrentFragment);
        }

        try
        {
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return null; return JSON.stringify({ left: el.scrollLeft || 0, top: el.scrollTop || 0, fragment: window.__kkindleReaderLogicalHash || location.hash || '' }); })();");
            var raw = DecodeReaderScriptString(result);
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
            var position = horizontal
                ? ReadDouble(root, "left")
                : ReadDouble(root, "top");
            if (_readerLayout.VerticalWriting)
                position = Math.Abs(position);
            var fragment = ReadString(root, "fragment").TrimStart('#');
            try { fragment = Uri.UnescapeDataString(fragment); } catch { }
            return new ReaderBookmarkLocation(
                chapterPath,
                Math.Max(0, (int)Math.Round(position)),
                flowMode,
                string.IsNullOrWhiteSpace(fragment) ? _readerCurrentFragment : fragment);
        }
        catch
        {
            // The native host can be between documents while a chapter is
            // swapping. The indicator will hide until the next scroll report;
            // saving a bookmark still has the in-memory position as a safe
            // fallback at the call site.
            return null;
        }
    }

    private static string? DecodeReaderScriptString(string? result)
    {
        var raw = result?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                return raw[1..^1];
            }
        }
        return raw;
    }

    private static bool AreReaderBookmarkChapterPathsEqual(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return TryGetReaderPdfPage(left, out var leftPage)
            && TryGetReaderPdfPage(right, out var rightPage)
            && leftPage == rightPage;
    }

    private static bool TryGetReaderPdfPage(string? chapterPath, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(chapterPath)
            || !chapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase))
            return false;
        var value = chapterPath[4..];
        if (value.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        return int.TryParse(value, out page) && page > 0;
    }

    // Shows a transient ToolTip near the bookmark button so the user gets
    // immediate feedback without clobbering the header status text (which is
    // also updated). Mirrors the WinUI ReaderBookmarkFeedbackToolTip.
    private void ShowReaderBookmarkFeedback(string message)
    {
        if (ReaderBookmarkButton is null) return;
        ToolTip.SetTip(ReaderBookmarkButton, message);
        ToolTip.SetIsOpen(ReaderBookmarkButton, true);
        _ = Task.Delay(1600).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReaderBookmarkButton is not null)
                    ToolTip.SetIsOpen(ReaderBookmarkButton, false);
            }),
            TaskScheduler.Default);
    }

    private void ClearReaderBookmarkPendingLocation()
    {
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
    }

    private async Task UpdateReaderBookmarkIndicatorAsync()
    {
        var sequence = ++_readerBookmarkIndicatorSequence;
        if (ReaderBookmarkCornerMarker is null) return;
        var location = await CaptureCurrentReaderBookmarkLocationAsync();
        if (sequence != _readerBookmarkIndicatorSequence) return;
        ApplyReaderBookmarkIndicator(location);
    }

    private void UpdateReaderBookmarkIndicatorFromTrackedLocation()
    {
        _readerBookmarkIndicatorSequence++;
        var chapterPath = _readerIsPdf
            ? $"pdf:{_readerPdfPage}"
            : GetReaderChapterPath();
        var location = string.IsNullOrWhiteSpace(chapterPath)
            ? null
            : new ReaderBookmarkLocation(
                chapterPath,
                Math.Max(0, (int)Math.Round(_readerScrollPosition)),
                _readerIsPdf ? 0 : _readerLayout.FlowMode,
                _readerIsPdf ? null : _readerCurrentFragment);
        ApplyReaderBookmarkIndicator(location);
    }

    private void ApplyReaderBookmarkIndicator(ReaderBookmarkLocation? location)
    {
        if (ReaderBookmarkCornerMarker is null) return;
        if (location is null)
        {
            ReaderBookmarkCornerMarker.IsVisible = false;
            if (!_readerIsPdf)
                _ = ObserveReaderTaskAsync(SetReaderDocumentBookmarkIndicatorAsync(false));
            return;
        }

        var tolerance = location.FlowMode == 1 ? 8 : 4;
        var isBookmarked = ReaderBookmarks.Any(bookmark =>
            ReaderBookmarkPolicy.MatchesVisiblePosition(
                bookmark.ChapterPath,
                bookmark.FlowMode,
                bookmark.ScrollPosition,
                location.ChapterPath,
                location.FlowMode,
                location.ScrollPosition,
                tolerance));
        ReaderBookmarkCornerMarker.IsVisible = _readerIsPdf && isBookmarked;
        if (!_readerIsPdf)
            _ = ObserveReaderTaskAsync(SetReaderDocumentBookmarkIndicatorAsync(isBookmarked));
    }

    private async Task SetReaderDocumentBookmarkIndicatorAsync(bool isBookmarked)
    {
        if (CurrentReaderHost is not { } host) return;
        try
        {
            await host.InvokeScriptAsync(
                $"window.__kkindleSetBookmarkMarked?.({(isBookmarked ? "true" : "false")});");
        }
        catch
        {
            // The outgoing chapter may be disposed while its indicator update
            // is still queued. The incoming chapter refreshes its own state.
        }
    }

    private async Task NavigateToReaderBookmarkAsync(ReaderBookmark bookmark)
    {
        if (_readerIsPdf)
        {
            var page = bookmark.ChapterIndex + 1;
            if (TryGetReaderPdfPage(bookmark.ChapterPath, out var savedPage))
                page = savedPage;
            await NavigatePdfPageAsync(page, ReaderToken);
            await UpdateReaderBookmarkIndicatorAsync();
            return;
        }
        if (_readerDocument is null) return;
        var path = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            bookmark.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, path) || !File.Exists(path)) return;

        var chapterIndex = _readerDocument.Chapters
            .Select((chapter, index) => (chapter, index))
            .Where(item => string.Equals(
                Path.GetFullPath(item.chapter),
                path,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (chapterIndex < 0) return;

        _readerChapterIndex = chapterIndex;
        _readerPendingBookmarkQuote = bookmark.Quote;
        _readerPendingBookmarkPosition = bookmark.ScrollPosition;
        _readerPendingBookmarkFlowMode = bookmark.FlowMode;
        var target = new Uri(path);
        var fragment = DecodeReaderFragment(bookmark.Fragment);
        if (!string.IsNullOrWhiteSpace(fragment))
            target = new Uri(target.AbsoluteUri + "#" + Uri.EscapeDataString(fragment));

        try
        {
            await NavigateToReaderItemAsync(
                new EpubReaderNavigationItem(bookmark.Title, target.AbsoluteUri, chapterIndex),
                ReaderToken,
                ReaderNavigationIntent.Bookmark);
            await UpdateReaderBookmarkIndicatorAsync();
        }
        finally
        {
            ClearReaderBookmarkPendingLocation();
        }
    }

    private async void ReaderTocSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_readerSearchVisible) return;
        var sequence = ++_readerWholeSearchSequence;
        var query = ReaderTocSearchBox.Text?.Trim() ?? string.Empty;
        _readerSearchQuery = query;
        if (query.Length == 0)
        {
            await RefreshReaderWholeSearchAsync(query, sequence);
            return;
        }
        ShowReaderSearchStatus("正在本地搜索…");
        await Task.Delay(180);
        if (sequence != _readerWholeSearchSequence) return;
        await RefreshReaderWholeSearchAsync(ReaderTocSearchBox.Text?.Trim() ?? string.Empty, sequence);
    }

    private async Task RefreshReaderWholeSearchAsync(string query, int? sequence = null)
    {
        if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
        if (query.Length == 0)
        {
            ClearReaderSearchResultSelection();
            ReaderSearchResults.Clear();
            ReaderWholeSearchCountText.Text = string.Empty;
            ShowReaderSearchStatus("输入关键词，实时搜索整本书。");
            return;
        }

        ReaderSearchResults.Clear();
        ClearReaderSearchResultSelection();
        ReaderWholeSearchCountText.Text = string.Empty;
        ShowReaderSearchStatus("正在本地搜索…");
        try
        {
            var pendingResults = new List<ReaderSearchResultViewModel>();
            if (_readerIsPdf)
            {
                var results = PdfTextService.Search(_readerPdfPages, query, int.MaxValue);
                foreach (var result in results)
                    pendingResults.Add(new ReaderSearchResultViewModel(
                        $"第 {result.PageNumber} 页",
                        result.Excerpt,
                        result.PageNumber - 1,
                        $"pdf:page:{result.PageNumber}",
                        pageNumber: result.PageNumber,
                        query: query));
            }
            else if (_readerBookCard is not null && _readerBookFile is not null && _readerDocument is not null)
            {
                await _bookContent.EnsureIndexedAsync(_readerBookCard.Book, _readerBookFile, _readerDocument, ReaderToken);
                var results = await _readerData.SearchBookAsync(
                    _readerBookCard.Book.Id,
                    query,
                    int.MaxValue,
                    ReaderToken,
                    exactPhraseOnly: true);
                // The same visible excerpt can come from duplicate EPUB spine
                // entries or legacy chunks with different paths/offsets. At this
                // final presentation boundary, identical title + snippet means an
                // identical user-facing result and must only be shown once.
                var distinct = results
                    .Select(result => new ReaderSearchResultViewModel(result, query))
                    .DistinctBy(
                        item => $"{item.Title}\u001f{item.Excerpt}",
                        StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                foreach (var item in distinct)
                    pendingResults.Add(item);
            }
            if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
            ReaderSearchResults.Clear();
            foreach (var item in pendingResults)
                ReaderSearchResults.Add(item);
            ReaderWholeSearchCountText.Text = _readerIsPdf
                ? $"全书 {ReaderSearchResults.Count} 条结果 · PDF 本地文本索引"
                : $"全书 {ReaderSearchResults.Count} 段结果";
            ShowReaderSearchStatus(
                ReaderSearchResults.Count == 0
                    ? (_readerIsPdf ? "没有找到匹配的内容。" : "没有找到匹配的片段。")
                    : null);
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
            ReaderWholeSearchCountText.Text = string.Empty;
            ShowReaderSearchStatus($"搜索失败：{exception.Message}");
        }
    }

    private void ClearReaderSearchResultSelection()
    {
        if (_selectedReaderSearchResult is not null)
            _selectedReaderSearchResult.IsSelected = false;
        _selectedReaderSearchResult = null;
    }

    private async void ReaderSearchResultButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderSearchResultViewModel result }) return;
        if (!ReferenceEquals(_selectedReaderSearchResult, result))
        {
            if (_selectedReaderSearchResult is not null)
                _selectedReaderSearchResult.IsSelected = false;
            _selectedReaderSearchResult = result;
            result.IsSelected = true;
        }
        if (result.PageNumber is { } page)
        {
            await NavigatePdfPageAsync(page, ReaderToken);
            return;
        }
        if (result.Source is { } source)
        {
            await NavigateToReaderChunkAsync(source, result.Query ?? string.Empty);
            return;
        }
        if (_readerDocument is null || string.IsNullOrWhiteSpace(result.Target)) return;
        ReaderSearchStatusText.Text = "正在跳转并定位关键词…";
        var navigated = await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(result.Title, result.Target, result.ChapterIndex),
            ReaderToken,
            ReaderNavigationIntent.Search);
        if (navigated && !string.IsNullOrWhiteSpace(result.Query))
        {
            var sequence = ++_readerSearchSequence;
            await ApplyReaderSearchAsync(result.Query, sequence);
        }
        ReaderSearchStatusText.Text = navigated
            ? $"已跳转到《{result.Title}》相关位置。"
            : "搜索结果定位失败，请重试。";
    }

    private async Task NavigateToReaderChunkAsync(BookContentChunk source, string query)
    {
        if (_readerDocument is null)
        {
            ShowReaderSearchStatus("无法定位正文：书籍内容尚未准备完成。");
            return;
        }

        var targetPath = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            source.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, targetPath) || !File.Exists(targetPath))
        {
            ShowReaderSearchStatus("无法定位正文：对应章节文件不存在。");
            return;
        }

        var matchOffset = FindReaderSearchMatchOffset(source.Content, query);
        _readerPendingChunkOffset = source.StartOffset + Math.Max(0, matchOffset);
        _readerPendingSearchQuery = query;
        _readerPendingSearchContext = CreateReaderSearchContext(
            source.Content,
            matchOffset,
            query.Length);

        ReaderSearchStatusText.Text = "正在跳转并定位关键词…";
        var navigated = await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(
                source.ChapterTitle,
                new Uri(targetPath).AbsoluteUri,
                source.ChapterIndex),
            ReaderToken,
            ReaderNavigationIntent.Search);
        ReaderSearchStatusText.Text = navigated
            ? $"已跳转到《{source.ChapterTitle}》相关位置。"
            : "搜索结果定位失败，请重试。";
    }

    // Mirrors the search index's whitespace handling when re-locating a
    // result: selection queries can contain newlines while indexed chunks use
    // normalized text. Try the exact raw query first, then the earliest term.
    private static int FindReaderSearchMatchOffset(string content, string query)
    {
        if (string.IsNullOrWhiteSpace(content)) return -1;
        var exact = content.IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase);
        if (exact >= 0) return exact;

        var normalizedQuery = Regex.Replace(query.Trim(), @"\s+", " ").Trim();
        var earliest = -1;
        foreach (var run in normalizedQuery.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = content.IndexOf(run, StringComparison.CurrentCultureIgnoreCase);
            if (index >= 0 && (earliest < 0 || index < earliest)) earliest = index;
        }
        return earliest;
    }

    private static string CreateReaderSearchContext(
        string content,
        int matchOffset,
        int matchLength)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var safeMatch = Math.Clamp(matchOffset, 0, Math.Max(0, content.Length - 1));
        var paragraphStart = content.LastIndexOf('\n', safeMatch);
        paragraphStart = paragraphStart < 0 ? 0 : paragraphStart + 1;
        var paragraphEnd = content.IndexOf(
            '\n',
            Math.Min(content.Length, safeMatch + Math.Max(1, matchLength)));
        if (paragraphEnd < 0) paragraphEnd = content.Length;

        while (paragraphStart < paragraphEnd && char.IsWhiteSpace(content[paragraphStart])) paragraphStart++;
        while (paragraphEnd > paragraphStart && char.IsWhiteSpace(content[paragraphEnd - 1])) paragraphEnd--;
        const int maximumContextLength = 420;
        if (paragraphEnd - paragraphStart <= maximumContextLength)
            return content[paragraphStart..paragraphEnd];

        var contextStart = Math.Max(paragraphStart, safeMatch - 150);
        var contextEnd = Math.Min(paragraphEnd, contextStart + maximumContextLength);
        contextStart = Math.Max(paragraphStart, contextEnd - maximumContextLength);
        return content[contextStart..contextEnd];
    }

    private async void ReaderInPageSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        try
        {
            var sequence = _readerIsPdf
                ? ++_readerPdfSearchSequence
                : ++_readerSearchSequence;
            await Task.Delay(120);
            if (_readerIsPdf
                ? sequence != _readerPdfSearchSequence
                : sequence != _readerSearchSequence)
                return;
            var query = ReaderInPageSearchBox.Text?.Trim() ?? string.Empty;
            if (_readerIsPdf)
                await ApplyReaderPdfSearchAsync(query, sequence);
            else
                await ApplyReaderSearchAsync(query, sequence);
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0
                ? "0/0"
                : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch
        {
            _readerSearchCount = 0;
            _readerSearchIndex = -1;
            UpdateReaderSearchCount();
        }
    }

    private async void ReaderInPageSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                await ReaderInPageSearchCloseAsync();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await NavigateReaderSearchAsync(_readerSearchIndex + ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1));
                ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
            }
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async void ReaderInPageSearchPreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateReaderSearchAsync(_readerSearchIndex - 1);
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
        catch { }
    }

    private async void ReaderInPageSearchNextButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateReaderSearchAsync(_readerSearchIndex + 1);
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
        catch { }
    }

    private async void ReaderInPageSearchCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        try { await ReaderInPageSearchCloseAsync(); }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task ReaderInPageSearchCloseAsync()
    {
        await ClearReaderSearchAsync();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderInPageSearchBox.Text = string.Empty;
    }

    // PDF in-page search cannot run inside WebView2's built-in viewer (no DOM
    // to mark); Ctrl+F routes PDF to the whole-book search tab instead, like
    // the WinUI reference. Kept as a guarded no-op for the text-search entry.
    private async Task ApplyReaderPdfSearchAsync(string query, int sequence)
        => await Task.CompletedTask;

    private void ReaderProgressSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_readerProgressSliderUpdating || !IsInitialized) return;
        if (_readerSliderDragging || _readerSliderPreviewVisible)
            UpdateReaderSliderPreview(GetReaderProgressThumb());
        _ = ObserveReaderTaskAsync(NavigateReaderProgressAsync(e.NewValue));
    }

    // The footer thumb and compact TOC share the same in-app chapter preview;
    // binding hover directly to Thumb keeps the track itself passive.
    private bool _readerSliderDragging;
    private bool _readerSliderPreviewVisible;
    private Thumb? _readerProgressHoverThumb;

    private void ReaderProgressSlider_Loaded(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(AttachReaderProgressThumbHover, DispatcherPriority.Background);
    }

    private void AttachReaderProgressThumbHover()
    {
        var thumb = GetReaderProgressThumb();
        if (thumb is null || ReferenceEquals(thumb, _readerProgressHoverThumb)) return;
        if (_readerProgressHoverThumb is not null)
        {
            _readerProgressHoverThumb.PointerEntered -= ReaderProgressThumb_PointerEntered;
            _readerProgressHoverThumb.PointerExited -= ReaderProgressThumb_PointerExited;
        }

        _readerProgressHoverThumb = thumb;
        thumb.PointerEntered += ReaderProgressThumb_PointerEntered;
        thumb.PointerExited += ReaderProgressThumb_PointerExited;
    }

    private void ReaderProgressThumb_PointerEntered(object? sender, PointerEventArgs e)
    {
        UpdateReaderSliderPreview(sender as Thumb);
    }

    private void ReaderProgressThumb_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_readerSliderDragging) CloseReaderSliderPreview();
    }

    private void ReaderProgressSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ReaderProgressSlider).Properties.IsLeftButtonPressed)
        {
            _readerSliderDragging = true;
            UpdateReaderSliderPreview(GetReaderProgressThumb());
        }
    }

    private void ReaderProgressSlider_PointerMoved(object? sender, PointerEventArgs e)
    {
        var thumb = GetReaderProgressThumb();
        if (_readerSliderDragging || IsPointerOverReaderProgressThumb(e, thumb))
        {
            UpdateReaderSliderPreview(thumb);
            return;
        }

        CloseReaderSliderPreview();
    }

    private void ReaderProgressSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _readerSliderDragging = false;
        var thumb = GetReaderProgressThumb();
        if (IsPointerOverReaderProgressThumb(e, thumb))
            UpdateReaderSliderPreview(thumb);
        else
            CloseReaderSliderPreview();
    }

    private void ReaderProgressSlider_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_readerSliderDragging) CloseReaderSliderPreview();
    }

    private Thumb? GetReaderProgressThumb() =>
        ReaderProgressSlider is null
            ? null
            : FindDescendants<Thumb>(ReaderProgressSlider).FirstOrDefault();

    private static bool IsPointerOverReaderProgressThumb(PointerEventArgs e, Thumb? thumb)
    {
        if (thumb is null || thumb.Bounds.Width <= 0 || thumb.Bounds.Height <= 0) return false;
        var point = e.GetPosition(thumb);
        return point.X >= 0
            && point.X <= thumb.Bounds.Width
            && point.Y >= 0
            && point.Y <= thumb.Bounds.Height;
    }

    private void UpdateReaderSliderPreview(Thumb? thumb)
    {
        if (ReaderProgressSlider is null) return;
        thumb ??= GetReaderProgressThumb();
        if (thumb is null) return;
        _readerSliderPreviewVisible = true;
        var index = _readerIsPdf
            ? Math.Clamp((int)Math.Round(ReaderProgressSlider.Value), 1, Math.Max(1, _readerPdfPages.Count)) - 1
            : Math.Clamp((int)Math.Round(ReaderProgressSlider.Value) - 1, 0, Math.Max(0, _readerTocItems.Count - 1));
        ShowReaderChapterPreview(thumb, index, placeAbove: true);
    }

    private void CloseReaderSliderPreview()
    {
        _readerSliderPreviewVisible = false;
        if (ReaderTocCompactPanel is not null && ReaderTocCompactPanel.IsVisible)
            UpdateReaderCompactMarkerWave();
        else
            HideReaderCompactHoverLabel();
    }

    // EPUB uses the visible TOC sequence so fragment subchapters are reachable;
    // PDF remains page-granular.
    private async Task NavigateReaderProgressAsync(double value)
    {
        if (_readerIsPdf)
        {
            var page = Math.Clamp((int)Math.Round(value), 1, Math.Max(1, _readerPdfPages.Count));
            await NavigatePdfPageAsync(page, ReaderToken);
            return;
        }
        if (_readerTocItems.Count == 0) return;
        var tocIndex = Math.Clamp((int)Math.Round(value) - 1, 0, _readerTocItems.Count - 1);
        if (tocIndex == GetCurrentReaderTocIndex()) return;
        await NavigateToReaderItemAsync(
            _readerTocItems[tocIndex],
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: tocIndex < GetCurrentReaderTocIndex() ? -1 : 1);
    }

    private void ReaderLayoutSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderVerticalWritingCheck.Content = OperatingSystem.IsLinux()
            ? "竖排排版（全局，自绘单页）"
            : "竖排排版（全局，仅支持单页）";
        _suppressReaderLayoutChange = true;
        try
        {
            ReaderFontScaleSlider.Value = _readerLayout.FontScale;
            ReaderLineHeightSlider.Value = _readerLayout.LineHeight;
            ReaderMaxWidthSlider.Value = _readerLayout.MaxWidth;
            ReaderBodyPaddingSlider.Value = _readerLayout.BodyPadding;
            ReaderVerticalWritingCheck.IsChecked = _readerLayout.VerticalWriting;
            ReaderVerticalDebugBoxesCheck.IsChecked = _readerVerticalDebugBoxesEnabled;
            ReaderParagraphIndentCheck.IsChecked = _readerLayout.ParagraphIndent;
            SelectReaderFontFamily(_readerLayout.FontFamily);
            SelectReaderFlowMode(_readerLayout.FlowMode, _readerLayout.TwoPageMode);
            SelectReaderPageAnimation(_readerPageAnimation);
        }
        finally
        {
            _suppressReaderLayoutChange = false;
        }
        UpdateReaderLayoutSliderLabels();
        UpdateReaderVerticalDebugBoxesControlAvailability();
        UpdateReaderLayoutStatus();
        ReaderLayoutSettingsPopup.PlacementTarget = ReaderRoot;
        ReaderLayoutSettingsPopup.Placement = PlacementMode.AnchorAndGravity;
        ReaderLayoutSettingsPopup.PlacementAnchor = PopupAnchor.TopLeft;
        ReaderLayoutSettingsPopup.PlacementGravity = PopupGravity.BottomRight;
        ReaderLayoutSettingsPopup.HorizontalOffset = 0;
        ReaderLayoutSettingsPopup.VerticalOffset = 0;
        ReaderLayoutSettingsOverlay.Margin = new Thickness(0);
        ReaderLayoutSettingsOverlay.Width = Math.Max(0, ReaderRoot.Bounds.Width);
        ReaderLayoutSettingsOverlay.Height = Math.Max(0, ReaderRoot.Bounds.Height);
        ReaderLayoutSettingsPopup.IsOpen = true;
    }

    // ValueChanged / SelectionChanged can fire while XAML is still being
    // parsed (slider Minimum/Maximum/Value assignment clamps the value and
    // raises the event before sibling controls exist). Guard every layout
    // event handler so a partially-initialized panel can never be touched.
    private bool AreReaderLayoutControlsReady() =>
        ReaderFontScaleSlider is not null
        && ReaderLineHeightSlider is not null
        && ReaderMaxWidthSlider is not null
        && ReaderBodyPaddingSlider is not null
        && ReaderFontFamilyBox is not null
        && ReaderVerticalWritingCheck is not null
        && ReaderVerticalDebugBoxesCheck is not null
        && ReaderParagraphIndentCheck is not null;

    private bool _suppressReaderLayoutChange;
    private CancellationTokenSource? _readerLayoutApplyCancellation;

    private void ReaderLayoutSettingChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutSliderLabels();
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void ReaderFontFamilyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private async void ReaderVerticalWritingCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;

        _readerLayout = NormalizeReaderLayoutForPlatform(ReadReaderLayoutFromControls());
        SyncReaderFlowMenu();
        UpdateReaderVerticalDebugBoxesControlAvailability();
        UpdateReaderLayoutStatus();
        try
        {
            UpdateReaderZoomLabel();
            await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
            await SaveReaderLayoutAsync(CancellationToken.None);
            await SaveGlobalReaderVerticalWritingAsync(
                _readerLayout.VerticalWriting,
                CancellationToken.None);
            ReaderLayoutSettingsStatusText.Text = _readerLayout.VerticalWriting
                ? "竖排已全局开启；段首缩进也对所有书生效，自绘阅读器使用单页阅读。"
                : "竖排已全局关闭；段首缩进仍对所有书生效，现在可选择滚动、单页或双栏。";
        }
        catch (OperationCanceledException) when (_readerSessionCancellation?.IsCancellationRequested == true)
        {
        }
        catch
        {
            ReaderLayoutSettingsStatusText.Text = "竖排设置保存失败，请重试。";
        }
    }

    private void ReaderParagraphIndentCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private async void ReaderVerticalDebugBoxesCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        _readerVerticalDebugBoxesEnabled = ReaderVerticalDebugBoxesCheck.IsChecked == true;
        _appSettings = AppSettings.Normalize(_appSettings with
        {
            ReaderVerticalDebugBoxesEnabled = _readerVerticalDebugBoxesEnabled
        });
        try
        {
            await ApplyReaderLayoutToHostsAsync(
                _readerSessionCancellation?.Token ?? CancellationToken.None);
            await _appSettingsStore.SaveAsync(_appSettings, CancellationToken.None);
            ReaderLayoutSettingsStatusText.Text = _readerVerticalDebugBoxesEnabled
                ? "竖排调试外框已显示，字号缩放时会随字格同步更新。"
                : "竖排调试外框已关闭。";
        }
        catch (OperationCanceledException) when (_readerSessionCancellation?.IsCancellationRequested == true)
        {
        }
        catch
        {
            ReaderLayoutSettingsStatusText.Text = "竖排调试外框切换失败，请重试。";
        }
    }

    private void UpdateReaderVerticalDebugBoxesControlAvailability()
    {
        ReaderVerticalDebugBoxesPanel.IsVisible = OperatingSystem.IsLinux();
        ReaderVerticalDebugBoxesCheck.IsEnabled = OperatingSystem.IsLinux()
            && ReaderVerticalWritingCheck.IsChecked == true;
    }

    private void UpdateReaderLayoutStatus()
    {
        var (flowMode, twoPageMode) = GetSelectedReaderFlowMode();
        ReaderLayoutSettingsStatusText.Text = ReaderVerticalWritingCheck.IsChecked == true
            ? "竖排和段首缩进是全局设置；自绘阅读器固定使用单页阅读。"
            : twoPageMode && flowMode != 1
            ? "双页仅用于分页模式；当前模式下暂不生效。"
            : "设置立即生效；段首缩进为全局设置，其他排版参数按书保存。";
    }

    private void ScheduleReaderLayoutApply()
    {
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = new CancellationTokenSource();
        var token = _readerLayoutApplyCancellation.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(160);
            if (token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    _readerLayout = NormalizeReaderLayoutForPlatform(ReadReaderLayoutFromControls());
                    _readerPageAnimation = GetSelectedReaderPageAnimation();
                    UpdateReaderZoomLabel();
                    await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
                    await SaveReaderLayoutAsync(CancellationToken.None);
                    await SaveGlobalReaderParagraphIndentAsync(
                        _readerLayout.ParagraphIndent,
                        CancellationToken.None);
                }
                catch
                {
                }
            });
        });
    }

    private ReaderLayoutSettings ReadReaderLayoutFromControls() => new ReaderLayoutSettings(
        ReaderFontScaleSlider.Value,
        ReaderLineHeightSlider.Value,
        ReaderMaxWidthSlider.Value,
        ReaderBodyPaddingSlider.Value,
        (ReaderFontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
        GetSelectedReaderFlowMode().FlowMode,
        ReaderVerticalWritingCheck.IsChecked == true,
        GetSelectedReaderFlowMode().TwoPageMode)
    {
        ParagraphIndent = ReaderParagraphIndentCheck.IsChecked == true
    };

    private void ReaderLayoutSettingsCloseButton_Click(object? sender, RoutedEventArgs e)
        => ReaderLayoutSettingsPopup.IsOpen = false;

    private async void ReaderLayoutResetButton_Click(object? sender, RoutedEventArgs e)
    {
        _suppressReaderLayoutChange = true;
        try
        {
            ReaderFontScaleSlider.Value = ReaderLayoutDefaults.DefaultFontScale;
            ReaderLineHeightSlider.Value = ReaderLayoutDefaults.DefaultLineHeight;
            ReaderMaxWidthSlider.Value = ReaderLayoutDefaults.DefaultMaxWidth;
            ReaderBodyPaddingSlider.Value = ReaderLayoutDefaults.DefaultBodyPadding;
            ReaderVerticalWritingCheck.IsChecked = false;
            ReaderParagraphIndentCheck.IsChecked = true;
            SelectReaderFontFamily(ReaderFontDefaults.DefaultFamily);
            SelectReaderFlowMode(1, false);
            SelectReaderPageAnimation(ReaderAnimationFade);
        }
        finally
        {
            _suppressReaderLayoutChange = false;
        }
        UpdateReaderLayoutSliderLabels();
        _readerLayout = NormalizeReaderLayoutForPlatform(ReadReaderLayoutFromControls());
        _readerPageAnimation = ReaderAnimationFade;
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(ReaderToken);
        await SaveReaderLayoutAsync(CancellationToken.None);
        await SaveGlobalReaderVerticalWritingAsync(false, CancellationToken.None);
        await SaveGlobalReaderParagraphIndentAsync(
            _readerLayout.ParagraphIndent,
            CancellationToken.None);
        ReaderLayoutSettingsStatusText.Text = "已恢复默认排版。";
    }

    private void UpdateReaderLayoutSliderLabels()
    {
        ReaderFontScaleValueText.Text = $"{ReaderFontScaleSlider.Value:0.00}×";
        ReaderLineHeightValueText.Text = ReaderLineHeightSlider.Value.ToString("0.00");
        ReaderMaxWidthValueText.Text = $"{ReaderMaxWidthSlider.Value:0} px";
        ReaderBodyPaddingValueText.Text = $"{ReaderBodyPaddingSlider.Value:0} px";
    }

    private void SelectReaderFontFamily(string family)
    {
        var item = ReaderFontFamilyBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, family, StringComparison.OrdinalIgnoreCase));
        ReaderFontFamilyBox.SelectedItem = item ?? ReaderFontFamilyBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private (int FlowMode, bool TwoPageMode) GetSelectedReaderFlowMode()
        => (_readerLayout.FlowMode, _readerLayout.TwoPageMode);

    private void SelectReaderFlowMode(int flowMode, bool twoPageMode)
    {
        // The flow mode lives in the header menu (ReaderFlowButton); keep the
        // menu state in sync when the layout settings panel is opened.
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPageMode
        });
        SyncReaderFlowMenu();
    }

    private int GetSelectedReaderPageAnimation() => _readerPageAnimation;

    private void SelectReaderPageAnimation(int animation)
    {
        _readerPageAnimation = animation;
        SyncReaderAnimationMenu();
    }

    private void ReaderZenMinimalTocButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_readerZenMode) return;
        _readerTocExpanded = false;
        _readerTocMinimal = !_readerTocMinimal;
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
    }

    private void ReaderExitZenButton_Click(object? sender, RoutedEventArgs e) => ExitReaderZenMode();

    private void ReaderAssistantToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerZenMode) return;
        var visible = !ReaderAssistantPanel.IsVisible;
        ReaderAssistantPanel.IsVisible = visible;
        ReaderRoot.ColumnDefinitions[2].Width = visible ? new GridLength(360) : new GridLength(0);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }

}
