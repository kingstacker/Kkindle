using System.Globalization;
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
    /// <summary>Original Unicode text represented by this cell, including combining marks.</summary>
    public string Text { get; init; } = string.Empty;
    public int[] Clusters { get; init; } = Array.Empty<int>();
    public required TypesetInlineStyle Style { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    public string? FootnoteText { get; init; }
    /// <summary>Resolved raster for an inline image; null for text cells.</summary>
    public string? ImagePath { get; init; }
    /// <summary>Physical width of the unrotated inline image.</summary>
    public float ImageWidth { get; init; }
    /// <summary>Physical height of the unrotated inline image.</summary>
    public float ImageHeight { get; init; }
    /// <summary>Vertical mode: paint the inline image as a sideways object.</summary>
    public bool ImageSideways { get; init; }
    public bool IsSpace { get; init; }
    public bool IsLineBreak { get; init; }
    /// <summary>Vertical mode: shaped horizontally, painted rotated 90° clockwise.</summary>
    public bool Sideways { get; init; }
    /// <summary>Vertical mode: combined digits centered inside one upright cell.</summary>
    public bool Combined { get; init; }
    /// <summary>
    /// Vertical mode: this punctuation has no usable vertical presentation
    /// glyph and must be painted as a quarter-turned horizontal glyph.
    /// </summary>
    public bool VerticalRotation { get; init; }
    /// <summary>
    /// A footnote reference is one compact superscript unit. Keeping the
    /// complete marker (for example, "[3]") in one cell prevents its brackets
    /// from being paginated and aligned as independent body characters.
    /// </summary>
    public bool FootnoteMarker { get; init; }
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

    public void RecordFragments(IEnumerable<string> fragmentIds)
    {
        foreach (var fragmentId in fragmentIds)
        {
            RecordFragment(fragmentId);
        }
    }

    public void NextIfUsed()
    {
        if (_used)
        {
            Next();
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
                DebugBoxes = last.DebugBoxes,
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

    public void AddDebugBox(TypesetDebugBox box) => _current.DebugBoxes.Add(box);

    public void AddHotZone(PlacedHotZone zone)
    {
        // A link is shaped as several cells (for example, the three
        // characters in "[1]"). Keep one hit target for the visible run so
        // pointer release and footnote hover do not depend on which glyph was
        // hit. Do not merge across a line/column break: wrapped links still
        // need one independent target per visible fragment.
        if (_current.HotZones.LastOrDefault() is { } previous
            && string.Equals(previous.Href, zone.Href, StringComparison.Ordinal)
            && previous.Kind == zone.Kind
            && string.Equals(previous.FootnoteText, zone.FootnoteText, StringComparison.Ordinal)
            && AreAdjacentOnFlowAxis(previous.Rect, zone.Rect))
        {
            _current.HotZones[^1] = new PlacedHotZone
            {
                Kind = previous.Kind,
                Rect = new SKRect(
                    Math.Min(previous.Rect.Left, zone.Rect.Left),
                    Math.Min(previous.Rect.Top, zone.Rect.Top),
                    Math.Max(previous.Rect.Right, zone.Rect.Right),
                    Math.Max(previous.Rect.Bottom, zone.Rect.Bottom)),
                Href = previous.Href,
                FootnoteText = previous.FootnoteText,
            };
            return;
        }

        _current.HotZones.Add(zone);
    }

    private bool AreAdjacentOnFlowAxis(SKRect previous, SKRect current)
    {
        const float epsilon = 0.75f;
        if (_options.WritingMode == TypesetWritingMode.HorizontalTb)
        {
            return Math.Abs(previous.Top - current.Top) <= epsilon
                && Math.Abs(previous.Bottom - current.Bottom) <= epsilon
                && current.Left <= previous.Right + epsilon
                && previous.Left <= current.Right + epsilon;
        }

        return Math.Abs(previous.Left - current.Left) <= epsilon
            && Math.Abs(previous.Right - current.Right) <= epsilon
            && current.Top <= previous.Bottom + epsilon
            && previous.Top <= current.Bottom + epsilon;
    }

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
        bool vertical,
        string? footnoteText = null,
        bool footnoteMarker = false)
    {
        var fontSize = _context.Options.BaseFontSize;
        if (style.Superscript)
        {
            // Footnote references use a compact marker size. Other <sup>
            // content keeps the slightly larger superscript scale so ordinary
            // mathematical/text superscripts do not change unexpectedly.
            fontSize *= footnoteMarker ? 0.70f : 0.75f;
        }

        string fontPath = _context.MainFont;
        // HTML collapses ordinary line breaks and runs of whitespace. Keep
        // the original UTF-16 length for offset mapping, but shape whitespace
        // as one regular space so a newline never turns into a .notdef box.
        var shapeText = unitText.All(TypesetText.IsSpace) ? " " : unitText;
        var shaped = _context.Shaper.Shape(shapeText, 0, shapeText.Length, fontPath, fontSize, vertical: false);

        if (ContainsNotdef(shaped))
        {
            foreach (var fallback in _context.Fonts.FontPaths)
            {
                if (fallback == fontPath)
                {
                    continue;
                }

                var retry = _context.Shaper.Shape(shapeText, 0, shapeText.Length, fallback, fontSize, vertical: false);
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
            Text = unitText,
            Clusters = shaped.Clusters,
            Style = style,
            LinkHref = linkHref,
            FootnoteHref = footnoteHref,
            FootnoteText = footnoteText,
            FootnoteMarker = footnoteMarker,
            Character = unitText.Length == 1 && !TypesetText.IsSpace(unitText[0]) ? unitText[0] : null,
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

        if (item.Kind == InlineKind.Image)
        {
            if (item.ImagePath is null || !File.Exists(item.ImagePath))
            {
                return cells;
            }

            using var codec = SKCodec.Create(item.ImagePath);
            var info = codec?.Info;
            if (info is null || info.Value.Width <= 0 || info.Value.Height <= 0)
            {
                return cells;
            }

            // Inline formula/glyph images specify a font-relative height.
            // Preserve that metric instead of treating source pixels as a
            // block image size. A conservative cap keeps malformed/oversized
            // assets from escaping the content box in either mode.
            var hasWidthConstraint = item.ImageWidthFactor is > 0f;
            var requestedHeight = _context.Options.BaseFontSize
                * Math.Clamp(item.ImageHeightEm ?? 1.5f, 0.5f, 8f);
            var scale = hasWidthConstraint
                ? Math.Min(
                    _context.Options.ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                    item.DecorativeQuote ? _context.Options.BaseFontSize * 5f : float.MaxValue)
                    / info.Value.Width
                : requestedHeight / info.Value.Height;
            var maxWidth = vertical
                ? _context.Options.ContentHeight
                : _context.Options.ContentWidth;
            scale = Math.Min(scale, maxWidth / info.Value.Width);
            if (!hasWidthConstraint)
            {
                scale = Math.Min(scale, 1f);
            }
            if (vertical)
            {
                // A vertical inline image is rotated like an existing sideways
                // Latin run. Its unrotated height becomes the cross-column
                // width, so keep it comfortably within one vertical column.
                scale = Math.Min(
                    scale,
                    (_context.Options.BodyLineHeight * 0.90f) / info.Value.Height);
            }

            var width = Math.Max(1f, info.Value.Width * scale);
            var height = Math.Max(1f, info.Value.Height * scale);
            cells.Add(new LayoutCell
            {
                Glyphs = Array.Empty<ushort>(),
                GlyphX = Array.Empty<float>(),
                GlyphY = Array.Empty<float>(),
                Advance = width,
                Ascent = height * 0.84f,
                Descent = height * 0.16f,
                FontPath = _context.MainFont,
                FontSize = _context.Options.BaseFontSize,
                TextStart = item.TextStart,
                TextLength = 0,
                Style = item.Style,
                LinkHref = item.LinkHref,
                FootnoteHref = item.FootnoteHref,
                FootnoteText = item.FootnoteText,
                ImagePath = item.ImagePath,
                ImageWidth = width,
                ImageHeight = height,
                ImageSideways = vertical,
            });
            return cells;
        }

        if (item.Kind == InlineKind.FootnoteMarker || item.FootnoteHref is not null)
        {
            var marker = item.Text.Length > 0 ? item.Text : "注";
            var markerStyle = item.Style with
            {
                Superscript = true,
                NoWrap = true,
            };
            var cell = ShapeCell(
                marker,
                item.TextStart,
                markerStyle,
                item.LinkHref,
                item.FootnoteHref,
                vertical,
                item.FootnoteText,
                footnoteMarker: true);
            cells.Add(cell with
            {
                FootnoteMarker = true,
                // In vertical writing a reference such as "[3]" is a
                // tate-chu-yoko unit: one compact horizontal marker centered
                // in one vertical cell, rather than three separate rows.
                Combined = vertical,
            });
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
            if (unit.Kind == TextUnitKind.LatinWord
                && unitText.Length > 1
                && cell.Advance > _context.Options.ContentWidth + 0.01f)
            {
                // A URL or an unspaced Latin token can be wider than the
                // entire content box. Keep ordinary words atomic, but split
                // this exceptional case into source-preserving glyph units so
                // one cell can never force a line past the right edge.
                for (var offset = 0; offset < unitText.Length;)
                {
                    var length = char.IsHighSurrogate(unitText[offset])
                        && offset + 1 < unitText.Length
                        && char.IsLowSurrogate(unitText[offset + 1])
                        ? 2
                        : 1;
                    cells.Add(ShapeCell(
                        unitText.Substring(offset, length),
                        item.TextStart + unit.Start + offset,
                        item.Style,
                        item.LinkHref,
                        item.FootnoteHref,
                        vertical: false));
                    offset += length;
                }
            }
            else
            {
                cell = cell with { IsSpace = unit.Kind == TextUnitKind.Space };
                cells.Add(cell);
            }
        }

        return cells;
    }

    /// <summary>
    /// Collapses whitespace that was split into separate XHTML text nodes by
    /// inline markup. The retained cell keeps the first source range, while
    /// later collapsed ranges remain in BodyText for offset compatibility.
    /// </summary>
    public static void CollapseWhitespace(List<LayoutCell> cells)
    {
        var previousWasSpace = false;
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (cell.IsLineBreak)
            {
                previousWasSpace = false;
                continue;
            }

            if (!cell.IsSpace)
            {
                previousWasSpace = false;
                continue;
            }

            if (previousWasSpace)
            {
                cells.RemoveAt(index--);
                continue;
            }

            previousWasSpace = true;
        }
    }

    private void AppendVerticalCells(List<LayoutCell> cells, InlineItem item)
    {
        var units = VerticalTextUnits.Tokenize(
            item.Text,
            item.Style.VerticalTextCombineLimit ?? 2,
            item.Style.VerticalTextOrientation ?? TypesetVerticalOrientation.Mixed);
        var previousWasSpace = cells.Count > 0 && cells[^1].IsSpace;
        var previousWasLineBreak = cells.Count > 0 && cells[^1].IsLineBreak;
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
                    FootnoteText = item.FootnoteText,
                    IsLineBreak = true,
                });
                previousWasSpace = false;
                previousWasLineBreak = true;
                continue;
            }

            var unitText = item.Text.Substring(unit.Offset, unit.Length);
            var isSpace = unitText.All(TypesetText.IsSpace);
            if (isSpace)
            {
                // XHTML source formatting is normally collapsed whitespace.
                // Do not let indentation/newlines become visible empty cells
                // at the start of a paragraph or after an explicit break.
                if (previousWasSpace || previousWasLineBreak)
                {
                    previousWasSpace = true;
                    continue;
                }
            }

            var cell = ShapeCell(
                unitText,
                item.TextStart + unit.Offset,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true,
                footnoteText: item.FootnoteText);
            cell = cell with
            {
                IsSpace = isSpace,
                Sideways = unit.IsSidewaysRun,
                Combined = unit.IsCombined,
            };
            if (cell.Sideways && cell.Advance > _context.Options.ContentHeight + 0.01f)
            {
                // A publication can contain an unspaced URL, identifier or
                // formula longer than one vertical column. Split only at text
                // element boundaries, retaining the original source offsets;
                // a long run must never be clipped or make pagination stall.
                cells.AddRange(SplitOversizedSidewaysCell(item, unitText, unit, cell));
            }
            else
            {
                cells.Add(cell);
            }
            previousWasSpace = isSpace;
            previousWasLineBreak = false;
        }
    }

    private List<LayoutCell> SplitOversizedSidewaysCell(
        InlineItem item,
        string unitText,
        VerticalTextUnits.Unit unit,
        LayoutCell original)
    {
        var result = new List<LayoutCell>();
        var starts = StringInfo.ParseCombiningCharacters(unitText);
        if (starts.Length == 0)
        {
            return [original];
        }

        var chunkStart = 0;
        for (var elementIndex = 0; elementIndex < starts.Length; elementIndex++)
        {
            var elementStart = starts[elementIndex];
            var elementEnd = elementIndex + 1 < starts.Length
                ? starts[elementIndex + 1]
                : unitText.Length;
            var candidateText = unitText[chunkStart..elementEnd];
            var candidate = ShapeCell(
                candidateText,
                item.TextStart + unit.Offset + chunkStart,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true,
                footnoteText: item.FootnoteText) with
            {
                Sideways = true,
            };

            if (candidate.Advance > _context.Options.ContentHeight + 0.01f
                && elementStart > chunkStart)
            {
                result.Add(ShapeCell(
                    unitText[chunkStart..elementStart],
                    item.TextStart + unit.Offset + chunkStart,
                    item.Style,
                    item.LinkHref,
                    item.FootnoteHref,
                    vertical: true,
                    footnoteText: item.FootnoteText) with
                {
                    Sideways = true,
                });
                chunkStart = elementStart;
                candidate = ShapeCell(
                    unitText[chunkStart..elementEnd],
                    item.TextStart + unit.Offset + chunkStart,
                    item.Style,
                    item.LinkHref,
                    item.FootnoteHref,
                    vertical: true,
                    footnoteText: item.FootnoteText) with
                {
                    Sideways = true,
                };
            }

            if (elementEnd == unitText.Length)
            {
                result.Add(candidate);
                chunkStart = elementEnd;
            }
        }

        if (result.Count == 0 || chunkStart < unitText.Length)
        {
            result.Add(ShapeCell(
                unitText[chunkStart..],
                item.TextStart + unit.Offset + chunkStart,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true,
                footnoteText: item.FootnoteText) with
            {
                Sideways = true,
            });
        }

        return result;
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
