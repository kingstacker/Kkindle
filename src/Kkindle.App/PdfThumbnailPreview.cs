using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kkindle;

public sealed record PdfThumbnailItem(NativePdfReaderHost Host, int PageNumber, string Label);

/// <summary>Only realized ListBox rows own a raster; recycling releases it.</summary>
public sealed class PdfThumbnailPreview : Image
{
    private CancellationTokenSource? _loading;
    private Bitmap? _bitmap;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public PdfThumbnailPreview()
    {
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            LoadPreview();
        };
        SizeChanged += (_, _) =>
        {
            if (!this.IsAttachedToVisualTree()) return;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 144;
        // Use a stable portrait slot while the PDF raster loads. Stretch.Uniform
        // preserves the original page's aspect ratio within that slot.
        return new Size(width, width * 1.25);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadPreview();
    }
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _resizeTimer.Stop();
        ClearPreview();
        if (this.IsAttachedToVisualTree()) LoadPreview();
    }
    private async void LoadPreview()
    {
        if (DataContext is not PdfThumbnailItem item || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _loading?.Cancel();
        _loading?.Dispose();
        var cancellation = _loading = new CancellationTokenSource();
        try
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var pixelWidth = (int)Math.Clamp(Math.Ceiling(Bounds.Width * scale), 1, 2048);
            var pixelHeight = (int)Math.Clamp(Math.Ceiling(Bounds.Height * scale), 1, 2560);
            var bitmap = await item.Host.RenderThumbnailAsync(item.PageNumber, cancellation.Token, pixelWidth, pixelHeight);
            if (cancellation.IsCancellationRequested || _loading != cancellation) { bitmap?.Dispose(); return; }
            var previous = _bitmap;
            Source = _bitmap = bitmap;
            previous?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { ToolTip.SetTip(this, "此页暂时无法预览，仍可点击跳转"); }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _resizeTimer.Stop();
        ClearPreview();
        base.OnDetachedFromVisualTree(e);
    }
    private void ClearPreview()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
