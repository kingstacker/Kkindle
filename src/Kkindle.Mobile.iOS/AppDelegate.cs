using Avalonia;
using Avalonia.iOS;
using Avalonia.Styling;
using Foundation;
using Kkindle.Mobile;
using UIKit;

namespace Kkindle.iOSHead;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    private static UIWindow? _observedWindow;

    /// <summary>Native glass bar, set before UIKit starts; absent on iOS &lt; 26.</summary>
    internal static IOSGlassTabBar? TabBar { get; set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);

    public AppDelegate()
    {
        // Scene-based launches create the window after the delegate has been
        // constructed. Configure it the moment it becomes visible so the frames
        // between the launch screen and Avalonia's first surface are already
        // painted with the page colour instead of flashing the default black.
        NSNotificationCenter.DefaultCenter.AddObserver(
            UIWindow.DidBecomeVisibleNotification,
            notification => ConfigureWindow(notification.Object as UIWindow));
        NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.DidBecomeActiveNotification,
            _ => ConfigureConnectedWindows());
    }

    private static void ConfigureConnectedWindows()
    {
        foreach (var window in UIApplication.SharedApplication.ConnectedScenes
                     .OfType<UIWindowScene>()
                     .SelectMany(scene => scene.Windows))
            ConfigureWindow(window);
    }

    private static void ConfigureWindow(UIWindow? window)
    {
        if (window is null)
            return;

        ApplyWindowAppearance(window);
        if (ReferenceEquals(_observedWindow, window) || !OperatingSystem.IsIOSVersionAtLeast(17))
            return;

        window.RegisterForTraitChanges<UITraitUserInterfaceStyle>((_, _) => ApplyWindowAppearance(window));
        _observedWindow = window;
    }

    /// <summary>
    /// Keep the window backing colour and the Avalonia theme in sync with the
    /// system appearance. Avalonia 12 listens for appearance changes through
    /// the deprecated <c>TraitCollectionDidChange</c> callback, which iOS 27 no
    /// longer delivers reliably, so the replacement API is registered from
    /// <see cref="ConfigureWindow"/>.
    /// </summary>
    private static void ApplyWindowAppearance(UIWindow window)
    {
        var style = window.TraitCollection.UserInterfaceStyle;
        window.BackgroundColor = PageColor(style);
        ApplySystemTheme(style);
    }

    private static UIColor PageColor(UIUserInterfaceStyle style) =>
        style == UIUserInterfaceStyle.Dark
            ? UIColor.FromRGB(0x12, 0x12, 0x12)
            : UIColor.White;

    private static void ApplySystemTheme(UIUserInterfaceStyle style)
    {
        if (Application.Current is not MobileApp { Shell: { } shell })
            return;

        shell.ApplySystemThemeVariant(
            style == UIUserInterfaceStyle.Dark ? ThemeVariant.Dark : ThemeVariant.Light);
    }
}
