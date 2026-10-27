using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    public ObservableCollection<ReaderAiImageViewModel> ReaderAiDraftImages { get; } = [];
    private bool _readerAiPastingImage;
    private int _readerAiImageDraftVersion;

    private async void ReaderAiQuestionBox_PastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await PasteReaderAiClipboardAsync();
    }

    private async Task PasteReaderAiClipboardAsync()
    {
        if (_readerAiPastingImage || Clipboard is not { } clipboard) return;
        _readerAiPastingImage = true;
        var version = _readerAiImageDraftVersion;
        try
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (version != _readerAiImageDraftVersion) return;
            if (bitmap is not null)
            {
                if (ReaderAiDraftImages.Count >= 4)
                    throw new InvalidOperationException(T("每条消息最多添加 4 张图片，请先移除部分图片。"));
                using var stream = new MemoryStream();
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                if (stream.Length + ReaderAiDraftImages.Sum(item => (long)item.Attachment.Data.Length) > 10 * 1024 * 1024)
                    throw new InvalidOperationException(T("图片总大小不能超过 10 MB，请缩小图片后粘贴。"));
                var image = new ReaderAiImageViewModel(new AiImageAttachment("image/png", stream.ToArray()));
                ReaderAiDraftImages.Add(image);
                SelectReaderAiDraftImage(image);
                ReaderAiImageStatus.Text = string.Empty;
            }
            else if (await clipboard.TryGetTextAsync() is { } text && version == _readerAiImageDraftVersion)
                ReaderAiQuestionBox.SelectedText = text;
        }
        catch (Exception exception)
        {
            ReaderAiImageStatus.Text = T("粘贴图片失败：{0}", exception.Message);
        }
        finally
        {
            _readerAiPastingImage = false;
        }
    }

    private void ReaderAiDraftImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: ReaderAiImageViewModel image } border
            || !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        SelectReaderAiDraftImage(image);
    }

    private void SelectReaderAiDraftImage(ReaderAiImageViewModel selected)
    {
        foreach (var image in ReaderAiDraftImages) image.IsSelected = ReferenceEquals(image, selected);
    }

    private void ReaderAiRemoveImageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAiImageViewModel image }) return;
        ReaderAiDraftImages.Remove(image);
        image.Dispose();
        ReaderAiImageStatus.Text = string.Empty;
    }

    private void ClearReaderAiDraftImages()
    {
        _readerAiImageDraftVersion++;
        foreach (var image in ReaderAiDraftImages) image.Dispose();
        ReaderAiDraftImages.Clear();
        ReaderAiImageStatus.Text = string.Empty;
    }
}
