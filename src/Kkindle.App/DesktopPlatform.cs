using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Kkindle;

internal static class DesktopPlatform
{
    public static bool UsesNativeWindowChrome => OperatingSystem.IsMacOS();
    public const double ReaderTitleBarInset = 38;
    public const double MainWindowDragHeaderHeight = 84;

    public static bool IsCommand(KeyModifiers modifiers) =>
        modifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    public static string ShortcutLabel(string label) => OperatingSystem.IsMacOS()
        ? label.Replace("Ctrl+", "⌘")
        : label;

    public static void ApplyNativeWindowChrome(Window window, bool reserveTitleBarSpace = true)
    {
        if (!UsesNativeWindowChrome) return;

        // A titled NSWindow supplies traffic lights, rounded corners, shadow,
        // resize hit-testing and the native full-screen Space. Removing its
        // decorations during a full-screen transition changes its style mask.
        window.WindowDecorations = WindowDecorations.Full;
        // Extend the page underneath AppKit's transparent title bar. This
        // retains real traffic lights without a separate grey title strip,
        // separator or duplicated window title above the app's own heading.
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = ReaderTitleBarInset;
        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        if (reserveTitleBarSpace)
            window.Padding = new Thickness(0, ReaderTitleBarInset, 0, 0);

    }

    internal static bool IsWindowDragArea(Window window, Visual? source, Point point, double headerHeight)
    {
        if (window.WindowState is WindowState.FullScreen or WindowState.Minimized
            || point.X < 0 || point.X >= window.Bounds.Width
            || point.Y < 0 || point.Y >= headerHeight)
            return false;

        for (var visual = source; visual is not null && visual != window; visual = visual.GetVisualParent())
        {
            // Some reader toolbar buttons deliberately do not take focus.
            // Exclude control types as well as focusable custom controls so
            // dragging cannot steal clicks, text selection or list gestures.
            if (visual is Button or TextBox or SelectingItemsControl or RangeBase
                or ToggleSwitch or MenuBase or SelectableTextBlock or Thumb or NativeControlHost
                || visual is InputElement { Focusable: true })
                return false;
        }
        return true;
    }
}
