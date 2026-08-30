using SkiaSharp;

namespace Kkindle.Layout;

/// <summary>
/// Lays a chapter out as horizontally flowing, vertically stacked pages:
/// blocks advance downward, lines wrap left-to-right, page breaks fall
/// between line boxes. Justification stretches inter-glyph gaps
/// (inter-character), matching the typography the WebKit reader enforced.
/// Closing punctuation hangs into the trailing margin; opening punctuation
/// never ends a line.
/// </summary>
internal sealed class HorizontalComposer
{
    private readonly ComposerContext _context;
    private readonly CellFactory _cells;
    private readonly PageBuilder _pages;
    private float _cursorY;

    public HorizontalComposer(ComposerContext context, CellFactory cells, PageBuilder pages)
    {
        _context = context;
        _cells = cells;
        _pages = pages;
        _cursorY = context.Options.InsetVertical;
    }

    private float ContentWidth => _context.Options.ContentWidth;

    private float ContentBottom => _context.Options.InsetVertical + _context.Options.ContentHeight;

    private float BodyLineHeight => _context.Options.BodyLineHeight;

    private float BaseFontSize => _context.Options.BaseFontSize;

    private float InsetH => _context.Options.InsetHorizontal;

    private float InsetV => _context.Options.InsetVertical;

    public void Compose(ChapterContent content)
    {
        foreach (var block in content.Blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Image:
                    PlaceImageBlock(block);
                    break;
                case BlockKind.Rule:
                    PlaceRule(block);
                    break;
                default:
                    PlaceTextBlock(block);
                    break;
            }
        }
    }

    private void PlaceTextBlock(ContentBlock block)
    {
        var isHeading = block.Kind == BlockKind.Heading;
        var startsAtPageTop = isHeading
            && (!string.IsNullOrWhiteSpace(block.ElementId) || block.FragmentIds.Count > 0);
        float leftInset = 0f;
        float rightInset = 0f;
        if (block.Kind == BlockKind.Blockquote)
        {
            leftInset = BaseFontSize * 1.1f + 3f;
            rightInset = BaseFontSize * 1.1f;
        }

        var availWidth = ContentWidth - leftInset - rightInset;
        var blockLeft = InsetH + leftInset;

        var cells = new List<LayoutCell>();
        foreach (var item in block.Items)
        {
            cells.AddRange(_cells.BuildCells(item, vertical: false));
        }
        CellFactory.CollapseWhitespace(cells);
        var lineHeight = isHeading
            ? BaseFontSize * 1.35f
            : Math.Max(BodyLineHeight, cells
                .Where(cell => cell.ImagePath is not null)
                .Select(cell => cell.ImageHeight)
                .DefaultIfEmpty(0f)
                .Max());

        if (startsAtPageTop)
        {
            // TOC headings are fragment targets. A page that merely contains
            // the heading is not enough: the target must be the first visible
            // line after a click, just like scrollIntoView({ block: "start" })
            // in the old document reader.
            _pages.NextIfUsed();
            _cursorY = InsetV;
        }
        else
        {
            _cursorY += block.SpaceBeforeLines * BodyLineHeight;
        }

        var lines = BreakLines(cells, availWidth, block.TextIndentEm * BaseFontSize);
        var quoteTop = float.MaxValue;
        var quoteBottom = float.MinValue;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (_cursorY + lineHeight > ContentBottom + 0.01f)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }

            var line = lines[lineIndex];
            if (lineIndex == 0)
            {
                _pages.RecordFragment(block.ElementId);
                _pages.RecordFragments(block.FragmentIds);
            }
            if (block.Kind == BlockKind.Blockquote)
            {
                quoteTop = Math.Min(quoteTop, _cursorY);
                quoteBottom = Math.Max(quoteBottom, _cursorY + lineHeight);
            }

            var baseline = _cursorY + lineHeight / 2f + (line.Ascent - line.Descent) / 2f;

            var startX = blockLeft + line.Indent;
            var isLastLine = lineIndex == lines.Count - 1 || line.EndsWithForcedBreak;
            if (isHeading || block.Center)
            {
                startX = blockLeft + Math.Max(0f, (availWidth - line.Used) / 2f);
            }
            else if (block.Justify && !isLastLine)
            {
                var extra = availWidth - line.Indent - line.Used;
                if (extra > 0f && line.GlyphCount > 1)
                {
                    line.Stretch(extra);
                }
            }

            PlaceLine(line, startX, baseline, _cursorY, lineHeight);
            _cursorY += lineHeight;
        }

        if (block.Kind == BlockKind.Blockquote && quoteTop <= quoteBottom)
        {
            _pages.AddDecoration(new PlacedRect
            {
                Kind = DecorationKind.BlockquoteBar,
                Rect = new SKRect(InsetH, quoteTop, InsetH + 3f, quoteBottom),
            });
        }

        _cursorY += block.SpaceAfterLines * BodyLineHeight;
    }

    private void PlaceLine(LineBox line, float startX, float baseline, float lineTop, float lineHeight)
    {
        float penX = startX;
        RunAccumulator? run = null;

        foreach (var cell in line.Cells)
        {
            if (cell.ImagePath is not null)
            {
                run?.Flush(_pages);
                run = null;

                var imageWidth = Math.Max(1f, cell.ImageWidth);
                var imageHeight = Math.Max(1f, cell.ImageHeight);
                var imageRect = new SKRect(
                    penX,
                    lineTop + Math.Max(0f, (lineHeight - imageHeight) / 2f),
                    penX + imageWidth,
                    lineTop + Math.Max(0f, (lineHeight - imageHeight) / 2f) + imageHeight);
                _pages.AddImage(new PlacedImage
                {
                    Path = cell.ImagePath,
                    Rect = imageRect,
                    LinkHref = cell.LinkHref,
                    FootnoteHref = cell.FootnoteHref,
                    FootnoteText = cell.FootnoteText,
                });

                if (cell.LinkHref is not null || cell.FootnoteHref is not null)
                {
                    _pages.AddHotZone(new PlacedHotZone
                    {
                        Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                        Rect = imageRect,
                        Href = cell.FootnoteHref ?? cell.LinkHref!,
                        FootnoteText = cell.FootnoteText,
                    });
                }

                penX += cell.Advance;
                continue;
            }

            var superscriptShift = cell.Superscript
                ? -(cell.FootnoteMarker ? BaseFontSize * 0.30f : cell.FontSize * 0.35f)
                : 0f;
            var cellBaseline = baseline + superscriptShift;

            if (run is null || !run.CanAppend(cell, cellBaseline))
            {
                run?.Flush(_pages);
                run = new RunAccumulator(cellBaseline);
            }

            run.BeginCell(cell);
            for (var i = 0; i < cell.Glyphs.Length; i++)
            {
                var cluster = i < cell.Clusters.Length ? cell.Clusters[i] : i;
                run.AddGlyph(cell.Glyphs[i], penX + cell.GlyphX[i], cell.GlyphY[i], cluster);
            }

            if (cell.LinkHref is not null || cell.FootnoteHref is not null)
            {
                _pages.AddHotZone(new PlacedHotZone
                {
                    Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                    Rect = new SKRect(penX, lineTop, penX + Math.Max(cell.Advance, 4f), lineTop + lineHeight),
                    Href = cell.FootnoteHref ?? cell.LinkHref!,
                    FootnoteText = cell.FootnoteText,
                });
            }

            if (cell.Style.Underline && cell.TextLength > 0)
            {
                var underlineY = baseline + cell.FontSize * 0.14f;
                _pages.AddDecoration(new PlacedRect
                {
                    Kind = DecorationKind.Underline,
                    Rect = new SKRect(penX, underlineY, penX + cell.Advance, underlineY + 1.2f),
                    TextStart = cell.TextStart,
                    TextLength = cell.TextLength,
                });
            }

            if (cell.Style.Strikeout && cell.TextLength > 0)
            {
                var strikeY = baseline - cell.FontSize * 0.25f;
                _pages.AddDecoration(new PlacedRect
                {
                    Kind = DecorationKind.Strikeout,
                    Rect = new SKRect(penX, strikeY, penX + cell.Advance, strikeY + 1.2f),
                    TextStart = cell.TextStart,
                    TextLength = cell.TextLength,
                });
            }

            penX += cell.Advance;
        }

        run?.Flush(_pages);
    }

    /// <summary>
    /// Greedy line fitting. A closing mark that does not fit hangs into the
    /// trailing margin; an opening mark never ends a line — it moves down
    /// together with the character that follows it.
    /// </summary>
    private List<LineBox> BreakLines(List<LayoutCell> cells, float availWidth, float firstLineIndent)
    {
        var lines = new List<LineBox>();
        var line = new LineBox();
        var indentApplied = false;

        foreach (var cell in cells)
        {
            if (cell.IsLineBreak)
            {
                line.EndsWithForcedBreak = true;
                lines.Add(line);
                line = new LineBox();
                continue;
            }

            if (cell.IsSpace && line.Cells.Count == 0)
            {
                continue; // no leading whitespace
            }

            if (!indentApplied)
            {
                line.Indent = firstLineIndent;
                indentApplied = true;
            }

            var fits = line.Indent + line.Used + cell.Advance <= availWidth + 0.01f;
            if (!fits)
            {
                var pulled = new List<LayoutCell>();

                if (IsClosingMark(cell))
                {
                    // Pull back trailing closers, then one leading cell, so
                    // the cluster moves down together and no closer starts
                    // the next line.
                    while (line.Cells.Count > 0 && IsClosingMark(line.Cells[^1]) && pulled.Count < 4)
                    {
                        pulled.Insert(0, line.Cells[^1]);
                        line.RemoveLast();
                    }

                    if (line.Cells.Count > 0)
                    {
                        pulled.Insert(0, line.Cells[^1]);
                        line.RemoveLast();
                    }
                }

                if (line.Cells.Count > 0 && IsOpeningMark(line.Cells[^1]))
                {
                    // An opening mark must not end the line.
                    pulled.Insert(0, line.Cells[^1]);
                    line.RemoveLast();
                }

                if (line.Cells.Count > 0)
                {
                    lines.Add(line);
                }

                line = new LineBox();
                line.Indent = 0f;
                foreach (var pulledCell in pulled)
                {
                    line.Add(pulledCell);
                }
            }

            if (cell.IsSpace && line.Cells.Count == 0)
            {
                continue;
            }

            line.Add(cell);
        }

        if (line.Cells.Count > 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static bool IsClosingMark(LayoutCell cell) =>
        cell.Character is { } c && TypesetText.IsProhibitedAtLineStart(c);

    private static bool IsOpeningMark(LayoutCell cell) =>
        cell.Character is { } c && TypesetText.IsProhibitedAtLineEnd(c);

    private void PlaceImageBlock(ContentBlock block)
    {
        var item = block.Items.FirstOrDefault(i => i.Kind == InlineKind.Image);
        if (item?.ImagePath is null || !File.Exists(item.ImagePath))
        {
            return;
        }

        using var codec = SKCodec.Create(item.ImagePath);
        if (codec is null)
        {
            return;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return;
        }
        var hasWidthConstraint = item.ImageWidthFactor is > 0f;
        var maxW = hasWidthConstraint
            ? Math.Min(
                ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                item.DecorativeQuote ? BaseFontSize * 5f : float.MaxValue)
            : ContentWidth;
        var maxH = Math.Max(1f, _context.Options.ContentHeight - BaseFontSize * 3.6f);
        var scale = Math.Min(maxW / info.Width, maxH / info.Height);
        if (!hasWidthConstraint)
        {
            scale = Math.Min(1f, scale);
        }
        var w = info.Width * scale;
        var h = info.Height * scale;

        _cursorY += BaseFontSize * 1.8f;
        if (_cursorY + h > ContentBottom + 0.01f)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV + BaseFontSize * 0.5f;
        }

        var x = block.AlignRight
            ? InsetH + ContentWidth - w
            : block.Center
                ? InsetH + (ContentWidth - w) / 2f
                : InsetH;
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddImage(new PlacedImage
        {
            Path = item.ImagePath,
            Rect = new SKRect(x, _cursorY, x + w, _cursorY + h),
            LinkHref = item.LinkHref,
            FootnoteHref = item.FootnoteHref,
        });
        if (item.LinkHref is not null || item.FootnoteHref is not null)
        {
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = item.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(x, _cursorY, x + w, _cursorY + h),
                Href = item.FootnoteHref ?? item.LinkHref!,
                FootnoteText = item.FootnoteText,
            });
        }
        _cursorY += h + BaseFontSize * 1.8f;
    }

    private void PlaceRule(ContentBlock block)
    {
        _cursorY += BaseFontSize * 2f;
        if (_cursorY + 1f > ContentBottom + 0.01f)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV + BaseFontSize;
        }

        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddDecoration(new PlacedRect
        {
            Kind = DecorationKind.Rule,
            Rect = new SKRect(InsetH, _cursorY, InsetH + ContentWidth, _cursorY + 1.2f),
        });
        _cursorY += BaseFontSize * 2f;
    }

    /// <summary>A laid-out line of cells.</summary>
    private sealed class LineBox
    {
        public List<LayoutCell> Cells { get; } = new();
        public float Used { get; private set; }
        public float Ascent { get; private set; }
        public float Descent { get; private set; }
        public int GlyphCount { get; private set; }
        public float Indent { get; set; }
        public bool EndsWithForcedBreak { get; set; }

        public void Add(LayoutCell cell)
        {
            Cells.Add(cell);
            Used += cell.Advance;
            Ascent = Math.Max(Ascent, cell.Ascent);
            Descent = Math.Max(Descent, cell.Descent);
            GlyphCount += cell.FootnoteMarker
                ? 1
                : Math.Max(1, cell.Glyphs.Length);
        }

        public void RemoveLast()
        {
            if (Cells.Count == 0)
            {
                return;
            }

            var removed = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            Used -= removed.Advance;
            GlyphCount -= removed.FootnoteMarker
                ? 1
                : Math.Max(1, removed.Glyphs.Length);
            Ascent = 0f;
            Descent = 0f;
            foreach (var cell in Cells)
            {
                Ascent = Math.Max(Ascent, cell.Ascent);
                Descent = Math.Max(Descent, cell.Descent);
            }
        }

        /// <summary>Distributes <paramref name="extra"/> pixels across all inter-glyph gaps.</summary>
        public void Stretch(float extra)
        {
            var gaps = GlyphCount - 1;
            if (gaps <= 0)
            {
                return;
            }

            var perGap = extra / gaps;
            for (var index = 0; index < Cells.Count; index++)
            {
                var cell = Cells[index];
                var shiftedX = new float[cell.GlyphX.Length];
                if (!cell.FootnoteMarker)
                {
                    for (var i = 0; i < cell.GlyphX.Length; i++)
                    {
                        // Gaps inside a shaped cell belong in its local glyph
                        // positions. The gap after a cell belongs in that
                        // cell's advance, so PlaceLine does not count the same
                        // distance twice when it advances to the next cell.
                        shiftedX[i] = cell.GlyphX[i] + perGap * i;
                    }
                }
                else
                {
                    // A marker is an atomic visual unit. Justification may add
                    // space after it, but must never stretch the distance
                    // between its paired brackets and the reference number.
                    Array.Copy(cell.GlyphX, shiftedX, cell.GlyphX.Length);
                }

                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                var gapsAfter = index == Cells.Count - 1
                    ? cell.FootnoteMarker ? 0 : Math.Max(0, glyphCount - 1)
                    : cell.FootnoteMarker ? 1 : glyphCount;
                Cells[index] = cell with
                {
                    GlyphX = shiftedX,
                    Advance = cell.Advance + perGap * gapsAfter,
                };
            }

            Used += extra;
        }
    }

    /// <summary>Accumulates consecutive compatible cells into one placed run.</summary>
    private sealed class RunAccumulator
    {
        private readonly float _baseline;
        private readonly List<ushort> _glyphs = new();
        private readonly List<float> _x = new();
        private readonly List<float> _y = new();
        private readonly List<int> _clusters = new();
        private LayoutCell? _first;
        private LayoutCell? _last;
        private int _cellClusterBase;
        private float _flowAdvance;

        public RunAccumulator(float baseline)
        {
            _baseline = baseline;
        }

        public bool CanAppend(LayoutCell cell, float baseline)
        {
            return _first is not null
                && _last is not null
                && cell.FontPath == _first.FontPath
                && Math.Abs(cell.FontSize - _first.FontSize) < 0.01f
                && cell.Style.Equals(_last.Style)
                && Math.Abs(baseline - _baseline) < 0.01f
                && cell.TextStart >= 0
                && _last.TextStart >= 0
                && cell.TextStart == _last.TextStart + _last.TextLength;
        }

        public void BeginCell(LayoutCell cell)
        {
            _first ??= cell;
            _last = cell;
            _cellClusterBase = _first.TextStart >= 0 && cell.TextStart >= 0
                ? cell.TextStart - _first.TextStart
                : 0;
            _flowAdvance += cell.Advance;
        }

        public void AddGlyph(ushort glyph, float x, float y, int cluster)
        {
            _glyphs.Add(glyph);
            _x.Add(x);
            _y.Add(y);
            _clusters.Add(_cellClusterBase + cluster);
        }

        public void Flush(PageBuilder pages)
        {
            if (_first is null || _glyphs.Count == 0)
            {
                Reset();
                return;
            }

            var originX = _x[0];
            var glyphs = _glyphs.ToArray();
            var x = new float[_x.Count];
            var y = new float[_y.Count];
            for (var i = 0; i < _x.Count; i++)
            {
                x[i] = _x[i] - originX;
                // Cell glyph offsets are already relative to the baseline.
                // Subtracting the absolute line baseline here moves every
                // horizontal glyph far above the page.
                y[i] = _y[i];
            }

            var textStart = _first.TextStart;
            var textLength = _first.TextStart >= 0 && _last is not null && _last.TextStart >= 0
                ? Math.Max(0, _last.TextStart + _last.TextLength - textStart)
                : 0;

            pages.AddRun(new PlacedRun
            {
                FontPath = _first.FontPath,
                FontSize = _first.FontSize,
                Glyphs = glyphs,
                X = x,
                Y = y,
                OriginX = originX,
                OriginY = _baseline,
                FlowAdvance = _flowAdvance,
                TextStart = textStart,
                TextLength = textLength,
                Clusters = _clusters.ToArray(),
                Style = _first.Style,
            });

            Reset();
        }

        private void Reset()
        {
            _first = null;
            _last = null;
            _flowAdvance = 0f;
            _glyphs.Clear();
            _x.Clear();
            _y.Clear();
            _clusters.Clear();
        }
    }
}
