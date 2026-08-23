using System.Text.RegularExpressions;

namespace Kkindle;

// Title matching between reading-material groups (Kindle clipping headings or
// local annotation book titles) and the cover dictionaries built from the PC
// library and the scanned Kindle device books.
internal static partial class ReadingMaterialCoverMatcher
{
    [GeneratedRegex(@"[（(][^）)]{0,100}[）)]")]
    private static partial Regex ParentheticalPattern();

    public static string NormalizeTitle(string value)
    {
        var withoutParenthetical = ParentheticalPattern().Replace(value, string.Empty);
        return new string(withoutParenthetical
            .Where(character => char.IsLetterOrDigit(character) || character >= '一' && character <= '鿿')
            .ToArray())
            .ToLowerInvariant();
    }

    public static bool AreTitlesRelated(string left, string right)
    {
        var normalizedLeft = NormalizeTitle(left);
        var normalizedRight = NormalizeTitle(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0) return false;
        // Short titles ("三体", "活着") are common; equal normalized titles are
        // the same book regardless of length.
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)) return true;
        // One-sided containment needs longer strings so "三体" does not match "新三体".
        return normalizedLeft.Length >= 4
            && normalizedRight.Length >= 4
            && (normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase)
                || normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase));
    }
}
