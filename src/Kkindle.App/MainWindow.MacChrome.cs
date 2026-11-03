using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace Kkindle;

public partial class MainWindow
{
    // Distinguishes a window close (hide to the menu bar on macOS) from an
    // application quit (tray 退出, Cmd+Q or OS shutdown).
    private bool _applicationShuttingDown;

    // macOS draws the real traffic lights when the window keeps its native
    // titlebar. Extending the client area makes that titlebar transparent and
    // lets the self-drawn reader chrome keep the full surface, while macOS
    // still owns close/minimise/zoom, hover glyphs, inactive state and zoom.
    private void ConfigureMacWindowChrome()
    {
        if (!MacTrafficLights.IsSupported) return;

        MacDockIcon.Apply();

        WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = MacTrafficLights.TitleBarHeight;

        ReaderZenPopupMacControls.CloseRequested += CloseWindowButton_Click;
        ReaderZenPopupMacControls.MinimizeRequested += MinimizeWindowButton_Click;
        ReaderZenPopupMacControls.ZoomRequested += MaximizeWindowButton_Click;
        ReaderZenPopupMacControls.AttachToWindow(this);

        SetWindowsCaptionButtonsVisible(false);
        ReaderZenPopupMinimizeButton.IsVisible = false;
        ReaderZenPopupMaximizeButton.IsVisible = false;
        ReaderZenPopupCloseButton.IsVisible = false;
        WindowBrandText.Margin = new Thickness(MacTrafficLights.TitleContentInset, 0, 0, 0);

        // The sidebar brand sits below the traffic lights on the same line as
        // the page title. Match the navigation rows' selected bounds: the brand
        // starts at the row's left edge and the cloud ends at its right edge.
        SidebarBrandPanel.Margin = new Thickness(0);
        S3SyncIndicatorButton.Margin = new Thickness(0);

        // Let AppKit own the window shape. With an opaque window macOS clips
        // the content to the native rounded frame and draws the native shadow;
        // making the window transparent leaves a gap at the corners where the
        // system shadow shows through as a grey band.
        TransparencyLevelHint = [WindowTransparencyLevel.None];
        Background = new SolidColorBrush(Colors.White);
        WindowShadowHost.BorderThickness = new Thickness(0);
        WindowFrameOverlay.BorderThickness = new Thickness(0);

        // The native red button closes the window the dock way: keep the app
        // in the menu bar. An actual application quit must still exit.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownRequested += MacShutdownRequested;
    }

    private void MacShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
        => _applicationShuttingDown = true;

    /// <summary>
    /// Shows or hides the square Windows/Linux caption row. macOS always keeps
    /// it hidden, including when the zen chrome restores the title row.
    /// </summary>
    private void SetWindowsCaptionButtonsVisible(bool visible)
    {
        var show = visible && !MacTrafficLights.IsSupported;
        MinimizeWindowButton.IsVisible = show;
        MaximizeWindowButton.IsVisible = show;
        CloseWindowButton.IsVisible = show;
    }
}
