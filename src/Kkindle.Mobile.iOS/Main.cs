using Foundation;
using CoreText;
using Kkindle.Mobile;
using Kkindle.Mobile.Controls;
using System.Runtime.InteropServices;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>
/// Process entry point. The platform adapters are published before UIKit (and
/// therefore Avalonia) can construct the application.
/// </summary>
public static class Program
{
    private static NSObject? _activationObserver;

    /// <summary>
    /// Builds pinned to an older iOS SDK resolve SkiaSharp's portable assembly,
    /// whose P/Invoke asks for the bare name "libSkiaSharp". iOS does not search
    /// the bundle's Frameworks directory for a bare name, so map it to the
    /// framework the build embeds. The simulator build already asks for the
    /// framework path and falls through untouched.
    /// </summary>
    private static void RegisterNativeLibraryResolvers()
    {
        NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKImageInfo).Assembly, (libraryName, _, _) =>
        {
            if (libraryName != "libSkiaSharp")
                return IntPtr.Zero;

            var frameworkPath = Path.Combine(
                NSBundle.MainBundle.BundlePath,
                "Frameworks",
                "libSkiaSharp.framework",
                "libSkiaSharp");

            return File.Exists(frameworkPath) ? NativeLibrary.Load(frameworkPath) : IntPtr.Zero;
        });
    }

    private static void Main(string[] args)
    {
        RegisterNativeLibraryResolvers();
#if DEBUG
        ObjCRuntime.Runtime.MarshalManagedException += (_, args) => Console.Error.WriteLine(args.Exception);
#endif

        var fontPath = Path.Combine(NSBundle.MainBundle.BundlePath, "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
        if (File.Exists(fontPath))
        {
            using var fontUrl = NSUrl.FromFilename(fontPath);
            CTFontManager.RegisterFontsForUrl(fontUrl, CTFontManagerScope.Process);
        }
        // iOS 26 owns the Liquid Glass material. Older systems keep the
        // Avalonia bar, which fakes the material with blurred colour fields.
        IOSGlassTabBar? tabBar = null;
#if !KKINDLE_SWIFT_UI
        if (OperatingSystem.IsIOSVersionAtLeast(26))
        {
            tabBar = new IOSGlassTabBar();
            NativeSegmentGroup.CreateNativeControl = buttons =>
                OperatingSystem.IsIOSVersionAtLeast(26)
                    ? new IOSNativeSegmentControl(buttons)
                    : throw new PlatformNotSupportedException();
            NativeSliderGroup.CreateNativeControl = (slider, started, completed) =>
                OperatingSystem.IsIOSVersionAtLeast(26)
                    ? new IOSNativeSliderControl(slider, started, completed)
                    : throw new PlatformNotSupportedException();
            NativeSwitchGroup.CreateNativeControl = (toggle, label) =>
                OperatingSystem.IsIOSVersionAtLeast(26)
                    ? new IOSNativeSwitchControl(toggle, label)
                    : throw new PlatformNotSupportedException();
            NativeMenuButton.CreateNativeControl = items =>
                OperatingSystem.IsIOSVersionAtLeast(26)
                    ? new IOSNativeMenuButton(items)
                    : throw new PlatformNotSupportedException();
            NativeActionButton.CreateNativeControl = button =>
                OperatingSystem.IsIOSVersionAtLeast(26)
                    ? new IOSNativeActionButton(button)
                    : throw new PlatformNotSupportedException();
        }
        AppDelegate.TabBar = tabBar;
#endif

        MobileApp.Services = IOSMobileHost.Create(tabBar);
        // Once active, the window and Avalonia's view exist, so the native bar
        // can be layered above them.
        _activationObserver = UIApplication.Notifications.ObserveDidBecomeActive(
            (_, _) =>
            {
#if !KKINDLE_SWIFT_UI
                if (OperatingSystem.IsIOSVersionAtLeast(26))
                    AppDelegate.TabBar?.Attach();
#endif
            });

        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
