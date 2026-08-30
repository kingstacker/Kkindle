using SkiaSharp;

namespace Kkindle.Layout;

/// <summary>
/// Lays a chapter out as vertical-rl pages: each column is one line of text,
/// ordinary characters flow top-to-bottom inside a fixed 1-em cell grid, and
/// sideways runs use their measured flow extent. Columns advance right-to-left.
/// The coordinate mapping is owned by this engine — there is no writing-mode
/// engine underneath to fight.
///
/// Grid rules carried over from the WebKit reader: one line pitch per column,
/// flush paragraph starts, headings bold and centered with one empty column
/// before and after, and opening punctuation never ending a column. Text is
/// always kept inside the content box; a full column continues in the next
/// column instead of hanging punctuation into the bottom page margin.
/// </summary>
internal sealed class VerticalComposer
{
    private readonly record struct ColumnPlacement(
        LayoutCell Cell,
        float FlowAdvance,
        float TopHang);

    private readonly ComposerContext _context;
    private readonly CellFactory _cells;
    private readonly PageBuilder _pages;
    private readonly Dictionary<(string FontPath, float FontSize, ushort GlyphId), SKRect> _glyphBoundsCache = new();
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
        CellFactory.CollapseWhitespace(cells);

        ResolveUprightGlyphs(cells);

        if (startsAtPageTop)
        {
            // Fragment-backed headings are the destinations of TOC entries.
            // Start them in the first column of a fresh vertical page instead
            // of leaving earlier paragraph columns above the target.
            _pages.NextIfUsed();
            _cursorX = ContentRight;
        }
        else
        {
            _cursorX -= block.SpaceBeforeLines * ColumnPitch;
        }
        _cursorX -= rightInset;

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
                _pages.RecordFragments(block.FragmentIds);
                firstColumnOfBlock = false;
            }

            var columnLeft = _cursorX - ColumnPitch;
            var colCenter = _cursorX - ColumnPitch / 2f;
            _lastColumnFirstRun = _pages.Current.Runs.Count;
            _lastColumnFirstDecoration = _pages.Current.Decorations.Count;
            _lastColumnFirstHotZone = _pages.Current.HotZones.Count;
            _lastColumnFirstImage = _pages.Current.Images.Count;
            _lastColumnFirstDebugBox = _pages.Current.DebugBoxes.Count;
            var (placed, flowUsed) = FillColumn(
                cells,
                index,
                colCenter,
                columnLeft,
                centerVertically: isHeading && !startsAtPageTop);

            if (block.Kind == BlockKind.Blockquote && flowUsed > 0)
            {
                quoteRight = Math.Max(quoteRight, _cursorX);
                quoteLeft = Math.Min(quoteLeft, columnLeft);
            }

            index = placed;
            _cursorX -= ColumnPitch;
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
    /// unplaced index and the physical flow extent consumed.
    /// </summary>
    private (int Next, float FlowUsed) FillColumn(
        List<LayoutCell> cells,
        int start,
        float colCenter,
        float columnLeft,
        bool centerVertically)
    {
        var capacity = ContentHeight;
        var placements = new List<ColumnPlacement>();
        var flowUsed = 0f;
        var index = start;
        var forcedBreak = false;

        while (index < cells.Count)
        {
            var cell = cells[index];
            if (cell.IsLineBreak)
            {
                index++;
                forcedBreak = true;
                break;
            }

            if (cell.IsSpace && placements.Count == 0)
            {
                // A wrapped/collapsed whitespace run must not create an
                // otherwise empty leading row in the next vertical column.
                index++;
                continue;
            }

            var flowAdvance = CellFlowAdvance(cell);

            if (flowUsed + flowAdvance > capacity + 0.01f)
            {
                break;
            }

            placements.Add(new ColumnPlacement(
                cell,
                flowAdvance,
                placements.Count == 0 ? GetTopHang(cell) : 0f));
            flowUsed += flowAdvance;
            index++;
        }

        // The greedy fit above deliberately does not make a local decision at
        // every character. Resolve the actual boundary now, moving a complete
        // trailing cluster to the next column until both sides are legal. This
        // handles arbitrary runs of closers and numeric affixes instead of the
        // old four-character look-ahead heuristic.
        if (!forcedBreak)
        {
            while (placements.Count > 1 && index < cells.Count && !cells[index].IsLineBreak)
            {
                var last = placements[^1];
                var next = cells[index];
                if (!TypesetText.IsProhibitedAtLineEnd(last.Cell.Text)
                    && !TypesetText.IsProhibitedAtLineStart(next.Text)
                    && !TypesetText.ShouldKeepTogether(last.Cell.Text, next.Text))
                {
                    break;
                }

                placements.RemoveAt(placements.Count - 1);
                index--;
                flowUsed -= last.FlowAdvance;
            }
        }

        // A pathological unbreakable object can be wider than the whole
        // column. Always consume one cell so malformed EPUB content cannot
        // make pagination loop forever. Normal long Latin/numeric runs are
        // split into grapheme-safe cells by CellFactory before reaching here.
        if (placements.Count == 0 && index < cells.Count && !cells[index].IsLineBreak)
        {
            var cell = cells[index];
            var flowAdvance = Math.Min(Math.Max(CellFlowAdvance(cell), CellPitch), capacity);
            placements.Add(new ColumnPlacement(
                cell,
                flowAdvance,
                GetTopHang(cell)));
            flowUsed = flowAdvance;
            index++;
        }

        var y = ContentTop;
        foreach (var placement in placements)
        {
            PlaceCell(
                placement.Cell,
                colCenter,
                columnLeft,
                y,
                placement.FlowAdvance,
                placement.TopHang);
            y += placement.FlowAdvance;
        }

        if (centerVertically && flowUsed > 0f && flowUsed < capacity)
        {
            // Headings center within the column: shift the placed cells down
            // by half the leftover space.
            var shift = (capacity - flowUsed) / 2f;
            ShiftPlacedDown(shift);
        }

        return (index, flowUsed);
    }

    private int _lastColumnFirstRun;
    private int _lastColumnFirstDecoration;
    private int _lastColumnFirstHotZone;
    private int _lastColumnFirstImage;
    private int _lastColumnFirstDebugBox;

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

        for (var i = _lastColumnFirstDecoration; i < _pages.Current.Decorations.Count; i++)
        {
            var decoration = _pages.Current.Decorations[i];
            _pages.Current.Decorations[i] = new PlacedRect
            {
                Kind = decoration.Kind,
                Rect = OffsetY(decoration.Rect, shift),
                TextStart = decoration.TextStart,
                TextLength = decoration.TextLength,
            };
        }

        for (var i = _lastColumnFirstHotZone; i < _pages.Current.HotZones.Count; i++)
        {
            var zone = _pages.Current.HotZones[i];
            _pages.Current.HotZones[i] = new PlacedHotZone
            {
                Kind = zone.Kind,
                Rect = OffsetY(zone.Rect, shift),
                Href = zone.Href,
                FootnoteText = zone.FootnoteText,
            };
        }

        for (var i = _lastColumnFirstImage; i < _pages.Current.Images.Count; i++)
        {
            var image = _pages.Current.Images[i];
            _pages.Current.Images[i] = new PlacedImage
            {
                Path = image.Path,
                Rect = OffsetY(image.Rect, shift),
                RotationDegrees = image.RotationDegrees,
                LinkHref = image.LinkHref,
                FootnoteHref = image.FootnoteHref,
                FootnoteText = image.FootnoteText,
            };
        }

        for (var i = _lastColumnFirstDebugBox; i < _pages.Current.DebugBoxes.Count; i++)
        {
            var box = _pages.Current.DebugBoxes[i];
            _pages.Current.DebugBoxes[i] = box with { Rect = OffsetY(box.Rect, shift) };
        }
    }

    private static SKRect OffsetY(SKRect rect, float amount) =>
        new(rect.Left, rect.Top + amount, rect.Right, rect.Bottom + amount);

    private float GetTopHang(LayoutCell cell) =>
        TypesetText.IsProhibitedAtLineStart(cell.Text)
        && TypesetText.IsHangablePunctuation(cell.Text)
            ? Math.Min(CellPitch * 0.5f, Math.Max(1f, CellPitch - 1f))
            : 0f;

    private void PlaceCell(
        LayoutCell cell,
        float colCenter,
        float columnLeft,
        float y,
        float flowAdvance,
        float topHang = 0f)
    {
        var paintY = y - topHang;
        var superscriptShift = cell.Superscript
            ? -(cell.FootnoteMarker ? _context.Options.BaseFontSize * 0.30f : cell.FontSize * 0.35f)
            : 0f;

        if (!cell.IsSpace)
        {
            _pages.AddDebugBox(new TypesetDebugBox(
                new SKRect(columnLeft, paintY, columnLeft + ColumnPitch, y + flowAdvance),
                IsCompatibilityCell(cell)
                    ? TypesetDebugBoxKind.CompatibilityCell
                    : TypesetDebugBoxKind.HanCell));
        }

        if (cell.ImagePath is not null)
        {
            var imageWidth = cell.ImageSideways
                ? Math.Max(1f, cell.ImageHeight)
                : Math.Max(1f, cell.ImageWidth);
            var imageHeight = cell.ImageSideways
                ? Math.Max(1f, cell.ImageWidth)
                : Math.Max(1f, cell.ImageHeight);
            var availableHeight = flowAdvance;
            var imageTop = paintY + Math.Max(0f, (availableHeight - imageHeight) / 2f) + superscriptShift;
            var imageRect = new SKRect(
                colCenter - imageWidth / 2f,
                imageTop,
                colCenter + imageWidth / 2f,
                imageTop + imageHeight);
            _pages.AddImage(new PlacedImage
            {
                Path = cell.ImagePath,
                Rect = imageRect,
                RotationDegrees = cell.ImageSideways ? 90f : 0f,
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

            return;
        }

        if (cell.Sideways)
        {
            var ascent = cell.Ascent;
            var descent = cell.Descent;
            var originX = colCenter - (ascent - descent) / 2f;
            var originY = paintY + superscriptShift - descent * 0f;
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
            AddDebugGlyphBox(cell, run);
        }
        else if (cell.Combined)
        {
            var advance = Math.Max(1f, cell.Advance);
            var scale = Math.Min(1f, _context.Options.BaseFontSize / advance);
            var baseline = paintY + CellPitch / 2f + (cell.Ascent - cell.Descent) / 2f + superscriptShift;
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
                CellWidth = ColumnPitch,
            };
            _pages.AddRun(run);
            AddDebugGlyphBox(cell, run);
        }
        else
        {
            // Upright single-glyph cell on the fixed grid.
            var inkBounds = GetUprightGlyphInkBounds(cell);
            var baselineCorrection = 0f;
            // ASCII digits and brackets are narrower than one em. Center the
            // actual shaped advance inside the cell; centering a fixed
            // font-size box leaves those glyphs visibly offset from the CJK
            // characters around them.
            var glyphAdvance = Math.Max(0f, cell.Advance);
            var glyphOffsetX = (ColumnPitch - glyphAdvance) / 2f;
            if (inkBounds is { } bounds
                && (!TypesetText.IsPunctuation(cell.Text)
                    || TypesetText.IsVerticallyCenteredMark(cell.Text)))
            {
                // A font's baseline metrics are deliberately generous. That
                // is correct for line layout, but a circular ideographic zero
                // (U+3007) can then sit visibly high inside a fixed book grid.
                // Center the actual upright ink for ordinary ideographs and
                // numeric glyphs. Mainland Chinese punctuation is excluded:
                // its right-upper placement is part of the publication style;
                // the Chinese interpunct (U+00B7) is an explicit centred mark.
                // Keep the established advance-based horizontal placement for
                // Latin/digit compatibility cells. Native CJK cells also use
                // their actual ink center so a font-sidebearing anomaly cannot
                // move an ideographic zero away from the grid center.
                if (TypesetText.IsVerticallyCenteredMark(cell.Text)
                    || TypesetText.TryGetFirstScalar(cell.Text, out var scalar)
                    && TypesetText.IsCjk(scalar))
                {
                    glyphOffsetX = ColumnPitch / 2f - (bounds.Left + bounds.Right) / 2f;
                }
                baselineCorrection = -(cell.Ascent - cell.Descent) / 2f
                    - (bounds.Top + bounds.Bottom) / 2f;
            }

            var baseline = paintY
                + CellPitch / 2f
                + (cell.Ascent - cell.Descent) / 2f
                + baselineCorrection
                + superscriptShift;
            var originX = columnLeft;
            var glyphX = new float[cell.Glyphs.Length];
            var glyphY = new float[cell.Glyphs.Length];
            for (var i = 0; i < glyphX.Length; i++)
            {
                glyphX[i] = i < cell.GlyphX.Length
                    ? glyphOffsetX + cell.GlyphX[i]
                    : glyphOffsetX;
                glyphY[i] = i < cell.GlyphY.Length ? cell.GlyphY[i] : 0f;
            }

            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = glyphX,
                Y = glyphY,
                OriginX = originX,
                OriginY = baseline,
                FlowAdvance = flowAdvance,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
                CellWidth = ColumnPitch,
            };
            _pages.AddRun(run);
            AddDebugGlyphBox(cell, run);
        }

        if (cell.TextStart >= 0 && cell.TextLength > 0)
        {
            _pages.TrackText(cell.TextStart, cell.TextStart + cell.TextLength);
        }

        if (cell.LinkHref is not null || cell.FootnoteHref is not null)
        {
            var top = paintY;
            var height = flowAdvance;
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(colCenter - ColumnPitch / 2f, top, colCenter + ColumnPitch / 2f, top + height),
                Href = cell.FootnoteHref ?? cell.LinkHref!,
                FootnoteText = cell.FootnoteText,
            });
        }

        if (cell.Style.Underline && cell.TextLength > 0)
        {
            var barX = colCenter - cell.FontSize / 2f - 1.6f;
            var height = cell.Sideways ? cell.Advance : CellPitch;
            _pages.AddDecoration(new PlacedRect
            {
                Kind = DecorationKind.Underline,
                Rect = new SKRect(barX, paintY, barX + 1.2f, paintY + height),
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
            });
        }
    }

    private static bool IsCompatibilityCell(LayoutCell cell)
    {
        if (cell.ImagePath is not null || cell.Sideways || cell.Combined)
        {
            return true;
        }

        return !string.IsNullOrEmpty(cell.Text)
            && (cell.Text.Any(char.IsAsciiLetterOrDigit)
                || TypesetText.ShouldRotateInVertical(cell.Text));
    }

    private void AddDebugGlyphBox(LayoutCell cell, PlacedRun run)
    {
        if (run.Glyphs.Length == 0 || run.X.Length == 0)
        {
            return;
        }

        var scale = run.Scale;
        var ascent = Math.Max(0.5f, cell.Ascent * scale);
        var descent = Math.Max(0.5f, cell.Descent * scale);
        SKRect rect;
        if (run.Sideways)
        {
            // Rotating a horizontal run maps its font metrics to the
            // cross-flow width while its shaped advance becomes the vertical
            // extent. This frame encloses the actual painted run, not the
            // integer grid cell that used to create the visible gap.
            rect = new SKRect(
                run.OriginX - descent,
                run.OriginY,
                run.OriginX + ascent,
                run.OriginY + Math.Max(1f, run.FlowAdvance));
        }
        else
        {
            var left = run.OriginX + run.X.Min();
            var width = Math.Max(1f, cell.Advance * scale);
            rect = new SKRect(
                left,
                run.OriginY - ascent,
                left + width,
                run.OriginY + descent);
        }

        _pages.AddDebugBox(new TypesetDebugBox(rect, TypesetDebugBoxKind.Glyph));
    }

    /// <summary>
    /// Returns the painted ink bounds relative to the cell's glyph origin.
    /// HarfBuzz's horizontal cell metrics are not enough after a vertical
    /// glyph substitution: U+3007 is a useful example because its outline is
    /// slightly taller and sits differently from the font-wide ascent box.
    /// </summary>
    private SKRect? GetUprightGlyphInkBounds(LayoutCell cell)
    {
        if (cell.Glyphs.Length == 0)
        {
            return null;
        }

        var missing = false;
        foreach (var glyphId in cell.Glyphs)
        {
            if (!_glyphBoundsCache.ContainsKey((cell.FontPath, cell.FontSize, glyphId)))
            {
                missing = true;
                break;
            }
        }

        if (missing)
        {
            using var font = new SKFont(_context.Fonts.GetTypeface(cell.FontPath), cell.FontSize);
            _ = font.GetGlyphWidths(cell.Glyphs.AsSpan(), out var rawBounds, null);
            for (var index = 0; index < cell.Glyphs.Length && index < rawBounds.Length; index++)
            {
                _glyphBoundsCache[(cell.FontPath, cell.FontSize, cell.Glyphs[index])] = rawBounds[index];
            }
        }

        var hasInk = false;
        var union = SKRect.Empty;
        for (var index = 0; index < cell.Glyphs.Length; index++)
        {
            if (!_glyphBoundsCache.TryGetValue(
                    (cell.FontPath, cell.FontSize, cell.Glyphs[index]),
                    out var glyphBounds)
                || glyphBounds.Width <= 0f
                || glyphBounds.Height <= 0f)
            {
                continue;
            }

            var positioned = new SKRect(
                glyphBounds.Left + (index < cell.GlyphX.Length ? cell.GlyphX[index] : 0f),
                glyphBounds.Top + (index < cell.GlyphY.Length ? cell.GlyphY[index] : 0f),
                glyphBounds.Right + (index < cell.GlyphX.Length ? cell.GlyphX[index] : 0f),
                glyphBounds.Bottom + (index < cell.GlyphY.Length ? cell.GlyphY[index] : 0f));
            if (!hasInk)
            {
                union = positioned;
                hasInk = true;
                continue;
            }

            union = new SKRect(
                Math.Min(union.Left, positioned.Left),
                Math.Min(union.Top, positioned.Top),
                Math.Max(union.Right, positioned.Right),
                Math.Max(union.Bottom, positioned.Bottom));
        }

        return hasInk ? union : null;
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

    private float CellFlowAdvance(LayoutCell cell)
    {
        if (cell.IsLineBreak)
        {
            return 0f;
        }

        if (cell.ImagePath is not null || cell.Sideways)
        {
            // A rotated run is already measured in the same physical units as
            // the canvas. Reserve that measured extent instead of rounding it
            // up to whole CJK rows; the rounded remainder was the blank gap
            // visible before the following upright character.
            return Math.Max(CellPitch, cell.Advance);
        }

        return CellPitch;
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
            if (cell.IsLineBreak
                || cell.Sideways
                || cell.Combined
                || cell.Glyphs.Length == 0
                || string.IsNullOrEmpty(cell.Text))
            {
                continue;
            }

            var rotate = _context.Shaper.NeedsVerticalRotation(cell.Text, cell.FontPath);
            if (rotate)
            {
                cells[i] = cell with
                {
                    VerticalRotation = rotate,
                    Sideways = rotate,
                };
                continue;
            }

            var vertical = _context.Shaper.Shape(
                cell.Text,
                0,
                cell.Text.Length,
                cell.FontPath,
                cell.FontSize,
                vertical: true);
            if (!ContainsNotdef(vertical))
            {
                // Keep the original horizontal cross-flow metrics, but use
                // the complete text element's vertical glyph sequence. This
                // allows vert/vrt2 substitutions for base-plus-selector and
                // combining clusters without permitting an internal break.
                cells[i] = cell with { Glyphs = vertical.GlyphIds };
            }
        }
    }

    private static bool ContainsNotdef(ShapedText shaped) =>
        shaped.GlyphIds.Any(glyph => glyph == 0);

    private void EnsureColumnSpace()
    {
        if (_cursorX - ColumnPitch < ContentLeft - 0.01f)
        {
            _pages.NextIfUsed();
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
        if (info.Width <= 0 || info.Height <= 0)
        {
            return;
        }
        var hasWidthConstraint = item.ImageWidthFactor is > 0f;
        var maxW = hasWidthConstraint
            ? Math.Min(
                _context.Options.ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                item.DecorativeQuote ? _context.Options.BaseFontSize * 5f : float.MaxValue)
            : _context.Options.ContentWidth;
        var maxH = Math.Max(1f, ContentHeight - _context.Options.BaseFontSize * 3.6f);
        var scale = Math.Min(maxW / info.Width, maxH / info.Height);
        if (!hasWidthConstraint)
        {
            scale = Math.Min(1f, scale);
        }
        var w = info.Width * scale;
        var h = info.Height * scale;

        // Images occupy their own page in vertical writing. The composer
        // keeps an empty current page after every forced image break; consume
        // it only for a later image so the first image does not get a blank
        // page before it, while consecutive images remain isolated.
        if (_pages.Current.Images.Count > 0)
        {
            _pages.Next();
        }
        else
        {
            _pages.NextIfUsed();
        }
        var x = block.AlignRight
            ? ContentRight - w
            : block.Center
                ? ContentLeft + (_context.Options.ContentWidth - w) / 2f
                : ContentLeft;
        var y = ContentTop + (ContentHeight - h) / 2f;
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddImage(new PlacedImage
        {
            Path = item.ImagePath,
            Rect = new SKRect(x, y, x + w, y + h),
            LinkHref = item.LinkHref,
            FootnoteHref = item.FootnoteHref,
        });
        if (item.LinkHref is not null || item.FootnoteHref is not null)
        {
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = item.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(x, y, x + w, y + h),
                Href = item.FootnoteHref ?? item.LinkHref!,
                FootnoteText = item.FootnoteText,
            });
        }
        _pages.Next();
        _cursorX = ContentRight;
    }

    private void PlaceRule(ContentBlock block)
    {
        EnsureColumnSpace();
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddDecoration(new PlacedRect
        {
            Kind = DecorationKind.Rule,
            Rect = new SKRect(ContentLeft, ContentTop + ContentHeight / 2f, ContentRight, ContentTop + ContentHeight / 2f + 1.2f),
        });
        _pages.MarkUsed();
        _cursorX -= ColumnPitch;
    }
}
