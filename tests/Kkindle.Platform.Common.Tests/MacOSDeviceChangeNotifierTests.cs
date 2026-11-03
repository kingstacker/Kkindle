using System.Reflection;
using System.Runtime.InteropServices;
using Kkindle.Platform.MacOS;
using Xunit;

namespace Kkindle.Platform.Common.Tests;

public sealed class MacOSDeviceChangeNotifierTests
{
    [Fact]
    public void UsbNotificationsRemainRegisteredUntilDisposed()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var notifier = new MacOSDeviceChangeNotifier();
        var field = typeof(MacOSDeviceChangeNotifier).GetField(
            "_notifications", BindingFlags.Instance | BindingFlags.NonPublic);
        var notifications = Assert.IsType<List<uint>>(field?.GetValue(notifier));

        // Both arrival and removal must be registered for each USB class.
        Assert.Equal(4, notifications.Count);
        foreach (var notification in notifications)
            Assert.Equal(0, IOObjectGetClass(notification, new byte[128]));
    }

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern int IOObjectGetClass(uint obj, byte[] className);
}
