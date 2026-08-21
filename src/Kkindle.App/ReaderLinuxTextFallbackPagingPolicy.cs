namespace Kkindle;

internal static class ReaderLinuxTextFallbackPagingPolicy
{
    public static int ResolveAnchorPageIndex(
        IReadOnlyList<int> pageOffsets,
        int anchorOffset,
        int spreadSize)
    {
        spreadSize = Math.Max(1, spreadSize);
        if (pageOffsets.Count == 0)
            return 0;

        var target = -1;
        for (var index = 0; index < pageOffsets.Count; index++)
        {
            var offset = pageOffsets[index];
            if (offset < 0) continue;
            if (offset > anchorOffset) break;
            target = index;
        }

        if (target < 0)
            target = pageOffsets.ToList().FindIndex(offset => offset >= 0);
        if (target < 0)
            target = 0;

        target = Math.Clamp(target, 0, pageOffsets.Count - 1);
        if (spreadSize > 1)
            target -= target % spreadSize;
        return target;
    }

    public static int ResolvePageIndex(
        int currentPageIndex,
        double scrollPosition,
        bool moveToChapterEnd,
        int pageCount,
        int spreadSize)
    {
        spreadSize = Math.Max(1, spreadSize);
        var maximum = Math.Max(0, pageCount - spreadSize);
        var moveToEnd = moveToChapterEnd
            || currentPageIndex < 0
            || scrollPosition < 0;
        var target = moveToEnd
            ? maximum
            // Once pagination has produced an explicit page index, preserve
            // it across the Loaded/layout rebuild. scrollPosition can still
            // contain the old chapter's zero during that second pass.
            : currentPageIndex >= 0
                ? currentPageIndex
                : (int)Math.Round(Math.Max(0, scrollPosition));
        target = Math.Clamp(target, 0, maximum);
        if (spreadSize > 1)
            target -= target % spreadSize;
        return target;
    }
}
