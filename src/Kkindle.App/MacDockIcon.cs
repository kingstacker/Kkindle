using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Kkindle;

/// <summary>
/// Uses the system-rendered bundle icon for the Dock, with a small size
/// adjustment to match adjacent icons. Loose Debug executables use the static
/// PNG instead because they have no bundle metadata.
/// </summary>
internal static class MacDockIcon
{
    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
    private const string IconUri = "avares://Kkindle.App/Assets/Icons/kkindle-dock.png";
    private const double DockIconScale = 1.04;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(double X, double Y, double Width, double Height);

    [DllImport(ObjCRuntime, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCRuntime, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendStr(
        IntPtr receiver,
        IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendSize(IntPtr receiver, IntPtr selector, NativeSize size);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern void SendRect(IntPtr receiver, IntPtr selector, NativeRect rect);

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            const string bundleMarker = ".app/Contents/MacOS";
            var bundleIndex = AppContext.BaseDirectory.IndexOf(bundleMarker, StringComparison.Ordinal);
            if (bundleIndex >= 0)
            {
                ApplyBundleIcon(AppContext.BaseDirectory[..(bundleIndex + ".app".Length)]);
                return;
            }

            var iconPath = Path.Combine(Path.GetTempPath(), "Kkindle", "Kkindle-dock.png");
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            using (var source = AssetLoader.Open(new Uri(IconUri)))
            using (var target = File.Create(iconPath))
                source.CopyTo(target);

            var nsPath = SendStr(
                objc_getClass("NSString"),
                sel_registerName("stringWithUTF8String:"),
                iconPath);
            var image = SendPtr(
                Send(objc_getClass("NSImage"), sel_registerName("alloc")),
                sel_registerName("initWithContentsOfFile:"),
                nsPath);
            if (image == IntPtr.Zero) return;

            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            SendVoidPtr(app, sel_registerName("setApplicationIconImage:"), image);
            SendVoid(image, sel_registerName("release"));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unable to set the macOS Dock icon: {exception}");
        }
    }

    private static void ApplyBundleIcon(string bundlePath)
    {
        var nsPath = SendStr(
            objc_getClass("NSString"),
            sel_registerName("stringWithUTF8String:"),
            bundlePath);
        var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        var source = SendPtr(workspace, sel_registerName("iconForFile:"), nsPath);
        if (source == IntPtr.Zero) return;

        const double canvas = 1024;
        var output = SendSize(
            Send(objc_getClass("NSImage"), sel_registerName("alloc")),
            sel_registerName("initWithSize:"),
            new NativeSize(canvas, canvas));
        if (output == IntPtr.Zero) return;

        try
        {
            SendVoid(output, sel_registerName("lockFocus"));
            try
            {
                var size = canvas * DockIconScale;
                var offset = (canvas - size) / 2;
                SendRect(source, sel_registerName("drawInRect:"), new NativeRect(offset, offset, size, size));
            }
            finally
            {
                SendVoid(output, sel_registerName("unlockFocus"));
            }

            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            SendVoidPtr(app, sel_registerName("setApplicationIconImage:"), output);
        }
        finally
        {
            SendVoid(output, sel_registerName("release"));
        }
    }
}
