using Foundation;
using Kkindle.Mobile;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>
/// Process entry point. The platform adapters are published before UIKit (and
/// therefore Avalonia) can construct the application.
/// </summary>
public static class Program
{
    private static NSObject? _activationObserver;

    private static void Main(string[] args)
    {
        // iOS 26 owns the Liquid Glass material. Older systems keep the
        // Avalonia bar, which fakes the material with blurred colour fields.
        IOSGlassTabBar? tabBar = null;
        if (OperatingSystem.IsIOSVersionAtLeast(26))
            tabBar = new IOSGlassTabBar();
        AppDelegate.TabBar = tabBar;

        MobileApp.Services = IOSMobileHost.Create(tabBar);
        // Once active, the window and Avalonia's view exist, so the native bar
        // can be layered above them.
        _activationObserver = UIApplication.Notifications.ObserveDidBecomeActive(
            (_, _) => AppDelegate.TabBar?.Attach());

        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
