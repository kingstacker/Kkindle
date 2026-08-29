using SkiaSharp;

namespace Kkindle.Layout;

/// <summary>
/// Lays a chapter out as vertical-rl pages: each column is one line of text,
/// characters flow top-to-bottom inside a fixed 1-em cell grid, and columns
/// advance right-to-left. The coordinate mapping is owned by this engine —
/// there is no writing-mode engine underneath to fight.
///
/// Grid rules carried over from the WebKit reader: one line pitch per column,
/// two-em first-column indent, headings bold and centered with one empty
/// column before and after, closing punctuation hanging into the bottom page
/// margin, opening punctuation never ending a column.
/// </summary>
internal sealed class VerticalComposer
{
    private readonly ComposerContext _context;
    private readonly CellFactory _cells;
    private readonly PageBuilder _pages;
    private float _cursorX;

    public VerticalComposer(ComposerContext context, CellFactory cells, PageBuilder pages)
    {
        _context = context;
        _cells = cells;
        _pages = pages;
        _cursorX = context.Options.ViewportWidth - context.Options.InsetHorizontal;
    }

    private float ColumnPitch => _context.Options.BodyLineHeight;

    private float CellPitch => _context.Options.BaseFontSize + _context.LetterSpacing;

    private int CharsPerColumn => Math.Max(1, (int)Math.Floor(_context.Options.ContentHeight / CellPitch));

    private int ColumnsPerPage => Math.Max(1, (int)Math.Floor(_context.Options.ContentWidth / ColumnPitch));

    private float ContentLeft => _context.Options.InsetHorizontal;

    private float ContentRight => _context.Options.ViewportWidth - _context.Options.InsetHorizontal;

    private float ContentTop => _context.Options.InsetVertical;

    private float ContentHeight => _context.Options.ContentHeight;

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
                    PlaceRule();
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

        float rightInset = 0f;
        if (block.Kind == BlockKind.Blockquote)
        {
            rightInset = _context.Options.BaseFontSize * 1.1f + 3f;
        }

        var cells = new List<LayoutCell>();
        foreach (var item in block.Items)
        {
            cells.AddRange(_cells.BuildCells(item, vertical: true));
        }

        ResolveUprightGlyphs(cells);

        _cursorX -= block.SpaceBeforeLines * ColumnPitch;
        _cursorX -= rightInset;

        var indent = block.TextIndentEm * CellPitch;
        var quoteRight = float.MinValue;
        var quoteLeft = float.MaxValue;

        var index = 0;
        var firstColumnOfBlock = true;
        while (index < cells.Count)
        {
            EnsureColumnSpace();
            if (firstColumnOfBlock)
            {
                _pages.RecordFragment(block.ElementId);
                firstColumnOfBlock = false;
            }

            var columnLeft = _cursorX - ColumnPitch;
            var colCenter = _cursorX - ColumnPitch / 2f;
            _lastColumnFirstRun = _pages.Current.Runs.Count;
            var (placed, rowsUsed) = FillColumn(cells, index, colCenter, columnLeft, indent, isHeading);

            if (block.Kind == BlockKind.Blockquote && rowsUsed > 0)
            {
                quoteRight = Math.Max(quoteRight, _cursorX);
                quoteLeft = Math.Min(quoteLeft, columnLeft);
            }

            index = placed;
            _cursorX -= ColumnPitch;
            indent = 0f;
        }

        if (block.Kind == BlockKind.Blockquote && quoteRight > quoteLeft)
        {
            _pages.AddDecoration(new PlacedRect
            {
                Kind = DecorationKind.BlockquoteBar,
                Rect = new SKRect(quoteRight, ContentTop, quoteRight + 3f, ContentTop + ContentHeight),
            });
        }

        _cursorX -= block.SpaceAfterLines * ColumnPitch;
    }

    /// <summary>
    /// Fills one column starting at <paramref name="start"/>. Returns the next
    /// unplaced index and the number of rows consumed.
    /// </summary>
    private (int Next, int RowsUsed) FillColumn(
        List<LayoutCell> cells,
        int start,
        float colCenter,
        float columnLeft,
        float indent,
        bool centerVertically)
    {
        var capacity = CharsPerColumn;
        var rowsUsed = 0;
        var y = ContentTop + indent;
        var index = start;
        var hung = false;

        while (index < cells.Count)
        {
            var cell = cells[index];
            if (cell.IsLineBreak)
            {
                index++;
                break;
            }

            var rows = CellRows(cell);

            if (rowsUsed + rows > capacity)
            {
                if (!hung && cell.Character is { } c && TypesetText.IsProhibitedAtLineStart(c) && rowsUsed > 0)
                {
                    // Hang the closing mark into the bottom page margin.
                    PlaceCell(cell, colCenter, columnLeft, y, rows);
                    index++;
                    rowsUsed = capacity;
                }

                break;
            }

            // Leave the remaining cells empty when filling them would strand
            // an opening mark at the column bottom, or push a closing cluster
            // to the top of the next column.
            if (rowsUsed + rows == capacity)
            {
                if (cell.Character is { } opener && TypesetText.IsProhibitedAtLineEnd(opener))
                {
                    break;
                }

                var clusterRows = 0;
                var clusterUnits = 0;
                for (var next = index + 1; next < cells.Count && clusterUnits < 4; next++, clusterUnits++)
                {
                    var candidate = cells[next];
                    if (candidate.IsLineBreak)
                    {
                        break;
                    }

                    if (candidate.Character is not { } closer
                        || !TypesetText.IsProhibitedAtLineStart(closer))
                    {
                        break;
                    }

                    clusterRows += CellRows(candidate);
                }

                if (clusterRows > 0 && rows + clusterRows <= capacity)
                {
                    break; // start the run of closers on a fresh column
                }
            }

            PlaceCell(cell, colCenter, columnLeft, y, rows);
            y += rows * CellPitch;
            rowsUsed += rows;
            index++;
        }

        if (centerVertically && rowsUsed > 0 && rowsUsed < capacity)
        {
            // Headings center within the column: shift the placed cells down
            // by half the leftover space.
            var shift = (capacity - rowsUsed) * CellPitch / 2f;
            ShiftPlacedDown(shift);
        }

        return (index, rowsUsed);
    }

    private int _lastColumnFirstRun;

    private void ShiftPlacedDown(float shift)
    {
        // Runs placed for the current column start at the page's run list
        // position recorded before the column began; shifting rewrites their
        // origins. This avoids a second pass over cell geometry.
        for (var i = _lastColumnFirstRun; i < _pages.Current.Runs.Count; i++)
        {
            var run = _pages.Current.Runs[i];
            _pages.Current.Runs[i] = run with { OriginY = run.OriginY + shift };
        }
    }

    private void PlaceCell(LayoutCell cell, float colCenter, float columnLeft, float y, int rows)
    {
        var superscriptShift = cell.Superscript ? -cell.FontSize * 0.35f : 0f;

        if (cell.Sideways)
        {
            var ascent = cell.Ascent;
            var descent = cell.Descent;
            var originX = colCenter - (ascent - descent) / 2f;
            var originY = y + superscriptShift - descent * 0f;
            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = cell.GlyphX,
                Y = cell.GlyphY,
                OriginX = originX,
                OriginY = originY,
                FlowAdvance = cell.Advance,
                Sideways = true,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
            };
            _pages.AddRun(run);
        }
        else if (cell.Combined)
        {
            var advance = Math.Max(1f, cell.Advance);
            var scale = Math.Min(1f, _context.Options.BaseFontSize / advance);
            var baseline = y + CellPitch / 2f + (cell.Ascent - cell.Descent) / 2f + superscriptShift;
            var originX = columnLeft + (ColumnPitch - advance * scale) / 2f;
            var glyphX = new float[cell.GlyphX.Length];
            for (var i = 0; i < glyphX.Length; i++)
            {
                glyphX[i] = cell.GlyphX[i] * scale;
            }

            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = glyphX,
                Y = Zeroed(cell.GlyphY.Length),
                OriginX = originX,
                OriginY = baseline,
                FlowAdvance = CellPitch,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
                Scale = scale,
            };
            _pages.AddRun(run);
        }
        else
        {
            // Upright single-glyph cell on the fixed grid.
            var baseline = y + CellPitch / 2f + (cell.Ascent - cell.Descent) / 2f + superscriptShift;
            var originX = columnLeft + (ColumnPitch - cell.FontSize) / 2f;
            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = cell.GlyphX.Length > 0 ? new[] { colCenter - cell.FontSize / 2f - originX } : Array.Empty<float>(),
                Y = cell.GlyphY.Length > 0 ? new[] { 0f } : Array.Empty<float>(),
                OriginX = originX,
                OriginY = baseline,
                FlowAdvance = rows * CellPitch,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
            };
            _pages.AddRun(run);
        }

        if (cell.TextStart >= 0 && cell.TextLength > 0)
        {
            _pages.TrackText(cell.TextStart, cell.TextStart + cell.TextLength);
        }

        if (cell.LinkHref is not null || cell.FootnoteHref is not null)
        {
            var top = y;
            var height = cell.Sideways ? Math.Max(CellPitch, cell.Advance) : rows * CellPitch;
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(colCenter - ColumnPitch / 2f, top, colCenter + ColumnPitch / 2f, top + height),
                Href = cell.FootnoteHref ?? cell.LinkHref!,
            });
        }

        if (cell.Style.Underline && cell.TextLength > 0)
        {
            var barX = colCenter - cell.FontSize / 2f - 1.6f;
            var height = cell.Sideways ? cell.Advance : CellPitch;
            _pages.AddDecoration(new PlacedRect
            {
                Kind = DecorationKind.Underline,
                Rect = new SKRect(barX, y, barX + 1.2f, y + height),
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
            });
        }
    }

    private static float[] Zeroed(int length)
    {
        var values = new float[length];
        return values;
    }

    private int[] CloneClusters(LayoutCell cell)
    {
        if (cell.Clusters.Length == 0)
        {
            return Array.Empty<int>();
        }

        var clusters = new int[cell.Clusters.Length];
        Array.Copy(cell.Clusters, clusters, clusters.Length);
        return clusters;
    }

    private int CellRows(LayoutCell cell)
    {
        if (cell.IsLineBreak)
        {
            return 0;
        }

        if (cell.Sideways)
        {
            return Math.Max(1, Math.Min(CharsPerColumn, (int)Math.Ceiling(cell.Advance / CellPitch)));
        }

        return 1;
    }

    /// <summary>
    /// Rewrites upright cells' glyph ids to the font's vertical presentation
    /// forms (OpenType vert). Combined and sideways cells keep their
    /// horizontal shaping.
    /// </summary>
    private void ResolveUprightGlyphs(List<LayoutCell> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsLineBreak || cell.Sideways || cell.Combined || cell.Glyphs.Length != 1)
            {
                continue;
            }

            var glyphId = _context.Shaper.GetVerticalGlyphId(cell.Character ?? '\0', cell.FontPath, out var notdef);
            if (!notdef)
            {
                cells[i] = cell with { Glyphs = new[] { glyphId } };
            }
        }
    }

    private void EnsureColumnSpace()
    {
        if (_cursorX - ColumnPitch < ContentLeft - 0.01f)
        {
            _pages.Next();
            _cursorX = ContentRight;
        }
    }

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
        var maxW = _context.Options.ContentWidth;
        var maxH = Math.Max(1f, ContentHeight - _context.Options.BaseFontSize * 3.6f);
        var scale = Math.Min(1f, Math.Min(maxW / info.Width, maxH / info.Height));
        var w = info.Width * scale;
        var h = info.Height * scale;

        // Images occupy their own page in vertical writing.
        _pages.Next();
        var x = ContentLeft + (_context.Options.ContentWidth - w) / 2f;
        var y = ContentTop + (ContentHeight - h) / 2f;
        _pages.AddImage(new PlacedImage
        {
            Path = item.ImagePath,
            Rect = new SKRect(x, y, x + w, y + h),
            LinkHref = item.LinkHref,
        });
        _pages.Next();
        _cursorX = ContentRight;
    }

    private void PlaceRule()
    {
        EnsureColumnSpace();
        _pages.AddDecoration(new PlacedRect
        {
            Kind = DecorationKind.Rule,
            Rect = new SKRect(ContentLeft, ContentTop + ContentHeight / 2f, ContentRight, ContentTop + ContentHeight / 2f + 1.2f),
        });
        _pages.MarkUsed();
        _cursorX -= ColumnPitch;
    }
}
