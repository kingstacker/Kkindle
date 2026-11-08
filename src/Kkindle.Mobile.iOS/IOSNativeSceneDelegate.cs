#if KKINDLE_SWIFT_UI
using Foundation;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>Creates the SwiftUI root before the window becomes visible.</summary>
[Register("IOSNativeSceneDelegate")]
public sealed class IOSNativeSceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions options)
    {
#if DEBUG
        Console.WriteLine($"Kkindle native: connecting scene ({session.Role}).");
#endif
        if (scene is not UIWindowScene windowScene || session.Role != UIWindowSceneSessionRole.Application) return;
        Window = new UIWindow(windowScene);
        IOSNativeFrontend.Attach(Window);
        Window.MakeKeyAndVisible();

        if (options.UrlContexts is { } urls) OpenUrlContexts(scene, urls);
        if (options.UserActivities is { } activities)
            foreach (var activity in activities) ContinueUserActivity(scene, activity);
    }

    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> contexts)
    {
        if (UIApplication.SharedApplication.Delegate is not AppDelegate appDelegate) return;
        foreach (var context in contexts)
            appDelegate.OpenUrl(UIApplication.SharedApplication, context.Url, new NSDictionary());
    }

    [Export("scene:continueUserActivity:")]
    public void ContinueUserActivity(UIScene scene, NSUserActivity activity)
    {
        if (UIApplication.SharedApplication.Delegate is AppDelegate appDelegate)
            appDelegate.ContinueUserActivity(UIApplication.SharedApplication, activity, _ => { });
    }
}
#endif
