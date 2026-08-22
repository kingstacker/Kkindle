using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Reflection;

namespace Kkindle;

/// <summary>
/// Selectable fallback text that can keep a footnote marker inside the same
/// paragraph instead of giving it a separate block in the recovery surface.
/// </summary>
public sealed class ReaderLinuxTextFallbackTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<MainWindow.ReaderLinuxTextFallbackBlock?> BlockProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, MainWindow.ReaderLinuxTextFallbackBlock?>(
            nameof(Block));

    public static readonly StyledProperty<int> ChapterTitleStartProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, int>(
            nameof(ChapterTitleStart), -1);

    public static readonly StyledProperty<int> ChapterTitleLengthProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, int>(
            nameof(ChapterTitleLength));

    public static readonly StyledProperty<string?> LayoutTextProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, string?>(
            nameof(LayoutText));

    public static readonly StyledProperty<IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>?> AnnotationRangesProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>?>(
            nameof(AnnotationRanges));

    /// <summary>
    /// Foreground used to repaint the selected range. Selection colors must
    /// stay out of layout: base SelectableTextBlock feeds this brush into the
    /// TextLayout as a per-run style override, and those overrides split the
    /// text runs exactly at the selection boundaries. When such a boundary
    /// lands on a soft line break, the reshaped runs measure slightly
    /// differently and the paragraph re-wraps mid-gesture. This control keeps
    /// the layout free of overrides and paints the inversion on top of the
    /// finished text lines instead.
    /// </summary>
    public static readonly StyledProperty<IBrush?> InvertedSelectionForegroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, IBrush?>(
            nameof(InvertedSelectionForeground));

    /// <summary>
    /// Backing painted under the inverted glyphs. Paragraphs that carry inline
    /// footnote markers keep the plain SelectionBrush highlight instead: their
    /// runs are shaped around embedded controls, so repainting them from a
    /// separate layout cannot be guaranteed to line up.
    /// </summary>
    public static readonly StyledProperty<IBrush?> InvertedSelectionBackgroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, IBrush?>(
            nameof(InvertedSelectionBackground));

    public MainWindow.ReaderLinuxTextFallbackBlock? Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    /// <summary>
    /// Local title range used by a paged item. Continuous items carry the
    /// same semantic information through <see cref="Block"/>; pages need the
    /// range because a title and its following paragraphs share one selectable
    /// text control.
    /// </summary>
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

    /// <summary>
    /// Logical page text retained when Avalonia renders the visible content
    /// through Inlines and consequently leaves Text empty.
    /// </summary>
    public string? LayoutText
    {
        get => GetValue(LayoutTextProperty);
        set => SetValue(LayoutTextProperty, value);
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

    public event EventHandler<PointerEventArgs>? FootnotePointerEntered;
    public event EventHandler<PointerEventArgs>? FootnotePointerExited;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlockProperty)
        {
            var chapterTitle = Block?.IsChapterTitle == true;
            // Keep the semantic heading style on the control itself. This
            // avoids relying on a DataTemplate binding to an Avalonia enum or
            // struct, which can otherwise leave a heading at the default
            // left-aligned, regular style during the first measure pass.
            FontWeight = chapterTitle ? FontWeight.Bold : FontWeight.Normal;
            TextAlignment = chapterTitle ? TextAlignment.Center : TextAlignment.Left;
            RebuildContent();
        }
        else if (change.Property == ChapterTitleStartProperty
            || change.Property == ChapterTitleLengthProperty
            || change.Property == LayoutTextProperty
            || change.Property == AnnotationRangesProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"fallback-render title={Block?.IsChapterTitle == true} chapter={ChapterTitleStart}/{ChapterTitleLength} inlines={Inlines?.Count ?? 0} text={Text}");
        }

        if (Block?.IsChapterTitle == true && Inlines is not { Count: > 0 })
        {
            var titleText = Text ?? string.Empty;
            var titleWidth = GetStyledLayoutWidth();
            var titleLayout = CreateStyledLayout(
                titleText,
                Brushes.Black,
                FontWeight.Bold,
                TextAlignment.Center,
                titleWidth);
            DrawCenteredLayout(context, titleLayout, origin);
            DrawAnnotationDecorations(context, origin, titleLayout);

            var titleSelectionLength = Math.Abs(SelectionEnd - SelectionStart);
            var inverted = InvertedSelectionForeground;
            var backing = InvertedSelectionBackground ?? SelectionBrush;
            if (titleSelectionLength > 0 && inverted is not null && backing is not null)
            {
                var selectionStart = Math.Min(SelectionStart, SelectionEnd);
                var selectionRects = titleLayout
                    .HitTestTextRange(selectionStart, titleSelectionLength)
                    .Select(rect => PixelRect.FromRect(rect, 1).ToRect(1))
                    .ToArray();
                var invertedTitleLayout = CreateStyledLayout(
                    titleText,
                    inverted,
                    FontWeight.Bold,
                    TextAlignment.Center,
                    titleWidth);
                foreach (var snapped in selectionRects)
                {
                    using (context.PushTransform(Matrix.CreateTranslation(origin)))
                    {
                        context.FillRectangle(backing, snapped);
                        using (context.PushClip(snapped))
                            DrawCenteredLayout(context, invertedTitleLayout, new Point(0, 0));
                    }
                }
            }
            return;
        }

        // Avalonia's TextAlignment.Justify also stretches the final line of a
        // paragraph. That makes a one-line heading look like a row of
        // separated characters. Keep the base layout left-aligned for hit
        // testing and selection, then justify only soft-wrapped lines here;
        // explicit paragraph endings and the final line retain normal spacing.
        var selectionLength = Math.Abs(SelectionEnd - SelectionStart);
        if (Inlines is not { Count: > 0 } && TextLayout is { } naturalLayout)
        {
            DrawNaturalJustifiedLayout(context, origin, naturalLayout);
            DrawAnnotationDecorations(context, origin, naturalLayout);
            if (selectionLength <= 0)
                return;

            var inverted = InvertedSelectionForeground;
            var backing = InvertedSelectionBackground ?? SelectionBrush;
            if (inverted is null || backing is null)
                return;

            var selectionStart = Math.Min(SelectionStart, SelectionEnd);
            var selectionRects = naturalLayout
                .HitTestTextRange(selectionStart, selectionLength)
                .Select(rect => PixelRect.FromRect(rect, 1).ToRect(1))
                .ToArray();
            if (selectionRects.Length == 0)
                return;

            // Paint the selected glyphs from a second layout that goes through
            // the same natural-justification pass. Calling the base renderer
            // here would rebuild a left-aligned selection layout, which is the
            // source of the visible one-line shrink while dragging.
            var swapped = CreateInvertedLayout(inverted);
            PrepareNaturalJustification(swapped);
            foreach (var snapped in selectionRects)
            {
                using (context.PushTransform(Matrix.CreateTranslation(origin)))
                {
                    context.FillRectangle(backing, snapped);
                    using (context.PushClip(snapped))
                    {
                        DrawNaturalJustifiedLayout(
                            context,
                            new Point(0, 0),
                            swapped,
                            inverted);
                    }
                }
            }
            return;
        }

        // Inline footnote controls have their own embedded layout. Retain the
        // base renderer for body content, then replace its first title line
        // with the same solid-black centered layout used by plain pages.
        base.RenderTextLayout(context, origin);
        if (TextLayout is { } inlineLayout)
            DrawAnnotationDecorations(context, origin, inlineLayout);
        DrawInlinePageChapterTitleOverlay(context, origin);
    }

    private void DrawAnnotationDecorations(
        DrawingContext context,
        Point origin,
        TextLayout layout)
    {
        if (AnnotationRanges is not { Count: > 0 }) return;
        var sourceLength = (!string.IsNullOrEmpty(LayoutText)
                ? LayoutText
                : Block?.Text ?? Text ?? string.Empty)
            .Length;
        foreach (var annotation in AnnotationRanges)
        {
            var start = Math.Clamp(annotation.Start, 0, sourceLength);
            var length = Math.Clamp(annotation.Length, 0, sourceLength - start);
            if (length <= 0) continue;

            Color color;
            try { color = Color.Parse(annotation.Color); }
            catch { color = Colors.Black; }
            var brush = new SolidColorBrush(color);
            foreach (var rect in layout.HitTestTextRange(start, length))
            {
                var lineRect = new Rect(
                    rect.X + origin.X,
                    rect.Y + origin.Y,
                    rect.Width,
                    rect.Height);
                if (annotation.Style == "marker")
                {
                    context.FillRectangle(
                        new SolidColorBrush(Color.FromArgb(72, color.R, color.G, color.B)),
                        lineRect);
                    continue;
                }

                // Keep the decoration inside the line's hit-test rectangle.
                // Drawing on its lower edge is clipped by some GTK backends,
                // which made the custom dotted and wavy paths disappear.
                var y = lineRect.Bottom - Math.Min(2, Math.Max(0.75, lineRect.Height / 3));
                if (annotation.Style == "dotted")
                {
                    DrawDottedAnnotationLine(
                        context,
                        brush,
                        lineRect.Left,
                        lineRect.Right,
                        y);
                    continue;
                }

                if (annotation.Style == "wavy")
                {
                    DrawWavyAnnotationLine(
                        context,
                        brush,
                        lineRect.Left,
                        lineRect.Right,
                        y);
                    continue;
                }

                var dash = annotation.Style == "dashed" ? DashStyle.Dash : null;
                var pen = new Pen(brush, 1.6, dash);
                context.DrawLine(
                    pen,
                    new Point(lineRect.Left, y),
                    new Point(lineRect.Right, y));
                if (annotation.Style == "double")
                {
                    context.DrawLine(
                        pen,
                        new Point(lineRect.Left, y - 2.5),
                        new Point(lineRect.Right, y - 2.5));
                }
            }
        }
    }

    private static void DrawDottedAnnotationLine(
        DrawingContext context,
        IBrush brush,
        double left,
        double right,
        double y)
    {
        if (right <= left) return;

        // Draw actual filled dots instead of relying on DashStyle.Dot. The
        // latter is treated as an empty dash pattern by the GTK renderer in
        // the Avalonia version used by the Linux fallback.
        const double radius = 1.15;
        const double spacing = 4.25;
        var first = left + radius;
        var last = right - radius;
        if (last < first)
            first = (left + right) / 2;

        for (var x = first; x <= last + 0.01; x += spacing)
            context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
    }

    private static void DrawWavyAnnotationLine(
        DrawingContext context,
        IBrush brush,
        double left,
        double right,
        double y)
    {
        if (right <= left) return;

        // Use short line segments rather than a StreamGeometry. A few GTK
        // Skia combinations drop an open StreamGeometry that has no fill,
        // while ordinary DrawLine calls are rendered consistently.
        var pen = new Pen(brush, 1.6)
        {
            LineCap = PenLineCap.Round
        };
        const double wavelength = 8;
        const double amplitude = 1.5;
        const double step = 1.5;
        var previous = new Point(left, y);
        for (var x = left + step; x < right; x += step)
        {
            var phase = (x - left) / wavelength * Math.PI * 2;
            var current = new Point(x, y + Math.Sin(phase) * amplitude);
            context.DrawLine(pen, previous, current);
            previous = current;
        }

        var endPhase = (right - left) / wavelength * Math.PI * 2;
        context.DrawLine(
            pen,
            previous,
            new Point(right, y + Math.Sin(endPhase) * amplitude));
    }

    private void DrawNaturalJustifiedLayout(
        DrawingContext context,
        Point origin,
        TextLayout layout,
        IBrush? foreground = null)
    {
        PrepareNaturalJustification(layout);
        var chapterTitleLayout = CreateChapterTitleLayout(foreground);
        if (string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"fallback-natural lines={layout.TextLines.Count} title-layout={chapterTitleLayout is not null} "
                + $"title-lines={chapterTitleLayout?.TextLines.Count ?? -1} width={Bounds.Width}/{layout.MaxWidth}");
        }
        var lineOffset = 0d;
        for (var lineIndex = 0; lineIndex < layout.TextLines.Count; lineIndex++)
        {
            var line = layout.TextLines[lineIndex];
            if (chapterTitleLayout is not null && IsChapterTitleLine(line, lineIndex))
                DrawCenteredLayout(
                    context,
                    chapterTitleLayout,
                    origin + new Vector(0, lineOffset));
            else
                line.Draw(context, origin + new Vector(0, lineOffset));
            lineOffset += line.Height;
        }
    }

    private void DrawCenteredLayout(
        DrawingContext context,
        TextLayout layout,
        Point origin)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            context.FillRectangle(Brushes.Red, new Rect(origin, new Size(8, 8)));
        }
        // TextLayout already owns paragraph alignment. Drawing its individual
        // TextLine objects adds their internal start offset a second time on
        // Linux and pushes a centered heading toward the right edge.
        layout.Draw(context, origin);
    }

    private void DrawInlinePageChapterTitleOverlay(
        DrawingContext context,
        Point origin)
    {
        if (Inlines is not { Count: > 0 }
            || TextLayout is not { TextLines.Count: > 0 } layout)
        {
            return;
        }

        var titleLayout = CreateChapterTitleLayout(foreground: null);
        if (titleLayout is null || !IsChapterTitleLine(layout.TextLines[0], 0))
            return;

        var width = double.IsFinite(Bounds.Width) && Bounds.Width > 0
            ? Bounds.Width
            : titleLayout.MaxWidth;
        var height = Math.Max(layout.TextLines[0].Height, titleLayout.Height);
        context.FillRectangle(
            Brushes.White,
            new Rect(origin, new Size(Math.Max(1, width), Math.Max(1, height))));
        DrawCenteredLayout(context, titleLayout, origin);
    }

    private void PrepareNaturalJustification(TextLayout layout)
    {
        var contentWidth = double.IsFinite(layout.MaxWidth) && layout.MaxWidth > 0
            ? layout.MaxWidth
            : Bounds.Width;
        var justification = CreateJustificationProperties(
            Math.Max(contentWidth, Bounds.Width));
        if (justification is null)
            return;
        var lines = layout.TextLines;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (IsChapterTitleLine(line, index))
                continue;
            var contentLength = line.Length
                - line.NewLineLength
                - line.TrailingWhitespaceLength;
            // Do not turn headings or short wrapped fragments into a row of
            // widely separated glyphs. A long prose line that is already
            // close to the right edge receives the small amount of expansion
            // needed to remove its apparent trailing blank cell.
            var isNaturalProseLine = contentLength >= 12
                && line.Width >= Math.Max(1, Math.Max(contentWidth, Bounds.Width) * 0.72);
            if (line.NewLineLength == 0
                && index < lines.Count - 1
                && isNaturalProseLine)
                line.Justify(justification);
        }
    }

    private bool IsChapterTitleLine(TextLine line, int lineIndex)
    {
        return ReaderLinuxTextFallbackTitleLinePolicy.IsTitleLine(
            ChapterTitleStart,
            ChapterTitleLength,
            line.Start,
            line.Length,
            line.NewLineLength,
            lineIndex);
    }

    private TextLayout? CreateChapterTitleLayout(IBrush? foreground)
    {
        if (ChapterTitleStart < 0 || ChapterTitleLength <= 0)
            return null;

        var text = !string.IsNullOrEmpty(LayoutText)
            ? LayoutText
            : Text ?? string.Empty;
        if (ChapterTitleStart >= text.Length)
            return null;

        var length = Math.Min(ChapterTitleLength, text.Length - ChapterTitleStart);
        var title = text.Substring(ChapterTitleStart, length).TrimEnd('\r', '\n');
        return string.IsNullOrWhiteSpace(title)
            ? null
            : CreateStyledLayout(
                title,
                foreground ?? Brushes.Black,
                FontWeight.Bold,
                TextAlignment.Center,
                GetStyledLayoutWidth());
    }

    private double? GetStyledLayoutWidth()
    {
        var width = double.IsFinite(MaxWidth) && MaxWidth > 0
            ? MaxWidth
            : Bounds.Width;
        return double.IsFinite(width) && width > 0 ? width : null;
    }

    private static JustificationProperties? CreateJustificationProperties(double width)
    {
        var type = typeof(TextLine).Assembly.GetType(
            "Avalonia.Media.TextFormatting.InterWordJustification");
        var constructor = type?.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(double) },
            modifiers: null);
        return constructor?.Invoke(new object[] { Math.Max(1, width) }) as JustificationProperties;
    }

    // Same shaping inputs as the regular layout with one exception: the
    // foreground brush. No style overrides are attached, so both layouts wrap
    // identically and the inverted glyphs land exactly on the inked ones.
    private TextLayout CreateInvertedLayout(IBrush foreground)
        => CreateStyledLayout(
            Text ?? string.Empty,
            foreground,
            FontWeight,
            TextAlignment);

    private TextLayout CreateStyledLayout(
        string text,
        IBrush foreground,
        FontWeight fontWeight,
        TextAlignment textAlignment,
        double? layoutWidth = null)
    {
        var typeface = new Typeface(FontFamily, FontStyle, fontWeight, FontStretch);
        var defaultProperties = new GenericTextRunProperties(
            typeface,
            FontSize,
            TextDecorations,
            foreground,
            fontFeatures: FontFeatures);
        var paragraphProperties = new GenericTextParagraphProperties(
            FlowDirection,
            textAlignment,
            true,
            false,
            defaultProperties,
            TextWrapping,
            LineHeight,
            0,
            LetterSpacing);
        var maxSize = GetMaxSizeFromConstraint();
        if (layoutWidth is > 0 && double.IsFinite(layoutWidth.Value))
            maxSize = new Size(layoutWidth.Value, maxSize.Height);
        return new TextLayout(
            new SimpleTextSource(text, defaultProperties),
            paragraphProperties,
            TextTrimming,
            maxSize.Width,
            maxSize.Height,
            MaxLines);
    }

    private void RebuildContent()
    {
        Inlines?.Clear();
        var block = Block;
        if (block is null)
        {
            Text = string.Empty;
            return;
        }

        if (!block.HasInlineFootnotes)
        {
            Text = block.Text;
            return;
        }

        Text = string.Empty;
        Inlines?.Clear();
        var footnoteIndex = 0;
        var textStart = 0;
        var text = block.Text;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != MainWindow.ReaderLinuxTextFallbackFootnoteMarker)
                continue;

            AddRun(text[textStart..index]);
            if (footnoteIndex < block.InlineFootnotes.Count)
            {
                var footnote = block.InlineFootnotes[footnoteIndex++];
                Inlines?.Add(new InlineUIContainer(CreateFootnoteButton(footnote)));
            }
            else
            {
                AddRun("注");
            }
            textStart = index + 1;
        }

        AddRun(text[textStart..]);
    }

    private void AddRun(string value)
    {
        if (value.Length > 0)
            Inlines?.Add(new Run(value));
    }

    private Button CreateFootnoteButton(MainWindow.ReaderLinuxTextFallbackFootnote footnote)
    {
        var button = new Button
        {
            Content = footnote.Label,
            Tag = footnote.Href,
            Focusable = true,
            Padding = new Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        button.Classes.Add("readerFootnoteMarker");
        button.PointerEntered += HandleFootnotePointerEntered;
        button.PointerExited += HandleFootnotePointerExited;
        return button;
    }

    private void HandleFootnotePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button button)
            FootnotePointerEntered?.Invoke(button, e);
    }

    private void HandleFootnotePointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Button button)
            FootnotePointerExited?.Invoke(button, e);
    }

}
