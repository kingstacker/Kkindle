using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Kkindle.Layout;

/// <summary>
/// The typesetting engine entry point. Pure C#, no UI dependencies: it turns
/// a loaded chapter into immutable pages that both paint and hit-testing
/// consume. The same content, options, fonts and library versions always
/// produce the same pages on every platform.
/// </summary>
public sealed class TypesetEngine : IDisposable
{
    private readonly TypesetFontLibrary _fonts;
    private readonly GlyphShaper _shaper;
    private readonly bool _ownsFonts;

    /// <param name="ownsFonts">
    /// False when the caller keeps one font library alive across engines
    /// (reader recompose/resize). Loading CJK faces is expensive, so the host
    /// shares them instead of rebuilding the library on every relayout.
    /// </param>
    public TypesetEngine(TypesetFontLibrary fonts, bool ownsFonts = true)
    {
        _fonts = fonts;
        _shaper = new GlyphShaper(fonts);
        _ownsFonts = ownsFonts;
    }

    /// <summary>The font library backing this engine (shared with the painter).</summary>
    public TypesetFontLibrary Fonts => _fonts;

    /// <summary>The engine takes ownership of the font library it was built with.</summary>
    public void Dispose()
    {
        if (_ownsFonts) _fonts.Dispose();
    }

    public ChapterLayout Compose(
        ChapterContent content,
        TypesetLayoutOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new ComposerContext
        {
            Options = options,
            Fonts = _fonts,
            Shaper = _shaper,
            CancellationToken = cancellationToken,
        };
        var cells = new CellFactory(context);
        var pages = new PageBuilder(options);

        if (options.WritingMode == TypesetWritingMode.VerticalRl)
        {
            new VerticalComposer(context, cells, pages).Compose(content);
        }
        else
        {
            new HorizontalComposer(context, cells, pages).Compose(content);
        }

        pages.Finish(endOfChapter: true);

        var layout = new ChapterLayout
        {
            Pages = pages.Pages,
            BodyTextLength = content.BodyText.Length,
            FragmentPages = pages.Fragments,
            Options = options,
        };

        // Block-level fragment ids are recorded when a block first enters a
        // page, which is sufficient for structural/image anchors. Inline ids
        // can occur after a long paragraph has already crossed a page break;
        // their source offsets are authoritative and must replace that coarse
        // first-page mapping.
        foreach (var (fragmentId, offset) in content.FragmentTextOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fragmentPage = layout.GetPageIndexOfOffset(offset);
            if (fragmentPage >= 0)
            {
                pages.Fragments[fragmentId] = fragmentPage;
            }
        }

        return layout;
    }
}

public enum OverlayKind
{
    Selection,
    Highlight,
    SearchMark,
    AnnotationUnderline,
}

public readonly record struct PageOverlay(int Start, int Length, OverlayKind Kind);

/// <summary>
/// Interaction helpers over a finished layout: offset-to-page resolution,
/// character rectangles, hit testing and overlay rect unions. These back
/// selection, annotations, search marks and progress restore in the host.
/// </summary>
public static class ChapterLayoutInteraction
{
    public static int GetPageIndexOfOffset(this ChapterLayout layout, int offset)
    {
        var firstTextPage = -1;
        var lastTextPage = -1;
        for (var i = 0; i < layout.Pages.Count; i++)
        {
            var page = layout.Pages[i];
            if (page.TextStartOffset < 0)
            {
                continue;
            }

            firstTextPage = firstTextPage < 0 ? i : firstTextPage;
            lastTextPage = i;
            if (offset < page.TextStartOffset)
            {
                return i == firstTextPage ? firstTextPage : i;
            }

            if (page.TextEndOffset < 0 || offset < page.TextEndOffset)
            {
                return i;
            }
        }

        if (firstTextPage < 0)
        {
            return -1;
        }

        // Offsets after the final visible run belong to the final page. This
        // is also where trailing whitespace safely restores.
        return lastTextPage;
    }

    public static int GetPageIndexOfFragment(this ChapterLayout layout, string fragmentId) =>
        layout.FragmentPages.TryGetValue(fragmentId, out var page) ? page : -1;

    /// <summary>
    /// Approximate rectangle of one character. Good enough for selection
    /// visuals; the engine never re-measures text for this.
    /// </summary>
    public static SKRect? GetCharRect(this ChapterLayout layout, int pageIndex, int offset)
    {
        if (pageIndex < 0 || pageIndex >= layout.Pages.Count)
        {
            return null;
        }

        var page = layout.Pages[pageIndex];
        var run = FindRunAt(page.Runs, offset);
        if (run is null)
        {
            return null;
        }

        var local = offset - run.TextStart;
        var glyphIndex = FindGlyphForCluster(run, local);
        if (glyphIndex < 0
            || glyphIndex >= run.Glyphs.Length
            || glyphIndex >= run.X.Length)
        {
            return null;
        }

        return GetGlyphRect(page, run, glyphIndex);
    }

    /// <summary>
    /// Runs are emitted in text order. A binary search turns overlay geometry
    /// (one lookup per selected character) from O(chars × runs) into
    /// O(chars × log runs); the linear fallback keeps malformed pages safe.
    /// </summary>
    private static PlacedRun? FindRunAt(IReadOnlyList<PlacedRun> runs, int offset)
    {
        var low = 0;
        var high = runs.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var run = runs[middle];
            if (run.TextStart < 0 || offset < run.TextStart)
            {
                high = middle - 1;
            }
            else if (offset >= run.TextStart + run.TextLength)
            {
                low = middle + 1;
            }
            else
            {
                found = middle;
                high = middle - 1;
            }
        }

        if (found >= 0)
        {
            // Ruby and base runs can share a TextStart; the linear version
            // returned the first match, so walk back to the earliest one.
            while (found > 0)
            {
                var previous = runs[found - 1];
                if (previous.TextStart != runs[found].TextStart
                    || previous.TextLength <= 0
                    || offset < previous.TextStart
                    || offset >= previous.TextStart + previous.TextLength)
                    break;
                found--;
            }
            return runs[found];
        }

        foreach (var run in runs)
        {
            if (run.TextLength > 0 && offset >= run.TextStart
                && offset < run.TextStart + run.TextLength)
                return run;
        }
        return null;
    }

    /// <summary>Returns the interactive zone under a page point, if any.</summary>
    public static PlacedHotZone? GetHotZoneAt(this ChapterLayout layout, int pageIndex, SKPoint point)
    {
        if (pageIndex < 0 || pageIndex >= layout.Pages.Count)
        {
            return null;
        }

        var zones = layout.Pages[pageIndex].HotZones;
        for (var i = zones.Count - 1; i >= 0; i--)
        {
            if (zones[i].Rect.Contains(point.X, point.Y))
            {
                return zones[i];
            }
        }

        return null;
    }

    /// <summary>Finds the nearest character boundary to a page point.</summary>
    public static int HitTest(this ChapterLayout layout, int pageIndex, SKPoint point)
    {
        if (pageIndex < 0 || pageIndex >= layout.Pages.Count)
        {
            return -1;
        }

        var best = -1;
        var bestDistance = float.MaxValue;
        var bestAfterMidpoint = false;
        var page = layout.Pages[pageIndex];
        var index = HitIndexes.GetValue(page, static candidate => new PageHitIndex(candidate));

        foreach (var (run, glyphIndex, rect) in index.Near(point, 128f))
        {
            var size = run.FontSize * run.Scale;
            var distance = DistanceToRect(point, rect);
            var tolerance = Math.Max(6f, size * 1.25f);
            if (distance > tolerance)
            {
                continue;
            }

            var cx = (rect.Left + rect.Right) / 2f;
            var cy = (rect.Top + rect.Bottom) / 2f;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                var local = glyphIndex < run.Clusters.Length ? run.Clusters[glyphIndex] : 0;
                best = run.TextStart + local;
                bestAfterMidpoint = page.WritingMode == TypesetWritingMode.VerticalRl
                    ? point.Y > cy
                    : point.X > cx;
            }
        }

        if (best >= 0 && bestAfterMidpoint)
        {
            best++;
        }

        return best < 0
            ? -1
            : Math.Clamp(best, 0, Math.Max(0, layout.BodyTextLength));
    }

    private static readonly ConditionalWeakTable<LayoutPage, PageHitIndex> HitIndexes = new();

    /// <summary>
    /// Uniform grid over one page's glyph rectangles. Selection dragging used
    /// to test every glyph on every pointer move; now only the cells around
    /// the pointer are visited.
    /// </summary>
    private sealed class PageHitIndex
    {
        private const float CellSize = 32f;
        private readonly Dictionary<(int X, int Y), List<(PlacedRun Run, int Glyph, SKRect Rect)>> _cells = [];

        public PageHitIndex(LayoutPage page)
        {
            foreach (var run in page.Runs)
            {
                var glyphCount = Math.Min(run.Glyphs.Length, run.X.Length);
                if (glyphCount == 0 || run.TextStart < 0) continue;
                for (var glyphIndex = 0; glyphIndex < glyphCount; glyphIndex++)
                {
                    var rect = GetGlyphRect(page, run, glyphIndex);
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    var minX = (int)MathF.Floor(rect.Left / CellSize);
                    var maxX = (int)MathF.Floor(rect.Right / CellSize);
                    var minY = (int)MathF.Floor(rect.Top / CellSize);
                    var maxY = (int)MathF.Floor(rect.Bottom / CellSize);
                    for (var x = minX; x <= maxX; x++)
                    for (var y = minY; y <= maxY; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out var entries))
                        {
                            entries = [];
                            _cells[(x, y)] = entries;
                        }
                        entries.Add((run, glyphIndex, rect));
                    }
                }
            }
        }

        public IEnumerable<(PlacedRun Run, int Glyph, SKRect Rect)> Near(SKPoint point, float tolerance)
        {
            var minX = (int)MathF.Floor((point.X - tolerance) / CellSize);
            var maxX = (int)MathF.Floor((point.X + tolerance) / CellSize);
            var minY = (int)MathF.Floor((point.Y - tolerance) / CellSize);
            var maxY = (int)MathF.Floor((point.Y + tolerance) / CellSize);
            for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
            {
                if (!_cells.TryGetValue((x, y), out var entries)) continue;
                foreach (var entry in entries) yield return entry;
            }
        }
    }

    /// <summary>
    /// Unions the character rectangles of a range into line/column bands for
    /// selection, annotation and search-mark painting.
    /// </summary>
    public static IReadOnlyList<SKRect> GetOverlayRects(
        this ChapterLayout layout,
        int pageIndex,
        int start,
        int length)
    {
        var rects = new List<SKRect>();
        if (pageIndex < 0 || pageIndex >= layout.Pages.Count || length <= 0)
        {
            return rects;
        }

        var page = layout.Pages[pageIndex];
        var pageStart = page.TextStartOffset < 0 ? start : page.TextStartOffset;
        var pageEnd = page.TextEndOffset < 0 ? start + length : page.TextEndOffset;
        var from = Math.Max(start, pageStart);
        var to = Math.Min(start + length, pageEnd);
        if (to <= from)
        {
            return rects;
        }

        var bandKey = float.NaN;
        SKRect band = default;
        for (var offset = from; offset < to; offset++)
        {
            if (layout.GetCharRect(pageIndex, offset) is not { } rect)
            {
                continue;
            }

            var bandIdentifier = page.WritingMode == TypesetWritingMode.VerticalRl
                ? MathF.Round(rect.Left)
                : MathF.Round(rect.Top);

            if (!float.IsNaN(bandKey) && SameBand(bandIdentifier, bandKey))
            {
                band = Union(band, rect);
            }
            else
            {
                if (!float.IsNaN(bandKey))
                {
                    rects.Add(band);
                }

                bandKey = bandIdentifier;
                band = rect;
            }
        }

        if (!float.IsNaN(bandKey))
        {
            rects.Add(band);
        }

        return rects;
    }

    private static bool SameBand(float a, float b) => MathF.Abs(a - b) < 2.5f;

    private static SKRect Union(SKRect a, SKRect b) =>
        new(
            MathF.Min(a.Left, b.Left),
            MathF.Min(a.Top, b.Top),
            MathF.Max(a.Right, b.Right),
            MathF.Max(a.Bottom, b.Bottom));

    private static int FindGlyphForCluster(PlacedRun run, int local)
    {
        if (run.Clusters.Length == 0)
        {
            return -1;
        }

        var best = -1;
        for (var i = 0; i < run.Clusters.Length; i++)
        {
            if (run.Clusters[i] <= local)
            {
                best = i;
            }
            else
            {
                break;
            }
        }

        return best;
    }

    internal static SKRect GetGlyphRect(LayoutPage page, PlacedRun run, int glyphIndex)
    {
        var size = run.FontSize * run.Scale;
        var nextIndex = glyphIndex + 1;
        if (run.Sideways)
        {
            var advance = nextIndex < run.X.Length
                ? run.X[nextIndex] - run.X[glyphIndex]
                : Math.Max(size * 0.5f, run.FlowAdvance - run.X[glyphIndex]);
            var top = run.OriginY + run.X[glyphIndex];
            return new SKRect(
                run.OriginX - size * 0.25f,
                top,
                run.OriginX + size * 0.9f,
                top + Math.Max(2f, advance));
        }

        var x0 = run.OriginX + run.X[glyphIndex];
        var y0 = run.OriginY + (glyphIndex < run.Y.Length ? run.Y[glyphIndex] : 0f);
        if (page.WritingMode == TypesetWritingMode.VerticalRl)
        {
            var left = run.CellWidth > 0f ? run.OriginX : x0;
            var right = run.CellWidth > 0f ? left + run.CellWidth : x0 + size;
            return new SKRect(
                left,
                y0 - size * 0.88f,
                right,
                y0 + size * 0.12f);
        }

        var width = nextIndex < run.X.Length
            ? run.X[nextIndex] - run.X[glyphIndex]
            : Math.Max(2f, run.FlowAdvance - run.X[glyphIndex]);
        return new SKRect(x0, y0 - size * 0.9f, x0 + Math.Max(2f, width), y0 + size * 0.24f);
    }

    private static float DistanceToRect(SKPoint point, SKRect rect)
    {
        var dx = point.X < rect.Left ? rect.Left - point.X : point.X > rect.Right ? point.X - rect.Right : 0f;
        var dy = point.Y < rect.Top ? rect.Top - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : 0f;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
