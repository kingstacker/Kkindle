using Avalonia;
using Avalonia.Controls;

namespace Kkindle;

internal static class ReaderAnnotationCardLayout
{
    // Prefer below the complete visible selection, then above. Limit long notes
    // to the available space and scroll them rather than covering the quote.
    internal static Rect Place(Rect quote, Size viewport, Func<double, double> measure)
    {
        const double gap = 8;
        var width = Math.Min(220, Math.Max(1, viewport.Width - 2 * gap));
        var height = measure(width);
        var x = Math.Clamp(quote.Left, gap, Math.Max(gap, viewport.Width - width - gap));
        var below = Math.Max(0, viewport.Height - gap - quote.Bottom - gap);
        var above = Math.Max(0, quote.Top - 2 * gap);
        if (below >= height) return new Rect(x, quote.Bottom + gap, width, height);
        if (above >= height) return new Rect(x, quote.Top - gap - height, width, height);
        var y = Math.Clamp(quote.Top, gap, Math.Max(gap, viewport.Height - height - gap));
        var sideHeight = Math.Min(height, Math.Max(1, viewport.Height - 2 * gap));
        if (viewport.Width - quote.Right - 2 * gap >= width)
            return new Rect(quote.Right + gap, y, width, sideHeight);
        if (quote.Left - 2 * gap >= width)
            return new Rect(quote.Left - gap - width, y, width, sideHeight);
        return below >= above
            ? new Rect(x, quote.Bottom + gap, width, Math.Max(1, below))
            : new Rect(x, gap, width, Math.Max(1, above));
    }

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
