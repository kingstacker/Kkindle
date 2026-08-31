using System.Text;

namespace Kkindle.Core;

public readonly record struct ReaderSearchMatch(int Start, int Length);

/// <summary>
/// Search matching shared by the index result list and the native reader.
/// The index collapses whitespace while the native reader keeps the XHTML
/// text-node whitespace, so every normalized character retains its source
/// offset before a result is sent to the renderer.
/// </summary>
public static class ReaderSearchTextPolicy
{
    public static IReadOnlyList<ReaderSearchMatch> FindMatches(
        string? text,
        string? query)
    {
        var normalizedText = Normalize(text);
        var normalizedQuery = Normalize(query).Text;
        if (normalizedText.Text.Length == 0 || normalizedQuery.Length == 0)
            return [];

        var matches = new List<ReaderSearchMatch>();
        var searchStart = 0;
        while (searchStart < normalizedText.Text.Length)
        {
            var normalizedStart = normalizedText.Text.IndexOf(
                normalizedQuery,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (normalizedStart < 0) break;

            var normalizedEnd = normalizedStart + normalizedQuery.Length - 1;
            var rawStart = normalizedText.RawOffsets[normalizedStart];
            var rawEnd = normalizedText.RawOffsets[normalizedEnd] + 1;
            matches.Add(new ReaderSearchMatch(rawStart, rawEnd - rawStart));
            searchStart = normalizedStart + Math.Max(1, normalizedQuery.Length);
        }

        return matches;
    }

    /// <summary>
    /// Resolves the occurrence represented by a result card. Prefer its
    /// surrounding context so repeated words in one chapter do not all jump
    /// to the first occurrence; the offset hint is the deterministic fallback
    /// when the publication changed whitespace around the context.
    /// </summary>
    public static int FindBestMatchOffset(
        string? text,
        string? query,
        string? context,
        int offsetHint = -1)
    {
        var normalizedText = Normalize(text);
        var normalizedQuery = Normalize(query).Text;
        if (normalizedText.Text.Length == 0 || normalizedQuery.Length == 0)
            return -1;

        var candidates = new List<int>();
        var normalizedContext = Normalize(context).Text;
        if (normalizedContext.Length > 0)
        {
            var queryOffset = normalizedContext.IndexOf(
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase);
            if (queryOffset >= 0)
            {
                var contextStart = 0;
                while (contextStart < normalizedText.Text.Length)
                {
                    contextStart = normalizedText.Text.IndexOf(
                        normalizedContext,
                        contextStart,
                        StringComparison.OrdinalIgnoreCase);
                    if (contextStart < 0) break;

                    var matchStart = contextStart + queryOffset;
                    var matchEnd = matchStart + normalizedQuery.Length - 1;
                    if (matchEnd < normalizedText.RawOffsets.Count)
                        candidates.Add(normalizedText.RawOffsets[matchStart]);
                    contextStart += Math.Max(1, normalizedContext.Length);
                }
            }
        }

        if (candidates.Count == 0)
            candidates.AddRange(FindMatches(text, query).Select(match => match.Start));
        if (candidates.Count == 0)
            return -1;

        return offsetHint < 0
            ? candidates[0]
            : candidates
                .OrderBy(candidate => Math.Abs((long)candidate - offsetHint))
                .ThenBy(candidate => candidate)
                .First();
    }

    private static NormalizedText Normalize(string? value)
    {
        var source = value ?? string.Empty;
        var builder = new StringBuilder(source.Length);
        var rawOffsets = new List<int>(source.Length);
        var pendingWhitespace = -1;

        for (var index = 0; index < source.Length; index++)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                if (builder.Length > 0 && pendingWhitespace < 0)
                    pendingWhitespace = index;
                continue;
            }

            if (pendingWhitespace >= 0)
            {
                builder.Append(' ');
                rawOffsets.Add(pendingWhitespace);
                pendingWhitespace = -1;
            }

            builder.Append(source[index]);
            rawOffsets.Add(index);
        }

        return new NormalizedText(builder.ToString(), rawOffsets);
    }

    private sealed record NormalizedText(string Text, IReadOnlyList<int> RawOffsets);
}
