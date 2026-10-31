namespace Kkindle.Infrastructure;

/// <summary>PDF document operations shared by metadata, indexing and rendering.</summary>
public interface IPdfDocument : IDisposable
{
    int PageCount { get; }
    PdfDocumentInfo ReadInfo(CancellationToken cancellationToken = default);
    PdfPageContent ReadPage(int pageNumber, CancellationToken cancellationToken = default);
    string ReadPageText(int pageNumber, CancellationToken cancellationToken = default);
    PdfRenderedPage RenderPage(int pageNumber, int maxWidth = 1600, int maxHeight = 2000,
        CancellationToken cancellationToken = default);
    PdfRasterRegion RenderRegion(int pageNumber, int pagePixelWidth, int pagePixelHeight,
        int left, int top, int width, int height, CancellationToken cancellationToken = default, int rotation = 0);
}

public interface IPdfDocumentFactory
{
    IPdfDocument Open(string path);
}

/// <summary>Desktop PDFium adapter. Other platforms can supply their own factory.</summary>
public sealed class PdfiumDocumentFactory : IPdfDocumentFactory
{
    public IPdfDocument Open(string path) => new PdfDocumentService(path);
}
