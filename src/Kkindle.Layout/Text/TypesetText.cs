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
        "、。，．：；？！‼⁇⁈⁉・‥…—―～ー）〕］｝〉》」』】〙〗〟’”｠»,.:;?!)]}";

    public const string ProhibitedAtLineEnd =
        "（〔［｛〈《「『【〘〖〝‘“｟«([{";

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

    public static bool IsSpace(char c) => c is ' ' or '\t' or '\u00A0' or '\u3000';

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
