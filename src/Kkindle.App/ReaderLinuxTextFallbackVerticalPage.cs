using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Kkindle;

/// <summary>
/// Vertical-writing (vertical-rl) page surface for the Linux text fallback
/// reader. Characters stack top-to-bottom inside columns that advance
/// right-to-left, mirroring CSS writing-mode:vertical-rl with mixed glyph
/// orientation: CJK stays upright while Latin runs rotate 90° clockwise.
///
/// The control owns pointer drag selection, annotation decorations and
/// footnote marker cells so the fallback reader keeps its selection toolbar,
/// highlights and note features in vertical mode — SelectableTextBlock has no
/// vertical text layout underneath.
/// </summary>
public sealed class ReaderLinuxTextFallbackVerticalPage : Control
{
    // Leading applied to each character cell along the column axis. A bare
    // font-size grid reads cramped for CJK; a small positive leading matches
    // the visual rhythm of the horizontal pages.
    private const double CharAdvanceRatio = 1.08;
    private const int ParagraphIndentRows = 2;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, string?>(nameof(Text));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, double>(nameof(FontSize), 16d);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, FontFamily>(nameof(FontFamily));

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, FontWeight>(
            nameof(FontWeight),
            FontWeight.Normal);

    /// <summary>Column pitch — the distance between adjacent column origins.</summary>
    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, double>(nameof(LineHeight), 28d);

    public static readonly StyledProperty<bool> ParagraphIndentProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, bool>(
            nameof(ParagraphIndent),
            true);

    public static readonly StyledProperty<bool> StartsParagraphProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, bool>(
            nameof(StartsParagraph),
            true);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<int> ChapterTitleStartProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(ChapterTitleStart), -1);

    public static readonly StyledProperty<int> ChapterTitleLengthProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(ChapterTitleLength));

    public static readonly StyledProperty<IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>?> AnnotationRangesProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>?>(
            nameof(AnnotationRanges));

    public static readonly StyledProperty<IBrush?> InvertedSelectionForegroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IBrush?>(nameof(InvertedSelectionForeground));

    public static readonly StyledProperty<IBrush?> InvertedSelectionBackgroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IBrush?>(nameof(InvertedSelectionBackground));

    public static readonly StyledProperty<int> SelectionStartProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(SelectionStart));

    public static readonly StyledProperty<int> SelectionEndProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(SelectionEnd));

    private struct Cell
    {
        public int Offset;
        public int Length;
        public Rect Bounds;
        public bool Combined;
        public bool Rotated;
        public bool IsFootnote;
        public int FootnoteIndex;
    }

    private List<Cell>? _cells;
    private bool _draggingSelection;
    private int _selectionAnchor;

    /// <summary>Raised after a drag produced a non-empty selection.</summary>
    public event EventHandler? SelectionCommitted;

    /// <summary>
    /// Raised when a tap lands on a footnote marker cell; the payload carries
    /// the footnote href and the pointer position in control coordinates.
    /// </summary>
    public event EventHandler<(string Href, Point Position)>? FootnoteActivated;

    /// <summary>Raised when a tap lands outside any footnote marker cell.</summary>
    public event EventHandler? FootnoteDismissed;

    /// <summary>
    /// Resolves a marker cell's sequential index (in page order) to its href.
    /// Wired by the reader window from the rendered page item's footnotes.
    /// </summary>
    public Func<int, string?>? FootnoteHrefResolver { get; set; }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public bool ParagraphIndent
    {
        get => GetValue(ParagraphIndentProperty);
        set => SetValue(ParagraphIndentProperty, value);
    }

    /// <summary>
    /// Whether the first visual column on this page begins a paragraph. A
    /// page can start in the middle of a long paragraph after pagination.
    /// </summary>
    public bool StartsParagraph
    {
        get => GetValue(StartsParagraphProperty);
        set => SetValue(StartsParagraphProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public int ChapterTitleStart
    {
        get => GetValue(ChapterTitleStartProperty);
        set => SetValue(ChapterTitleStartProperty, value);
    }

    public int ChapterTitleLength
    {
        get => GetValue(ChapterTitleLengthProperty);
        set => SetValue(ChapterTitleLengthProperty, value);
    }

    public IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>? AnnotationRanges
    {
        get => GetValue(AnnotationRangesProperty);
        set => SetValue(AnnotationRangesProperty, value);
    }

    public IBrush? InvertedSelectionForeground
    {
        get => GetValue(InvertedSelectionForegroundProperty);
        set => SetValue(InvertedSelectionForegroundProperty, value);
    }

    public IBrush? InvertedSelectionBackground
    {
        get => GetValue(InvertedSelectionBackgroundProperty);
        set => SetValue(InvertedSelectionBackgroundProperty, value);
    }

    public int SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public string SelectedText
    {
        get
        {
            var text = Text ?? string.Empty;
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Max(SelectionStart, SelectionEnd);
            if (start < 0 || end > text.Length || end <= start) return string.Empty;
            return text[start..end].Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
        }
    }

    /// <summary>
    /// Shared grid metrics for pagination and rendering. The paginator must
    /// use these exact numbers so page boundaries land on column starts.
    /// </summary>
    public static (int CharsPerColumn, int ColumnsPerPage) ComputeGrid(
        double width,
        double height,
        double fontSize,
        double lineHeight)
    {
        var charAdvance = Math.Max(1, fontSize * CharAdvanceRatio);
        var columnPitch = Math.Max(fontSize, lineHeight);
        return (
            Math.Max(1, (int)Math.Floor(Math.Max(1, height) / charAdvance)),
            Math.Max(1, (int)Math.Floor(Math.Max(1, width) / columnPitch)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty
            || change.Property == FontSizeProperty
            || change.Property == LineHeightProperty
            || change.Property == ParagraphIndentProperty
            || change.Property == StartsParagraphProperty
            || change.Property == ChapterTitleStartProperty
            || change.Property == ChapterTitleLengthProperty)
        {
            _cells = null;
        }
        InvalidateVisual();
    }

    private List<Cell> BuildCells()
    {
        var cells = new List<Cell>();
        var text = Text ?? string.Empty;
        if (text.Length == 0 || Bounds.Width < 1 || Bounds.Height < 1)
            return cells;

        var charAdvance = Math.Max(1, FontSize * CharAdvanceRatio);
        var columnPitch = Math.Max(FontSize, LineHeight);
        var charsPerColumn = Math.Max(1, (int)Math.Floor(Bounds.Height / charAdvance));
        var rowIndex = 0;
        var columnIndex = 0;
        var paragraphIndentPending = ParagraphIndent && StartsParagraph;
        var titleEnd = ChapterTitleLength > 0 ? ChapterTitleStart + ChapterTitleLength : -1;
        var footnoteIndex = 0;
        var titleColumnStart = -1;

        // A chapter heading centers itself vertically inside its column,
        // matching the centered heading style of the horizontal pages.
        void CenterCompletedTitleColumn()
        {
            if (titleColumnStart < 0)
            {
                return;
            }

            var count = cells.Count - titleColumnStart;
            if (count > 1 && count < charsPerColumn)
            {
                var offset = Math.Floor((Bounds.Height - count * charAdvance) / 2d);
                if (offset > 0)
                {
                    for (var index = titleColumnStart; index < cells.Count; index++)
                    {
                        var shifted = cells[index];
                        shifted.Bounds = shifted.Bounds.WithY(shifted.Bounds.Y + offset);
                        cells[index] = shifted;
                    }
                }
            }
            titleColumnStart = -1;
        }

        foreach (var unit in ReaderLinuxVerticalTextUnits.Tokenize(text))
        {
            if (unit.IsLineBreak)
            {
                CenterCompletedTitleColumn();
                rowIndex = 0;
                columnIndex++;
                paragraphIndentPending = ParagraphIndent;
                continue;
            }

            if (rowIndex >= charsPerColumn)
            {
                CenterCompletedTitleColumn();
                rowIndex = 0;
                columnIndex++;
            }

            if (paragraphIndentPending)
            {
                // Keep the first line of each paragraph two glyph cells down
                // from the logical inline start, the vertical equivalent of
                // text-indent: 2em. Clamp only degenerate tiny test surfaces;
                // normal reading pages have ample room for both rows.
                rowIndex = Math.Min(
                    ParagraphIndentRows,
                    Math.Max(0, charsPerColumn - 1));
                paragraphIndentPending = false;
            }

            if (columnPitch * (columnIndex + 1) > Bounds.Width + 0.5) break;

            if (unit.Offset >= ChapterTitleStart
                && unit.Offset < titleEnd
                && titleColumnStart < 0)
                titleColumnStart = cells.Count;

            var bandLeft = Bounds.Width - (columnIndex + 1) * columnPitch;
            var character = text[unit.Offset];
            var isFootnoteMarker = character == MainWindow.ReaderLinuxTextFallbackFootnoteMarker;
            cells.Add(new Cell
            {
                Offset = unit.Offset,
                Length = unit.Length,
                Bounds = new Rect(bandLeft, rowIndex * charAdvance, Math.Max(1, columnPitch), charAdvance),
                Combined = unit.IsCombined,
                Rotated = !unit.IsCombined && !IsVerticalUpright(character),
                IsFootnote = isFootnoteMarker,
                FootnoteIndex = isFootnoteMarker ? footnoteIndex++ : -1
            });
            rowIndex++;
        }

        CenterCompletedTitleColumn();
        return cells;
    }

    private static bool IsVerticalUpright(char character) =>
        character is >= '0' and <= '9'              // keep Arabic digits on the CJK cell grid
        or >= '\u2E80' and <= '\u9FFF'              // CJK strokes, kana, ideographs, CJK punctuation
        or >= '\uF900' and <= '\uFAFF'              // compatibility ideographs
        or >= '\uFF00';                             // fullwidth and halfwidth forms

    public override void Render(DrawingContext context)
    {
        var text = Text ?? string.Empty;
        if (text.Length == 0) return;
        _cells ??= BuildCells();
        if (_cells.Count == 0) return;

        var selectionStart = Math.Min(SelectionStart, SelectionEnd);
        var selectionEnd = Math.Max(SelectionStart, SelectionEnd);
        var backing = InvertedSelectionBackground ?? Brushes.Black;
        var invertedForeground = InvertedSelectionForeground ?? Brushes.White;
        var bodyTypeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
        var footnoteTypeface = new Typeface(
            FontFamily,
            FontStyle.Normal,
            FontWeight.Bold,
            FontStretch.Normal);

        DrawAnnotationFills(context);

        foreach (var cell in _cells)
        {
            var character = text[cell.Offset];
            var selected = cell.Offset < selectionEnd
                && cell.Offset + cell.Length > selectionStart;
            if (selected)
                context.FillRectangle(backing, cell.Bounds);
            var brush = selected ? invertedForeground : Foreground ?? Brushes.Black;

            if (character == MainWindow.ReaderLinuxTextFallbackFootnoteMarker)
            {
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(28, 96, 96, 96)),
                    cell.Bounds);
                DrawCenteredGlyph(
                    context,
                    cell,
                    CreateFormatted("注", footnoteTypeface, brush),
                    brush);
                continue;
            }

            var glyphText = cell.Length > 1
                ? text.Substring(cell.Offset, cell.Length)
                : character.ToString();
            var glyph = CreateFormatted(glyphText, bodyTypeface, brush);
            if (cell.Combined)
            {
                // A compact horizontal run is still one logical vertical
                // cell. Fit its actual ink bounds, rather than its advance
                // width, so a percent sign or a decimal cannot bleed into
                // the next row/column.
                DrawGlyphInCell(context, cell, glyph, brush, rotation: 0, compressWidth: true);
            }
            else if (cell.Rotated)
            {
                // Latin and other horizontal-only scripts turn 90° clockwise,
                // the mixed-orientation behavior of CSS vertical writing.
                // Rotate the glyph around its own cell center. Composing a
                // translation with a rotation here rotates the cell's
                // position as well, which makes a run such as "100" leave
                // its CJK column and appear as a detached horizontal row.
                DrawGlyphInCell(
                    context,
                    cell,
                    glyph,
                    brush,
                    rotation: Math.PI / 2,
                    compressWidth: false);
            }
            else
            {
                DrawCenteredGlyph(context, cell, glyph, brush);
            }
        }

        DrawAnnotationUnderlines(context);
    }

    // FormattedText's advance metrics include font leading and alignment
    // space. Geometry.Bounds is the actual ink rectangle, which is the only
    // reliable basis for centering mixed-orientation glyphs in a publication
    // cell. The clip is intentional: a bad font overhang must never paint
    // over its neighbor.
    private static void DrawGlyphInCell(
        DrawingContext context,
        Cell cell,
        FormattedText glyph,
        IBrush brush,
        double rotation,
        bool compressWidth)
    {
        if (glyph.BuildGeometry(new Point(0, 0)) is not { } geometry)
            return;

        var sourceBounds = geometry.Bounds;
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
            return;

        var inset = Math.Min(1.25, Math.Min(cell.Bounds.Width, cell.Bounds.Height) * 0.06);
        var targetWidth = Math.Max(1, cell.Bounds.Width - inset * 2);
        var targetHeight = Math.Max(1, cell.Bounds.Height - inset * 2);
        double scaleX;
        double scaleY;
        if (rotation != 0)
        {
            // A 90-degree rotation swaps the source width and height. Keep
            // the scale uniform so punctuation does not become distorted.
            var scale = Math.Min(
                1d,
                Math.Min(
                    targetWidth / Math.Max(1, sourceBounds.Height),
                    targetHeight / Math.Max(1, sourceBounds.Width)));
            scaleX = scale;
            scaleY = scale;
        }
        else if (compressWidth)
        {
            scaleX = Math.Min(1d, targetWidth / Math.Max(1, sourceBounds.Width));
            scaleY = Math.Min(1d, targetHeight / Math.Max(1, sourceBounds.Height));
        }
        else
        {
            var scale = Math.Min(
                1d,
                Math.Min(
                    targetWidth / Math.Max(1, sourceBounds.Width),
                    targetHeight / Math.Max(1, sourceBounds.Height)));
            scaleX = scale;
            scaleY = scale;
        }

        var center = cell.Bounds.Center;
        var transform = Matrix.CreateTranslation(center.X, center.Y)
            * Matrix.CreateRotation(rotation)
            * Matrix.CreateScale(scaleX, scaleY)
            * Matrix.CreateTranslation(-sourceBounds.Center.X, -sourceBounds.Center.Y);
        using var clip = context.PushClip(cell.Bounds);
        using var placement = context.PushTransform(transform);
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawCenteredGlyph(
        DrawingContext context,
        Cell cell,
        FormattedText glyph,
        IBrush brush)
        => DrawGlyphInCell(context, cell, glyph, brush, rotation: 0, compressWidth: false);

    private FormattedText CreateFormatted(string value, Typeface typeface, IBrush brush) => new(
        value,
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        typeface,
        FontSize,
        brush);

    private void DrawAnnotationFills(DrawingContext context)
    {
        if (AnnotationRanges is not { Count: > 0 } || _cells is not { } cells) return;
        var sourceLength = (Text ?? string.Empty).Length;
        foreach (var annotation in AnnotationRanges)
        {
            if (annotation.Style != "marker") continue;
            var color = ParseAnnotationColor(annotation.Color);
            var brush = new SolidColorBrush(Color.FromArgb(72, color.R, color.G, color.B));
            foreach (var rect in EnumerateAnnotationRects(annotation, cells, sourceLength))
                context.FillRectangle(brush, rect);
        }
    }

    private void DrawAnnotationUnderlines(DrawingContext context)
    {
        if (AnnotationRanges is not { Count: > 0 } || _cells is not { } cells) return;
        var sourceLength = (Text ?? string.Empty).Length;
        foreach (var annotation in AnnotationRanges)
        {
            if (annotation.Style == "marker") continue;
            var color = ParseAnnotationColor(annotation.Color);
            var brush = new SolidColorBrush(color);
            foreach (var rect in EnumerateAnnotationRects(annotation, cells, sourceLength))
            {
                // The vertical counterpart of the baseline underline runs along
                // the right edge of the column band, where Chinese emphasis
                // lines traditionally sit.
                var x = rect.Right - Math.Min(2, Math.Max(0.75, rect.Width / 3));
                switch (annotation.Style)
                {
                    case "dotted":
                        DrawDottedVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                    case "wavy":
                        DrawWavyVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                    case "double":
                        DrawSolidVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        DrawSolidVerticalLine(context, brush, x - 2.5, rect.Top, rect.Bottom);
                        break;
                    case "dashed":
                        DrawDashedVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                    default:
                        DrawSolidVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                }
            }
        }
    }

    private IEnumerable<Rect> EnumerateAnnotationRects(
        MainWindow.ReaderLinuxTextFallbackAnnotationRange annotation,
        List<Cell> cells,
        int sourceLength)
    {
        var start = Math.Clamp(annotation.Start, 0, sourceLength);
        var end = Math.Clamp(start + annotation.Length, start, sourceLength);
        foreach (var cell in cells)
        {
            if (cell.Offset >= end || cell.Offset + cell.Length <= start) continue;
            yield return cell.Bounds;
        }
    }

    private static Color ParseAnnotationColor(string value)
    {
        try { return Color.Parse(value); }
        catch { return Colors.Black; }
    }

    private static void DrawSolidVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        if (bottom <= top) return;
        context.DrawLine(new Pen(brush, 1.6), new Point(x, top), new Point(x, bottom));
    }

    private static void DrawDashedVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        if (bottom <= top) return;
        const double dash = 4.5;
        const double gap = 3.5;
        var pen = new Pen(brush, 1.6);
        for (var y = top; y < bottom; y += dash + gap)
            context.DrawLine(pen, new Point(x, y), new Point(x, Math.Min(bottom, y + dash)));
    }

    private static void DrawDottedVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        // Filled dots instead of DashStyle.Dot: some GTK/Skia combinations
        // render that pattern as an empty stroke, the same renderer quirk
        // documented on the horizontal annotation path.
        const double radius = 1.15;
        const double spacing = 4.25;
        var first = top + radius;
        var last = bottom - radius;
        if (last < first) first = (top + bottom) / 2;
        for (var y = first; y <= last + 0.01; y += spacing)
            context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
    }

    private static void DrawWavyVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        if (bottom <= top) return;
        var pen = new Pen(brush, 1.6) { LineCap = PenLineCap.Round };
        const double wavelength = 8;
        const double amplitude = 1.5;
        const double step = 1.5;
        var previous = new Point(x, top);
        for (var y = top + step; y < bottom; y += step)
        {
            var phase = (y - top) / wavelength * Math.PI * 2;
            var current = new Point(x + Math.Sin(phase) * amplitude, y);
            context.DrawLine(pen, previous, current);
            previous = current;
        }

        var endPhase = (bottom - top) / wavelength * Math.PI * 2;
        context.DrawLine(pen, previous, new Point(x + Math.Sin(endPhase) * amplitude, bottom));
    }

    /// <summary>Maps a point in control coordinates to a character offset.</summary>
    public int HitTestOffset(Point point)
    {
        _cells ??= BuildCells();
        var bestOffset = -1;
        var bestDistance = 64d * 64;
        foreach (var cell in _cells)
        {
            var bounds = cell.Bounds;
            var dx = point.X < bounds.Left
                ? bounds.Left - point.X
                : point.X > bounds.Right
                    ? point.X - bounds.Right
                    : 0;
            var dy = point.Y < bounds.Top
                ? bounds.Top - point.Y
                : point.Y > bounds.Bottom
                    ? point.Y - bounds.Bottom
                    : 0;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestOffset = cell.Offset;
            }
        }
        return bestOffset;
    }

    /// <summary>
    /// Bounding rectangle of a character range in control coordinates, used to
    /// anchor the shared selection toolbar popup.
    /// </summary>
    public Rect? GetRangeAnchorRect(int start, int end)
    {
        _cells ??= BuildCells();
        Rect? result = null;
        foreach (var cell in _cells)
        {
            if (cell.Offset >= end || cell.Offset + cell.Length <= start) continue;
            result = result is { } current
                ? current.Union(cell.Bounds)
                : cell.Bounds;
        }
        return result;
    }

    public void ClearSelection()
    {
        SelectionStart = 0;
        SelectionEnd = 0;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var offset = HitTestOffset(e.GetPosition(this));
        if (offset < 0) return;

        e.Pointer.Capture(this);
        _draggingSelection = true;
        _selectionAnchor = offset;
        SelectionStart = offset;
        SelectionEnd = offset;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_draggingSelection) return;
        var offset = HitTestOffset(e.GetPosition(this));
        if (offset < 0) return;
        SelectionStart = Math.Min(_selectionAnchor, offset);
        SelectionEnd = Math.Max(_selectionAnchor, offset);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_draggingSelection) return;
        _draggingSelection = false;
        e.Pointer.Capture(null);

        var offset = HitTestOffset(e.GetPosition(this));
        if (offset >= 0)
        {
            SelectionStart = Math.Min(_selectionAnchor, offset);
            SelectionEnd = Math.Max(_selectionAnchor, offset);
        }
        InvalidateVisual();

        if (SelectedText.Length > 0)
        {
            SelectionCommitted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            TryActivateFootnoteAt(e.GetPosition(this));
        }
        e.Handled = true;
    }

    private void TryActivateFootnoteAt(Point position)
    {
        _cells ??= BuildCells();
        foreach (var cell in _cells)
        {
            if (!cell.IsFootnote || !cell.Bounds.Contains(position)) continue;
            if (FootnoteHrefResolver?.Invoke(cell.FootnoteIndex) is not { } href) return;
            FootnoteActivated?.Invoke(this, (href, position));
            return;
        }
        FootnoteDismissed?.Invoke(this, EventArgs.Empty);
    }
}
