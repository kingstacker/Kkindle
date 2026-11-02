using Avalonia;
using Avalonia.iOS;
using Foundation;
using Kkindle.Mobile;

namespace Kkindle.iOSHead;

[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    /// <summary>Native glass bar, set before UIKit starts; absent on iOS &lt; 26.</summary>
    internal static IOSGlassTabBar? TabBar { get; set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);
}
