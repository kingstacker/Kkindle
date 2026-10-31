using Kkindle.Infrastructure;

namespace Kkindle.TestFixtures;

public sealed class FakePdfDocumentFactory : IPdfDocumentFactory
{
    public int OpenCount { get; private set; }
    public int DisposeCount { get; private set; }
    public IPdfDocument Open(string path)
    {
        OpenCount++;
        return new FakePdfDocument(() => DisposeCount++);
    }

    private sealed class FakePdfDocument(Action onDispose) : IPdfDocument
    {
        public int PageCount => 2;
        public PdfDocumentInfo ReadInfo(CancellationToken cancellationToken = default) => new(2, "Injected title", "Injected author", []);
        public PdfPageContent ReadPage(int pageNumber, CancellationToken cancellationToken = default) => new(100, 100, ReadPageText(pageNumber, cancellationToken), []);
        public string ReadPageText(int pageNumber, CancellationToken cancellationToken = default) => $"Page {pageNumber}";
        public PdfRenderedPage RenderPage(int pageNumber, int maxWidth = 1600, int maxHeight = 2000, CancellationToken cancellationToken = default) =>
            new(pageNumber, ReadPage(pageNumber, cancellationToken), [1, 2, 3]);
        public PdfRasterRegion RenderRegion(int pageNumber, int pagePixelWidth, int pagePixelHeight, int left, int top,
            int width, int height, CancellationToken cancellationToken = default, int rotation = 0) =>
            new(pageNumber, pagePixelWidth, pagePixelHeight, left, top, width, height, new byte[width * height * 4], rotation);
        public void Dispose() => onDispose();
    }
}
