using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.MacOS;

/// <summary>
/// Reports USB attach/detach events through IOKit service notifications, so
/// the Kindle device list refreshes when a reader is plugged in without
/// polling. Both the modern <c>IOUSBHostDevice</c> and the legacy
/// <c>IOUSBDevice</c> classes are matched; notifications are delivered on the
/// main dispatch queue, which is the Avalonia UI thread.
/// </summary>
public sealed class MacOSDeviceChangeNotifier : IDeviceChangeNotifier
{
    private const string IOKitLibrary =
        "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string LibSystemLibrary = "/usr/lib/libSystem.B.dylib";

    private static readonly string[] ServiceClasses = ["IOUSBHostDevice", "IOUSBDevice"];
    private static readonly string[] NotificationTypes = ["IOServiceFirstMatch", "IOServiceTerminate"];

    private static readonly ServiceCallback Callback = OnServicesChanged;
    private static readonly Lazy<IntPtr> MainQueue = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load(LibSystemLibrary), "_dispatch_main_q"));

    private IntPtr _notificationPort;
    private GCHandle _self;
    private readonly List<uint> _notifications = [];
    private bool _disposed;

    public event EventHandler? DeviceChanged;

    public MacOSDeviceChangeNotifier()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("IOKit notifications are only available on macOS.");
        if (IOMasterPort(0, out var masterPort) != 0 || masterPort == 0)
            throw new InvalidOperationException("无法创建 IOKit 主端口。");

        _notificationPort = IONotificationPortCreate(masterPort);
        if (_notificationPort == IntPtr.Zero)
            throw new InvalidOperationException("无法创建 IOKit USB 通知端口。");

        _self = GCHandle.Alloc(this);
        try
        {
            IONotificationPortSetDispatchQueue(_notificationPort, MainQueue.Value);
            var callback = Marshal.GetFunctionPointerForDelegate(Callback);
            foreach (var serviceClass in ServiceClasses)
            {
                foreach (var notificationType in NotificationTypes)
                {
                    var matching = IOServiceMatching(serviceClass);
                    if (matching == IntPtr.Zero) continue;
                    // The dictionary is consumed by the notification, even when
                    // registration fails; it must not be released here.
                    if (IOServiceAddMatchingNotification(
                            _notificationPort,
                            notificationType,
                            matching,
                            callback,
                            GCHandle.ToIntPtr(_self),
                            out var iterator) != 0)
                        continue;
                    // The initial iteration is required: it arms the
                    // notification and must be drained, otherwise the callback
                    // never fires for later arrivals.
                    _notifications.Add(iterator);
                    Drain(iterator);
                }
            }
        }
        catch
        {
            ReleaseNotifications();
            IONotificationPortDestroy(_notificationPort);
            _notificationPort = IntPtr.Zero;
            _self.Free();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_notificationPort != IntPtr.Zero)
            {
                IONotificationPortSetDispatchQueue(_notificationPort, IntPtr.Zero);
                ReleaseNotifications();
                IONotificationPortDestroy(_notificationPort);
            }
        }
        finally
        {
            _notificationPort = IntPtr.Zero;
            if (_self.IsAllocated) _self.Free();
        }
    }

    private void ReleaseNotifications()
    {
        foreach (var notification in _notifications)
            IOObjectRelease(notification);
        _notifications.Clear();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ServiceCallback(IntPtr refcon, uint iterator);

    private static void OnServicesChanged(IntPtr refcon, uint iterator)
    {
        try
        {
            // Drain the published services so IOKit keeps re-arming.
            Drain(iterator);
            if (refcon == IntPtr.Zero) return;
            var handle = GCHandle.FromIntPtr(refcon);
            if (handle.Target is MacOSDeviceChangeNotifier { _disposed: false } notifier)
                notifier.DeviceChanged?.Invoke(notifier, EventArgs.Empty);
        }
        catch
        {
            // No managed exception may cross a native callback boundary.
        }
    }

    private static void Drain(uint iterator)
    {
        if (iterator == 0) return;
        while (true)
        {
            var service = IOIteratorNext(iterator);
            if (service == 0) break;
            IOObjectRelease(service);
        }
    }

    [DllImport(IOKitLibrary)]
    private static extern int IOMasterPort(uint bootstrapPort, out uint masterPort);

    [DllImport(IOKitLibrary)]
    private static extern IntPtr IOServiceMatching(string name);

    [DllImport(IOKitLibrary)]
    private static extern IntPtr IONotificationPortCreate(uint masterPort);

    [DllImport(IOKitLibrary)]
    private static extern void IONotificationPortDestroy(IntPtr notifyPort);

    [DllImport(IOKitLibrary)]
    private static extern void IONotificationPortSetDispatchQueue(IntPtr notifyPort, IntPtr queue);

    [DllImport(IOKitLibrary)]
    private static extern int IOServiceAddMatchingNotification(
        IntPtr notifyPort,
        string notificationType,
        IntPtr matching,
        IntPtr callback,
        IntPtr refcon,
        out uint iterator);

    [DllImport(IOKitLibrary)]
    private static extern uint IOIteratorNext(uint iterator);

    [DllImport(IOKitLibrary)]
    private static extern int IOObjectRelease(uint obj);
}
