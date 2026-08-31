using Kkindle.Core;

namespace Kkindle.Infrastructure;

// Whole-book search should follow the same authoritative chapter set as the
// reader rail. EPUB spine files before the first real navigation entry are
// often copyright pages, introductions or dedications and must not appear as
// fake numbered chapters in the result list.
internal static class EpubReaderSearchPolicy
{
    public static IReadOnlyList<BookContentChunk> FilterAndOrder(
        EpubReaderDocument document,
        IEnumerable<BookContentChunk> candidates)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidates);

        var searchableChapterIndexes = document.Navigation.Count == 0
            ? null
            : document.Navigation
                .Select(item => item.ChapterIndex)
                .Where(index => index >= 0 && index < document.Chapters.Count)
                .ToHashSet();

        return candidates
            .Where(candidate => candidate.ChapterIndex >= 0
                && candidate.ChapterIndex < document.Chapters.Count)
            .Where(candidate => searchableChapterIndexes is null
                || searchableChapterIndexes.Contains(candidate.ChapterIndex))
            .OrderBy(candidate => candidate.ChapterIndex)
            .ThenBy(candidate => candidate.ChunkIndex)
            .ThenBy(candidate => candidate.StartOffset)
            .ThenBy(candidate => candidate.Id)
            .ToArray();
    }
}
