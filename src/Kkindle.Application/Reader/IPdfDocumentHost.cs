using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

/// <summary>PDF document and tool capabilities independent of a native control.</summary>
public interface IPdfDocumentHost : IReaderHost
{
    PdfDocumentInfo? CachedDocumentInfo { get; }
    Task<PdfDocumentInfo> ReadDocumentInfoAsync(CancellationToken token);
    Task<IReadOnlyList<PdfPageText>> BuildTextIndexAsync(CancellationToken token);
    bool IsPointAnnotationMode { get; }
    bool IsHandToolMode { get; }
    void SetPointAnnotationMode(bool enabled);
    void SetHandToolMode(bool enabled);
}

/// <summary>Page labels, history and document-coordinate navigation.</summary>
public interface IPdfNavigationHost : IReaderHost
{
    int PageNumber { get; }
    int PageCount { get; }
    int GetAdjacentPage(int direction);
    Task<bool> NavigateHistoryAsync(int direction, CancellationToken token);
    string GetPageLabel(int page);
    string GetPageDescription(int page);
    int FindLabeledPage(string label);
    void SetPageNumberOffset(int? offset);
    void ScrollToTop(double top, bool preserveVerticalPosition = false);
    void ScrollToPdfPoint(double x, double y);
    Task RefreshViewportAsync(CancellationToken token);
}

/// <summary>Opening, display and persistence capabilities for a PDF reader.</summary>
public interface IPdfReadingHost : IPdfDocumentHost, IPdfNavigationHost
{
    PdfReaderDisplayMode DisplayMode { get; }
    PdfReaderFitMode FitMode { get; }
    double Zoom { get; }
    string? LastError { get; }
    double NavigationTop { get; }
    double NavigationBottom { get; }
    IReadOnlyList<int> VisiblePageNumbers { get; }
    PdfPageContent? GetPageContent(int page);
    Task<int> PrepareDocumentAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> NavigateAsync(Uri uri, CancellationToken cancellationToken = default,
        bool recordHistory = true, bool preserveVerticalPosition = false);
    Task SetDisplayModeAsync(PdfReaderDisplayMode mode);
    Task SetFitModeAsync(PdfReaderFitMode mode);
    Task RotateClockwiseAsync();
    string CaptureViewState();
    void RestoreViewSettings(string? state);
    Task RestoreViewStateAsync(string? state, int legacyScrollPosition = 0);
}

/// <summary>Applies saved annotations without exposing UI geometry types.</summary>
public interface IPdfAnnotationsHost : IReaderAnnotationHost
{
    void SelectAnnotation(Guid id);
    void ClearAnnotationSelection();
}

/// <summary>Text selection, search and spoken-text highlighting.</summary>
public interface IPdfTextHost : IReaderSelectionHost
{
    string? SelectionFragment { get; }
    string GetSelectedTextForCopy();
    string GetSelectedTextForTranslation();
    void ScrollToOffset(int offset);
    void ClearSearch();
    void SetSpeechHighlight(int start, int length);
    void ClearSpeechHighlight();
}

/// <summary>In-page PDF search independent of the rendered control.</summary>
public interface IPdfSearchHost : IPdfTextHost
{
    (int Count, int Index) Find(string query, int? offset = null);
    void ScrollToSearchHit(int index);
}
