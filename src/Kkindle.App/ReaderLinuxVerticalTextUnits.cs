using System.Text.RegularExpressions;

namespace Kkindle;

internal readonly record struct ReaderLinuxVerticalTextUnit(
    int Offset,
    int Length,
    bool IsLineBreak,
    bool IsCombined);

/// <summary>
/// Tokenizes the plain-text vertical fallback with the same short-run policy
/// as the HTML reader: two-to-four digit numbers and compact numeric units
/// occupy one upright square, while a single digit stays on the CJK grid.
/// Keeping this policy shared by pagination and drawing prevents a combined
/// run from consuming three rows during pagination but one row during paint.
/// </summary>
internal static class ReaderLinuxVerticalTextUnits
{
    private static readonly Regex NumericTokenPattern = new(
        """[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<ReaderLinuxVerticalTextUnit> Tokenize(string text)
    {
        var result = new List<ReaderLinuxVerticalTextUnit>();
        for (var offset = 0; offset < text.Length;)
        {
            if (text[offset] == '\n')
            {
                result.Add(new ReaderLinuxVerticalTextUnit(offset, 1, true, false));
                offset++;
                continue;
            }

            if (TryGetCombinedLength(text, offset, out var combinedLength))
            {
                result.Add(new ReaderLinuxVerticalTextUnit(
                    offset,
                    combinedLength,
                    false,
                    true));
                offset += combinedLength;
                continue;
            }

            result.Add(new ReaderLinuxVerticalTextUnit(offset, 1, false, false));
            offset++;
        }

        return result;
    }

    public static bool TryGetCombinedLength(
        string text,
        int offset,
        out int length)
    {
        length = 0;
        if (offset < 0 || offset >= text.Length || !char.IsAsciiDigit(text[offset]))
            return false;

        var match = NumericTokenPattern.Match(text, offset);
        if (!match.Success || match.Index != offset)
            return false;

        var token = match.Value;
        var before = offset > 0 ? text[offset - 1] : '\0';
        var afterOffset = offset + token.Length;
        var after = afterOffset < text.Length ? text[afterOffset] : '\0';
        if ((before != '\0' && char.IsAsciiLetter(before))
            || (after != '\0' && char.IsAsciiLetter(after)))
        {
            return false;
        }

        var pureDigits = token.All(char.IsAsciiDigit);
        if ((pureDigits && token.Length is >= 2 and <= 4)
            || (!pureDigits && token.Length <= 4))
        {
            length = token.Length;
            return true;
        }

        return false;
    }
}
