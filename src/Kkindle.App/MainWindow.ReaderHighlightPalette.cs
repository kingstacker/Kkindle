using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Kkindle;

public partial class MainWindow
{
    private PopupFlyoutBase? ReaderMarkerPaletteFlyout => ReaderSelectionMarkerColorButton is { } button
        ? FlyoutBase.GetAttachedFlyout(button) as PopupFlyoutBase
        : null;

    private void ReaderSelectionHighlightFlyout_Opened(object? sender, EventArgs e)
        => RefreshReaderMarkerColorPreview();

    private void ReaderSelectionHighlightFlyout_Closed(object? sender, EventArgs e)
    {
        ReaderMarkerPaletteFlyout?.Hide();
        StopReaderSelectionHighlightPointerTracking();
    }

    private void ReaderSelectionMarkerColorButton_Click(object? sender, RoutedEventArgs e)
    {
        // Opening the palette must not invoke the containing highlight row.
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        RefreshReaderMarkerColorPreview();
        if (ReaderMarkerPaletteFlyout is { } flyout)
        {
            if (flyout.IsOpen) flyout.Hide();
            else flyout.ShowAt(ReaderSelectionMarkerColorButton);
        }
    }

    private string ReaderMarkerColorForSelection()
    {
        var chapterPath = _readerIsPdf ? $"pdf:page:{_readerPdfPage}" : GetReaderChapterPath();
        var annotation = _selectedReaderAnnotation ?? ReaderAnnotations.FirstOrDefault(item =>
            string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase)
            && item.StartOffset == _readerPendingSelectionStartOffset
            && item.EndOffset == _readerPendingSelectionEndOffset);
        return NormalizeReaderAnnotationColor(annotation?.UnderlineStyle == "marker"
            ? annotation.Color
            : _readerLastMarkerColor);
    }

    private void RefreshReaderMarkerColorPreview()
    {
        var color = ReaderMarkerColorForSelection();
        ReaderSelectionMarkerColorPreview.Background = new SolidColorBrush(Color.Parse(color));
        ReaderSelectionMarkerColorText.Text = color switch
        {
            "#000000" => T("黑白反色"),
            "#FFD54F" => T("黄色"),
            "#81C784" => T("绿色"),
            "#64B5F6" => T("蓝色"),
            "#F48FB1" => T("粉色"),
            "#B39DDB" => T("紫色"),
            "#FFB74D" => T("橙色"),
            _ => color
        };
        foreach (var option in ReaderSelectionMarkerPalette.Children.OfType<Button>())
            option.Classes.Set("selected", string.Equals(option.Tag as string, color, StringComparison.OrdinalIgnoreCase));
    }
}
