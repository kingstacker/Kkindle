using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Kkindle.Infrastructure;

/// <summary>
/// Probes native libraries without ever unloading them. WebKitGTK/WPE (and
/// similar GLib-based libraries) register TLS destructors, atexit handlers
/// and background threads that keep running after dlclose; unloading them
/// crashes the process at a random later moment. Every successful load is
/// therefore cached and kept alive for the process lifetime — the extra
/// dlopen reference is intentional, do not "fix" it by freeing the handle.
/// </summary>
public static class KeepAliveNativeLibrary
{
    private static readonly ConcurrentDictionary<string, IntPtr?> Handles =
        new(StringComparer.Ordinal);

    public static bool TryLoad(string libraryName) => TryLoad(libraryName, out _);

    public static bool TryLoad(string libraryName, out IntPtr handle)
    {
        var cached = Handles.GetOrAdd(
            libraryName,
            static name => NativeLibrary.TryLoad(name, out var loaded) ? loaded : null);
        handle = cached ?? IntPtr.Zero;
        return cached is not null;
    }
}
