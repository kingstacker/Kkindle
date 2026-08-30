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
        IReadOnlyList<SKRect>? searchBands = null,
        IReadOnlyList<TypesetAnnotationOverlay>? annotationOverlays = null,
        bool showVerticalDebugBoxes = false,
        IReadOnlyList<SKRect>? focusedSearchBands = null)
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
            // Secondary search hits stay on the muted paper-tone band with
            // ordinary text so the focused hit stands out.
            using var mark = new SKPaint { Color = _theme.SearchMark, Style = SKPaintStyle.Fill };
            foreach (var band in searchBands)
            {
                canvas.DrawRect(band, mark);
            }
        }

        if (focusedSearchBands is not null)
        {
            // The focused search hit (Ctrl+F current match, or every hit of a
            // whole-book result jump when no focus exists) uses the same
            // black-white inversion as the selection: an ink band whose
            // glyphs repaint in the paper colour, so the keyword stays
            // findable in dense vertical body text.
            using var mark = new SKPaint { Color = _theme.Selection, Style = SKPaintStyle.Fill };
            foreach (var band in focusedSearchBands)
            {
                canvas.DrawRect(band, mark);
            }
        }

        var markerBands = PaintAnnotationMarkers(canvas, annotationOverlays);

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
                DrawPlacedImage(canvas, skImage, image);
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

        // Selection, the focused search hit and annotation markers are
        // painted between the text background and the glyphs with inverted
        // glyph color, matching the WebKit reader's black-on-white inverted
        // selection.
        var inversionBands = new List<SKRect>();
        if (selectionBands is { Count: > 0 })
        {
            inversionBands.AddRange(selectionBands);
        }

        if (focusedSearchBands is { Count: > 0 })
        {
            inversionBands.AddRange(focusedSearchBands);
        }

        if (markerBands.Count > 0)
        {
            inversionBands.InsertRange(0, markerBands);
        }

        if (inversionBands.Count == 0)
        {
            inversionBands = null;
        }

        if (selectionBands is { Count: > 0 } bands)
        {
            using var selection = new SKPaint { Color = _theme.Selection, Style = SKPaintStyle.Fill };
            foreach (var band in bands)
            {
                canvas.DrawRect(band, selection);
            }
        }
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
            var glyphCount = Math.Min(run.Glyphs.Length, run.X.Length);
            if (glyphCount == 0)
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
            var buffer = builder.AllocatePositionedRun(font, glyphCount);
            buffer.SetGlyphs(run.Glyphs.AsSpan(0, glyphCount));
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

            var syntheticBold = run.SyntheticBold || run.Style.Bold;
            DrawPlacedBlob(canvas, run, font, blob, textFill, syntheticBold, run.FontSize * run.Scale);

            // Paint only selected or marker-covered glyphs in the inverted
            // color. The old implementation inverted every run on a page as
            // soon as a selection existed, making unrelated text unreadable.
            if (inversionBands is not null)
            {
                for (var glyphIndex = 0; glyphIndex < glyphCount; glyphIndex++)
                {
                    var glyphRect = ChapterLayoutInteraction.GetGlyphRect(page, run, glyphIndex);
                    if (!inversionBands.Any(band => band.IntersectsWith(glyphRect)))
                    {
                        continue;
                    }

                    using var glyphBuilder = new SKTextBlobBuilder();
                    var glyphBuffer = glyphBuilder.AllocatePositionedRun(font, 1);
                    glyphBuffer.SetGlyphs(new[] { run.Glyphs[glyphIndex] });
#pragma warning disable CS0618 // SkiaSharp 3.119 has no non-obsolete accessor for positioned runs
                    var glyphPositions = glyphBuffer.GetPositionSpan();
#pragma warning restore CS0618
                    glyphPositions[0] = new SKPoint(
                        run.X[glyphIndex],
                        glyphIndex < run.Y.Length ? run.Y[glyphIndex] : 0f);
                    using var glyphBlob = glyphBuilder.Build();
                    if (glyphBlob is not null)
                    {
                        DrawPlacedBlob(canvas, run, font, glyphBlob, textInverted, syntheticBold, run.FontSize * run.Scale);
                    }
                }
            }
        }

        // Annotation lines are deliberately painted after the glyphs. A
        // pre-glyph line can be completely hidden by a descender when the
        // font's ink extends beyond the approximate character rectangle.
        PaintAnnotationUnderlines(canvas, page, annotationOverlays);

        if (showVerticalDebugBoxes && page.WritingMode == TypesetWritingMode.VerticalRl)
        {
            PaintDebugBoxes(canvas, page);
        }
    }

    private static void PaintDebugBoxes(SKCanvas canvas, LayoutPage page)
    {
        using var hanCell = CreateDebugPaint(new SKColor(22, 163, 74, 220));
        using var compatibilityCell = CreateDebugPaint(new SKColor(220, 38, 38, 225));
        using var glyph = CreateDebugPaint(new SKColor(37, 99, 235, 225));

        foreach (var box in page.DebugBoxes)
        {
            if (box.Rect.Width <= 0f || box.Rect.Height <= 0f)
            {
                continue;
            }

            var paint = box.Kind switch
            {
                TypesetDebugBoxKind.CompatibilityCell => compatibilityCell,
                TypesetDebugBoxKind.Glyph => glyph,
                _ => hanCell,
            };
            canvas.DrawRect(box.Rect, paint);
        }
    }

    private static SKPaint CreateDebugPaint(SKColor color) => new()
    {
        Color = color,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f,
        IsAntialias = true,
    };

    private List<SKRect> PaintAnnotationMarkers(
        SKCanvas canvas,
        IReadOnlyList<TypesetAnnotationOverlay>? overlays)
    {
        var bands = new List<SKRect>();
        if (overlays is not { Count: > 0 })
        {
            return bands;
        }

        // The 荧光标记（黑白反色） style is a black-white inversion: a solid
        // ink band whose glyphs are repainted in the paper colour, exactly
        // like the selection rendering below.
        using var marker = new SKPaint
        {
            Color = _theme.Selection,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        foreach (var overlay in overlays)
        {
            if (NormalizeAnnotationStyle(overlay.Style) != "marker")
            {
                continue;
            }

            foreach (var band in overlay.Bands)
            {
                if (band.Width > 0 && band.Height > 0)
                {
                    canvas.DrawRect(band, marker);
                    bands.Add(band);
                }
            }
        }

        return bands;
    }

    private static void PaintAnnotationUnderlines(
        SKCanvas canvas,
        LayoutPage page,
        IReadOnlyList<TypesetAnnotationOverlay>? overlays)
    {
        if (overlays is not { Count: > 0 })
        {
            return;
        }

        foreach (var overlay in overlays)
        {
            var style = NormalizeAnnotationStyle(overlay.Style);
            if (style == "marker")
            {
                continue;
            }

            foreach (var band in overlay.Bands)
            {
                if (band.Width <= 0 || band.Height <= 0)
                {
                    continue;
                }

                if (page.WritingMode == TypesetWritingMode.VerticalRl)
                {
                    DrawVerticalUnderline(canvas, band, overlay.Color, style);
                }
                else
                {
                    DrawHorizontalUnderline(canvas, band, overlay.Color, style);
                }
            }
        }
    }

    private static void DrawHorizontalUnderline(SKCanvas canvas, SKRect band, SKColor color, string style)
    {
        // GetGlyphRect includes a small descender allowance. Put the line at
        // the lower edge of that allowance; painting after the glyphs keeps it
        // visible even for fonts with unusually deep descenders.
        var thickness = Math.Clamp(band.Height * 0.055f, 1.1f, 1.8f);
        var y = band.Bottom + Math.Clamp(band.Height * 0.025f, 0.7f, 1.5f);
        var paint = CreateUnderlinePaint(color, thickness);
        switch (style)
        {
            case "double":
            {
                var gap = Math.Max(1.5f, thickness * 0.9f);
                canvas.DrawLine(band.Left, y, band.Right, y, paint);
                canvas.DrawLine(band.Left, y + thickness + gap, band.Right, y + thickness + gap, paint);
                break;
            }
            case "dashed":
                DrawDashedHorizontal(canvas, band.Left, band.Right, y, paint);
                break;
            case "dotted":
                DrawDottedHorizontal(canvas, band.Left, band.Right, y, color, thickness);
                break;
            case "wavy":
                DrawWavyHorizontal(canvas, band.Left, band.Right, y, paint, band.Height);
                break;
            default:
                canvas.DrawLine(band.Left, y, band.Right, y, paint);
                break;
        }

        paint.Dispose();
    }

    private static void DrawVerticalUnderline(SKCanvas canvas, SKRect band, SKColor color, string style)
    {
        var thickness = Math.Clamp(band.Width * 0.055f, 1.1f, 1.8f);
        // Match the native vertical composer’s underline side: the line sits
        // immediately to the left of the upright glyph column.
        var x = band.Left - Math.Clamp(band.Width * 0.08f, 1.5f, 3.0f);
        var paint = CreateUnderlinePaint(color, thickness);
        switch (style)
        {
            case "double":
            {
                var gap = Math.Max(1.5f, thickness * 0.9f);
                canvas.DrawLine(x, band.Top, x, band.Bottom, paint);
                canvas.DrawLine(x - thickness - gap, band.Top, x - thickness - gap, band.Bottom, paint);
                break;
            }
            case "dashed":
                DrawDashedVertical(canvas, x, band.Top, band.Bottom, paint);
                break;
            case "dotted":
                DrawDottedVertical(canvas, x, band.Top, band.Bottom, color, thickness);
                break;
            case "wavy":
                DrawWavyVertical(canvas, x, band.Top, band.Bottom, paint, band.Width);
                break;
            default:
                canvas.DrawLine(x, band.Top, x, band.Bottom, paint);
                break;
        }

        paint.Dispose();
    }

    private static SKPaint CreateUnderlinePaint(SKColor color, float thickness) => new()
    {
        Color = color,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = thickness,
        StrokeCap = SKStrokeCap.Round,
        IsAntialias = true,
    };

    private static void DrawDashedHorizontal(SKCanvas canvas, float left, float right, float y, SKPaint paint)
    {
        const float dash = 6f;
        const float gap = 4f;
        for (var x = left; x < right; x += dash + gap)
        {
            canvas.DrawLine(x, y, Math.Min(right, x + dash), y, paint);
        }
    }

    private static void DrawDashedVertical(SKCanvas canvas, float x, float top, float bottom, SKPaint paint)
    {
        const float dash = 6f;
        const float gap = 4f;
        for (var y = top; y < bottom; y += dash + gap)
        {
            canvas.DrawLine(x, y, x, Math.Min(bottom, y + dash), paint);
        }
    }

    private static void DrawDottedHorizontal(
        SKCanvas canvas,
        float left,
        float right,
        float y,
        SKColor color,
        float thickness)
    {
        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        var radius = Math.Max(0.8f, thickness * 0.7f);
        var spacing = Math.Max(3.5f, radius * 3.8f);
        for (var x = left + radius; x <= right - radius + 0.01f; x += spacing)
        {
            canvas.DrawCircle(x, y, radius, paint);
        }
    }

    private static void DrawDottedVertical(
        SKCanvas canvas,
        float x,
        float top,
        float bottom,
        SKColor color,
        float thickness)
    {
        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        var radius = Math.Max(0.8f, thickness * 0.7f);
        var spacing = Math.Max(3.5f, radius * 3.8f);
        for (var y = top + radius; y <= bottom - radius + 0.01f; y += spacing)
        {
            canvas.DrawCircle(x, y, radius, paint);
        }
    }

    private static void DrawWavyHorizontal(
        SKCanvas canvas,
        float left,
        float right,
        float y,
        SKPaint paint,
        float bandHeight)
    {
        var amplitude = Math.Clamp(bandHeight * 0.07f, 0.9f, 1.8f);
        const float wavelength = 8f;
        const float step = 1.5f;
        using var path = new SKPath();
        path.MoveTo(left, y);
        for (var x = left + step; x < right; x += step)
        {
            var phase = (x - left) / wavelength * MathF.PI * 2f;
            path.LineTo(x, y + MathF.Sin(phase) * amplitude);
        }

        var endPhase = (right - left) / wavelength * MathF.PI * 2f;
        path.LineTo(right, y + MathF.Sin(endPhase) * amplitude);
        canvas.DrawPath(path, paint);
    }

    private static void DrawWavyVertical(
        SKCanvas canvas,
        float x,
        float top,
        float bottom,
        SKPaint paint,
        float bandWidth)
    {
        var amplitude = Math.Clamp(bandWidth * 0.07f, 0.9f, 1.8f);
        const float wavelength = 8f;
        const float step = 1.5f;
        using var path = new SKPath();
        path.MoveTo(x, top);
        for (var y = top + step; y < bottom; y += step)
        {
            var phase = (y - top) / wavelength * MathF.PI * 2f;
            path.LineTo(x + MathF.Sin(phase) * amplitude, y);
        }

        var endPhase = (bottom - top) / wavelength * MathF.PI * 2f;
        path.LineTo(x + MathF.Sin(endPhase) * amplitude, bottom);
        canvas.DrawPath(path, paint);
    }

    private static string NormalizeAnnotationStyle(string? style) =>
        style?.Trim().ToLowerInvariant() switch
        {
            "double" => "double",
            "dashed" => "dashed",
            "dotted" => "dotted",
            "wavy" => "wavy",
            "marker" => "marker",
            _ => "solid",
        };

    private static void DrawPlacedImage(SKCanvas canvas, SKImage image, PlacedImage placed)
    {
        if (Math.Abs(placed.RotationDegrees - 90f) < 0.01f)
        {
            // Rect is the final, rotated bounding box. Draw the source image
            // in its original orientation, then rotate it clockwise around the
            // box's top-right corner so vertical formulas follow the same
            // sideways convention as Latin runs.
            canvas.Save();
            canvas.Translate(placed.Rect.Right, placed.Rect.Top);
            canvas.RotateDegrees(90f);
            canvas.DrawImage(
                image,
                new SKRect(0f, 0f, placed.Rect.Height, placed.Rect.Width));
            canvas.Restore();
            return;
        }

        canvas.DrawImage(image, placed.Rect);
    }

    private static void DrawPlacedBlob(
        SKCanvas canvas,
        PlacedRun run,
        SKFont font,
        SKTextBlob blob,
        SKPaint fill,
        bool syntheticBold,
        float fontSize)
    {
        if (run.Sideways)
        {
            canvas.Save();
            canvas.Translate(run.OriginX, run.OriginY);
            canvas.RotateDegrees(90);
            if (run.Style.Italic)
            {
                canvas.Skew(-0.22f, 0f);
            }

            DrawRun(canvas, blob, font, fill, syntheticBold, fontSize);
            canvas.Restore();
            return;
        }

        if (run.OriginX != 0f || run.OriginY != 0f)
        {
            canvas.Save();
            canvas.Translate(run.OriginX, run.OriginY);
            if (run.Style.Italic)
            {
                canvas.Skew(-0.22f, 0f);
            }

            DrawRun(canvas, blob, font, fill, syntheticBold, fontSize);
            canvas.Restore();
            return;
        }

        if (run.Style.Italic)
        {
            canvas.Save();
            canvas.Skew(-0.22f, 0f);
            DrawRun(canvas, blob, font, fill, syntheticBold, fontSize);
            canvas.Restore();
            return;
        }

        DrawRun(canvas, blob, font, fill, syntheticBold, fontSize);
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
