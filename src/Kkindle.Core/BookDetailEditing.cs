using System.Globalization;

namespace Kkindle.Core;

public static class BookDetailEditing
{
    public const string DeveloperPassword = "1314";

    public static bool TryParseRating(string? text, out double? rating)
    {
        rating = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value > 10) return false;
        rating = value;
        return true;
    }
}
