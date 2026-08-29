using SkiaSharp;

namespace Kkindle.Layout;

public sealed class TypesetPaintTheme
{
    public static readonly TypesetPaintTheme Paper = new()
    {
        Background = new SKColor(0xFF, 0xFF, 0xFF),
        Text = new SKColor(0x11, 0x11, 0x11),
        Muted = new SKColor(0x22, 0x22, 0x22),
        Selection = new SKColor(0x00, 0x00, 0x00),
        SelectionText = new SKColor(0xFF, 0xFF, 0xFF),
        Highlight = new SKColor(0xE8, 0xE8, 0xE4),
        SearchMark = new SKColor(0xD8, 0xD8, 0xD0),
        Rule = new SKColor(0x22, 0x22, 0x22, 0x3D),
    };

    public SKColor Background { get; init; }
    public SKColor Text { get; init; }
    public SKColor Muted { get; init; }
    public SKColor Selection { get; init; }
    public SKColor SelectionText { get; init; }
    public SKColor Highlight { get; init; }
    public SKColor SearchMark { get; init; }
    public SKColor Rule { get; init; }
}

/// <summary>
/// One painted page: overlay bands (selection/annotations/search), structural
/// decorations, images and finally glyph runs. Glyph ids come straight from
/// HarfBuzz shaping; Skia renders them with the same font file, so what is
/// measured is what is drawn.
/// </summary>
public sealed class TypesetPainter
{
    private readonly TypesetFontLibrary _fonts;
    private readonly TypesetPaintTheme _theme;
    private readonly Func<string, SKImage?>? _imageResolver;

    public TypesetPainter(
        TypesetFontLibrary fonts,
        TypesetPaintTheme? theme = null,
        Func<string, SKImage?>? imageResolver = null)
    {
        _fonts = fonts;
        _theme = theme ?? TypesetPaintTheme.Paper;
        _imageResolver = imageResolver;
    }

    public void Paint(
        SKCanvas canvas,
        LayoutPage page,
        IReadOnlyList<SKRect>? selectionBands = null,
        IReadOnlyList<SKRect>? highlightBands = null,
        IReadOnlyList<SKRect>? searchBands = null)
    {
        using var background = new SKPaint { Color = _theme.Background, Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, 0, page.Width, page.Height, background);

        if (highlightBands is not null)
        {
            using var highlight = new SKPaint { Color = _theme.Highlight, Style = SKPaintStyle.Fill };
            foreach (var band in highlightBands)
            {
                canvas.DrawRect(band, highlight);
            }
        }

        if (searchBands is not null)
        {
            using var mark = new SKPaint { Color = _theme.SearchMark, Style = SKPaintStyle.Fill };
            foreach (var band in searchBands)
            {
                canvas.DrawRect(band, mark);
            }
        }

        using var rulePaint = new SKPaint { Color = _theme.Rule, Style = SKPaintStyle.Fill, IsAntialias = true };
        foreach (var decoration in page.Decorations)
        {
            switch (decoration.Kind)
            {
                case DecorationKind.BlockquoteBar:
                    using (var bar = new SKPaint { Color = _theme.Muted, Style = SKPaintStyle.Fill, IsAntialias = true })
                    {
                        canvas.DrawRect(decoration.Rect, bar);
                    }

                    break;
                default:
                    canvas.DrawRect(decoration.Rect, rulePaint);
                    break;
            }
        }

        foreach (var image in page.Images)
        {
            var skImage = _imageResolver?.Invoke(image.Path);
            if (skImage is not null)
            {
                canvas.DrawImage(skImage, image.Rect);
            }
            else
            {
                using var placeholder = new SKPaint
                {
                    Color = _theme.Highlight,
                    Style = SKPaintStyle.Fill,
                };
                canvas.DrawRect(image.Rect, placeholder);
            }
        }

        // Selection is painted between the text background and the glyphs with
        // inverted glyph color, matching the WebKit reader's black-on-white
        // inverted selection.
        if (selectionBands is { Count: > 0 } bands)
        {
            using var selection = new SKPaint { Color = _theme.Selection, Style = SKPaintStyle.Fill };
            foreach (var band in bands)
            {
                canvas.DrawRect(band, selection);
            }
        }

        var inverted = selectionBands is { Count: > 0 };

        using var textFill = new SKPaint
        {
            Color = _theme.Text,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var textInverted = new SKPaint
        {
            Color = _theme.SelectionText,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

        foreach (var run in page.Runs)
        {
            if (run.Glyphs.Length == 0)
            {
                continue;
            }

            var typeface = _fonts.GetTypeface(run.FontPath);
            if (typeface is null)
            {
                continue;
            }

            using var font = new SKFont(typeface, run.FontSize * run.Scale);
            using var builder = new SKTextBlobBuilder();
            var buffer = builder.AllocatePositionedRun(font, run.Glyphs.Length);
            buffer.SetGlyphs(run.Glyphs);
#pragma warning disable CS0618 // SkiaSharp 3.119 has no non-obsolete accessor for positioned runs
            var positions = buffer.GetPositionSpan();
#pragma warning restore CS0618
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = new SKPoint(run.X[i], run.Y[i]);
            }

            using var blob = builder.Build();
            if (blob is null)
            {
                continue;
            }

            var fill = inverted ? textInverted : textFill;
            var syntheticBold = run.SyntheticBold || run.Style.Bold;

            if (run.Sideways)
            {
                canvas.Save();
                canvas.Translate(run.OriginX, run.OriginY);
                canvas.RotateDegrees(90);
                if (run.Style.Italic)
                {
                    canvas.Skew(-0.22f, 0f);
                }

                DrawRun(canvas, blob, font, fill, syntheticBold, run.FontSize * run.Scale);
                canvas.Restore();
                continue;
            }

            if (run.OriginX != 0f || run.OriginY != 0f)
            {
                canvas.Save();
                canvas.Translate(run.OriginX, run.OriginY);
                if (run.Style.Italic)
                {
                    canvas.Skew(-0.22f, 0f);
                }

                DrawRun(canvas, blob, font, fill, syntheticBold, run.FontSize * run.Scale);
                canvas.Restore();
                continue;
            }

            if (run.Style.Italic)
            {
                canvas.Save();
                canvas.Skew(-0.22f, 0f);
                DrawRun(canvas, blob, font, fill, syntheticBold, run.FontSize * run.Scale);
                canvas.Restore();
                continue;
            }

            DrawRun(canvas, blob, font, fill, syntheticBold, run.FontSize * run.Scale);
        }

        if (inverted)
        {
            // Nothing further: underlines/strikeouts drawn below already invert
            // with the selection bands when they overlap.
        }
    }

    private static void DrawRun(
        SKCanvas canvas,
        SKTextBlob blob,
        SKFont font,
        SKPaint fill,
        bool syntheticBold,
        float fontSize)
    {
        if (syntheticBold)
        {
            using var bold = new SKPaint
            {
                Color = fill.Color,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = MathF.Max(0.6f, fontSize * 0.028f),
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawText(blob, 0f, 0f, bold);
            return;
        }

        canvas.DrawText(blob, 0f, 0f, fill);
    }
}
