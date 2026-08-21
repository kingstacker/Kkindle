namespace Kkindle;

internal static class ReaderLinuxTextFallbackTitleLinePolicy
{
    public static bool TryFindLeadingTitleRange(
        string? text,
        string? expectedTitle,
        out int start,
        out int length)
    {
        start = -1;
        length = 0;
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(expectedTitle))
            return false;

        var title = expectedTitle.Trim();
        var candidateStart = 0;
        while (candidateStart < text.Length && char.IsWhiteSpace(text[candidateStart]))
            candidateStart++;
        if (candidateStart + title.Length > text.Length
            || !text.AsSpan(candidateStart, title.Length).Equals(
                title.AsSpan(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidateEnd = candidateStart + title.Length;
        if (candidateEnd < text.Length && !char.IsWhiteSpace(text[candidateEnd]))
            return false;

        start = candidateStart;
        length = title.Length;
        return true;
    }

    public static bool IsTitleLine(
        int chapterTitleStart,
        int chapterTitleLength,
        double lineStart,
        int lineLength,
        int newLineLength,
        int lineIndex)
    {
        if (chapterTitleStart < 0 || chapterTitleLength <= 0)
            return false;

        var lineEnd = lineStart + lineLength - newLineLength;

        // Linux/Avalonia can expose a local start of zero for every line in a
        // paged TextLayout. The paginator always puts a title range beginning
        // at zero on the first visual line, so never use the repeated offsets
        // to repaint the title over each following body line.
        if (chapterTitleStart == 0)
            return lineIndex == 0 && lineEnd > lineStart;

        var titleEnd = chapterTitleStart + chapterTitleLength;
        return lineStart < titleEnd && lineEnd > chapterTitleStart;
    }
}
