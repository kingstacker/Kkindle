using Foundation;
using CoreText;
using Kkindle.Mobile;
using Kkindle.Mobile.Controls;
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
        var fontPath = Path.Combine(NSBundle.MainBundle.BundlePath, "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
        if (File.Exists(fontPath))
        {
            using var fontUrl = NSUrl.FromFilename(fontPath);
            CTFontManager.RegisterFontsForUrl(fontUrl, CTFontManagerScope.Process);
        }
        // iOS 26 owns the Liquid Glass material. Older systems keep the
        // Avalonia bar, which fakes the material with blurred colour fields.
        IOSGlassTabBar? tabBar = null;
        if (OperatingSystem.IsIOSVersionAtLeast(26))
        {
            tabBar = new IOSGlassTabBar();
            NativeSegmentGroup.CreateNativeControl = buttons =>
                OperatingSystem.IsIOSVersionAtLeast(26)
                    ? new IOSNativeSegmentControl(buttons)
                    : throw new PlatformNotSupportedException();
        }
        AppDelegate.TabBar = tabBar;

        MobileApp.Services = IOSMobileHost.Create(tabBar);
        // Once active, the window and Avalonia's view exist, so the native bar
        // can be layered above them.
        _activationObserver = UIApplication.Notifications.ObserveDidBecomeActive(
            (_, _) =>
            {
                if (OperatingSystem.IsIOSVersionAtLeast(26))
                    AppDelegate.TabBar?.Attach();
            });

        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
