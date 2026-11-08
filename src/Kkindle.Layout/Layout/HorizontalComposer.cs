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
        var previousBlockWasHeading = false;
        foreach (var block in content.Blocks)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            switch (block.Kind)
            {
                case BlockKind.Image:
                    PlaceImageBlock(block);
                    break;
                case BlockKind.Rule:
                    PlaceRule(block);
                    break;
                case BlockKind.Table:
                    PlaceTable(block);
                    break;
                default:
                    PlaceTextBlock(block, previousBlockWasHeading);
                    break;
            }

            previousBlockWasHeading = block.Kind == BlockKind.Heading;
        }
    }

    private void PlaceTextBlock(ContentBlock block, bool followsHeading)
    {
        var isHeading = block.Kind == BlockKind.Heading;
        var startsAtPageTop = isHeading
            && !followsHeading
            && (!string.IsNullOrWhiteSpace(block.ElementId)
                || block.FragmentIds.Any(id => !block.FootnoteReferenceFragmentIds.Contains(id)));
        float leftInset = 0f;
        float rightInset = 0f;
        if (block.Kind == BlockKind.Blockquote)
        {
            leftInset = BaseFontSize * 1.1f + 3f;
            rightInset = BaseFontSize * 1.1f;
        }

        // Publisher margins and padding narrow the text column. They are
        // stored in em so the reader's font scale keeps the proportions.
        leftInset += (block.MarginLeftEm + block.PaddingLeftEm) * BaseFontSize;
        rightInset += (block.MarginRightEm + block.PaddingRightEm) * BaseFontSize;

        var availWidth = Math.Max(1f, ContentWidth - leftInset - rightInset);
        var blockLeft = InsetH + leftInset;

        var cells = new List<LayoutCell>();
        foreach (var item in block.Items)
        {
            cells.AddRange(_cells.BuildCells(item, vertical: false));
        }
        CellFactory.CollapseWhitespace(cells);
        // A publisher line-height replaces the reader default for this block,
        // but never below the glyphs' own ink extent (or they would overlap).
        // Publisher font-size scales the pitch so an enlarged span or heading
        // keeps its proportion.
        var fontScale = Math.Clamp(
            cells.Select(cell => cell.Style.FontSizeFactor > 0f ? cell.Style.FontSizeFactor : 1f)
                .DefaultIfEmpty(1f)
                .Max(),
            0.5f,
            3f);
        var inkExtent = cells
            .Select(cell => cell.Ascent + cell.Descent)
            .DefaultIfEmpty(0f)
            .Max();
        var lineHeight = block.LineHeightFactor > 0f
            ? Math.Max(BaseFontSize * fontScale * block.LineHeightFactor, inkExtent + 1f)
            : isHeading
                ? Math.Max(BaseFontSize * fontScale * 1.35f, inkExtent + 1f)
                : Math.Max(BodyLineHeight * fontScale, inkExtent);
        lineHeight = Math.Max(lineHeight, cells
            .Where(cell => cell.ImagePath is not null)
            .Select(cell => cell.ImageHeight)
            .DefaultIfEmpty(0f)
            .Max());
        var rubyGap = _context.RubyGap;
        var rubyShelf = cells
            .Where(cell => cell.RubyAnnotation is not null)
            .Select(cell => cell.RubyAnnotation!.Ascent + cell.RubyAnnotation.Descent + rubyGap)
            .DefaultIfEmpty(0f)
            .Max();
        if (rubyShelf > 0f)
        {
            // Move the base line down by the full annotation shelf. Merely
            // increasing lineHeight leaves the baseline centered in the old
            // position, which makes the larger ruby overlap the base text.
            lineHeight += rubyShelf;
        }

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
            _cursorY += block.SpaceBeforeLines * BodyLineHeight
                + block.MarginTopEm * BaseFontSize;
        }

        var lines = BreakLines(cells, availWidth, block.TextIndentEm * BaseFontSize);

        var lineIndex = 0;
        while (lineIndex < lines.Count)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (_cursorY + lineHeight > ContentBottom + 0.01f)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }

            var capacity = (int)Math.Floor((ContentBottom + 0.01f - _cursorY) / lineHeight);
            if (capacity <= 0)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
                capacity = Math.Max(
                    1,
                    (int)Math.Floor((ContentBottom + 0.01f - _cursorY) / lineHeight));
            }

            var remaining = lines.Count - lineIndex;
            if (capacity >= remaining)
            {
                capacity = remaining;
            }
            else
            {
                // Widow: a single line stranded at the bottom of a page is
                // moved with its paragraph to the next page when possible.
                if (capacity == 1
                    && remaining > 1
                    && _pages.CurrentPageHasContent)
                {
                    _pages.NextIfUsed();
                    _cursorY = InsetV;
                    continue;
                }

                // Orphan: leave at least two lines for the following page
                // instead of a single line starting a new page. When only two
                // lines fit, reducing further would create a widow instead.
                if (capacity >= 3 && remaining - capacity == 1)
                {
                    capacity--;
                }
            }

            for (var placed = 0; placed < capacity; placed++, lineIndex++)
            {
                var line = lines[lineIndex];
                if (lineIndex == 0)
                {
                    _pages.RecordFragment(block.ElementId);
                    _pages.RecordFragments(block.FragmentIds);
                }

                var baseline = _cursorY
                    + lineHeight / 2f
                    + (line.Ascent - line.Descent) / 2f
                    + rubyShelf / 2f;

                var startX = blockLeft + line.Indent;
                var isLastLine = lineIndex == lines.Count - 1 || line.EndsWithForcedBreak;
                if (isHeading || block.Center)
                {
                    startX = blockLeft + Math.Max(0f, (availWidth - line.Used) / 2f);
                }
                else if (block.Justify && !isLastLine)
                {
                    var extra = availWidth - line.Indent - line.Used;
                    if (extra > 0f)
                    {
                        line.TryJustify(extra, BaseFontSize);
                    }
                }

                PlaceLine(line, startX, baseline, _cursorY, lineHeight, rubyGap);
                _cursorY += lineHeight;
            }

            if (lineIndex < lines.Count)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        _cursorY += block.SpaceAfterLines * BodyLineHeight
            + block.MarginBottomEm * BaseFontSize;
        if (block.Kind == BlockKind.Paragraph)
        {
            _cursorY += _context.Options.ParagraphSpacingEm * BaseFontSize;
        }
    }

    private void PlaceLine(
        LineBox line,
        float startX,
        float baseline,
        float lineTop,
        float lineHeight,
        float rubyGap)
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

            var baseInset = cell.RubyAnnotation is { } ruby
                ? (Math.Max(cell.RubyBaseAdvance, ruby.Advance + 2f * rubyGap) - cell.RubyBaseAdvance) / 2f
                : 0f;
            run.BeginCell(cell, baseInset);
            for (var i = 0; i < cell.Glyphs.Length; i++)
            {
                var cluster = i < cell.Clusters.Length ? cell.Clusters[i] : i;
                run.AddGlyph(
                    cell.Glyphs[i],
                    penX + baseInset + cell.GlyphX[i],
                    cell.GlyphY[i],
                    cluster,
                    cell.GlyphInkShiftX is { } shifts && i < shifts.Length ? shifts[i] : 0f);
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

            if (cell.RubyAnnotation is not null)
            {
                run?.Flush(_pages);
                run = null;
                PlaceRubyAnnotation(cell, penX, baseline, rubyGap);
            }

            penX += cell.Advance;
        }

        run?.Flush(_pages);
    }

    private void PlaceRubyAnnotation(
        LayoutCell baseCell,
        float baseLeft,
        float baseline,
        float rubyGap)
    {
        var annotation = baseCell.RubyAnnotation!;
        if (annotation.Glyphs.Length == 0)
        {
            return;
        }

        _pages.AddRun(new PlacedRun
        {
            FontPath = annotation.FontPath,
            FontSize = annotation.FontSize,
            Glyphs = annotation.Glyphs,
            X = annotation.GlyphX,
            Y = annotation.GlyphY,
            // Justification adds space after this unit. It must not move the
            // annotation away from the base's original center.
            OriginX = baseLeft
                + (Math.Max(baseCell.RubyBaseAdvance, annotation.Advance + 2f * rubyGap) - annotation.Advance) / 2f,
            OriginY = baseline - baseCell.Ascent - annotation.Descent - rubyGap,
            FlowAdvance = annotation.Advance,
            TextStart = -1,
            TextLength = 0,
            Clusters = Array.Empty<int>(),
            Style = annotation.Style,
        });
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
            _context.CancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Lays a publisher table out as real columns. Column widths follow the
    /// cells' natural text widths, cells wrap independently, and a header row
    /// is kept with its first data row. Very narrow tables and vertical
    /// writing fall back to the flattened text flow carried on
    /// <see cref="ContentBlock.Items"/>.
    /// </summary>
    private void PlaceTable(ContentBlock block)
    {
        if (block.Table is not { Rows.Count: > 0 } table)
        {
            PlaceTextBlock(block, false);
            return;
        }

        // An upper bound on the grid width, before rowspans are resolved; the
        // real column count comes back from the grid simulation below.
        var capacity = table.Rows
            .Select(row => row.Cells.Sum(cell => Math.Max(1, cell.ColSpan)))
            .DefaultIfEmpty(0)
            .Max();
        if (capacity < 2)
        {
            PlaceTextBlock(block, false);
            return;
        }

        var leftInset = (block.MarginLeftEm + block.PaddingLeftEm) * BaseFontSize;
        var rightInset = (block.MarginRightEm + block.PaddingRightEm) * BaseFontSize;
        var availWidth = Math.Max(1f, ContentWidth - leftInset - rightInset);
        var blockLeft = InsetH + leftInset;

        var cellPadX = BaseFontSize * 0.35f;
        var cellPadY = BaseFontSize * 0.25f;
        var columnGap = BaseFontSize * 0.4f;
        var minColumnWidth = BaseFontSize * 2.2f;
        if (minColumnWidth * capacity + columnGap * (capacity - 1) > availWidth)
        {
            // Many columns on a phone: readable prose beats unreadable slivers.
            PlaceTextBlock(block, false);
            return;
        }

        var measuredRows = MeasureTableRows(block, table, capacity);
        var columnCount = 0;
        var hasBorders = false;
        foreach (var row in measuredRows)
        {
            foreach (var cell in row.Cells)
            {
                columnCount = Math.Max(columnCount, cell.Column + cell.Span);
                hasBorders |= cell.HasBorder;
            }
        }

        if (columnCount < 2)
        {
            PlaceTextBlock(block, false);
            return;
        }

        var gaps = columnGap * (columnCount - 1);
        if (minColumnWidth * columnCount + gaps > availWidth)
        {
            PlaceTextBlock(block, false);
            return;
        }

        var columnWidths = FitTableColumns(
            measuredRows, columnCount, availWidth, gaps, minColumnWidth, cellPadX);
        if (columnWidths is null)
        {
            PlaceTextBlock(block, false);
            return;
        }

        LayOutTableCells(measuredRows, columnWidths, columnGap, cellPadX);
        SetRowHeights(measuredRows, cellPadY);

        _cursorY += block.SpaceBeforeLines * BodyLineHeight
            + block.MarginTopEm * BaseFontSize;
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);

        var groups = BuildRowGroups(measuredRows);
        var rowTops = new float[measuredRows.Count];

        // Keep a header row together with the first data row instead of
        // stranding the header alone at the bottom of a page. Rows tied
        // together by rowspans form one group and move as a unit.
        if (groups.Count > 1)
        {
            var keepWith = GroupHeight(measuredRows, groups[0])
                + GroupHeight(measuredRows, groups[1]);
            if (_cursorY + keepWith > ContentBottom + 0.01f
                && _pages.CurrentPageHasContent)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        foreach (var (start, end) in groups)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var groupHeight = 0f;
            for (var rowIndex = start; rowIndex <= end; rowIndex++)
            {
                groupHeight += measuredRows[rowIndex].Height;
            }

            if (_cursorY + groupHeight > ContentBottom + 0.01f
                && _pages.CurrentPageHasContent)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }

            var top = _cursorY;
            for (var rowIndex = start; rowIndex <= end; rowIndex++)
            {
                rowTops[rowIndex] = top;
                top += measuredRows[rowIndex].Height;
            }

            for (var rowIndex = start; rowIndex <= end; rowIndex++)
            {
                var row = measuredRows[rowIndex];
                var rowTop = rowTops[rowIndex];
                foreach (var cell in row.Cells)
                {
                    var lastRow = Math.Min(measuredRows.Count - 1, rowIndex + cell.RowSpan - 1);
                    var cellBottom = rowTop;
                    for (var spanned = rowIndex; spanned <= lastRow; spanned++)
                    {
                        cellBottom += measuredRows[spanned].Height;
                    }

                    PlaceTableCell(
                        block, blockLeft, rowTop, cellBottom, cell, cellPadX, cellPadY);
                }

                if (!hasBorders && rowIndex < measuredRows.Count - 1)
                {
                    var ruleY = rowTop + row.Height;
                    _pages.AddDecoration(new PlacedRect
                    {
                        Kind = DecorationKind.Rule,
                        Rect = new SKRect(blockLeft, ruleY - 0.6f, blockLeft + availWidth, ruleY + 0.6f),
                    });
                }
            }

            _cursorY = top;
        }

        _cursorY += block.SpaceAfterLines * BodyLineHeight
            + block.MarginBottomEm * BaseFontSize;
    }

    private void PlaceTableCell(
        ContentBlock block,
        float blockLeft,
        float rowTop,
        float cellBottom,
        MeasuredCell cell,
        float cellPadX,
        float cellPadY)
    {
        var lineTop = rowTop + cellPadY;
        for (var lineIndex = 0; lineIndex < cell.Lines.Count; lineIndex++)
        {
            var line = cell.Lines[lineIndex];
            var baseline = lineTop + cell.LineHeight / 2f
                + (line.Ascent - line.Descent) / 2f;
            if (block.Justify && lineIndex < cell.Lines.Count - 1)
            {
                var extra = cell.TextWidth - line.Indent - line.Used;
                if (extra > 0f)
                {
                    line.TryJustify(extra, BaseFontSize);
                }
            }

            PlaceLine(
                line,
                blockLeft + cell.X + cellPadX + line.Indent,
                baseline,
                lineTop,
                cell.LineHeight,
                _context.RubyGap);
            lineTop += cell.LineHeight;
        }

        if (!cell.HasBorder)
        {
            return;
        }

        var left = blockLeft + cell.X;
        var right = left + cell.Width;
        DrawCellBorders(left, rowTop, right, cellBottom, cell);
    }

    private void DrawCellBorders(float left, float top, float right, float bottom, MeasuredCell cell)
    {
        var maxThickness = BaseFontSize * 0.5f;
        DrawBorderSide(left, top, right, top, cell.BorderTop);
        DrawBorderSide(right, top, right, bottom, cell.BorderRight);
        DrawBorderSide(left, bottom, right, bottom, cell.BorderBottom);
        DrawBorderSide(left, top, left, bottom, cell.BorderLeft);

        void DrawBorderSide(float x1, float y1, float x2, float y2, float widthEm)
        {
            if (widthEm <= 0f)
            {
                return;
            }

            var thickness = Math.Clamp(widthEm * BaseFontSize, 0.6f, maxThickness);
            // Keep the rule inside the cell rectangle on both axes.
            var half = thickness / 2f;
            var rect = y1 == y2
                ? new SKRect(x1, y1 - half, x2, y1 + half)
                : new SKRect(x1 - half, y1, x1 + half, y2);
            _pages.AddDecoration(new PlacedRect { Kind = DecorationKind.Rule, Rect = rect });
        }
    }

    private static List<(int Start, int End)> BuildRowGroups(List<MeasuredRow> rows)
    {
        var groups = new List<(int Start, int End)>();
        var start = 0;
        while (start < rows.Count)
        {
            var end = start;
            for (var scan = start; scan <= end; scan++)
            {
                foreach (var cell in rows[scan].Cells)
                {
                    end = Math.Max(end, scan + Math.Max(1, cell.RowSpan) - 1);
                }
            }

            end = Math.Min(end, rows.Count - 1);
            groups.Add((start, end));
            start = end + 1;
        }

        return groups;
    }

    private static float GroupHeight(List<MeasuredRow> rows, (int Start, int End) group)
    {
        var height = 0f;
        for (var index = group.Start; index <= group.End; index++)
        {
            height += rows[index].Height;
        }

        return height;
    }

    /// <summary>Table cell after shaping, before column widths are known.</summary>
    private sealed class MeasuredCell
    {
        public required IReadOnlyList<InlineItem> Items { get; init; }
        public required List<LayoutCell> Cells { get; set; }
        public required int Row { get; init; }
        public required int RowSpan { get; init; }
        public required int Column { get; init; }
        public required int Span { get; init; }
        public required float NaturalWidth { get; init; }
        public required float LineHeight { get; init; }
        public required float BorderTop { get; init; }
        public required float BorderRight { get; init; }
        public required float BorderBottom { get; init; }
        public required float BorderLeft { get; init; }
        public float X { get; set; }
        public float Width { get; set; }
        public float TextWidth { get; set; }
        public List<LineBox> Lines { get; set; } = [];

        public bool HasBorder =>
            BorderTop > 0f || BorderRight > 0f || BorderBottom > 0f || BorderLeft > 0f;
    }

    private sealed class MeasuredRow
    {
        public required List<MeasuredCell> Cells { get; init; }
        public float Height { get; set; }
    }

    private List<MeasuredRow> MeasureTableRows(
        ContentBlock block,
        ContentTable table,
        int capacity)
    {
        var rows = new List<MeasuredRow>(table.Rows.Count);
        // HTML tables are positional: a rowspan cell removes a slot from later
        // rows, so the grid has to be simulated before columns are numbered.
        var occupied = new HashSet<long>();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var cells = new List<MeasuredCell>(row.Cells.Count);
            var column = 0;
            foreach (var cell in row.Cells)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                while (column < capacity && occupied.Contains(GridKey(rowIndex, column)))
                {
                    column++;
                }

                if (column >= capacity)
                {
                    break;
                }

                var span = Math.Clamp(Math.Max(1, cell.ColSpan), 1, capacity - column);
                while (span > 1 && IsOccupied(occupied, rowIndex, column, span))
                {
                    span--;
                }

                var rowSpan = Math.Clamp(
                    Math.Max(1, cell.RowSpan), 1, table.Rows.Count - rowIndex);
                for (var spannedRow = rowIndex; spannedRow < rowIndex + rowSpan; spannedRow++)
                {
                    for (var spannedColumn = column;
                         spannedColumn < column + span;
                         spannedColumn++)
                    {
                        occupied.Add(GridKey(spannedRow, spannedColumn));
                    }
                }

                var cellCells = new List<LayoutCell>();
                foreach (var item in cell.Items)
                {
                    cellCells.AddRange(_cells.BuildCells(item, vertical: false));
                }

                CellFactory.CollapseWhitespace(cellCells);
                var inkExtent = cellCells
                    .Select(candidate => candidate.Ascent + candidate.Descent)
                    .DefaultIfEmpty(0f)
                    .Max();
                var cellScale = Math.Clamp(
                    cellCells
                        .Select(candidate => candidate.Style.FontSizeFactor > 0f
                            ? candidate.Style.FontSizeFactor
                            : 1f)
                        .DefaultIfEmpty(1f)
                        .Max(),
                    0.5f,
                    3f);
                var lineHeight = block.LineHeightFactor > 0f
                    ? Math.Max(BaseFontSize * cellScale * block.LineHeightFactor, inkExtent + 1f)
                    : Math.Max(BaseFontSize * cellScale * 1.45f, inkExtent + 1f);
                lineHeight = Math.Max(lineHeight, cellCells
                    .Where(candidate => candidate.ImagePath is not null)
                    .Select(candidate => candidate.ImageHeight)
                    .DefaultIfEmpty(0f)
                    .Max());

                cells.Add(new MeasuredCell
                {
                    Items = cell.Items,
                    Cells = cellCells,
                    Row = rowIndex,
                    RowSpan = rowSpan,
                    Column = column,
                    Span = span,
                    NaturalWidth = cellCells.Sum(candidate => candidate.Advance),
                    LineHeight = lineHeight,
                    BorderTop = cell.BorderTopEm,
                    BorderRight = cell.BorderRightEm,
                    BorderBottom = cell.BorderBottomEm,
                    BorderLeft = cell.BorderLeftEm, // copied from the content cell
                });
                column += span;
            }

            rows.Add(new MeasuredRow { Cells = cells });
        }

        return rows;
    }

    private static long GridKey(int row, int column) => ((long)row << 32) | (uint)column;

    private static bool IsOccupied(
        HashSet<long> occupied,
        int row,
        int column,
        int span)
    {
        for (var offset = 0; offset < span; offset++)
        {
            if (occupied.Contains(GridKey(row, column + offset)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Auto table layout: start from each column's widest natural cell, then
    /// either distribute spare width proportionally or shrink toward a minimum
    /// so the table always fits the content box. Returns null when even the
    /// minimum widths cannot fit.
    /// </summary>
    private static float[]? FitTableColumns(
        List<MeasuredRow> rows,
        int columnCount,
        float availWidth,
        float gaps,
        float minColumnWidth,
        float cellPadX)
    {
        var weights = new float[columnCount];
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                var share = cell.NaturalWidth / cell.Span;
                for (var offset = 0; offset < cell.Span; offset++)
                {
                    var column = cell.Column + offset;
                    weights[column] = Math.Max(weights[column], share);
                }
            }
        }

        var desired = new float[columnCount];
        var total = 0f;
        for (var column = 0; column < columnCount; column++)
        {
            desired[column] = Math.Max(minColumnWidth, weights[column] + cellPadX * 2f);
            total += desired[column];
        }

        var available = availWidth - gaps;
        if (available <= 0f)
        {
            return null;
        }

        if (total > available)
        {
            var minimum = minColumnWidth * columnCount;
            if (minimum > available)
            {
                return null;
            }

            // Water-filling: narrow columns keep their natural width and only
            // the widest ones shrink, so a short "数量" column never wraps
            // while a long prose column gives up space.
            var low = minColumnWidth;
            var high = desired.Max();
            for (var iteration = 0; iteration < 24; iteration++)
            {
                var limit = (low + high) / 2f;
                var sum = 0f;
                for (var column = 0; column < columnCount; column++)
                {
                    sum += Math.Clamp(desired[column], minColumnWidth, limit);
                }

                if (sum > available)
                {
                    high = limit;
                }
                else
                {
                    low = limit;
                }
            }

            var waterLevel = (low + high) / 2f;
            for (var column = 0; column < columnCount; column++)
            {
                desired[column] = Math.Clamp(desired[column], minColumnWidth, waterLevel);
            }
        }
        else if (total < available)
        {
            // Let wide tables breathe; narrow ones share the spare width in
            // proportion to their natural size.
            var extra = available - total;
            for (var column = 0; column < columnCount; column++)
            {
                desired[column] += extra * (desired[column] / total);
            }
        }

        return desired;
    }

    private void LayOutTableCells(
        List<MeasuredRow> rows,
        float[] columnWidths,
        float columnGap,
        float cellPadX)
    {
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                var x = 0f;
                var width = 0f;
                for (var column = 0; column < cell.Column; column++)
                {
                    x += columnWidths[column] + columnGap;
                }

                for (var offset = 0; offset < cell.Span; offset++)
                {
                    width += columnWidths[cell.Column + offset];
                }

                width += columnGap * (cell.Span - 1);
                cell.X = x;
                cell.Width = width;
                cell.TextWidth = Math.Max(1f, width - cellPadX * 2f + 2f);
                cell.Lines = BreakLines(cell.Cells, cell.TextWidth, 0f);

                // A word can be narrower than the page but wider than a narrow
                // table column. Rebuild just that cell with the column width
                // as the splitting limit so long English words wrap inside
                // the cell instead of spilling into its neighbour.
                var overflow = cell.Lines.Any(line => line.Used > cell.TextWidth + 0.01f);
                var hasWideWord = cell.Cells.Any(candidate =>
                    candidate.Advance > cell.TextWidth + 0.01f
                    && candidate.Text.Length > 1
                    && candidate.Text.Any(char.IsAsciiLetterOrDigit));
                if (overflow && hasWideWord)
                {
                    var rebuilt = new List<LayoutCell>();
                    foreach (var item in cell.Items)
                    {
                        rebuilt.AddRange(_cells.BuildCells(
                            item, vertical: false, maxWordWidth: cell.TextWidth));
                    }

                    CellFactory.CollapseWhitespace(rebuilt);
                    if (rebuilt.Count > 0)
                    {
                        cell.Cells = rebuilt;
                        cell.Lines = BreakLines(rebuilt, cell.TextWidth, 0f);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves row heights. Single-row cells set their row directly; a cell
    /// spanning several rows can only grow the last row it covers, which is
    /// how browsers distribute the extra height when no explicit height is set.
    /// </summary>
    private static void SetRowHeights(List<MeasuredRow> rows, float cellPadY)
    {
        foreach (var row in rows)
        {
            row.Height = 0f;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            foreach (var cell in rows[rowIndex].Cells)
            {
                var contentHeight = cell.Lines.Count * cell.LineHeight + cellPadY * 2f;
                if (cell.RowSpan == 1)
                {
                    rows[rowIndex].Height = Math.Max(rows[rowIndex].Height, contentHeight);
                }
            }

            rows[rowIndex].Height = Math.Max(rows[rowIndex].Height, cellPadY * 2f);
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            foreach (var cell in rows[rowIndex].Cells)
            {
                if (cell.RowSpan <= 1)
                {
                    continue;
                }

                var end = Math.Min(rows.Count - 1, rowIndex + cell.RowSpan - 1);
                var current = 0f;
                for (var spanned = rowIndex; spanned <= end; spanned++)
                {
                    current += rows[spanned].Height;
                }

                var required = cell.Lines.Count * cell.LineHeight + cellPadY * 2f;
                if (required > current)
                {
                    rows[end].Height += required - current;
                }
            }
        }
    }

    /// <summary>A laid-out line of cells.</summary>
    private sealed class LineBox
    {
        public List<LayoutCell> Cells { get; } = new();
        public float Used { get; private set; }
        public float Ascent { get; private set; }
        public float Descent { get; private set; }
        public float Indent { get; set; }
        public bool EndsWithForcedBreak { get; set; }

        public void Add(LayoutCell cell)
        {
            Cells.Add(cell);
            Used += cell.Advance;
            Ascent = Math.Max(Ascent, cell.Ascent);
            Descent = Math.Max(Descent, cell.Descent);
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
            Ascent = 0f;
            Descent = 0f;
            foreach (var cell in Cells)
            {
                Ascent = Math.Max(Ascent, cell.Ascent);
                Descent = Math.Max(Descent, cell.Descent);
            }
        }

        /// <summary>
        /// Distributes <paramref name="extra"/> pixels. Latin-majority lines
        /// only expand their word spaces: expanding every glyph gap (the old
        /// behavior) pulled English words apart and produced the tell-tale
        /// "loose line" when a long word wrapped. When even the word spaces
        /// would need more than a readable amount, the line is left ragged.
        /// CJK and mixed lines keep inter-character justification, where the
        /// per-gap amount is spread across enough characters to stay subtle
        /// even inside an embedded Latin word.
        /// </summary>
        public bool TryJustify(float extra, float fontSize)
        {
            if (extra <= 0.01f)
            {
                return true;
            }

            var end = Cells.Count;
            while (end > 0 && Cells[end - 1].IsSpace)
            {
                end--;
            }

            if (end <= 1)
            {
                return true;
            }

            var latinGlyphs = 0;
            var inkGlyphs = 0;
            var totalGlyphs = 0;
            for (var index = 0; index < end; index++)
            {
                var cell = Cells[index];
                var glyphCount = cell.JustifyAsUnit
                    ? 1
                    : Math.Max(1, cell.Glyphs.Length);
                totalGlyphs += glyphCount;
                if (cell.IsSpace)
                {
                    continue;
                }

                inkGlyphs += glyphCount;
                if (cell.Text.Length > 1 && cell.Text.Any(char.IsAsciiLetter))
                {
                    latinGlyphs += glyphCount;
                }
            }

            // Only a line whose visible ink is majority Latin takes the
            // space-only path. A Chinese sentence with an English name or an
            // acronym is still justified between characters, which keeps the
            // right edge flush instead of leaving it conspicuously ragged.
            if (latinGlyphs > 0 && latinGlyphs * 2 >= inkGlyphs)
            {
                var spaceCount = 0;
                for (var index = 0; index < end; index++)
                {
                    if (Cells[index].IsSpace)
                    {
                        spaceCount++;
                    }
                }

                if (spaceCount == 0
                    || extra / spaceCount > fontSize * 1.2f)
                {
                    return false;
                }

                var perSpace = extra / spaceCount;
                for (var index = 0; index < end; index++)
                {
                    var cell = Cells[index];
                    if (cell.IsSpace)
                    {
                        // A space cell contains one glyph; growing its advance
                        // widens the word gap without touching the letters.
                        Cells[index] = cell with { Advance = cell.Advance + perSpace };
                    }
                }

                Used += extra;
                return true;
            }

            var gaps = totalGlyphs - 1;
            if (gaps <= 0)
            {
                return true;
            }

            var perGap = extra / gaps;
            if (perGap > fontSize * 1.5f)
            {
                return false;
            }

            for (var index = 0; index < end; index++)
            {
                var cell = Cells[index];
                var shiftedX = new float[cell.GlyphX.Length];
                if (!cell.JustifyAsUnit)
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
                    // Markers and ruby bases stay intact. Justification adds
                    // space after the unit, not between its own glyphs.
                    Array.Copy(cell.GlyphX, shiftedX, cell.GlyphX.Length);
                }

                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                var gapsAfter = index == end - 1
                    ? cell.JustifyAsUnit ? 0 : Math.Max(0, glyphCount - 1)
                    : cell.JustifyAsUnit ? 1 : glyphCount;
                Cells[index] = cell with
                {
                    GlyphX = shiftedX,
                    Advance = cell.Advance + perGap * gapsAfter,
                };
            }

            Used += extra;
            return true;
        }
    }

    /// <summary>Accumulates consecutive compatible cells into one placed run.</summary>
    private sealed class RunAccumulator
    {
        private readonly float _baseline;
        private readonly List<ushort> _glyphs = new();
        private readonly List<float> _x = new();
        private readonly List<float> _y = new();
        private readonly List<float> _inkShiftX = new();
        private readonly List<int> _clusters = new();
        private LayoutCell? _first;
        private LayoutCell? _last;
        private int _cellClusterBase;
        private float _flowAdvance;
        private float _leadingInset;

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

        public void BeginCell(LayoutCell cell, float leadingInset)
        {
            if (_first is null) _leadingInset = leadingInset;
            _first ??= cell;
            _last = cell;
            _cellClusterBase = _first.TextStart >= 0 && cell.TextStart >= 0
                ? cell.TextStart - _first.TextStart
                : 0;
            _flowAdvance += cell.Advance;
        }

        public void AddGlyph(ushort glyph, float x, float y, int cluster, float inkShiftX = 0f)
        {
            _glyphs.Add(glyph);
            _x.Add(x);
            _y.Add(y);
            _inkShiftX.Add(inkShiftX);
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
                InkShiftX = _inkShiftX.Any(shift => shift != 0f) ? _inkShiftX.ToArray() : null,
                OriginX = originX,
                OriginY = _baseline,
                Ascent = _first.Ascent,
                Descent = _first.Descent,
                FlowAdvance = _flowAdvance - _leadingInset,
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
            _leadingInset = 0f;
            _glyphs.Clear();
            _x.Clear();
            _y.Clear();
            _inkShiftX.Clear();
            _clusters.Clear();
        }
    }
}
