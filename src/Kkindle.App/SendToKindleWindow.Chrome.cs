using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

public partial class SendToKindleWindow
{
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateWindowChrome();
    }

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximized();
        else BeginMoveDrag(e);
        e.Handled = true;
    }

    private void MinimizeWindowButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximized();

    // Use the normal closing path, which protects a send still in progress.
    private void CloseWindowButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() => WindowState = WindowState is WindowState.Maximized or WindowState.FullScreen
        ? WindowState.Normal : WindowState.Maximized;

    private void UpdateWindowChrome()
    {
        if (MaximizeWindowGlyph is null || MaximizeWindowButton is null || WindowResizeLayer is null) return;
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        MaximizeWindowGlyph.Data = Geometry.Parse(maximized
            ? "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z"
            : "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z");
        var label = UiText.Get(maximized ? "还原" : "最大化");
        AutomationProperties.SetName(MaximizeWindowButton, label);
        ToolTip.SetTip(MaximizeWindowButton, label);
        WindowResizeLayer.IsVisible = !maximized;
    }

    private void WindowResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !CanResize
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: string edge }
            || !Enum.TryParse<WindowEdge>(edge, out var windowEdge)) return;
        BeginResizeDrag(windowEdge, e);
        e.Handled = true;
    }
}
