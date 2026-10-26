using Avalonia;
using Avalonia.Controls;

namespace Kkindle;

internal static class ReaderAnnotationCardLayout
{
    public static Func<double, double> CreateMeasure(Border card, TextBlock text)
    {
        var lastWidth = double.NaN;
        var lastHeight = 0d;
        return width =>
        {
            if (width != lastWidth)
            {
                var inset = card.Padding + card.BorderThickness;
                var contentWidth = Math.Max(1, width - inset.Left - inset.Right);
                var content = (Grid)card.Child!;
                var heading = content.Children[0];
                heading.Measure(new Size(contentWidth, double.PositiveInfinity));
                // Measure the text directly: a ScrollViewer can report its old
                // viewport instead of its full content before the popup opens.
                text.Measure(new Size(Math.Max(1, contentWidth - 16), double.PositiveInfinity));
                lastHeight = Math.Ceiling(heading.DesiredSize.Height + content.RowSpacing
                    + text.DesiredSize.Height + inset.Top + inset.Bottom);
                lastWidth = width;
            }
            return lastHeight;
        };
    }
}
