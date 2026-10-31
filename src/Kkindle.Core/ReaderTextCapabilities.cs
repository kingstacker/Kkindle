namespace Kkindle.Core;

/// <summary>Clears the active text selection on any reader surface.</summary>
public interface IReaderSelectionHost : IReaderHost
{
    void ClearSelection();
}

/// <summary>Reading position and viewport state independent of the host control.</summary>
public interface IReaderPositionHost : IReaderHost
{
    bool IsPaginated { get; }
    ReaderContentPosition? CaptureContentPosition();
    bool IsContentPositionVisible(ReaderContentPosition? position);
    (double Position, double Ratio, double ScrollWidth, double ScrollHeight,
        double ClientWidth, double ClientHeight) GetScrollState();
    string? GetCurrentNavigationFragment();
    string? GetCurrentPageQuote(int maxLength = 72);
}

/// <summary>Text stream and search capabilities provided by a reader surface.</summary>
public interface IReaderTextSearchHost : IReaderHost
{
    string? BodyText { get; }
    IReadOnlyList<(int Start, int Length)> RubyAnnotationRanges { get; }
    IReadOnlyList<ReaderSearchMatch> FindSearchMatches(string query);
    void SetSearchHighlights(List<(int Start, int Length)>? hits, int? focusIndex);
    void ScrollToSearchHit(int index);
    void ScrollToOffset(int offset);
}

/// <summary>Restores and follows semantic positions in an EPUB chapter.</summary>
public interface IReaderLocationHost : IReaderTextSearchHost
{
    bool Vertical { get; }
    bool RestoreContentPosition(ReaderContentPosition? position);
    bool TryGetAnnotationRange(ReaderAnnotation annotation, out int start, out int end);
    void SeekToPixelScroll(double pixelScroll);
    void SeekToBoundary(bool toEnd);
    void ScrollToFragment(string fragmentId);
    Task RevealFootnoteAsync(string fragmentId);
}

/// <summary>Prepares chapter navigation with per-book layout preferences.</summary>
public interface IReaderLayoutNavigationHost : IReaderHost
{
    void SetNavigationFragments(IEnumerable<string> fragments);
    void Navigate(Uri uri, ReaderLayoutSettings settings, bool showVerticalDebugBoxes);
}

/// <summary>Host-owned layout that preserves semantic positions during reflow.</summary>
public interface IReaderConfiguredHost : IReaderPositionHost, IReaderTextSearchHost
{
    Task Configure(ReaderLayoutSettings settings, double scrollPosition, string? fragment,
        bool restoreFromProgress, bool showVerticalDebugBoxes,
        ReaderContentPosition? contentPosition = null, double? legacyChapterRatio = null);
    void PreservePositionForViewportChange();
}

/// <summary>Page turns and continuous scrolling without a concrete control.</summary>
public interface IReaderPaginationHost : IReaderHost
{
    int PageCount { get; }
    int CurrentPage { get; }
    bool CanTurn(int direction);
    bool TurnPage(int direction);
    void ScrollByPixel(double delta);
}

public interface IReaderAnnotationHost : IReaderHost
{
    void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations);
}

/// <summary>Optional renderer-specific estimates for a whole book.</summary>
public interface IReaderPageCountHost : IReaderHost
{
    (string ChapterPath, int PageCount)? GetCurrentChapterPageCountFor(
        ReaderLayoutSettings settings, double width, double height);
    int[] EstimateChapterPageCounts(IReadOnlyList<string> chapterPaths,
        ReaderLayoutSettings settings, double width, double height,
        CancellationToken cancellationToken, (string ChapterPath, int PageCount)? knownChapter);
}
