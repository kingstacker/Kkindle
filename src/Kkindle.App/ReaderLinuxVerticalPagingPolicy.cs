namespace Kkindle;

/// <summary>
/// Vertical-writing pagination for the Linux text fallback reader. Columns
/// fill top-to-bottom and advance right-to-left, mirroring CSS
/// writing-mode:vertical-rl. The output contract matches the horizontal
/// measured paginator — page text plus its start offset inside the normalized
/// stream — so progress, bookmark offsets and page-count machinery stay
/// unchanged between flow modes.
/// </summary>
internal static class ReaderLinuxVerticalPagingPolicy
{
    public static List<(string Text, int Start)> Paginate(
        string text,
        int charsPerColumn,
        int columnsPerPage,
        bool paragraphIndent = false,
        bool startsWithParagraph = true)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
            return [(string.Empty, 0)];

        charsPerColumn = Math.Max(1, charsPerColumn);
        columnsPerPage = Math.Max(1, columnsPerPage);

        // A column boundary opens after every newline and after every full
        // run of visual units. A two-to-four digit run is one visual unit,
        // matching the renderer's compact upright number cell. Two consecutive newlines therefore create an
        // empty spacer column between paragraphs, the direct transpose of a
        // blank separator line in the horizontal layout.
        var columnStarts = new List<int> { 0 };
        var unitsInColumn = 0;
        var paragraphIndentPending = paragraphIndent && startsWithParagraph;
        foreach (var unit in ReaderLinuxVerticalTextUnits.Tokenize(normalized))
        {
            if (unit.IsLineBreak)
            {
                columnStarts.Add(unit.Offset + unit.Length);
                unitsInColumn = 0;
                paragraphIndentPending = paragraphIndent;
                continue;
            }

            if (paragraphIndentPending)
            {
                // CSS text-indent: 2em is two glyph advances along the
                // vertical inline axis. Reserve those rows in the paginator
                // so the drawing surface and page boundaries stay identical.
                unitsInColumn = Math.Min(2, Math.Max(0, charsPerColumn - 1));
                paragraphIndentPending = false;
            }

            unitsInColumn++;
            if (unitsInColumn >= charsPerColumn
                && unit.Offset + unit.Length < normalized.Length)
            {
                columnStarts.Add(unit.Offset + unit.Length);
                unitsInColumn = 0;
            }
        }

        var pages = new List<(string Text, int Start)>();
        for (var firstColumn = 0; firstColumn < columnStarts.Count; firstColumn += columnsPerPage)
        {
            var start = columnStarts[firstColumn];
            var nextColumn = firstColumn + columnsPerPage;
            var end = nextColumn < columnStarts.Count
                ? Math.Min(columnStarts[nextColumn], normalized.Length)
                : normalized.Length;
            if (end <= start) continue;

            // Trailing newlines belong to this page's tail; trimming them
            // keeps the following page free of leading spacer columns.
            var page = normalized[start..end].TrimEnd();
            if (page.Length == 0) continue;
            pages.Add((page, start));
        }

        return pages.Count > 0 ? pages : [(string.Empty, 0)];
    }
}
