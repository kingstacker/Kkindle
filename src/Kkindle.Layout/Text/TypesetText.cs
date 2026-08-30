using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kkindle.Layout;

public enum TextUnitKind
{
    Cjk,
    LatinWord,
    Space,
    Punct,
}

public readonly record struct TextUnit(int Start, int Length, TextUnitKind Kind);

/// <summary>
/// GB/T 15834-style line-start/line-end prohibitions shared by both writing
/// modes, plus the unit segmentation used by the horizontal line breaker.
/// The sets are the same ones the previous WebKit reader enforced through
/// line-break:strict and the vertical fallback enforced through its policy.
/// </summary>
public static class TypesetText
{
    public const string ProhibitedAtLineStart =
        "、。，．：；？！‼⁇⁈⁉・‥…—―～ー々〻ゝゞヽヾ"
        + "）〕］｝〉》」』】〙〗〟’”｠»"
        + "︶︺︼︾﹀﹈﹂﹄"
        + "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮ"
        + ",.:;?!)]}";

    public const string ProhibitedAtLineEnd =
        "（〔［｛〈《「『【〘〖〝‘“｟«"
        + "︵︹︻︽﹇﹁﹃"
        + "([{";

    private const string HangablePunctuation =
        "、。，．：；？！‼⁇⁈⁉・‥…—―～ー）〕］｝〉》」』】〙〗〟’”"
        + "︶︺︼︾﹀﹈﹂﹄";

    private const string NumericPrefixes = "第$€£¥￥";

    private const string NumericPostfixes =
        "年月日时分秒刻周卷章回集节页册部篇号条款级度届季期"
        + "℃℉％%¢";

    /// <summary>
    /// Internal connectors keep "don't", "AT&T", "well-known" and "20°C"
    /// whole, mirroring the inline-run policy the sanitizer and the previous
    /// vertical tokenizer used.
    /// </summary>
    private static readonly Regex LatinWordPattern = new(
        """[A-Za-z0-9]+(?:['’&.,:/+\-–—][A-Za-z0-9]+|%|°[CF])*""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsProhibitedAtLineStart(char c) => ProhibitedAtLineStart.IndexOf(c) >= 0;

    public static bool IsProhibitedAtLineEnd(char c) => ProhibitedAtLineEnd.IndexOf(c) >= 0;

    /// <summary>
    /// Tests the first Unicode scalar of a complete text element. Layout code
    /// must use this overload for combining sequences and supplementary-plane
    /// characters; checking only the first UTF-16 code unit misclassifies both.
    /// </summary>
    public static bool IsProhibitedAtLineStart(string text) =>
        TryGetFirstScalar(text, out var scalar)
        && ContainsScalar(ProhibitedAtLineStart, scalar);

    public static bool IsProhibitedAtLineEnd(string text) =>
        TryGetLastScalar(text, out var scalar)
        && ContainsScalar(ProhibitedAtLineEnd, scalar);

    public static bool IsOpeningPunctuation(string text) => IsProhibitedAtLineEnd(text);

    public static bool IsClosingPunctuation(string text) => IsProhibitedAtLineStart(text);

    /// <summary>
    /// Unicode line breaking keeps combining marks attached to the preceding
    /// base. StringInfo has already grouped most of these into one text element,
    /// but this predicate also protects callers that construct a cell directly.
    /// </summary>
    public static bool IsCombiningMark(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    public static bool IsWordJoiner(string text) =>
        text.IndexOf('\u2060') >= 0
        || text.IndexOf('\u200D') >= 0
        || text.IndexOf('\u200C') >= 0;

    public static bool IsHangablePunctuation(string text) =>
        TryGetFirstScalar(text, out var scalar)
        && ContainsScalar(HangablePunctuation, scalar);

    public static bool IsPunctuation(string text)
    {
        if (!TryGetFirstScalar(text, out _))
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>
    /// U+00B7 is punctuation in Unicode, but Chinese publishing uses it as a
    /// centered interpunct between name parts and in copyright notices. It
    /// must use the centre of its actual mark rather than sentence-punctuation
    /// hanging placement.
    /// </summary>
    public static bool IsVerticallyCenteredMark(string text) =>
        TryGetFirstScalar(text, out var scalar)
        && scalar == 0x00B7;

    /// <summary>
    /// Keeps common Chinese numeric expressions intact at a column boundary,
    /// such as "第12章" and "2026年". This is the local tailoring layered on
    /// top of the general Unicode numeric rules.
    /// </summary>
    public static bool ShouldKeepTogether(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        if (IsCombiningMark(right) || IsWordJoiner(left) || IsWordJoiner(right))
        {
            return true;
        }

        if (IsProhibitedAtLineEnd(left) || IsProhibitedAtLineStart(right))
        {
            return true;
        }

        if (IsNumericLike(left)
            && (IsNumericPostfix(right) || IsOpeningPunctuation(right)))
        {
            return true;
        }

        return (IsNumericPrefix(left) && IsNumericLike(right))
            || (IsClosingPunctuation(left) && IsNumericLike(right));
    }

    public static bool IsNumericLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsAsciiDigit))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c) || c is ',' or '.' or ':' or '/' or '+' or '-' or '–' or '—'
                or '%' or '°' or 'C' or 'F')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static bool IsNumericPrefix(string text) =>
        text.Length == 1 && NumericPrefixes.IndexOf(text[0]) >= 0;

    public static bool IsNumericPostfix(string text) =>
        text.Length == 1 && NumericPostfixes.IndexOf(text[0]) >= 0;

    public static bool TryGetFirstScalar(string text, out int scalar)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            scalar = rune.Value;
            return true;
        }

        scalar = 0;
        return false;
    }

    public static bool TryGetLastScalar(string text, out int scalar)
    {
        var found = false;
        scalar = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            scalar = rune.Value;
            found = true;
        }

        return found;
    }

    private static bool ContainsScalar(string characters, int scalar) =>
        scalar <= char.MaxValue && characters.IndexOf((char)scalar) >= 0;

    // UAX #50 / CSS Writing Modes Tr characters: the glyph stays upright only
    // when the font supplies its typographic vertical alternate. ASCII
    // punctuation is R and is dealt with by ShouldRotateInVertical. These are
    // the CJK/General-Punctuation Tr marks that occur in normal EPUB text;
    // vertical presentation forms (FE10..FE48) are already U and therefore
    // intentionally not listed here.
    private static readonly HashSet<char> VerticalTransformedCharacters = new(
        "‘’“”"
        + "〈〉"
        + "〈〉《》「」『』【】〔〕〖〗〘〙〚〛〝〞〟"
        + "〜〰゠ー"
        + "﹙﹚﹛﹜﹝﹞"
        + "（）：；［］＿｛｝｜～｟｠￣");

    public static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\u00A0' or '\u3000';

    public static bool IsVerticalTransformed(char c) => VerticalTransformedCharacters.Contains(c);

    public static bool IsVerticalTransformed(string text) =>
        TryGetFirstScalar(text, out var scalar)
        && scalar <= char.MaxValue
        && VerticalTransformedCharacters.Contains((char)scalar);

    /// <summary>
    /// Returns the mixed-orientation fallback for one character. This follows
    /// UAX #50's default of R for characters not assigned to the CJK vertical
    /// ranges, while accounting for fullwidth/halfwidth forms whose data file
    /// explicitly assigns R. ASCII letters/digits are excluded because the
    /// vertical inline tokenizer applies the reader's Latin/numeric run rule.
    /// </summary>
    public static bool ShouldRotateInVertical(char c)
    {
        if (IsSpace(c) || char.IsAsciiLetterOrDigit(c))
        {
            return false;
        }

        if (IsVerticalTransformed(c))
        {
            return false; // Tr: use vert, with a font-dependent fallback.
        }

        if (c is >= '\uFE10' and <= '\uFE19'
            or >= '\uFE30' and <= '\uFE48'
            or >= '\uFE50' and <= '\uFE57'
            or >= '\uFE5F' and <= '\uFE62'
            or >= '\uFE68' and <= '\uFE6F')
        {
            return false; // U/Tu compatibility forms are upright.
        }

        if (c is '\uFE53' or '\uFE67')
        {
            return false; // Reserved/compatibility forms with U in UAX #50.
        }

        if (c is >= '\uFF61' and <= '\uFFEE')
        {
            return true; // UAX #50 assigns the halfwidth forms R.
        }

        if (c is '－' or '＜' or '＝' or '＞')
        {
            return true; // Explicit R assignments in Fullwidth Forms.
        }

        if (IsCjk(c))
        {
            return false;
        }

        // Common non-CJK marks with an explicit U assignment in UAX #50.
        // U+25CB is also kept upright deliberately: Chinese EPUBs often use
        // WHITE CIRCLE as the zero in historical year forms (for example
        // "前四○三"). Its shape is rotationally symmetric, but treating it
        // as a sideways run applies horizontal baseline metrics to a vertical
        // one-em cell and visibly shifts the circle off the grid centre.
        if (c is '·' or '○' or '§' or '©' or '®' or '±' or '×' or '÷' or '‖'
            or '†' or '‡' or '‰' or '‱' or '※' or '‼' or '⁇' or '⁈' or '⁉'
            or '⁂' or '⁑' or '∞' or '∴' or '∵'
            or '◌' or '◍' or '◉')
        {
            return false;
        }

        // The UAX data file defaults all other code points to R. That makes
        // ASCII ;, ~, brackets, Unicode em/en dashes and ordinary Western
        // punctuation rotate instead of being painted horizontally.
        return true;
    }

    public static bool ShouldRotateInVertical(string text)
    {
        if (!TryGetFirstScalar(text, out var scalar))
        {
            return false;
        }

        if (scalar <= char.MaxValue)
        {
            return ShouldRotateInVertical((char)scalar);
        }

        // Supplementary-plane Han and kana retain their intrinsic upright
        // orientation; other supplementary symbols use the conservative R
        // fallback from UAX #50.
        return !IsCjk(scalar);
    }

    public static bool IsCjk(char c)
    {
        if (char.IsAsciiLetterOrDigit(c) || IsSpace(c))
        {
            return false;
        }

        return c is (>= '\u2E80' and <= '\u9FFF')     // radicals, kana, CJK
            or (>= '\uF900' and <= '\uFAFF')          // compatibility ideographs
            or (>= '\uFF00' and <= '\uFFEF');         // fullwidth forms
    }

    public static bool IsCjk(int scalar) =>
        scalar is (>= 0x2E80 and <= 0x9FFF)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFF00 and <= 0xFFEF)
            or (>= 0x20000 and <= 0x323AF);

    /// <summary>
    /// Splits a text range into breakable units: each CJK character stands
    /// alone, Latin/digit words stay whole, whitespace collapses into one
    /// unit, and everything else is treated as punctuation.
    /// </summary>
    public static List<TextUnit> Itemize(string text, int start, int length)
    {
        var units = new List<TextUnit>(length);
        var end = start + length;
        var i = start;
        while (i < end)
        {
            var c = text[i];

            if (IsSpace(c))
            {
                var j = i + 1;
                while (j < end && IsSpace(text[j]))
                {
                    j++;
                }

                units.Add(new TextUnit(i - start, j - i, TextUnitKind.Space));
                i = j;
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
            {
                units.Add(new TextUnit(i - start, 2, TextUnitKind.Punct));
                i += 2;
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                var match = LatinWordPattern.Match(text, i);
                var wordEnd = match.Success && match.Index == i
                    ? Math.Min(end, match.Index + match.Length)
                    : i + 1;
                units.Add(new TextUnit(i - start, wordEnd - i, TextUnitKind.LatinWord));
                i = wordEnd;
                continue;
            }

            if (IsCjk(c))
            {
                units.Add(new TextUnit(i - start, 1, TextUnitKind.Cjk));
                i++;
                continue;
            }

            units.Add(new TextUnit(i - start, 1, TextUnitKind.Punct));
            i++;
        }

        return units;
    }
}
