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
    private static readonly string[] NotificationTypes = ["IOServiceFirstMatch", "IOServiceTerminated"];

    private static readonly ServiceCallback Callback = OnServicesChanged;

    private IntPtr _notificationPort;
    private GCHandle _self;
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
            IONotificationPortSetDispatchQueue(_notificationPort, dispatch_get_main_queue());
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
                    Drain(iterator);
                    IOObjectRelease(iterator);
                }
            }
        }
        catch
        {
            _self.Free();
            IONotificationPortDestroy(_notificationPort);
            _notificationPort = IntPtr.Zero;
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
                IONotificationPortDestroy(_notificationPort);
            }
        }
        finally
        {
            _notificationPort = IntPtr.Zero;
            if (_self.IsAllocated) _self.Free();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ServiceCallback(IntPtr refcon, IntPtr iterator);

    private static void OnServicesChanged(IntPtr refcon, IntPtr iterator)
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

    private static void Drain(IntPtr iterator)
    {
        if (iterator == IntPtr.Zero) return;
        while (true)
        {
            var service = IOIteratorNext(iterator);
            if (service == IntPtr.Zero) break;
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
        out IntPtr iterator);

    [DllImport(IOKitLibrary)]
    private static extern IntPtr IOIteratorNext(IntPtr iterator);

    [DllImport(IOKitLibrary)]
    private static extern int IOObjectRelease(IntPtr obj);

    [DllImport(LibSystemLibrary)]
    private static extern IntPtr dispatch_get_main_queue();
}
