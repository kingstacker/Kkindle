using Kkindle.Core;

namespace Kkindle;

public sealed partial class NativePdfReaderHost
{
    private static void ApplyPageColors(byte[] pixels, ReaderPalette palette)
    {
        if (palette == ReaderPalette.For(ReaderTheme.Classic)) return;
        var red = new byte[256];
        var green = new byte[256];
        var blue = new byte[256];
        for (var value = 0; value < 256; value++)
        {
            red[value] = (byte)((palette.Ink.R * (255 - value) + palette.Page.R * value + 127) / 255);
            green[value] = (byte)((palette.Ink.G * (255 - value) + palette.Page.G * value + 127) / 255);
            blue[value] = (byte)((palette.Ink.B * (255 - value) + palette.Page.B * value + 127) / 255);
        }

        // White paper and neutral ink follow the reader theme, including scans
        // and explicit white fills. Saturated colors in figures stay unchanged;
        // a gradual blend avoids halos at antialiased color/gray boundaries.
        const int colorThreshold = 48;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            if (r == g && g == b)
            {
                pixels[index] = blue[b];
                pixels[index + 1] = green[g];
                pixels[index + 2] = red[r];
                continue;
            }
            var chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            if (chroma >= colorThreshold) continue;
            var gray = (r * 77 + g * 150 + b * 29 + 128) >> 8;
            var weight = colorThreshold - chroma;
            pixels[index] = (byte)((b * chroma + blue[gray] * weight + colorThreshold / 2) / colorThreshold);
            pixels[index + 1] = (byte)((g * chroma + green[gray] * weight + colorThreshold / 2) / colorThreshold);
            pixels[index + 2] = (byte)((r * chroma + red[gray] * weight + colorThreshold / 2) / colorThreshold);
        }
    }
}
