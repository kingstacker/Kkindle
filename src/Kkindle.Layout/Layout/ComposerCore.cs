using SkiaSharp;

namespace Kkindle.Layout;

/// <summary>
/// One unbreakable laid-out unit: a shaped CJK character, a Latin word, a
/// space, or an atomic vertical run. Produced once per block and consumed by
/// both the horizontal and the vertical composer.
/// </summary>
internal sealed record LayoutCell
{
    public required ushort[] Glyphs { get; init; }
    /// <summary>Per-glyph pen positions relative to the cell origin.</summary>
    public required float[] GlyphX { get; init; }
    public required float[] GlyphY { get; init; }
    public required float Advance { get; init; }
    public float Ascent { get; init; }
    public float Descent { get; init; }
    public required string FontPath { get; init; }
    public required float FontSize { get; init; }
    /// <summary>Global offset into ChapterContent.BodyText, or -1 for non-text cells.</summary>
    public required int TextStart { get; init; }
    public required int TextLength { get; init; }
    public int[] Clusters { get; init; } = Array.Empty<int>();
    public required TypesetInlineStyle Style { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    public bool IsSpace { get; init; }
    public bool IsLineBreak { get; init; }
    /// <summary>Vertical mode: shaped horizontally, painted rotated 90° clockwise.</summary>
    public bool Sideways { get; init; }
    /// <summary>Vertical mode: combined digits centered inside one upright cell.</summary>
    public bool Combined { get; init; }
    /// <summary>The cell's single character when it is exactly one char, else null.</summary>
    public char? Character { get; init; }
    /// <summary>Superscript cells (footnote markers, sup) shift toward the line start edge.</summary>
    public bool Superscript => Style.Superscript;
}

/// <summary>Shared context for the two composers.</summary>
internal sealed class ComposerContext
{
    public required TypesetLayoutOptions Options { get; init; }
    public required TypesetFontLibrary Fonts { get; init; }
    public required GlyphShaper Shaper { get; init; }

    public string MainFont => Fonts.MainFontPath;

    public float LetterSpacing => Options.LetterSpacingEm * Options.BaseFontSize;
}

/// <summary>
/// Accumulates placed content and closes pages. Both composers push their
/// absolute-geometry output here so text-offset bookkeeping lives in one place.
/// </summary>
internal sealed class PageBuilder
{
    private readonly TypesetLayoutOptions _options;
    private readonly List<LayoutPage> _pages = new();
    private LayoutPage _current;
    private bool _used;

    public PageBuilder(TypesetLayoutOptions options)
    {
        _options = options;
        _current = MakePage();
    }

    public IReadOnlyList<LayoutPage> Pages => _pages;

    public LayoutPage Current => _current;

    public int CurrentPageIndex => _current.Index;

    public Dictionary<string, int> Fragments { get; } = new(StringComparer.Ordinal);

    public void RecordFragment(string? elementId)
    {
        if (!string.IsNullOrEmpty(elementId) && !Fragments.ContainsKey(elementId))
        {
            Fragments[elementId] = _current.Index;
        }
    }

    public void Next()
    {
        _pages.Add(_current);
        _current = MakePage();
        _used = false;
    }

    public void Finish(bool endOfChapter)
    {
        if (_used || _pages.Count == 0)
        {
            _pages.Add(_current);
            _used = false;
        }

        if (_pages.Count > 0)
        {
            var last = _pages[^1];
            _pages[^1] = new LayoutPage
            {
                Index = last.Index,
                WritingMode = last.WritingMode,
                Width = last.Width,
                Height = last.Height,
                InsetHorizontal = last.InsetHorizontal,
                InsetVertical = last.InsetVertical,
                TextStartOffset = last.TextStartOffset,
                TextEndOffset = last.TextEndOffset,
                EndOfChapter = endOfChapter,
                Runs = last.Runs,
                Images = last.Images,
                Decorations = last.Decorations,
                HotZones = last.HotZones,
            };
        }
    }

    public void AddRun(PlacedRun run)
    {
        _current.Runs.Add(run);
        _used = true;
        if (run.TextLength > 0 && run.TextStart >= 0)
        {
            TrackText(run.TextStart, run.TextStart + run.TextLength);
        }
    }

    public void AddImage(PlacedImage image)
    {
        _current.Images.Add(image);
        _used = true;
    }

    public void AddDecoration(PlacedRect rect)
    {
        _current.Decorations.Add(rect);
        _used = true;
    }

    public void AddHotZone(PlacedHotZone zone) => _current.HotZones.Add(zone);

    public void MarkUsed() => _used = true;

    public void TrackText(int start, int end)
    {
        if (start < 0 || end <= start)
        {
            return;
        }

        if (_current.TextStartOffset < 0 || start < _current.TextStartOffset)
        {
            _current.TextStartOffset = start;
        }

        if (_current.TextEndOffset < 0 || end > _current.TextEndOffset)
        {
            _current.TextEndOffset = end;
        }
    }

    private LayoutPage MakePage() => new()
    {
        Index = _pages.Count,
        WritingMode = _options.WritingMode,
        Width = _options.ViewportWidth,
        Height = _options.ViewportHeight,
        InsetHorizontal = _options.InsetHorizontal,
        InsetVertical = _options.InsetVertical,
    };
}

/// <summary>
/// Shapes inline items into layout cells. Font fallback: when the main font
/// produces a .notdef glyph for a unit, the unit is reshaped with the first
/// registered fallback that covers it.
/// </summary>
internal sealed class CellFactory
{
    private readonly ComposerContext _context;
    private readonly Dictionary<(string FontPath, float Size), (float Ascent, float Descent)> _metricsCache = new();

    public CellFactory(ComposerContext context)
    {
        _context = context;
    }

    public float BodyLineHeight => _context.Options.BodyLineHeight;

    /// <summary>Shapes one already-extracted unit string as an unbreakable cell.</summary>
    public LayoutCell ShapeCell(
        string unitText,
        int globalTextStart,
        TypesetInlineStyle style,
        string? linkHref,
        string? footnoteHref,
        bool vertical)
    {
        var fontSize = _context.Options.BaseFontSize;
        if (style.Superscript)
        {
            fontSize *= 0.75f;
        }

        string fontPath = _context.MainFont;
        var shaped = _context.Shaper.Shape(unitText, 0, unitText.Length, fontPath, fontSize, vertical: false);

        if (ContainsNotdef(shaped))
        {
            foreach (var fallback in _context.Fonts.FontPaths)
            {
                if (fallback == fontPath)
                {
                    continue;
                }

                var retry = _context.Shaper.Shape(unitText, 0, unitText.Length, fallback, fontSize, vertical: false);
                if (!ContainsNotdef(retry))
                {
                    fontPath = fallback;
                    shaped = retry;
                    break;
                }
            }
        }

        var (ascent, descent) = GetMetrics(fontPath, fontSize);
        var letterSpacing = _context.LetterSpacing * (fontSize / _context.Options.BaseFontSize);

        var glyphCount = shaped.GlyphIds.Length;
        var glyphX = new float[glyphCount];
        var glyphY = new float[glyphCount];
        float pen = 0f;
        for (var i = 0; i < glyphCount; i++)
        {
            glyphX[i] = pen + shaped.OffsetsX[i];
            glyphY[i] = shaped.OffsetsY[i];
            pen += shaped.Advances[i] + letterSpacing;
        }

        // The trailing letter spacing hangs past the last glyph; exclude it so
        // line fitting matches the visible ink.
        var advance = glyphCount > 0 ? pen - letterSpacing : 0f;

        return new LayoutCell
        {
            Glyphs = shaped.GlyphIds,
            GlyphX = glyphX,
            GlyphY = glyphY,
            Advance = advance,
            Ascent = ascent,
            Descent = descent,
            FontPath = fontPath,
            FontSize = fontSize,
            TextStart = globalTextStart,
            TextLength = unitText.Length,
            Clusters = shaped.Clusters,
            Style = style,
            LinkHref = linkHref,
            FootnoteHref = footnoteHref,
            Character = unitText.Length == 1 ? unitText[0] : null,
        };
    }

    /// <summary>
    /// Builds the cell sequence for one inline item, honoring the vertical
    /// inline-run policy (single digits upright, two digits combined, Latin
    /// runs sideways) and splitting horizontal text into per-unit cells.
    /// </summary>
    public List<LayoutCell> BuildCells(InlineItem item, bool vertical)
    {
        var cells = new List<LayoutCell>();

        if (item.Kind == InlineKind.LineBreak)
        {
            cells.Add(new LayoutCell
            {
                Glyphs = Array.Empty<ushort>(),
                GlyphX = Array.Empty<float>(),
                GlyphY = Array.Empty<float>(),
                Advance = 0f,
                FontPath = _context.MainFont,
                FontSize = _context.Options.BaseFontSize,
                TextStart = item.TextStart,
                TextLength = 0,
                Style = item.Style,
                LinkHref = item.LinkHref,
                FootnoteHref = item.FootnoteHref,
                IsLineBreak = true,
            });
            return cells;
        }

        if (item.Kind == InlineKind.FootnoteMarker)
        {
            var marker = item.Text.Length > 0 ? item.Text : "注";
            cells.Add(ShapeCell(
                marker,
                item.TextStart,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical));
            return cells;
        }

        if (item.Kind != InlineKind.Text || item.Text.Length == 0 || item.Ghost)
        {
            return cells;
        }

        if (vertical)
        {
            AppendVerticalCells(cells, item);
            return cells;
        }

        var units = TypesetText.Itemize(item.Text, 0, item.Text.Length);
        foreach (var unit in units)
        {
            if (unit.Kind == TextUnitKind.Space && cells.Count > 0 && cells[^1].IsSpace)
            {
                continue; // collapse consecutive whitespace
            }

            var unitText = item.Text.Substring(unit.Start, unit.Length);
            var cell = ShapeCell(
                unitText,
                item.TextStart + unit.Start,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: false);
            cell = cell with { IsSpace = unit.Kind == TextUnitKind.Space };
            cells.Add(cell);
        }

        return cells;
    }

    private void AppendVerticalCells(List<LayoutCell> cells, InlineItem item)
    {
        var units = VerticalTextUnits.Tokenize(item.Text);
        foreach (var unit in units)
        {
            if (unit.IsLineBreak)
            {
                cells.Add(new LayoutCell
                {
                    Glyphs = Array.Empty<ushort>(),
                    GlyphX = Array.Empty<float>(),
                    GlyphY = Array.Empty<float>(),
                    Advance = 0f,
                    FontPath = _context.MainFont,
                    FontSize = _context.Options.BaseFontSize,
                    TextStart = item.TextStart + unit.Offset,
                    TextLength = 0,
                    Style = item.Style,
                    LinkHref = item.LinkHref,
                    FootnoteHref = item.FootnoteHref,
                    IsLineBreak = true,
                });
                continue;
            }

            var unitText = item.Text.Substring(unit.Offset, unit.Length);
            var cell = ShapeCell(
                unitText,
                item.TextStart + unit.Offset,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true);
            cell = cell with
            {
                Sideways = unit.IsSidewaysRun,
                Combined = unit.IsCombined,
            };
            cells.Add(cell);
        }
    }

    private (float Ascent, float Descent) GetMetrics(string fontPath, float fontSize)
    {
        var key = (fontPath, fontSize);
        if (_metricsCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var font = new SKFont(_context.Fonts.GetTypeface(fontPath), fontSize);
        var metrics = font.Metrics;
        var value = (-metrics.Ascent, metrics.Descent);
        _metricsCache[key] = value;
        return value;
    }

    private static bool ContainsNotdef(ShapedText shaped)
    {
        foreach (var glyph in shaped.GlyphIds)
        {
            if (glyph == 0)
            {
                return true;
            }
        }

        return false;
    }
}
