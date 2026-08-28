using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPlatformLayoutPolicy
{
    private const double MinimumVerticalPageContentWidth = 240;
    private const double MinimumVerticalPageContentHeight = 180;

    public static ReaderLayoutSettings Normalize(
        ReaderLayoutSettings settings,
        bool preferContinuousVerticalFlow)
    {
        var normalized = ReaderLayoutDefaults.Normalize(settings);
        return preferContinuousVerticalFlow && normalized.VerticalWriting
            ? normalized with { FlowMode = 0, TwoPageMode = false }
            : normalized;
    }

    /// <summary>
    /// Resolves the physical blank boundary around the Linux fallback page.
    /// The requested book inset is preserved whenever the viewport can hold it;
    /// a very small window reduces it only enough to keep a usable content box,
    /// so the fixed drawing surface can never extend through its parent edge.
    /// </summary>
    public static (double Horizontal, double Vertical) GetVerticalPageInsets(
        double viewportWidth,
        double viewportHeight,
        double requestedInset)
    {
        var width = double.IsFinite(viewportWidth) ? Math.Max(1, viewportWidth) : 1;
        var height = double.IsFinite(viewportHeight) ? Math.Max(1, viewportHeight) : 1;
        var requested = double.IsFinite(requestedInset)
            ? Math.Clamp(
                requestedInset,
                ReaderLayoutDefaults.MinBodyPadding,
                ReaderLayoutDefaults.MaxBodyPadding)
            : ReaderLayoutDefaults.DefaultBodyPadding;
        var maximumHorizontal = Math.Max(0, (width - MinimumVerticalPageContentWidth) / 2);
        var maximumVertical = Math.Max(0, (height - MinimumVerticalPageContentHeight) / 2);
        return (
            Math.Min(requested, maximumHorizontal),
            Math.Min(requested, maximumVertical));
    }
}
