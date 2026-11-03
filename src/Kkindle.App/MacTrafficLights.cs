using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kkindle.Core;

namespace Kkindle;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;

/// <summary>
/// Drawn macOS traffic lights for surfaces that cannot use the native
/// titlebar: the zen fullscreen popup lives outside the native window chrome.
/// Normal windows keep <c>WindowDecorations=Full</c>, so macOS draws the real
/// controls there and this control is not used.
/// </summary>
public sealed class MacTrafficLights : StackPanel
{
    public const double TitleBarHeight = 38;

    // 16 leading margin + 3 x 12 lights + 2 x 8 gaps, then breathing room.
    // Title text starts here so the brand never collides with the lights.
    public const double TitleContentInset = 84;

    private const string CloseGlyph = "M 3.5,3.5 L 8.5,8.5 M 8.5,3.5 L 3.5,8.5";
    private const string MinimizeGlyph = "M 3,6 H 9";
    private const string ZoomGlyph =
        "M 2.5,5.5 L 5.5,2.5 L 5.5,5.5 Z M 9.5,6.5 L 6.5,9.5 L 6.5,6.5 Z";

    private readonly Button _closeButton;
    private readonly Button _minimizeButton;
    private readonly Button _zoomButton;
    private readonly Button[] _buttons;
    private Window? _window;
    private bool _explicitWindow;

    public MacTrafficLights()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = IsSupported;

        _closeButton = CreateButton("close", UiText.Get("关闭"), CloseGlyph);
        _minimizeButton = CreateButton("minimize", UiText.Get("最小化"), MinimizeGlyph);
        _zoomButton = CreateButton("zoom", UiText.Get("最大化"), ZoomGlyph);
        _buttons = [_closeButton, _minimizeButton, _zoomButton];

        _closeButton.Click += (_, e) => RequestClose(e);
        _minimizeButton.Click += (_, e) => RequestMinimize(e);
        _zoomButton.Click += (_, e) => RequestZoom(e);

        Children.Add(_closeButton);
        Children.Add(_minimizeButton);
        Children.Add(_zoomButton);

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    /// <summary>True only on macOS; the control stays collapsed elsewhere.</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS();

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? MinimizeRequested;

    public event EventHandler<RoutedEventArgs>? ZoomRequested;

    /// <summary>
    /// Tracks the window that owns the controls. Normally resolved from the
    /// visual tree; the zen popup lives in a separate popup root, so the
    /// window is supplied explicitly there to keep the inactive state in sync.
    /// </summary>
    public void AttachToWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        DetachWindow();
        _window = window;
        _explicitWindow = true;
        window.Activated += Window_Activated;
        window.Deactivated += Window_Deactivated;
        UpdateActiveAppearance(window.IsActive);
    }

    private static Button CreateButton(string styleClass, string label, string geometry)
    {
        var button = new Button
        {
            Content = new AvaloniaPath
            {
                Data = Geometry.Parse(geometry),
                Width = 12,
                Height = 12,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        button.Classes.Add("trafficLight");
        button.Classes.Add(styleClass);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    private void RequestClose(RoutedEventArgs e)
    {
        if (CloseRequested is { } handler) handler(this, e);
        else (TopLevel.GetTopLevel(this) as Window)?.Close();
    }

    private void RequestMinimize(RoutedEventArgs e)
    {
        if (MinimizeRequested is { } handler) handler(this, e);
        else if (TopLevel.GetTopLevel(this) is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void RequestZoom(RoutedEventArgs e)
    {
        if (ZoomRequested is { } handler)
        {
            handler(this, e);
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        window.WindowState = window.WindowState is WindowState.Maximized or WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // An explicit AttachToWindow call wins: the control may live in a
        // popup root that is not itself a Window (the zen control strip).
        if (_window is not null) return;
        if (TopLevel.GetTopLevel(this) is Window window)
            AttachToWindow(window);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // A popup root detaches while the owning window still exists. Keep an
        // explicitly supplied window subscribed so the lights stay in sync on
        // the next time the zen strip opens.
        if (!_explicitWindow) DetachWindow();
    }

    private void DetachWindow()
    {
        _explicitWindow = false;
        if (_window is null) return;
        _window.Activated -= Window_Activated;
        _window.Deactivated -= Window_Deactivated;
        _window = null;
    }

    private void Window_Activated(object? sender, EventArgs e) => UpdateActiveAppearance(true);

    private void Window_Deactivated(object? sender, EventArgs e) => UpdateActiveAppearance(false);

    // An unfocused macOS window greys out all three lights and hides the
    // hover glyphs, so the controls do not look clickable while inactive.
    private void UpdateActiveAppearance(bool active)
    {
        foreach (var button in _buttons)
            button.Classes.Set("inactive", !active);
    }
}
