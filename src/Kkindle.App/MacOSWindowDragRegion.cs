using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.VisualTree;

namespace Kkindle;

/// <summary>
/// Gives AppKit a native titlebar hit target without drawing a separate bar.
/// The native backend checks the exact visual hit before routing pointer
/// events, so a normal ancestor PointerPressed handler is not sufficient.
/// </summary>
internal sealed class MacOSWindowDragRegion : Border, ICustomHitTest
{
    private readonly Window _window;
    private readonly Control _content;

    public MacOSWindowDragRegion(Window window, Control content)
    {
        _window = window;
        _content = content;
        Name = "MacOSWindowDragRegion";
        Height = DesktopPlatform.MainWindowDragHeaderHeight;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background = Brushes.Transparent;
        ZIndex = 1001;
        WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);
    }

    public bool HitTest(Point point)
    {
        var windowPoint = this.TranslatePoint(point, _window);
        var contentPoint = this.TranslatePoint(point, _content);
        if (windowPoint is null || contentPoint is null) return false;

        // Hit-test only the underlying content, not the window containing this
        // overlay. This avoids recursion and makes holes for real controls at
        // their current positions, including buttons that do not take focus.
        var source = _content.InputHitTest(contentPoint.Value, enabledElementsOnly: false) as Visual;
        return DesktopPlatform.IsWindowDragArea(_window, source, windowPoint.Value, Height);
    }
}
