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
            || change.Property == ChapterTitleLengthProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        // Avalonia's TextAlignment.Justify also stretches the final line of a
        // paragraph. That makes a one-line heading look like a row of
        // separated characters. Keep the base layout left-aligned for hit
        // testing and selection, then justify only soft-wrapped lines here;
        // explicit paragraph endings and the final line retain normal spacing.
        var selectionLength = Math.Abs(SelectionEnd - SelectionStart);
        if (Inlines is not { Count: > 0 } && TextLayout is { } naturalLayout)
        {
            DrawNaturalJustifiedLayout(context, origin, naturalLayout);
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

        // Inline footnote controls have their own embedded layout, so retain
        // Avalonia's renderer for those exceptional blocks.
        base.RenderTextLayout(context, origin);
    }

    private void DrawNaturalJustifiedLayout(
        DrawingContext context,
        Point origin,
        TextLayout layout,
        IBrush? foreground = null)
    {
        PrepareNaturalJustification(layout);
        var chapterTitleLayout = CreateChapterTitleLayout(foreground);
        var lineOffset = 0d;
        for (var lineIndex = 0; lineIndex < layout.TextLines.Count; lineIndex++)
        {
            var line = layout.TextLines[lineIndex];
            if (chapterTitleLayout is not null && IsChapterTitleLine(line, lineIndex))
                chapterTitleLayout.Draw(context, origin + new Vector(0, lineOffset));
            else
                line.Draw(context, origin + new Vector(0, lineOffset));
            lineOffset += line.Height;
        }
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
        if (ChapterTitleStart < 0 || ChapterTitleLength <= 0)
            return false;

        // In a paged TextLayout the first line of a page can expose a local
        // start of zero for subsequent lines as well. The title range is
        // always placed at the beginning of its page by the paginator, so use
        // the line ordinal for that stable case and never repaint the same
        // title once per line.
        if (ChapterTitleStart == 0)
            return lineIndex == 0;

        var titleEnd = ChapterTitleStart + ChapterTitleLength;
        var lineStart = line.Start;
        var lineEnd = line.Start + line.Length - line.NewLineLength;
        return lineStart < titleEnd && lineEnd > ChapterTitleStart;
    }

    private TextLayout? CreateChapterTitleLayout(IBrush? foreground)
    {
        if (ChapterTitleStart < 0 || ChapterTitleLength <= 0)
            return null;

        var text = Text ?? string.Empty;
        if (ChapterTitleStart >= text.Length)
            return null;

        var length = Math.Min(ChapterTitleLength, text.Length - ChapterTitleStart);
        var title = text.Substring(ChapterTitleStart, length).TrimEnd('\r', '\n');
        return string.IsNullOrWhiteSpace(title)
            ? null
            : CreateStyledLayout(
                title,
                foreground ?? Foreground ?? Brushes.Black,
                FontWeight.Bold,
                TextAlignment.Center);
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
        TextAlignment textAlignment)
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
