using Avalonia.Interactivity;
using Avalonia.Controls;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private int _bookReflectionDetailsVersion;
    private bool _bookReflectionEditorBusy;
    private ReaderBookReflection? _selectedBookReflection;

    private async Task RefreshBookReflectionDetailsAsync(Guid bookId)
    {
        var version = ++_bookReflectionDetailsVersion;
        _selectedBookReflection = null;
        DetailReflectionPreviewText.Markdown = T("正在读取读后思考…");
        DetailReflectionUpdatedText.Text = string.Empty;
        ToolTip.SetTip(DetailReflectionPanel, null);
        ToolTip.SetTip(DetailReflectionPreviewText, null);
        try
        {
            var reflection = await _readerData.GetBookReflectionAsync(
                bookId,
                _lifetimeCancellation.Token);
            if (version != _bookReflectionDetailsVersion
                || _selectedCard?.Book.Id != bookId)
                return;

            _selectedBookReflection = reflection;
            UpdateBookReflectionPreview(reflection);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version != _bookReflectionDetailsVersion
                || _selectedCard?.Book.Id != bookId)
                return;
            DetailReflectionPreviewText.Markdown = T(
                "读后思考暂时不可用：{0}",
                UiText.Localize(exception.Message));
            DetailReflectionUpdatedText.Text = string.Empty;
            ToolTip.SetTip(DetailReflectionPanel, null);
            ToolTip.SetTip(DetailReflectionPreviewText, null);
        }
    }

    private void UpdateBookReflectionPreview(ReaderBookReflection? reflection)
    {
        var normalizedContent = reflection is null
            ? string.Empty
            : BookReflectionEditorSurface.NormalizeMarkdown(reflection.Content);
        if (reflection is null || string.IsNullOrWhiteSpace(normalizedContent))
        {
            DetailReflectionPreviewText.Markdown = T("还没有写下读后思考。");
            DetailReflectionUpdatedText.Text = string.Empty;
            ToolTip.SetTip(DetailReflectionPanel, null);
            ToolTip.SetTip(DetailReflectionPreviewText, null);
            return;
        }

        DetailReflectionPreviewText.Markdown = BuildBookReflectionPreview(normalizedContent);
        ToolTip.SetTip(DetailReflectionPanel, normalizedContent);
        ToolTip.SetTip(DetailReflectionPreviewText, normalizedContent);
        DetailReflectionUpdatedText.Text = T(
            "更新时间：{0}",
            reflection.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    private async void EditBookReflectionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await EditBookReflectionAsync(_selectedCard);
    }

    private async Task EditBookReflectionAsync(BookCardViewModel card)
    {
        if (_bookReflectionEditorBusy) return;
        _bookReflectionEditorBusy = true;
        EditBookReflectionButton.IsEnabled = false;
        try
        {
            var existing = await _readerData.GetBookReflectionAsync(
                card.Book.Id,
                _lifetimeCancellation.Token);
            var editor = new BookReflectionEditorWindow(
                card.Title,
                existing is null
                    ? string.Empty
                    : BookReflectionEditorSurface.NormalizeMarkdown(existing.Content));
            var result = await editor.ShowAsync(this);
            if (result is null) return;

            var now = DateTimeOffset.UtcNow;
            var reflection = new ReaderBookReflection
            {
                BookId = card.Book.Id,
                Content = result.Content.Trim(),
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now
            };
            await _readerData.SaveBookReflectionAsync(
                reflection,
                _lifetimeCancellation.Token);

            if (_selectedCard?.Book.Id == card.Book.Id)
            {
                _selectedBookReflection = reflection;
                UpdateBookReflectionPreview(reflection);
            }
            MarkReadingMaterialsDirty();
            if (ReadingMaterialsPage.IsVisible)
                await RefreshReadingMaterialsAsync();
            SetTaskStatus(string.IsNullOrWhiteSpace(reflection.Content)
                ? T("读后思考已清空。")
                : T("读后思考已保存。"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("保存读后思考失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            _bookReflectionEditorBusy = false;
            if (_selectedCard is not null)
                EditBookReflectionButton.IsEnabled = true;
        }
    }

    private async Task OpenBookReflectionFromReadingMaterialsAsync(ReaderBookReflection reflection)
    {
        var card = ViewModel.Books.FirstOrDefault(item => item.Book.Id == reflection.BookId);
        if (card is null)
        {
            SetTaskStatus(T("找不到这条读后思考对应的本地书籍。"));
            return;
        }

        SelectBook(card);
        await EditBookReflectionAsync(card);
    }

    private static string BuildBookReflectionPreview(string content)
    {
        var normalized = BookReflectionEditorSurface.NormalizeMarkdown(content)
            .Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        // Image data is stored inline in the reflection Markdown. Truncating
        // the raw string in the middle of a data URI would break the preview.
        if (normalized.Contains("![", StringComparison.Ordinal))
            return normalized;
        return normalized.Length <= 480
            ? normalized
            : normalized[..480].TrimEnd() + "…";
    }
}
