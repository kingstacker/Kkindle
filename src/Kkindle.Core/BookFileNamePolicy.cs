namespace Kkindle.Core;

/// <summary>
/// Keeps generated book variants recognizable when a library book is renamed.
/// The file contents and BookFile identity remain unchanged; only the local
/// display name is rebuilt from the new book title and the known variant kind.
/// </summary>
public static class BookFileNamePolicy
{
    private const int MaximumStemLength = 180;
    private static readonly (string Marker, string CanonicalSuffix)[] VariantMarkers =
    [
        ("_单译版", "_单译版"),
        ("-单译版", "_单译版"),
        ("_双语版", "_双语版"),
        ("-双语版", "_双语版"),
        ("_译文", "_单译版"),
        ("-译文", "_单译版"),
        ("_双语", "_双语版"),
        ("-双语", "_双语版"),
        (PinyinBookPolicy.GeneratedTitleSuffix, PinyinBookPolicy.GeneratedTitleSuffix),
        ("+pinyin", PinyinBookPolicy.GeneratedTitleSuffix),
        ("-拼音版", PinyinBookPolicy.GeneratedTitleSuffix),
        ("_拼音版", PinyinBookPolicy.GeneratedTitleSuffix)
    ];

    public static string CreateRenamedFileName(string? currentFileName, string title)
    {
        var extension = Path.GetExtension(currentFileName ?? string.Empty);
        var stem = Path.GetFileNameWithoutExtension(currentFileName ?? string.Empty);
        var duplicateSuffix = TakeDuplicateSuffix(ref stem);
        var variantSuffix = FindVariantSuffix(stem);
        var baseName = SanitizeTitle(title);
        var maximumBaseLength = Math.Max(1, MaximumStemLength - variantSuffix.Length - duplicateSuffix.Length);
        if (baseName.Length > maximumBaseLength)
            baseName = baseName[..maximumBaseLength].TrimEnd();

        return $"{baseName}{variantSuffix}{duplicateSuffix}{extension}";
    }

    private static string FindVariantSuffix(string stem)
    {
        foreach (var (marker, canonicalSuffix) in VariantMarkers)
        {
            if (stem.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                return canonicalSuffix;
        }

        // Legacy files sometimes used "bookpinyin" without a separator.
        return stem.Length > "pinyin".Length
            && stem.EndsWith("pinyin", StringComparison.OrdinalIgnoreCase)
            ? PinyinBookPolicy.GeneratedTitleSuffix
            : string.Empty;
    }

    private static string TakeDuplicateSuffix(ref string stem)
    {
        if (!stem.EndsWith(')')) return string.Empty;
        var open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0
            || open + 3 >= stem.Length
            || !int.TryParse(stem.AsSpan(open + 2, stem.Length - open - 3), out var number)
            || number < 2)
        {
            return string.Empty;
        }

        var suffix = stem[open..];
        stem = stem[..open];
        return suffix;
    }

    private static string SanitizeTitle(string title)
    {
        var value = (title ?? string.Empty).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        value = string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character))
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "未命名书籍" : value;
    }
}
