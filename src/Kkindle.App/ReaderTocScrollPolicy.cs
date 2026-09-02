namespace Kkindle;

/// <summary>
/// The two guards that keep the expanded TOC rail still while the reading
/// position moves through it.
///
/// Both decisions used to be unconditional, and together they made the rail
/// creep downward on every click. Assigning <c>ItemsSource</c> is a collection
/// reset: the virtualizing panel drops every realized container along with the
/// measured row heights it derives scroll offsets from. A ScrollIntoView issued
/// straight afterwards therefore re-anchors the rail from an estimated average
/// height. TOC titles wrap, so the real heights genuinely differ from that
/// average and each round landed a little lower than the one before.
/// </summary>
internal static class ReaderTocScrollPolicy
{
    // Layout arrives in device pixels after rounding, so an edge that sits
    // half a pixel outside the viewport is still "in view".
    private const double EdgeTolerance = 0.5d;

    /// <summary>
    /// A fold only needs a new ItemsSource when the visible rows really
    /// changed. Selecting a row whose branch is already open — what a plain
    /// TOC click does — must leave the panel and its measurements untouched.
    /// </summary>
    public static bool RequiresRowRebuild<T>(IReadOnlyList<T>? current, IReadOnlyList<T> next)
        where T : class
    {
        if (current is null || current.Count != next.Count) return true;
        for (var index = 0; index < next.Count; index++)
            if (!ReferenceEquals(current[index], next[index])) return true;
        return false;
    }

    /// <summary>
    /// Only chase a row that is not already fully inside the viewport.
    /// <paramref name="rowTop"/> is the row's top edge relative to the
    /// viewport's, so a fully visible row spans 0..<paramref name="viewportHeight"/>.
    /// </summary>
    public static bool RequiresScrollIntoView(
        double rowTop,
        double rowHeight,
        double viewportHeight)
    {
        // Before the first layout pass there is no viewport to judge against;
        // scrolling then would only feed the estimator bad numbers.
        if (viewportHeight <= 0) return false;

        // A row taller than the rail can never sit fully inside it. Chasing its
        // bottom edge would scroll the title itself out of sight, so treat it
        // as settled once its top edge reaches the top of the rail.
        if (rowHeight >= viewportHeight)
            return Math.Abs(rowTop) > EdgeTolerance;

        return rowTop < -EdgeTolerance
            || rowTop + rowHeight > viewportHeight + EdgeTolerance;
    }
}
