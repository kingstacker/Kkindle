using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Xml.Linq;
using SkiaSharp;

namespace Kkindle.Infrastructure;

/// <summary>Local Song-type covers with grayscale landscapes and a vermilion seal.</summary>
public static class TitleCoverService
{
    private const float SealLeft = 82f;
    private const float SealTop = 740f;
    private const float SealSize = 58f;

    /// <summary>Recognizes our embedded TXT cover while preserving user-replaced artwork.</summary>
    public static bool IsGeneratedTxtCover(string epubPath, string? storedCoverPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(epubPath);
            var package = zip.GetEntry("OEBPS/content.opf");
            var cover = zip.GetEntry("OEBPS/cover.png");
            if (package is null || cover is null || zip.GetEntry("OEBPS/chapter-00001.xhtml") is null) return false;
            using var packageStream = package.Open();
            var xml = XDocument.Load(packageStream);
            if (!xml.Descendants().Any(e => e.Name.LocalName == "identifier" && e.Value.StartsWith("urn:sha256:", StringComparison.Ordinal))) return false;
            if (string.IsNullOrWhiteSpace(storedCoverPath) || !File.Exists(storedCoverPath)) return true;
            if (cover.Length != new FileInfo(storedCoverPath).Length || cover.Length > 10 * 1024 * 1024) return false;
            using var embedded = cover.Open();
            using var buffer = new MemoryStream();
            embedded.CopyTo(buffer);
            return buffer.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(storedCoverPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { return false; }
    }

    public static byte[] CreatePng(string title)
    {
        title = string.IsNullOrWhiteSpace(title) ? "无题" : title.Trim();
        var titleHash = SHA256.HashData(Encoding.UTF8.GetBytes(title));
        var ink = SKColor.Parse("#282828");
        using var surface = SKSurface.Create(new SKImageInfo(600, 900));
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse("#F7F7F7"));
        using var paint = new SKPaint { IsAntialias = true };
        // Stable, low-contrast paper grain keeps the cover quiet at thumbnail size.
        var random = new Random(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(titleHash));
        paint.Color = ink.WithAlpha(9);
        for (var i = 0; i < 5000; i++)
            canvas.DrawCircle(random.Next(600), random.Next(900), 0.3f + (float)random.NextDouble() * 0.7f, paint);
        DrawLandscape(canvas, ink);
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
        using var face = File.Exists(bundled) ? SKTypeface.FromFile(bundled)
            : SKFontManager.Default.MatchCharacter(title.EnumerateRunes().First().Value) ?? SKTypeface.Default;
        using var font = new SKFont(face, 76);
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(title);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        List<string> Wrap()
        {
            var lines = new List<string>();
            var line = "";
            foreach (var element in elements)
            {
                if (line.Length > 0 && font.MeasureText(line + element) > 420) { lines.Add(line.Trim()); line = ""; }
                line += element;
            }
            if (line.Length > 0) lines.Add(line.Trim());
            return lines;
        }
        var lines = Wrap();
        while (lines.Count * font.Size * 1.45f > 300 && font.Size > 12) { font.Size -= 2; lines = Wrap(); }
        var lineHeight = font.Size * 1.45f;
        var baseline = 250 - font.Metrics.Ascent;
        paint.Color = ink;
        foreach (var line in lines)
        {
            canvas.DrawText(line, 80, baseline, SKTextAlign.Left, font, paint);
            baseline += lineHeight;
        }
        // Keep the vermilion collector's seal fixed in the lower-left so title
        // wrapping never changes its position from one book cover to another.
        var red = SKColor.Parse("#A84C3B");
        paint.Color = red;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 3;
        canvas.DrawRect(SealLeft, SealTop, SealSize, SealSize, paint);
        paint.StrokeWidth = 1.2f;
        canvas.DrawRect(SealLeft + 4, SealTop + 4, SealSize - 8, SealSize - 8, paint);
        paint.Style = SKPaintStyle.Fill;
        using var sealFont = new SKFont(face, 38);
        var sealTextY = SealTop + SealSize / 2 - (sealFont.Metrics.Ascent + sealFont.Metrics.Descent) / 2;
        canvas.DrawText("藏", SealLeft + SealSize / 2, sealTextY,
            SKTextAlign.Center, sealFont, paint);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void DrawLandscape(SKCanvas canvas, SKColor ink)
    {
        using var paint = new SKPaint { IsAntialias = true };
        // Overlapping curved silhouettes dissolve into the paper like distant ink washes.
        for (var layer = 0; layer < 3; layer++)
        {
            using var path = new SKPath();
            var y = 630 + layer * 66;
            path.MoveTo(-20, y + 105);
            path.CubicTo(45, y + 80, 70, y + 20, 130, y + 55);
            path.CubicTo(175, y + 90, 206, y - 18, 251, y + 12);
            path.CubicTo(302, y + 54, 323, y - 60, 372, y - 12);
            path.CubicTo(421, y + 25, 470, y - 110, 513, y - 58);
            path.CubicTo(550, y - 18, 585, y + 20, 620, y + 5);
            path.LineTo(620, 930); path.LineTo(-20, 930); path.Close();
            using var wash = SKShader.CreateLinearGradient(new SKPoint(0, y - 65), new SKPoint(0, 930),
                new[] { ink.WithAlpha((byte)(24 + layer * 13)), ink.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
            paint.Shader = wash;
            canvas.DrawPath(path, paint);
        }
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 0.8f;
        paint.Color = ink.WithAlpha(28);
        for (var i = 0; i < 4; i++)
        {
            using var water = new SKPath();
            var y = 807 + i * 13;
            water.MoveTo(90 + i * 17, y);
            water.CubicTo(178, y - 5, 246, y + 5, 360 - i * 23, y);
            canvas.DrawPath(water, paint);
        }
    }
}
