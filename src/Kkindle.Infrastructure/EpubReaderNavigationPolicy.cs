namespace Kkindle.Infrastructure;

// The spine contains every readable page, including copyright pages,
// dedications and quotations. The reader rail should use the book's actual
// navigation entries, with only the opening cover and the physical TOC page
// added as useful entry points.
internal static class EpubReaderNavigationPolicy
{
    private static readonly HashSet<string> KnownTocNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "toc",
        "tocpage",
        "contents",
        "contentspage",
        "tableofcontents",
        "目录",
        "目录页",
        "目錄",
        "目錄頁",
        "目次"
    };
    private static readonly HashSet<string> KnownCoverNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "coverpage",
        "封面",
        "封面页",
        "封面頁"
    };

    public static IReadOnlyList<EpubReaderNavigationItem> Build(
        EpubReaderDocument document,
        string coverTitle,
        string tocTitle,
        Func<int, string> chapterTitleFactory)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chapterTitleFactory);

        if (document.Chapters.Count == 0)
            return [];

        // No authoritative TOC means the existing spine fallback is the only
        // usable navigation source. Preserve it rather than hiding readable
        // chapters from books that do not publish a TOC.
        if (document.Navigation.Count == 0)
            return CreateSpineFallback(document, chapterTitleFactory);

        var orderedNavigation = EpubReaderPreparationService.OrderNavigationTree(
            document.Navigation
                .Where(item => item.ChapterIndex >= 0
                    && item.ChapterIndex < document.Chapters.Count)
                .ToArray());
        if (orderedNavigation.Count == 0)
            return CreateSpineFallback(document, chapterTitleFactory);

        var firstNavigationChapter = orderedNavigation[0].ChapterIndex;
        var tocPageIndex = FindTocPageIndex(document, firstNavigationChapter);
        var hasOpeningCover = firstNavigationChapter > 0
            || tocPageIndex > 0
            || IsCoverPage(document, 0);
        var result = new List<EpubReaderNavigationItem>();

        if (hasOpeningCover)
        {
            result.Add(CreateSpineItem(
                document,
                0,
                string.IsNullOrWhiteSpace(coverTitle)
                    ? chapterTitleFactory(0)
                    : coverTitle));
        }

        if (tocPageIndex > 0)
        {
            result.Add(CreateSpineItem(
                document,
                tocPageIndex,
                string.IsNullOrWhiteSpace(tocTitle)
                    ? chapterTitleFactory(tocPageIndex)
                    : tocTitle));
        }

        // The parsed navigation is the book's authoritative chapter list. Do
        // not rebuild it from the spine: that is what previously pulled
        // copyright pages, dedications and quotations into this rail. Drop
        // only leaf entries — removing a part heading would orphan every
        // chapter nested under it.
        result.AddRange(orderedNavigation.Where((item, index) =>
            ((!hasOpeningCover || item.ChapterIndex > 0)
                && item.ChapterIndex != tocPageIndex)
            || HasNestedChildren(orderedNavigation, index)));

        return result;
    }

    private static bool HasNestedChildren(
        IReadOnlyList<EpubReaderNavigationItem> items,
        int index) =>
        index + 1 < items.Count && items[index + 1].Level > items[index].Level;

    private static IReadOnlyList<EpubReaderNavigationItem> CreateSpineFallback(
        EpubReaderDocument document,
        Func<int, string> chapterTitleFactory) =>
        document.Chapters
            .Select((chapter, index) => new EpubReaderNavigationItem(
                chapterTitleFactory(index),
                new Uri(chapter).AbsoluteUri,
                index))
            .ToArray();

    private static EpubReaderNavigationItem CreateSpineItem(
        EpubReaderDocument document,
        int chapterIndex,
        string title) =>
        new(
            title,
            new Uri(document.Chapters[chapterIndex]).AbsoluteUri,
            chapterIndex);

    private static int FindTocPageIndex(
        EpubReaderDocument document,
        int firstNavigationChapter)
    {
        var searchLimit = Math.Min(firstNavigationChapter, document.Chapters.Count);
        for (var chapterIndex = 1; chapterIndex < searchLimit; chapterIndex++)
        {
            if (IsTocPage(document, chapterIndex))
                return chapterIndex;
        }

        return -1;
    }

    private static bool IsTocPage(EpubReaderDocument document, int chapterIndex)
    {
        var title = chapterIndex < document.ChapterTitles.Count
            ? NormalizeTocName(document.ChapterTitles[chapterIndex])
            : string.Empty;
        if (IsKnownTocName(title))
            return true;

        var fileName = Path.GetFileNameWithoutExtension(document.Chapters[chapterIndex]);
        return IsKnownTocName(NormalizeTocName(fileName));
    }

    private static bool IsCoverPage(EpubReaderDocument document, int chapterIndex)
    {
        var title = chapterIndex < document.ChapterTitles.Count
            ? NormalizeTocName(document.ChapterTitles[chapterIndex])
            : string.Empty;
        if (KnownCoverNames.Contains(title))
            return true;

        var fileName = Path.GetFileNameWithoutExtension(document.Chapters[chapterIndex]);
        return KnownCoverNames.Contains(NormalizeTocName(fileName));
    }

    private static bool IsKnownTocName(string value) =>
        KnownTocNames.Contains(value)
        || value.Contains("目录", StringComparison.Ordinal)
        || value.Contains("目錄", StringComparison.Ordinal)
        || value.Contains("tableofcontents", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTocName(string? value) =>
        new string((value ?? string.Empty)
            .Trim()
            .Where(character => char.IsLetterOrDigit(character)
                || character is >= '\u3400' and <= '\u9fff')
            .ToArray());
}
