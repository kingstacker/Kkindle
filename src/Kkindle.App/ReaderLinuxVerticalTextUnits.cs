using System.Text.RegularExpressions;

namespace Kkindle;

internal readonly record struct ReaderLinuxVerticalTextUnit(
    int Offset,
    int Length,
    bool IsLineBreak,
    bool IsCombined,
    bool IsSidewaysRun = false);

/// <summary>
/// Tokenizes the plain-text vertical fallback with the same inline-run policy
/// as the WebKit reader, so the two surfaces agree on what a vertical unit is:
/// a single digit occupies one upright CJK cell, two plain digits occupy one
/// tate-chu-yoko square, and every other Latin/numeric token — including a
/// whole English phrase merged across short ASCII gaps — stays one atomic
/// sideways run.
///
/// Keeping this policy shared by pagination and drawing prevents a run from
/// consuming a different number of rows during pagination than during paint.
/// </summary>
internal static class ReaderLinuxVerticalTextUnits
{
    // GB/T 15834-style line-start/line-end prohibitions used by the native
    // fallback. The WebKit reader gets the equivalent behavior from
    // line-break:strict; keeping the sets here prevents the Linux fallback
    // from placing closing punctuation at the top of a column or opening
    // punctuation at its bottom. The ASCII members matter because standalone
    // ASCII punctuation is its own one-cell unit here.
    private const string ProhibitedAtColumnStart =
        "、。，．：；？！‼⁇⁈⁉・‥…—―～ー）〕］｝〉》」』】〙〗〟’”｠»,.:;?!)]}";
    private const string ProhibitedAtColumnEnd =
        "（〔［｛〈《「『【〘〖〝‘“｟«([{";

    // How many CJK cells one character of a sideways run consumes. A sideways
    // run keeps its natural horizontal metrics, so it is far shorter than its
    // character count: Latin letters and digits average roughly 0.55em of
    // advance against the 1.08em CJK cell advance this surface paints on. The
    // WebKit reader gets that ratio for free from real font metrics;
    // approximating it here keeps the two renderers' column breaks close
    // instead of an order apart.
    private const double SidewaysRowsPerCharacter = 0.55 / 1.08;

    // Kinsoku look-ahead bound. A pathological run of closing marks must not
    // turn column fitting into a quadratic scan.
    private const int MaxProhibitedClusterUnits = 4;

    // One to three separator characters covers "a b", "a, b" and "a & b". A
    // longer or non-ASCII gap means the tokens belong to different phrases.
    private const int MaxPhraseGap = 3;

    private const string PunctuationCharacters =
        "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    // Mirrors VerticalInlineTokenPattern in EpubReaderPreparationService:
    // internal connectors keep "don't", "AT&T", "well-known" and "20°C" whole.
    private static readonly Regex InlineTokenPattern = new(
        """[A-Za-z0-9]+(?:['’&.,:/+\-–—][A-Za-z0-9]+|%|°[CF])*""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NumericTokenPattern = new(
        """^[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*$""",
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

            if (TryGetInlineRun(text, offset, out var runLength, out var combined, out var sideways))
            {
                result.Add(new ReaderLinuxVerticalTextUnit(
                    offset,
                    runLength,
                    false,
                    combined,
                    sideways));
                offset += runLength;
                continue;
            }

            result.Add(new ReaderLinuxVerticalTextUnit(offset, 1, false, false));
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
        if (offset < 0 || offset >= text.Length) return false;
        if (!char.IsAsciiLetterOrDigit(text[offset])) return false;

        var match = InlineTokenPattern.Match(text, offset);
        if (!match.Success || match.Index != offset) return false;

        // Merge adjacent tokens into one phrase across a short ASCII gap when
        // either side contains a letter. The pairwise test is deliberately the
        // same one the sanitizer and the bridge apply.
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
        var classifierToken = numericGrouping
            ? RemoveGroupingSpaces(token)
            : token;
        var hasLetter = classifierToken.Any(char.IsAsciiLetter);
        length = end - offset;
        if (merged)
        {
            // A merged phrase without a single letter cannot happen (the merge
            // test requires one), but stay defensive rather than emitting a run
            // the classifier has no rule for.
            if (!hasLetter) return false;
            sideways = true;
            return true;
        }

        if (hasLetter)
        {
            sideways = true;
            return true;
        }

        if (!NumericTokenPattern.IsMatch(classifierToken) || !classifierToken.Any(char.IsAsciiDigit))
            return false;

        if (classifierToken.All(char.IsAsciiDigit))
        {
            // One digit keeps the plain CJK grid cell; exactly two become one
            // tate-chu-yoko square; longer stays a sideways run.
            if (classifierToken.Length == 1) return false;
            if (classifierToken.Length == 2)
            {
                combined = true;
                return true;
            }
        }

        sideways = true;
        return true;
    }

    private static bool TryGetNextNumericGroup(
        string text,
        int gapStart,
        string previousToken,
        out Match next)
    {
        next = Match.Empty;
        if (previousToken.Length is < 1 or > 3
            || !previousToken.All(char.IsAsciiDigit))
        {
            return false;
        }

        for (var gapLength = 1; gapLength <= 3; gapLength++)
        {
            var candidateStart = gapStart + gapLength;
            if (candidateStart >= text.Length)
                return false;

            for (var gapOffset = gapStart; gapOffset < candidateStart; gapOffset++)
            {
                if (text[gapOffset] is not (' ' or '\t' or '\u00A0'))
                    return false;
            }

            if (!char.IsAsciiDigit(text[candidateStart]))
                continue;

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
        out Match next)
    {
        next = Match.Empty;
        for (var gapLength = 1; gapLength <= MaxPhraseGap; gapLength++)
        {
            var candidateStart = gapStart + gapLength;
            if (candidateStart >= text.Length) return false;
            var gapCharacter = text[candidateStart - 1];
            if (gapCharacter is not (' ' or '\t') && !IsPunctuation(gapCharacter))
                return false;
            if (!char.IsAsciiLetterOrDigit(text[candidateStart])) continue;

            var match = InlineTokenPattern.Match(text, candidateStart);
            if (!match.Success || match.Index != candidateStart) return false;
            if (!previousToken.Any(char.IsAsciiLetter)
                && !match.Value.Any(char.IsAsciiLetter))
                return false;

            next = match;
            return true;
        }

        return false;
    }

    public static int GetVisualRows(ReaderLinuxVerticalTextUnit unit, int charsPerColumn)
    {
        charsPerColumn = Math.Max(1, charsPerColumn);
        var rows = unit.IsSidewaysRun && !unit.IsCombined
            ? Math.Max(1, (int)Math.Ceiling(unit.Length * SidewaysRowsPerCharacter))
            : 1;
        // An atomic sideways run may still be longer than the physical column.
        // It owns at most one full column and the painter scales it uniformly
        // to that cell instead of extending below the page.
        return Math.Min(charsPerColumn, rows);
    }

    public static bool ShouldBreakBefore(
        IReadOnlyList<ReaderLinuxVerticalTextUnit> units,
        string text,
        int index,
        int rowsUsed,
        int charsPerColumn)
    {
        if (index < 0 || index >= units.Count || rowsUsed <= 0)
            return false;

        charsPerColumn = Math.Max(1, charsPerColumn);
        var unit = units[index];
        if (unit.IsLineBreak) return false;
        var rows = GetVisualRows(unit, charsPerColumn);
        if (rowsUsed + rows > charsPerColumn)
            return true;

        // Leave the remaining grid cells empty when filling them would strand
        // an opening mark at the column bottom, or push a closing mark — or a
        // whole cluster of them, as in 」。— to the top of the next column.
        // This is the deterministic fixed-grid form of line adjustment.
        if (rowsUsed + rows == charsPerColumn && IsProhibitedAtColumnEnd(unit, text))
            return true;

        var clusterRows = 0;
        var clusterUnits = 0;
        for (var next = index + 1;
             next < units.Count && clusterUnits < MaxProhibitedClusterUnits;
             next++, clusterUnits++)
        {
            var candidate = units[next];
            if (candidate.IsLineBreak) break;
            if (!IsProhibitedAtColumnStart(candidate, text)) break;
            clusterRows += GetVisualRows(candidate, charsPerColumn);
        }

        if (clusterRows == 0) return false;
        // Breaking only helps when the unit plus its trailing closers actually
        // fit in a fresh column; otherwise the cluster would be stranded again.
        if (rows + clusterRows > charsPerColumn) return false;
        return rowsUsed + rows + clusterRows > charsPerColumn;
    }

    private static bool IsProhibitedAtColumnStart(
        ReaderLinuxVerticalTextUnit unit,
        string text)
        => TryGetCharacter(unit, text, out var character)
            && ProhibitedAtColumnStart.Contains(character, StringComparison.Ordinal);

    private static bool IsProhibitedAtColumnEnd(
        ReaderLinuxVerticalTextUnit unit,
        string text)
        => TryGetCharacter(unit, text, out var character)
            && ProhibitedAtColumnEnd.Contains(character, StringComparison.Ordinal);

    private static bool TryGetCharacter(
        ReaderLinuxVerticalTextUnit unit,
        string text,
        out char character)
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

    private static string RemoveGroupingSpaces(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is not (' ' or '\t' or '\u00A0'))
                builder.Append(character);
        }

        return builder.ToString();
    }
}
