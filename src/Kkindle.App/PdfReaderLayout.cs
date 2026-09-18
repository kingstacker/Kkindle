using Avalonia;

namespace Kkindle;

public enum PdfReaderDisplayMode { Continuous, SinglePage, TwoPage }
public enum PdfReaderFitMode { Width, Page }

/// <summary>Page geometry is independent of the bounded cache of rendered regions.</summary>
internal sealed class PdfReaderLayout
{
    internal const double Margin = 16;
    internal const double Gap = 18;
    public Rect[] Pages { get; }
    public Size Extent { get; }
    public PdfReaderDisplayMode Mode { get; }
    public int FirstPage { get; }
    public int LastPage { get; }

    public PdfReaderLayout(IReadOnlyList<Size> sizes, Size viewport, int pageNumber,
        PdfReaderDisplayMode mode, PdfReaderFitMode fit, double zoom, double dpi, int rotation = 0)
    {
        Mode = mode;
        Pages = new Rect[sizes.Count];
        if (sizes.Count == 0) { Extent = viewport; return; }
        pageNumber = Math.Clamp(pageNumber, 1, sizes.Count);
        FirstPage = mode == PdfReaderDisplayMode.Continuous ? 1
            : mode == PdfReaderDisplayMode.TwoPage ? (pageNumber - 1) / 2 * 2 + 1 : pageNumber;
        LastPage = mode == PdfReaderDisplayMode.Continuous ? sizes.Count
            : Math.Min(sizes.Count, FirstPage + (mode == PdfReaderDisplayMode.TwoPage ? 1 : 0));
        var availableWidth = Math.Max(1, viewport.Width - Margin * 2);
        var availableHeight = Math.Max(1, viewport.Height - Margin * 2);
        double Snap(double value) => Math.Round(value * dpi) / dpi;
        Size PageSize(int index)
        {
            var size = sizes[index];
            return rotation % 180 == 0 ? size : new(size.Height, size.Width);
        }
        var extentWidth = viewport.Width;
        var extentHeight = viewport.Height;
        if (mode == PdfReaderDisplayMode.TwoPage)
        {
            var left = PageSize(FirstPage - 1);
            var right = PageSize(Math.Min(sizes.Count - 1, FirstPage));
            var scale = Math.Max(1, availableWidth - Gap) / (left.Width + right.Width);
            if (fit == PdfReaderFitMode.Page) scale = Math.Min(scale, availableHeight / Math.Max(left.Height, right.Height));
            scale *= zoom;
            var totalWidth = (left.Width + right.Width) * scale + Gap;
            var x = Math.Max(Margin, (viewport.Width - totalWidth) / 2);
            var y = Math.Max(Margin, (viewport.Height - Math.Max(left.Height, right.Height) * scale) / 2);
            Pages[FirstPage - 1] = new(Snap(x), Snap(y), Snap(left.Width * scale), Snap(left.Height * scale));
            if (LastPage > FirstPage)
                Pages[LastPage - 1] = new(Snap(x + left.Width * scale + Gap), Snap(y), Snap(right.Width * scale), Snap(right.Height * scale));
            extentWidth = Math.Max(extentWidth, x + totalWidth + Margin);
            extentHeight = Math.Max(extentHeight, y + Math.Max(left.Height, right.Height) * scale + Margin);
        }
        else
        {
            var y = Margin;
            for (var page = FirstPage; page <= LastPage; page++)
            {
                var size = PageSize(page - 1);
                var scale = availableWidth / size.Width;
                if (fit == PdfReaderFitMode.Page) scale = Math.Min(scale, availableHeight / size.Height);
                var width = Math.Max(1 / dpi, Snap(size.Width * scale * zoom));
                var height = Math.Max(1 / dpi, Snap(size.Height * scale * zoom));
                var x = Math.Max(Margin, (viewport.Width - width) / 2);
                if (mode == PdfReaderDisplayMode.SinglePage) y = Math.Max(Margin, (viewport.Height - height) / 2);
                Pages[page - 1] = new(Snap(x), Snap(y), width, height);
                extentWidth = Math.Max(extentWidth, x + width + Margin);
                extentHeight = Math.Max(extentHeight, y + height + Margin);
                y += height + Gap;
            }
        }
        Extent = new(Snap(extentWidth), Snap(extentHeight));
    }

    public int PageAt(double y)
    {
        if (Mode != PdfReaderDisplayMode.Continuous || Pages.Length == 0) return FirstPage;
        var low = 0;
        var high = Pages.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Pages[middle].Bottom < y) low = middle + 1; else high = middle;
        }
        return low + 1;
    }

    public IEnumerable<int> VisiblePages(Rect viewport)
    {
        var first = Mode == PdfReaderDisplayMode.Continuous ? PageAt(viewport.Top) : FirstPage;
        for (var page = first; page <= LastPage; page++)
        {
            var bounds = Pages[page - 1];
            if (Mode == PdfReaderDisplayMode.Continuous && bounds.Top > viewport.Bottom) yield break;
            if (bounds.Intersects(viewport)) yield return page;
        }
    }
}
