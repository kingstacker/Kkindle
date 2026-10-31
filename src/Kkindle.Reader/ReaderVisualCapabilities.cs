using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Input;
using Kkindle.Core;

namespace Kkindle;

/// <summary>Optional Avalonia thumbnail surface for a PDF reader.</summary>
public interface IPdfThumbnailHost : IPdfNavigationHost
{
    Task<Bitmap?> RenderThumbnailAsync(int page, CancellationToken token, int maxWidth = 144, int maxHeight = 180);
}

/// <summary>Optional zoom capability with an Avalonia viewport anchor.</summary>
public interface IPdfZoomHost : IPdfReadingHost
{
    Task SetZoomAsync(double zoom, Point? anchor = null);
}

/// <summary>Optional keyboard handling supplied by an Avalonia reader view.</summary>
public interface IReaderKeyboardHost : IReaderHost
{
    void HandleReaderKey(KeyEventArgs e);
}

/// <summary>Reader supplies footnote content and hover events directly.</summary>
public interface IReaderFootnoteVisualHost : IReaderHost { }

/// <summary>The Avalonia view can be captured by the visual transition player.</summary>
public interface IReaderVisualTransitionHost : IReaderHost { }

/// <summary>Annotation bounds in the host's Avalonia coordinate system.</summary>
public interface IReaderAnnotationBoundsHost : IReaderHost
{
    IReadOnlyList<Rect> GetAnnotationBounds(ReaderAnnotation annotation);
}

/// <summary>Optional PDF callout placement and annotation geometry.</summary>
public interface IPdfAnnotationVisualHost : IPdfNavigationHost, IReaderAnnotationBoundsHost
{
    Point? GetPdfPointPosition(ReaderAnnotation annotation);
    Point? GetTextAnnotationAnchor(int pageNumber, int start, int end, string? fragment = null);
    IReadOnlyList<Rect> GetRangeBounds(int start, int end);
    bool TryGetAnnotationAnchor(ReaderAnnotation annotation, out int pageNumber, out Point anchor);
    bool TryGetAnnotationCalloutBounds(int pageNumber, Point anchor, Size preferredSize, out Rect bounds,
        Func<double, double>? measureHeight = null);
    void SetAnnotationCallout(Point anchor, Rect bounds, Action<Rect>? moved = null, int pageNumber = 0,
        Size? preferredSize = null, Func<double, double>? measureHeight = null);
    void ClearAnnotationCallout();
}
