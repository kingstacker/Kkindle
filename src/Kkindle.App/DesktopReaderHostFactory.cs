using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed class DesktopReaderHostFactory : IReaderHostFactory
{
    private readonly IPdfDocumentFactory _pdfDocuments;

    public DesktopReaderHostFactory(IPdfDocumentFactory? pdfDocuments = null) =>
        _pdfDocuments = pdfDocuments ?? new PdfiumDocumentFactory();

    public IReaderHost Create(ReaderHostFormat format) => format switch
    {
        ReaderHostFormat.Epub => new NativeReaderHost(),
        ReaderHostFormat.Pdf => new NativePdfReaderHost(_pdfDocuments),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public bool IsCompatible(IReaderHost host, ReaderHostFormat format) => format switch
    {
        ReaderHostFormat.Epub => host is NativeReaderHost,
        ReaderHostFormat.Pdf => host is NativePdfReaderHost,
        _ => false
    };
}
