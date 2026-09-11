namespace Kkindle;

/// <summary>
/// Keeps the progress streams in both book-processing windows visually
/// consistent: completed items first, then the active item, then the rest of
/// the queue, with terminal items at the end.
/// </summary>
internal static class ProgressWaterfallOrder
{
    public static T[] Order<T>(
        IEnumerable<T> rows,
        int? currentIndex,
        Func<T, int> getIndex,
        Func<T, bool> isCompleted,
        Func<T, bool> isProcessing)
    {
        return rows
            .OrderBy(row => GetGroup(row, currentIndex, getIndex, isCompleted, isProcessing))
            .ThenBy(getIndex)
            .ToArray();
    }

    private static int GetGroup<T>(
        T row,
        int? currentIndex,
        Func<T, int> getIndex,
        Func<T, bool> isCompleted,
        Func<T, bool> isProcessing)
    {
        if (isCompleted(row)) return 0;
        if (isProcessing(row) && getIndex(row) == currentIndex) return 1;
        if (isProcessing(row)) return 2;
        return 3;
    }
}
