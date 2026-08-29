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

    // Kinsoku look-ahead bound. A pathological run of closing marks must not
    // turn column fitting into a quadratic scan.
    private const int MaxProhibitedClusterUnits = 4;

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

    public static IReadOnlyList<Unit> Tokenize(string text)
    {
        var result = new List<Unit>(text.Length);
        for (var offset = 0; offset < text.Length;)
        {
            if (text[offset] == '\n')
            {
                result.Add(new Unit(offset, 1, true, false));
                offset++;
                continue;
            }

            if (TryGetInlineRun(text, offset, out var runLength, out var combined, out var sideways))
            {
                result.Add(new Unit(offset, runLength, false, combined, sideways));
                offset += runLength;
                continue;
            }

            result.Add(new Unit(offset, 1, false, false));
            offset++;
        }

        return result;
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
        out bool sideways)
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
        var merged = false;
        while (TryGetNextTokenAcrossGap(text, end, previousToken, out var nextMatch))
        {
            end = nextMatch.Index + nextMatch.Length;
            previousToken = nextMatch.Value;
            merged = true;
        }

        var token = text[offset..end];
        var hasLetter = token.Any(char.IsAsciiLetter);
        length = end - offset;
        if (merged)
        {
            if (!hasLetter)
            {
                return false;
            }

            sideways = true;
            return true;
        }

        if (hasLetter)
        {
            sideways = true;
            return true;
        }

        if (!NumericTokenPattern.IsMatch(token) || !token.Any(char.IsAsciiDigit))
        {
            return false;
        }

        if (token.All(char.IsAsciiDigit))
        {
            // One digit keeps the plain CJK grid cell; exactly two become one
            // tate-chu-yoko square; longer stays a sideways run.
            if (token.Length == 1)
            {
                return false;
            }

            if (token.Length == 2)
            {
                combined = true;
                return true;
            }
        }

        sideways = true;
        return true;
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

            var gapCharacter = text[candidateStart - 1];
            if (gapCharacter is not (' ' or '\t') && !IsPunctuation(gapCharacter))
            {
                return false;
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

    public static bool IsProhibitedAtColumnStart(Unit unit, string text) =>
        TryGetCharacter(unit, text, out var character)
        && TypesetText.IsProhibitedAtLineStart(character);

    public static bool IsProhibitedAtColumnEnd(Unit unit, string text) =>
        TryGetCharacter(unit, text, out var character)
        && TypesetText.IsProhibitedAtLineEnd(character);

    private static bool TryGetCharacter(Unit unit, string text, out char character)
    {
        // Only a single-cell unit can be a prohibited mark: every longer unit
        // is a Latin/numeric run that starts with an alphanumeric character.
        character = unit.Length == 1 && unit.Offset >= 0 && unit.Offset < text.Length
            ? text[unit.Offset]
            : '\0';
        return character != '\0';
    }

    private static bool IsPunctuation(char character) =>
        PunctuationCharacters.IndexOf(character) >= 0;
}
