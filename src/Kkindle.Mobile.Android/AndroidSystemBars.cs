using Android.Graphics;
using AndroidX.Core.View;
using Kkindle.Mobile;

namespace Kkindle.AndroidHead;

/// <summary>Light icons on dark paper, dark icons on light paper.</summary>
internal sealed class AndroidSystemBars : ISystemBarAppearance
{
    public void SetIconsForBackground(bool darkBackground)
    {
        if (MainActivity.Current is not { Window: { } window } activity) return;
        activity.RunOnUiThread(() =>
        {
            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            controller.AppearanceLightStatusBars = !darkBackground;
            controller.AppearanceLightNavigationBars = !darkBackground;
        });
    }

    /// <remarks>Android grants at most 200dp of exclusion per edge.</remarks>
    public void SetEdgeGesturesCaptured(bool captured)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return;
        if (MainActivity.Current is not { Window: { } window } activity) return;
        activity.RunOnUiThread(() =>
        {
            var view = window.DecorView;
            if (!captured)
            {
                view.SystemGestureExclusionRects = [];
                return;
            }
            var density = view.Resources?.DisplayMetrics?.Density ?? 1;
            var band = (int)(200 * density);
            var top = Math.Max(0, (view.Height - band) / 2);
            var width = (int)(32 * density);
            view.SystemGestureExclusionRects =
            [
                new Rect(0, top, width, top + band),
                new Rect(view.Width - width, top, view.Width, top + band)
            ];
        });
    }
}
