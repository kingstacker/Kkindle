using Kkindle.Core;

namespace Kkindle.Infrastructure;

// Search all readable spine documents, including those without a TOC entry.
internal static class EpubReaderSearchPolicy
{
    public static IReadOnlyList<BookContentChunk> FilterAndOrder(
        EpubReaderDocument document,
        IEnumerable<BookContentChunk> candidates)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => candidate.ChapterIndex >= 0
                && candidate.ChapterIndex < document.Chapters.Count)
            .OrderBy(candidate => candidate.ChapterIndex)
            .ThenBy(candidate => candidate.ChunkIndex)
            .ThenBy(candidate => candidate.StartOffset)
            .ThenBy(candidate => candidate.Id)
            .ToArray();
    }
}
