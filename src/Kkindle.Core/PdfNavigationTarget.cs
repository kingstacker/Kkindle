using System.Globalization;

namespace Kkindle.Core;

/// <summary>Reads the portable page destination encoded in a PDF URI.</summary>
public static class PdfNavigationTarget
{
    public static int ReadPage(Uri uri) => int.TryParse(ReadValue(uri, "page"), out var page) ? Math.Max(1, page) : 1;

    public static double ReadTop(Uri uri) =>
        double.TryParse(ReadValue(uri, "top"), NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
        && double.IsFinite(top) ? Math.Clamp(top, 0, 1) : 0;

    public static int ReadOffset(Uri uri) =>
        int.TryParse(ReadValue(uri, "offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            ? Math.Max(0, offset) : -1;

    private static string? ReadValue(Uri uri, string key) => uri.Fragment.TrimStart('#').Split('&')
        .Select(part => part.Split('=', 2)).FirstOrDefault(parts => parts.Length == 2 && parts[0] == key)?[1];
}
