using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Kkindle;

/// <summary>
/// The release build is a proper .app bundle, so LaunchServices reads its Dock
/// icon from Info.plist. The Debug head runs as a loose executable with no
/// bundle metadata, which makes the Dock fall back to the generic app icon.
/// Apply the shared PNG at runtime so both builds look the same.
/// </summary>
internal static class MacDockIcon
{
    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
    private const string IconUri = "avares://Kkindle.App/Assets/Icons/kkindle-dock.png";

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

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // A bundled app already has the icon; only the loose Debug executable
        // needs this fallback.
        if (AppContext.BaseDirectory.Contains(".app/Contents/MacOS", StringComparison.Ordinal)) return;

        try
        {
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
}
