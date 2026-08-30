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

    public TypesetEngine(TypesetFontLibrary fonts)
    {
        _fonts = fonts;
        _shaper = new GlyphShaper(fonts);
    }

    /// <summary>The font library backing this engine (shared with the painter).</summary>
    public TypesetFontLibrary Fonts => _fonts;

    /// <summary>The engine takes ownership of the font library it was built with.</summary>
    public void Dispose() => _fonts.Dispose();

    public ChapterLayout Compose(ChapterContent content, TypesetLayoutOptions options)
    {
        var context = new ComposerContext
        {
            Options = options,
            Fonts = _fonts,
            Shaper = _shaper,
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

        foreach (var run in layout.Pages[pageIndex].Runs)
        {
            if (run.TextLength <= 0 || offset < run.TextStart || offset >= run.TextStart + run.TextLength)
            {
                continue;
            }

            var local = offset - run.TextStart;
            var glyphIndex = FindGlyphForCluster(run, local);
            if (glyphIndex < 0
                || glyphIndex >= run.Glyphs.Length
                || glyphIndex >= run.X.Length)
            {
                continue;
            }

            return GetGlyphRect(layout.Pages[pageIndex], run, glyphIndex);
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

        foreach (var run in layout.Pages[pageIndex].Runs)
        {
            var glyphCount = Math.Min(run.Glyphs.Length, run.X.Length);
            if (glyphCount == 0 || run.TextStart < 0)
            {
                continue;
            }

            for (var i = 0; i < glyphCount; i++)
            {
                var size = run.FontSize * run.Scale;
                var rect = GetGlyphRect(page, run, i);
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
                    var local = i < run.Clusters.Length ? run.Clusters[i] : 0;
                    best = run.TextStart + local;
                    bestAfterMidpoint = page.WritingMode == TypesetWritingMode.VerticalRl
                        ? point.Y > cy
                        : point.X > cx;
                }
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
