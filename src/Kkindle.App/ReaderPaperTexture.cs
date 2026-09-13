using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using SkiaSharp;

namespace Kkindle;

/// <summary>
/// Two seamless, deterministic texture tiles shared for the process lifetime.
/// Theme/strength changes only change brushes and opacity; neither textures nor
/// chapter layouts are regenerated during slider drags or page turns.
/// </summary>
public static class ReaderPaperTexture
{
    private const int TileSize = 768;
    private static readonly Lazy<Layer> Grain = new(() => CreateLayer(fibers: false));
    private static readonly Lazy<Layer> Fibers = new(() => CreateLayer(fibers: true));

    private sealed record Layer(SKImage SkiaImage, Bitmap Image);

    public static IBrush CreateBrush(Color color, ReaderAppearanceSettings settings, double strengthScale = 1)
    {
        var strength = settings.EffectivePaperStrength * strengthScale;
        if (strength <= 0) return new SolidColorBrush(color);

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var rect = new Rect(0, 0, TileSize, TileSize);
            context.FillRectangle(new SolidColorBrush(color), rect);
            using (context.PushOpacity(strength))
            {
                // DrawingGroup records geometry brushes; its bitmap recording
                // overload is not implemented by Avalonia. ImageBrush keeps
                // the shared bitmap in the retained drawing without copying it.
                context.FillRectangle(new ImageBrush(Grain.Value.Image) { Stretch = Stretch.Fill }, rect);
                if (settings.FibersEnabled)
                    context.FillRectangle(new ImageBrush(Fibers.Value.Image) { Stretch = Stretch.Fill }, rect);
            }
        }
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(0, 0, TileSize, TileSize, RelativeUnit.Absolute)
        };
    }

    public static void Paint(SKCanvas canvas, SKRect bounds, Color color, ReaderAppearanceSettings settings)
    {
        using var background = new SKPaint { Color = ReaderPalette.ToSkia(color) };
        canvas.DrawRect(bounds, background);
        var strength = settings.EffectivePaperStrength;
        if (strength <= 0) return;
        PaintLayer(canvas, bounds, Grain.Value, strength);
        if (settings.FibersEnabled) PaintLayer(canvas, bounds, Fibers.Value, strength);
    }

    private static void PaintLayer(SKCanvas canvas, SKRect bounds, Layer layer, double strength)
    {
        using var shader = layer.SkiaImage.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        using var paint = new SKPaint
        {
            Shader = shader,
            Color = SKColors.White.WithAlpha((byte)Math.Round(255 * strength))
        };
        canvas.DrawRect(bounds, paint);
    }

    private static Layer CreateLayer(bool fibers)
    {
        using var bitmap = new SKBitmap(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        if (fibers)
        {
            var random = new Random(20260912);
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round
            };
            for (var i = 0; i < 1500; i++)
            {
                var x = (float)(random.NextDouble() * TileSize);
                var y = (float)(random.NextDouble() * TileSize);
                var length = (float)(2 + random.NextDouble() * 13);
                var angle = random.NextDouble() * Math.PI;
                var dx = (float)Math.Cos(angle) * length;
                var dy = (float)Math.Sin(angle) * length;
                paint.Color = (i % 4 == 0 ? SKColors.White : SKColors.Black)
                    .WithAlpha((byte)random.Next(14, 43));
                paint.StrokeWidth = (float)(0.35 + random.NextDouble() * 0.45);
                using var path = new SKPath();
                path.MoveTo(x, y);
                path.QuadTo(x + dx * 0.45f - dy * 0.12f, y + dy * 0.45f + dx * 0.12f, x + dx, y + dy);
                // Wrapping the edge strokes makes every repetition seamless.
                for (var ty = -1; ty <= 1; ty++)
                for (var tx = -1; tx <= 1; tx++)
                {
                    canvas.Save();
                    canvas.Translate(tx * TileSize, ty * TileSize);
                    canvas.DrawPath(path, paint);
                    canvas.Restore();
                }
            }
        }
        else
        {
            var pixels = new byte[TileSize * TileSize * 4];
            for (var y = 0; y < TileSize; y++)
            for (var x = 0; x < TileSize; x++)
            {
                var variation = 14 * Noise(x, y, 6, 17)
                    + 8 * Noise(x, y, 24, 71)
                    + 4 * Noise(x, y, 96, 133)
                    + 12 * Hash(x, y, 239);
                var alpha = (byte)Math.Clamp((int)Math.Round(Math.Abs(variation)), 0, 45);
                var offset = (y * TileSize + x) * 4;
                // Premultiplied white brightens; black darkens. The original
                // palette stays visible through the tiny alpha variations.
                var channel = variation > 0 ? alpha : (byte)0;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = channel;
                pixels[offset + 3] = alpha;
            }
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        }
        canvas.Flush();
        var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new Layer(image, new Bitmap(stream));
    }

    private static double Noise(int x, int y, int cells, uint seed)
    {
        var fx = (double)x / TileSize * cells;
        var fy = (double)y / TileSize * cells;
        var ix = (int)fx;
        var iy = (int)fy;
        var tx = fx - ix;
        var ty = fy - iy;
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        var top = Lerp(Hash(ix, iy, seed), Hash((ix + 1) % cells, iy, seed), tx);
        var bottom = Lerp(Hash(ix, (iy + 1) % cells, seed), Hash((ix + 1) % cells, (iy + 1) % cells, seed), tx);
        return Lerp(top, bottom, ty);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Hash(int x, int y, uint seed)
    {
        unchecked
        {
            var value = (uint)x * 374761393u + (uint)y * 668265263u + seed * 1442695041u;
            value = (value ^ (value >> 13)) * 1274126177u;
            value ^= value >> 16;
            return value / (double)uint.MaxValue * 2 - 1;
        }
    }
}
