using System.Globalization;

namespace Kkindle.Infrastructure;

/// <summary>Portable anchor encoding used by PDF point annotations.</summary>
public static class ReaderPdfPointAnchor
{
    private const string Prefix = "pdf-point:";

    public static string Encode(double x, double y)
    {
        x = Math.Clamp(double.IsFinite(x) ? x : 0.5, 0, 1);
        y = Math.Clamp(double.IsFinite(y) ? y : 0.5, 0, 1);
        return Prefix
            + x.ToString("R", CultureInfo.InvariantCulture)
            + ","
            + y.ToString("R", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? value, out double x, out double y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = value[Prefix.Length..].Split(',', 2);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            && double.IsFinite(x) && double.IsFinite(y)
            && x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }
}
