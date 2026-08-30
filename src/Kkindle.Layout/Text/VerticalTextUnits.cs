using System.Globalization;

namespace Kkindle.Layout;

/// <summary>
/// Vertical inline-unit policy, ported from the previous Avalonia vertical
/// fallback (ReaderLinuxVerticalTextUnits): a single digit occupies one
/// upright CJK cell, exactly two plain digits form one tate-chu-yoko square,
/// and every other Latin/numeric token — including a whole English phrase
/// merged across short ASCII gaps — stays one atomic sideways run. Keeping
/// the policy shared by pagination and paint prevents a run from consuming a
/// different number of rows during pagination than during painting.
/// </summary>
public static class VerticalTextUnits
{
    public readonly record struct Unit(
        int Offset,
        int Length,
        bool IsLineBreak,
        bool IsCombined,
        bool IsSidewaysRun = false);

    // How many upright CJK cells one character of a sideways run occupies when
    // real metrics are unavailable. The engine measures sideways runs with
    // actual shaped advances; this ratio is only a defensive fallback.
    public const double SidewaysRowsPerCharacter = 0.55 / 1.08;

    // One to three separator characters covers "a b", "a, b" and "a & b". A
    // longer or non-ASCII gap means the tokens belong to different phrases.
    private const int MaxPhraseGap = 3;

    private const string PunctuationCharacters =
        "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private static readonly System.Text.RegularExpressions.Regex InlineTokenPattern = new(
        """[A-Za-z0-9]+(?:['’&.,:/+\-–—][A-Za-z0-9]+|%|°[CF])*""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex NumericTokenPattern = new(
        """^[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*$""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<Unit> Tokenize(
        string text,
        int combineDigits = 2,
        TypesetVerticalOrientation orientation = TypesetVerticalOrientation.Mixed)
    {
        var result = new List<Unit>(text.Length);
        var textElementStarts = StringInfo.ParseCombiningCharacters(text);
        var textElementStartSet = textElementStarts.ToHashSet();
        for (var offset = 0; offset < text.Length;)
        {
            if (text[offset] is '\r' or '\n')
            {
                var breakLength = text[offset] == '\r'
                    && offset + 1 < text.Length
                    && text[offset + 1] == '\n'
                    ? 2
                    : 1;
                result.Add(new Unit(offset, breakLength, true, false));
                offset += breakLength;
                continue;
            }

            if (char.IsHighSurrogate(text[offset])
                && offset + 1 < text.Length
                && char.IsLowSurrogate(text[offset + 1]))
            {
                var elementEnd = NextTextElementEnd(text, offset, textElementStarts);
                result.Add(new Unit(offset, elementEnd - offset, false, false));
                offset = elementEnd;
                continue;
            }

            if (TryGetInlineRun(
                text,
                offset,
                out var runLength,
                out var combined,
                out var sideways,
                combineDigits,
                orientation))
            {
                var runEnd = offset + runLength;
                // Keep variation selectors and combining marks attached to
                // the final ASCII scalar of a run. They are text elements in
                // their own right from the regex's UTF-16 point of view, but
                // never valid break positions in a publication.
                while (runEnd < text.Length && !textElementStartSet.Contains(runEnd))
                {
                    runEnd++;
                }

                result.Add(new Unit(offset, runEnd - offset, false, combined, sideways));
                offset = runEnd;
                continue;
            }

            var nextElementEnd = NextTextElementEnd(text, offset, textElementStarts);
            result.Add(new Unit(offset, nextElementEnd - offset, false, false));
            offset = nextElementEnd;
        }

        return result;
    }

    private static int NextTextElementEnd(string text, int offset, int[] starts)
    {
        for (var index = 0; index < starts.Length; index++)
        {
            if (starts[index] == offset)
            {
                return index + 1 < starts.Length ? starts[index + 1] : text.Length;
            }
        }

        // This is only reachable for a malformed UTF-16 sequence. Preserve
        // the source unit rather than allowing a zero-length token loop.
        return Math.Min(text.Length, offset + 1);
    }

    /// <summary>
    /// Matches the vertical inline run that starts at <paramref name="offset"/>.
    /// Returns false when the character there is not the start of one, or when
    /// the run is a single digit — that stays on the plain CJK cell grid.
    /// </summary>
    public static bool TryGetInlineRun(
        string text,
        int offset,
        out int length,
        out bool combined,
        out bool sideways,
        int combineDigits = 2,
        TypesetVerticalOrientation orientation = TypesetVerticalOrientation.Mixed)
    {
        length = 0;
        combined = false;
        sideways = false;
        if (offset < 0 || offset >= text.Length)
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(text[offset]))
        {
            return false;
        }

        var match = InlineTokenPattern.Match(text, offset);
        if (!match.Success || match.Index != offset)
        {
            return false;
        }

        // Merge adjacent tokens into one phrase across a short ASCII gap when
        // either side contains a letter.
        var end = offset + match.Length;
        var previousToken = match.Value;
        var numericGrouping = false;
        while (TryGetNextNumericGroup(text, end, previousToken, out var numericMatch))
        {
            end = numericMatch.Index + numericMatch.Length;
            previousToken = numericMatch.Value;
            numericGrouping = true;
        }

        var merged = false;
        while (TryGetNextTokenAcrossGap(text, end, previousToken, out var nextMatch))
        {
            end = nextMatch.Index + nextMatch.Length;
            previousToken = nextMatch.Value;
            merged = true;
        }

        var token = text[offset..end];
        // A thousands separator is meaningful in horizontal text, but it is
        // not a vertical book cell. Keep the source space in this atomic run
        // so it paints with the numeric run's small horizontal advance instead
        // of becoming a full blank upright row between "5" and "000".
        var classifierToken = numericGrouping
            ? RemoveGroupingSpaces(token)
            : token;
        var hasLetter = classifierToken.Any(char.IsAsciiLetter);
        length = end - offset;
        if (combineDigits == int.MaxValue && token.Length > 1)
        {
            // text-combine-upright: all is an explicit author instruction and
            // therefore wins over the mixed-orientation Latin default.
            combined = true;
            return true;
        }

        if (merged)
        {
            if (!hasLetter || orientation == TypesetVerticalOrientation.Upright)
            {
                return false;
            }

            sideways = true;
            return true;
        }

        if (hasLetter)
        {
            if (orientation == TypesetVerticalOrientation.Upright)
            {
                return false;
            }

            sideways = true;
            return true;
        }

        if (!NumericTokenPattern.IsMatch(classifierToken) || !classifierToken.Any(char.IsAsciiDigit))
        {
            return false;
        }

        if (classifierToken.All(char.IsAsciiDigit))
        {
            // A document can request no combining, a bounded digits-N run, or
            // all digits in one tate-chu-yoko cell. A single digit remains an
            // upright cell unless the author explicitly asks for sideways.
            if (classifierToken.Length == 1 && orientation != TypesetVerticalOrientation.Sideways)
            {
                return false;
            }

            if (combineDigits > 0
                && classifierToken.Length >= 2
                && classifierToken.Length <= combineDigits)
            {
                combined = true;
                return true;
            }

            if (orientation == TypesetVerticalOrientation.Upright || combineDigits == 0)
            {
                return false;
            }
        }

        if (orientation == TypesetVerticalOrientation.Upright)
        {
            return false;
        }

        sideways = true;
        return true;
    }

    private static bool TryGetNextNumericGroup(
        string text,
        int gapStart,
        string previousToken,
        out System.Text.RegularExpressions.Match next)
    {
        next = System.Text.RegularExpressions.Match.Empty;
        if (previousToken.Length is < 1 or > 3
            || !previousToken.All(char.IsAsciiDigit))
        {
            return false;
        }

        for (var gapLength = 1; gapLength <= 3; gapLength++)
        {
            var candidateStart = gapStart + gapLength;
            if (candidateStart >= text.Length)
            {
                return false;
            }

            for (var gapOffset = gapStart; gapOffset < candidateStart; gapOffset++)
            {
                if (!TypesetText.IsSpace(text[gapOffset]))
                {
                    return false;
                }
            }

            if (!char.IsAsciiDigit(text[candidateStart]))
            {
                continue;
            }

            var match = InlineTokenPattern.Match(text, candidateStart);
            if (!match.Success
                || match.Index != candidateStart
                || match.Length != 3
                || !match.Value.All(char.IsAsciiDigit))
            {
                return false;
            }

            next = match;
            return true;
        }

        return false;
    }

    private static bool TryGetNextTokenAcrossGap(
        string text,
        int gapStart,
        string previousToken,
        out System.Text.RegularExpressions.Match next)
    {
        next = System.Text.RegularExpressions.Match.Empty;
        for (var gapLength = 1; gapLength <= MaxPhraseGap; gapLength++)
        {
            var candidateStart = gapStart + gapLength;
            if (candidateStart >= text.Length)
            {
                return false;
            }

            for (var gapOffset = gapStart; gapOffset < candidateStart; gapOffset++)
            {
                var gapCharacter = text[gapOffset];
                if (!TypesetText.IsSpace(gapCharacter) && !IsPunctuation(gapCharacter))
                {
                    return false;
                }
            }

            if (!char.IsAsciiLetterOrDigit(text[candidateStart]))
            {
                continue;
            }

            var match = InlineTokenPattern.Match(text, candidateStart);
            if (!match.Success || match.Index != candidateStart)
            {
                return false;
            }

            if (!previousToken.Any(char.IsAsciiLetter)
                && !match.Value.Any(char.IsAsciiLetter))
            {
                return false;
            }

            next = match;
            return true;
        }

        return false;
    }

    private static string RemoveGroupingSpaces(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!TypesetText.IsSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static bool IsProhibitedAtColumnStart(Unit unit, string text) =>
        TryGetText(unit, text, out var unitText)
        && TypesetText.IsProhibitedAtLineStart(unitText);

    public static bool IsProhibitedAtColumnEnd(Unit unit, string text) =>
        TryGetText(unit, text, out var unitText)
        && TypesetText.IsProhibitedAtLineEnd(unitText);

    private static bool TryGetText(Unit unit, string text, out string unitText)
    {
        unitText = unit.Offset >= 0
            && unit.Length > 0
            && unit.Offset <= text.Length - unit.Length
            ? text.Substring(unit.Offset, unit.Length)
            : string.Empty;
        return unitText.Length > 0;
    }

    private static bool IsPunctuation(char character) =>
        PunctuationCharacters.IndexOf(character) >= 0;
}
