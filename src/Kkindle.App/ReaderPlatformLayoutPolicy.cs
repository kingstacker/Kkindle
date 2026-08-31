using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPlatformLayoutPolicy
{
    private const double MinimumVerticalPageContentWidth = 240;
    private const double MinimumVerticalPageContentHeight = 180;

    /// <summary>
    /// Resolves the physical blank boundary around a reader page.
    /// The requested book inset is the minimum outer margin. When the viewport
    /// is wider than the requested maximum body width, the remaining space is
    /// split evenly on both sides so the body stays centered. A very small
    /// window reduces the margin only enough to keep a usable content box.
    /// </summary>
    public static (double Horizontal, double Vertical) GetVerticalPageInsets(
        double viewportWidth,
        double viewportHeight,
        double requestedInset,
        double requestedMaxWidth = double.PositiveInfinity)
    {
        var width = double.IsFinite(viewportWidth) ? Math.Max(1, viewportWidth) : 1;
        var height = double.IsFinite(viewportHeight) ? Math.Max(1, viewportHeight) : 1;
        var requested = double.IsFinite(requestedInset)
            ? Math.Clamp(
                requestedInset,
                ReaderLayoutDefaults.MinBodyPadding,
                ReaderLayoutDefaults.MaxBodyPadding)
            : ReaderLayoutDefaults.DefaultBodyPadding;

        // MaxWidth is the readable body width, not the bitmap/page width.
        // Keep the requested margin when it is the limiting factor; otherwise
        // center the capped body inside the viewport.
        var maximumBodyWidth = double.IsFinite(requestedMaxWidth)
            ? Math.Clamp(
                requestedMaxWidth,
                ReaderLayoutDefaults.MinMaxWidth,
                ReaderLayoutDefaults.MaxMaxWidth)
            : double.PositiveInfinity;
        var availableWithRequestedMargins = Math.Max(1, width - requested * 2);
        var contentWidth = Math.Min(availableWithRequestedMargins, maximumBodyWidth);
        var horizontal = Math.Max(requested, (width - contentWidth) / 2);

        var maximumHorizontal = Math.Max(0, (width - MinimumVerticalPageContentWidth) / 2);
        var maximumVertical = Math.Max(0, (height - MinimumVerticalPageContentHeight) / 2);
        return (
            Math.Min(horizontal, maximumHorizontal),
            Math.Min(requested, maximumVertical));
    }
}
