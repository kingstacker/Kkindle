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

        return new ChapterLayout
        {
            Pages = pages.Pages,
            BodyTextLength = content.BodyText.Length,
            FragmentPages = pages.Fragments,
            Options = options,
        };
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
        var best = -1;
        for (var i = 0; i < layout.Pages.Count; i++)
        {
            var page = layout.Pages[i];
            if (page.TextStartOffset < 0)
            {
                continue;
            }

            if (page.TextStartOffset <= offset)
            {
                best = i;
            }
            else
            {
                break;
            }
        }

        if (best >= 0 && layout.Pages[best].TextEndOffset > 0 && offset >= layout.Pages[best].TextEndOffset)
        {
            // Offset sits in a gap (whitespace between pages); clamp forward.
            for (var i = best + 1; i < layout.Pages.Count; i++)
            {
                if (layout.Pages[i].TextStartOffset >= 0)
                {
                    return i;
                }
            }
        }

        return best;
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
            if (glyphIndex < 0)
            {
                continue;
            }

            var size = run.FontSize * run.Scale;
            var nextIndex = glyphIndex + 1;
            var x0 = run.OriginX + run.X[glyphIndex];
            var y0 = run.OriginY + run.Y[glyphIndex];

            if (run.Sideways)
            {
                var advance = nextIndex < run.X.Length
                    ? run.X[nextIndex] - run.X[glyphIndex]
                    : Math.Max(size * 0.5f, run.FlowAdvance - run.X[glyphIndex]);
                return new SKRect(
                    run.OriginX - size * 0.25f,
                    y0,
                    run.OriginX + size * 0.9f,
                    y0 + Math.Max(2f, advance));
            }

            if (layout.Pages[pageIndex].WritingMode == TypesetWritingMode.VerticalRl)
            {
                // Upright cells: one cell box per glyph.
                return new SKRect(
                    x0,
                    y0 - size * 0.88f,
                    x0 + size,
                    y0 + size * 0.12f);
            }

            var width = nextIndex < run.X.Length
                ? run.X[nextIndex] - run.X[glyphIndex]
                : Math.Max(2f, size * 0.6f);
            return new SKRect(x0, y0 - size * 0.9f, x0 + Math.Max(2f, width), y0 + size * 0.24f);
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

        foreach (var run in layout.Pages[pageIndex].Runs)
        {
            if (run.Glyphs.Length == 0)
            {
                continue;
            }

            for (var i = 0; i < run.X.Length; i++)
            {
                var size = run.FontSize * run.Scale;
                float cx;
                float cy;
                if (run.Sideways)
                {
                    var nextX = i + 1 < run.X.Length ? run.X[i + 1] : run.X[i] + size * 0.55f;
                    cx = run.OriginX;
                    cy = run.OriginY + (run.X[i] + nextX) / 2f;
                }
                else
                {
                    var nextX = i + 1 < run.X.Length ? run.X[i + 1] : run.X[i] + size * 0.55f;
                    cx = run.OriginX + (run.X[i] + nextX) / 2f;
                    cy = run.OriginY;
                }

                var distance = Distance(point, new SKPoint(cx, cy));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    var local = i < run.Clusters.Length ? run.Clusters[i] : 0;
                    best = run.TextStart >= 0 ? run.TextStart + local : -1;
                    bestAfterMidpoint = distance > size * 0.45f;
                }
            }
        }

        if (best >= 0 && bestAfterMidpoint)
        {
            best++;
        }

        return Math.Clamp(best, 0, Math.Max(0, layout.BodyTextLength));
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

    private static float Distance(SKPoint a, SKPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
