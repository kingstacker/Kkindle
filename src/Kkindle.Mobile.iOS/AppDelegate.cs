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
#if !KKINDLE_SWIFT_UI
    private static UIWindow? _observedWindow;
#else
    private static UIWindow? _restoredSessionWindow;
#endif

    /// <summary>Native glass bar, set before UIKit starts; absent on iOS &lt; 26.</summary>
    internal static IOSGlassTabBar? TabBar { get; set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);

    public AppDelegate()
    {
#if !KKINDLE_SWIFT_UI
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
#else
        // UIKit can restore a scene session created by the previous Avalonia
        // delegate instead of asking for the new scene configuration. Such a
        // session may have no window. Migrate its UI in place without touching
        // Documents or requiring a reinstall that would erase the book library.
        NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.DidBecomeActiveNotification,
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(ConfigureRestoredNativeSession));
#endif
    }

#if KKINDLE_SWIFT_UI
    [Export("application:configurationForConnectingSceneSession:options:")]
    public new UISceneConfiguration GetConfiguration(UIApplication application,
        UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Kkindle Native", connectingSceneSession.Role) { DelegateType = typeof(IOSNativeSceneDelegate) };

    private static void ConfigureRestoredNativeSession()
    {
        if (IOSNativeFrontend.IsAttached || Application.Current is not MobileApp { Shell: { } }) return;
        var scene = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>()
            .FirstOrDefault(scene => scene.Session.Role == UIWindowSceneSessionRole.Application
                && scene.ActivationState is UISceneActivationState.ForegroundActive or UISceneActivationState.ForegroundInactive);
        if (scene is null) return;
        var previousWindows = scene.Windows;
        var window = new UIWindow(scene);
        IOSNativeFrontend.Attach(window);
        if (!IOSNativeFrontend.IsAttached) return;
        _restoredSessionWindow = window;
        window.MakeKeyAndVisible();
        foreach (var previous in previousWindows) previous.Hidden = true;
#if DEBUG
        Console.WriteLine("Kkindle native: restored the previous scene session without replacing app data.");
#endif
    }
#endif

    private static void ConfigureConnectedWindows()
    {
        foreach (var window in UIApplication.SharedApplication.ConnectedScenes
                     .OfType<UIWindowScene>()
                     .SelectMany(scene => scene.Windows))
            ConfigureWindow(window);
    }

    private static void ConfigureWindow(UIWindow? window)
    {
        if (window?.RootViewController?.View is not AvaloniaView)
            return;

#if KKINDLE_SWIFT_UI
        // Keep Avalonia's controller only as an embeddable reader surface.
        // SwiftUI owns the window, navigation, scrolling and all controls.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IOSNativeFrontend.Attach(window));
        return;
#else
        ApplyWindowAppearance(window);
        if (ReferenceEquals(_observedWindow, window) || !OperatingSystem.IsIOSVersionAtLeast(17))
            return;

        window.RegisterForTraitChanges<UITraitUserInterfaceStyle>((_, _) => ApplyWindowAppearance(window));
        _observedWindow = window;
#endif
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
