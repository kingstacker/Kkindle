using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    // The annotation editor lives in the in-body input popup now; the notes
    // pane only lists annotations. Style/color therefore come from the last
    // 划线 ▾ choice (or the defaults below) instead of panel combos.
    private string _readerLastHighlightStyle = "solid";
    private string _readerLastMarkerColor = "#000000";
    private Point? _readerLastSelectionPopupAnchor;
    private double? _readerLastSelectionPopupBottom;

    private async Task SaveReaderAnnotationAsync(
        string? note,
        string? underlineStyle = null,
        string? color = null)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var selectedText = (_readerPendingSelection ?? string.Empty).Trim();
        var point = _readerPendingPdfPoint;
        if (point is null
            && _selectedReaderAnnotation is { } selected
            && ReaderPdfPointAnchor.TryParse(selected.Fragment, out var pointX, out var pointY))
        {
            point = (pointX, pointY);
        }
        var isPdfPageNote = _readerIsPdf
            && (point is not null
                || _selectedReaderAnnotation is { StartOffset: 0, EndOffset: 0 }
                || selectedText.Length == 0 && _selectedReaderAnnotation is null);
        if (selectedText.Length == 0 && _selectedReaderAnnotation is null && !isPdfPageNote)
        {
            ReaderStatusText.Text = T("请先在正文中选择一段文字。");
            return;
        }

        var chapterPath = _readerIsPdf
            ? $"pdf:page:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(chapterPath)) return;

        var exact = selectedText.Length > 0
            ? ReaderAnnotations.FirstOrDefault(item =>
                string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase)
                && item.StartOffset == _readerPendingSelectionStartOffset
                && item.EndOffset == _readerPendingSelectionEndOffset)
            : null;
        var existing = _selectedReaderAnnotation ?? exact;
        var annotation = existing ?? new ReaderAnnotation
        {
            BookId = _readerBookCard.Book.Id,
            BookFileId = _readerBookFile.Id,
            ChapterPath = chapterPath,
            SelectedText = isPdfPageNote
                ? T("PDF 第 {0} 页 · 页面批注", _readerPdfPage)
                : selectedText,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Reject overlapping annotations in the same chapter unless the user is
        // editing the exact annotation being saved (mirrors the WinUI guard).
        if (selectedText.Length > 0 && _selectedReaderAnnotation is null)
        {
            var overlaps = ReaderAnnotations.Any(item =>
                item.Id != exact?.Id
                && string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase)
                && _readerPendingSelectionStartOffset < item.EndOffset
                && _readerPendingSelectionEndOffset > item.StartOffset);
            if (overlaps)
            {
                ShowReaderTransientStatus(T("这段文字与已有划线重叠，请缩小选择范围"));
                return;
            }
        }

        var normalizedStyle = NormalizeReaderAnnotationStyle(underlineStyle ?? existing?.UnderlineStyle ?? _readerLastHighlightStyle);
        annotation.ChapterPath = chapterPath;
        annotation.Fragment = _readerIsPdf
            ? point is { } pdfPoint
                ? ReaderPdfPointAnchor.Encode(pdfPoint.X, pdfPoint.Y)
                : null
            : _readerCurrentFragment;
        if (selectedText.Length > 0) annotation.SelectedText = selectedText;
        // Quick style/color changes preserve notes; the editor can still
        // explicitly clear a note by saving an empty string.
        if (note is not null) annotation.Note = note.Trim();
        annotation.Color = NormalizeReaderAnnotationColor(
            color ?? existing?.Color ?? (normalizedStyle == "marker" ? _readerLastMarkerColor : "#000000"));
        annotation.UnderlineStyle = normalizedStyle;
        annotation.StartOffset = isPdfPageNote ? 0 : _readerPendingSelectionStartOffset;
        annotation.EndOffset = isPdfPageNote
            ? 0
            : _readerPendingSelectionEndOffset > _readerPendingSelectionStartOffset
            ? _readerPendingSelectionEndOffset
            : annotation.StartOffset + annotation.SelectedText.Length;
        annotation.Prefix = _readerPendingSelectionPrefix;
        annotation.Suffix = _readerPendingSelectionSuffix;
        annotation.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _readerData.SaveAnnotationAsync(annotation, ReaderToken);
            MarkReadingMaterialsDirty();
            await RefreshReaderAnnotationsAsync(ReaderToken);
            _selectedReaderAnnotation = ReaderAnnotations.FirstOrDefault(item => item.Id == annotation.Id);
            if (IsLinuxReaderTextFallbackActive())
            {
                ApplyLinuxReaderTextFallbackAnnotationRanges();
                ClearLinuxReaderTextFallbackVisualSelection();
            }
            if (CurrentReaderHost is { } host)
            {
                await ApplySavedAnnotationsAsync(host, ReaderToken);
                await ClearCurrentReaderSelectionAsync(host);
            }
            HideReaderSelectionPopup();
            HideReaderAnnotationInputPopup();
            _readerPendingSelection = null;
            _readerPendingSelectionStartOffset = 0;
            _readerPendingSelectionEndOffset = 0;
            _readerPendingSelectionPrefix = string.Empty;
            _readerPendingSelectionSuffix = string.Empty;
            _readerPendingPdfPoint = null;
            ShowReaderTransientStatus(string.IsNullOrWhiteSpace(annotation.Note) ? T("划线已保存") : T("批注已保存"));
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("保存批注失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void ShowReaderAnnotationInputPopup()
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)
            && _readerPendingPdfPoint is null
            && _selectedReaderAnnotation is null)
            return;
        ReaderAnnotationInputQuote.Text = string.IsNullOrWhiteSpace(_readerPendingSelection)
            ? _selectedReaderAnnotation?.SelectedText
                ?? T("PDF 第 {0} 页 · 页面批注", _readerPdfPage)
            : _readerPendingSelection;
        ReaderAnnotationInputBox.Text = _selectedReaderAnnotation?.Note ?? string.Empty;
        ShowReaderPopupNearSelection(
            ReaderAnnotationInputPopup,
            ReaderAnnotationInputPanel,
            fallbackWidth: 320,
            fallbackHeight: 190,
            _readerLastSelectionPopupAnchor,
            _readerLastSelectionPopupBottom);
        ReaderAnnotationInputBox.Focus();
    }

    private void HideReaderAnnotationInputPopup()
    {
        ReaderAnnotationInputPopup.IsOpen = false;
    }

    private void ReaderAnnotationInputPopup_Closed(object? sender, EventArgs e)
    {
        // Light dismiss (clicking back into the body) closes the popup without
        // going through cancel/save; drop the pending selection so a stale
        // highlight doesn't linger behind the Closed cleanup.
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)
            && _readerPendingPdfPoint is null) return;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerPendingPdfPoint = null;
        _selectedReaderAnnotation = null;
        if (CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }

    private void ReaderAnnotationInputCancelButton_Click(object? sender, RoutedEventArgs e)
        => CloseReaderAnnotationInputPopup();

    private async void ReaderAnnotationInputSaveButton_Click(object? sender, RoutedEventArgs e)
        => await SaveReaderAnnotationAsync(ReaderAnnotationInputBox.Text ?? string.Empty);

    private async void ReaderSelectionDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedReaderAnnotation is { } annotation)
            await DeleteReaderAnnotationAsync(annotation);
    }

    private void ReaderAnnotationInputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseReaderAnnotationInputPopup();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            ReaderAnnotationInputSaveButton_Click(sender, e);
        }
    }

    // Cancel/light dismiss: close the window and release the live selection so
    // the body returns to its plain rendering.
    private void CloseReaderAnnotationInputPopup()
    {
        HideReaderAnnotationInputPopup();
        if (IsLinuxReaderTextFallbackActive())
        {
            ClearLinuxReaderTextFallbackSelectionState();
            return;
        }
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)
            && _readerPendingPdfPoint is null) return;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerPendingPdfPoint = null;
        _selectedReaderAnnotation = null;
        if (CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }

    private async void ReaderAnnotationItemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAnnotation annotation }) return;
        await NavigateToReaderAnnotationAsync(annotation);
        if (_readerIsPdf) EditReaderPdfAnnotation(annotation);
    }

    private async Task NavigateToReaderAnnotationAsync(ReaderAnnotation annotation)
    {
        if (_readerIsPdf)
        {
            if (TryGetReaderPdfPage(annotation.ChapterPath, out var page))
            {
                await NavigatePdfPageAsync(page, ReaderToken);
                if (CurrentReaderHost is NativePdfReaderHost pdf && pdf.PageNumber == page)
                {
                    if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var x, out var y))
                    {
                        pdf.ScrollToPdfPoint(x, y);
                    }
                    else
                    {
                        pdf.ScrollToOffset(annotation.StartOffset);
                    }
                }
            }
            return;
        }
        if (_readerDocument is null) return;
        var chapterIndex = _readerDocument.Chapters
            .Select((path, index) => (path, index))
            .Where(item => string.Equals(
                Path.GetRelativePath(_readerDocument.RootPath, item.path).Replace('\\', '/'),
                annotation.ChapterPath,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (chapterIndex < 0 || chapterIndex >= _readerDocument.Chapters.Count) return;
        _readerPendingAnnotation = annotation;
        var chapterUri = new Uri(_readerDocument.Chapters[chapterIndex]);
        var fragment = DecodeReaderFragment(annotation.Fragment);
        if (!string.IsNullOrWhiteSpace(fragment))
            chapterUri = new Uri(chapterUri.AbsoluteUri + "#" + Uri.EscapeDataString(fragment));
        try
        {
            await NavigateToReaderItemAsync(
                new EpubReaderNavigationItem(
                    T("第 {0} 章", chapterIndex + 1),
                    chapterUri.AbsoluteUri,
                    chapterIndex),
                ReaderToken,
                ReaderNavigationIntent.Annotation);
        }
        finally
        {
            if (ReferenceEquals(_readerPendingAnnotation, annotation))
                _readerPendingAnnotation = null;
        }
    }

    private async void ReaderAnnotationItemDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAnnotation annotation }) return;
        await DeleteReaderAnnotationAsync(annotation);
    }

    private async Task DeleteReaderAnnotationAsync(ReaderAnnotation annotation)
    {
        var isEditing = ReferenceEquals(_selectedReaderAnnotation, annotation);
        try
        {
            await _readerData.DeleteAnnotationAsync(annotation.Id, ReaderToken);
            MarkReadingMaterialsDirty();
            await RefreshReaderAnnotationsAsync(ReaderToken);
            if (IsLinuxReaderTextFallbackActive())
                ApplyLinuxReaderTextFallbackAnnotationRanges();
            if (CurrentReaderHost is { } host)
            {
                await ApplySavedAnnotationsAsync(host, ReaderToken);
                if (isEditing)
                    await ClearCurrentReaderSelectionAsync(host);
            }
            if (isEditing)
            {
                HideReaderSelectionPopup();
                HideReaderAnnotationHoverPopup();
                HideReaderAnnotationInputPopup();
                _readerPendingSelection = null;
                _readerPendingSelectionStartOffset = 0;
                _readerPendingSelectionEndOffset = 0;
                _readerPendingSelectionPrefix = string.Empty;
                _readerPendingSelectionSuffix = string.Empty;
                _readerPendingPdfPoint = null;
                _selectedReaderAnnotation = null;
            }
            ShowReaderTransientStatus(T("批注已删除"));
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("删除批注失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task PerformReaderSelectionCopyAsync()
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(_readerPendingSelection);
        // Clear the live DOM selection so the highlighted text returns to the
        // normal body rendering after the copy action (WinUI reference); the
        // in-page selection bar hides itself once the selection is empty.
        if (CurrentReaderHost is { } host) await ClearCurrentReaderSelectionAsync(host);
        HideReaderSelectionPopup();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        ShowReaderTransientStatus(T("已复制选中文字"));
    }

    // The "划线 ▾" quick-style actions now arrive from the in-page selection
    // bar (the webview is a native HWND island Avalonia cannot paint over);
    // the chosen style is remembered as the default for later annotations.
    private async Task ApplyReaderHighlightStyleAsync(string style, string? color = null)
    {
        _readerLastHighlightStyle = NormalizeReaderAnnotationStyle(style);
        if (_readerLastHighlightStyle == "marker")
        {
            _readerLastMarkerColor = NormalizeReaderAnnotationColor(color ?? _readerLastMarkerColor);
            color = _readerLastMarkerColor;
        }
        await SaveReaderAnnotationAsync(
            null,
            underlineStyle: _readerLastHighlightStyle,
            color: color);
    }

    private static async Task ClearCurrentReaderSelectionAsync(IReaderHost host)
    {
        if (host is NativePdfReaderHost pdf)
        {
            pdf.ClearSelection();
            return;
        }
        if (host is NativeReaderHost nativeReader)
        {
            nativeReader.ClearSelection();
            return;
        }

        try
        {
            await host.InvokeScriptAsync(
                "(() => { const s = window.getSelection(); if (s) s.removeAllRanges(); return true; })();");
        }
        catch
        {
        }
    }

    private static string NormalizeReaderAnnotationStyle(string? style) =>
        style?.Trim().ToLowerInvariant() switch
        {
            "double" => "double",
            "dashed" => "dashed",
            "dotted" => "dotted",
            "wavy" => "wavy",
            "marker" => "marker",
            _ => "solid"
        };

    private void EditReaderAnnotation(ReaderAnnotation annotation)
    {
        if (_readerIsPdf
            || !string.Equals(
                annotation.ChapterPath,
                GetReaderChapterPath(),
                StringComparison.OrdinalIgnoreCase))
            return;

        _selectedReaderAnnotation = annotation;
        _readerPendingSelection = annotation.SelectedText;
        _readerPendingSelectionStartOffset = annotation.StartOffset;
        _readerPendingSelectionEndOffset = annotation.EndOffset;
        _readerPendingSelectionPrefix = annotation.Prefix;
        _readerPendingSelectionSuffix = annotation.Suffix;
        _readerPendingPdfPoint = null;
        _readerLastHighlightStyle = NormalizeReaderAnnotationStyle(annotation.UnderlineStyle);
        _readerLastMarkerColor = NormalizeReaderAnnotationColor(annotation.Color);
        _readerLastSelectionPopupAnchor = null;
        _readerLastSelectionPopupBottom = null;

        if (CurrentReaderHost is NativeReaderHost nativeReader)
        {
            var bounds = nativeReader.GetAnnotationBounds(annotation);
            if (bounds.Count > 0
                && nativeReader.TranslatePoint(
                    new Point(bounds[0].X, bounds[0].Y),
                    ReaderWebViewHost) is { } anchor)
            {
                _readerLastSelectionPopupAnchor = anchor;
                _readerLastSelectionPopupBottom = anchor.Y + bounds[0].Height;
            }
        }

        HideReaderAnnotationHoverPopup();
        HideReaderSelectionPopup();
        ShowReaderSelectionPopup(
            _readerLastSelectionPopupAnchor,
            _readerLastSelectionPopupBottom);
    }

    private async Task PerformReaderSelectionDictionaryAsync()
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        var term = _readerPendingSelection.Trim();
        var entries = await _dictionaryService.LookupAsync(term, ReaderToken);
        // Show every dictionary entry in a dialog, matching the WinUI
        // reference's ReaderSelectionDictionaryButton_Click.
        await ShowMessageAsync(T("词典 · {0}", term), entries.Count == 0
            ? T("没有找到释义。请先在“字典管理”中导入词典。")
            : string.Join("\n\n", entries.Select(entry => $"[{entry.DictionaryName}] {entry.Definition}")),
            T("返回阅读"));
    }

    private void EditReaderPdfAnnotation(ReaderAnnotation annotation)
    {
        if (!_readerIsPdf || !TryGetReaderPdfPage(annotation.ChapterPath, out var page) || page != _readerPdfPage) return;
        _selectedReaderAnnotation = annotation;
        var isPoint = ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var pointX, out var pointY);
        _readerPendingPdfPoint = isPoint ? (pointX, pointY) : null;
        _readerPendingSelection = isPoint || annotation.EndOffset <= annotation.StartOffset
            ? null
            : annotation.SelectedText;
        _readerPendingSelectionStartOffset = annotation.StartOffset;
        _readerPendingSelectionEndOffset = annotation.EndOffset;
        _readerPendingSelectionPrefix = annotation.Prefix;
        _readerPendingSelectionSuffix = annotation.Suffix;
        _readerLastHighlightStyle = annotation.UnderlineStyle;
        _readerLastSelectionPopupAnchor = null;
        _readerLastSelectionPopupBottom = null;
        if (CurrentReaderHost is NativePdfReaderHost pdf)
        {
            var point = isPoint
                ? pdf.GetPdfPointPosition(annotation)
                : null;
            var bounds = pdf.GetRangeBounds(annotation.StartOffset, annotation.EndOffset);
            var position = point ?? (bounds.Count > 0 ? bounds[0].TopLeft : (Point?)null);
            if (position is { } localPosition
                && pdf.TranslatePoint(localPosition, ReaderWebViewHost) is { } anchor)
            {
                _readerLastSelectionPopupAnchor = anchor;
                _readerLastSelectionPopupBottom = anchor.Y + (bounds.Count > 0 ? bounds[0].Height : 1);
            }
        }
        HideReaderAnnotationHoverPopup();
        HideReaderSelectionPopup();
        ShowReaderAnnotationInputPopup();
    }

    private void BeginReaderPdfPointAnnotation(
        int page,
        double pageX,
        double pageY,
        double hostX,
        double hostY)
    {
        if (!_readerIsPdf
            || CurrentReaderHost is not NativePdfReaderHost pdf
            || page != pdf.PageNumber)
            return;

        UpdateReaderPdfPointNoteButton(false);
        _selectedReaderAnnotation = null;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerPendingPdfPoint = (
            Math.Clamp(double.IsFinite(pageX) ? pageX : 0.5, 0, 1),
            Math.Clamp(double.IsFinite(pageY) ? pageY : 0.5, 0, 1));
        _readerLastSelectionPopupAnchor = new Point(hostX, hostY);
        _readerLastSelectionPopupBottom = hostY + 1;
        if (pdf.View is Control nativeView
            && nativeView.TranslatePoint(new Point(0, 0), ReaderWebViewHost) is { } nativeOrigin)
        {
            _readerLastSelectionPopupAnchor = new Point(
                _readerLastSelectionPopupAnchor.Value.X + nativeOrigin.X,
                _readerLastSelectionPopupAnchor.Value.Y + nativeOrigin.Y);
            _readerLastSelectionPopupBottom += nativeOrigin.Y;
        }
        HideReaderSelectionPopup();
        HideReaderAnnotationHoverPopup();
        ShowReaderAnnotationInputPopup();
    }

    private void ReaderFootnoteCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        _readerFootnoteHoverSequence++;
        HideReaderFootnotePopup();
    }

    private async void ReaderExportMarkdownButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReaderAnnotationsAsync(markdown: true);

    private async void ReaderExportTextButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReaderAnnotationsAsync(markdown: false);

    private async Task ExportReaderAnnotationsAsync(bool markdown)
    {
        if (_readerBookCard is null) return;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var extension = markdown ? "md" : "txt";
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = markdown ? T("导出 Kreader 批注 Markdown") : T("导出 Kreader 批注文本"),
                SuggestedFileName = T("{0}-批注.{1}", _readerBookCard.Title, extension),
                FileTypeChoices = [new FilePickerFileType(markdown ? "Markdown" : T("文本")) { Patterns = [$"*.{extension}"] }]
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            var resolver = new Func<string, string>(chapterPath =>
                _readerDocument?.Navigation
                    .FirstOrDefault(item => string.Equals(
                        Path.GetRelativePath(_readerDocument.RootPath, new Uri(item.Target).LocalPath).Replace('\\', '/'),
                        chapterPath,
                        StringComparison.OrdinalIgnoreCase))?.Title
                ?? chapterPath);
            var content = markdown
                ? ReaderAnnotationExport.BuildMarkdown(_readerBookCard.Title, _readerBookCard.Authors, ReaderAnnotations, resolver)
                : ReaderAnnotationExport.BuildPlainText(_readerBookCard.Title, _readerBookCard.Authors, ReaderAnnotations, resolver);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), ReaderToken);
            ReaderExportStatusText.Text = T("已导出 {0} 条批注。", ReaderAnnotations.Count);
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("导出批注失败：{0}", UiText.Localize(exception.Message));
        }
    }
}
