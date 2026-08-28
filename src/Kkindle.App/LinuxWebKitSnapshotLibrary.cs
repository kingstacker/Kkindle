using System.Runtime.InteropServices;

namespace Kkindle;

/// <summary>
/// Minimal P/Invoke surface for capturing a visible-page snapshot from the
/// native Linux WebKit view (WPE WebKit or WebKitGTK, whichever adapter
/// Avalonia created). The reader's slide/wave transitions need a frozen
/// bitmap of the outgoing page; Windows captures that with GDI, while the
/// Linux native view lives outside Avalonia's compositor and must be
/// rendered by WebKit itself through webkit_web_view_get_snapshot.
///
/// Every resolution step is optional: a missing library or symbol disables
/// the snapshot provider and the transition pipeline falls back to its
/// opacity-only path. The async-ready callback delegate is rooted for the
/// process lifetime because WebKit may invoke it after the caller has
/// already timed out.
/// </summary>
internal sealed class LinuxWebKitSnapshotLibrary
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WebKitGetSnapshotDelegate(
        IntPtr webView, uint region, uint options, IntPtr cancellable, IntPtr callback, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WebKitSnapshotFinishDelegate(IntPtr webView, IntPtr asyncResult);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GAsyncReadyCallback(IntPtr sourceObject, IntPtr asyncResult, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CairoWriteToPngStreamDelegate(IntPtr surface, IntPtr writeFunc, IntPtr closure);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CairoWriteFunc(IntPtr closure, IntPtr data, UIntPtr length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CairoSurfaceDestroyDelegate(IntPtr surface);

    // webkit_web_view_get_snapshot enumerations. VISIBLE and NONE are both 0,
    // kept here so the call site states its intent.
    private const uint SnapshotRegionVisible = 0;
    private const uint SnapshotOptionsNone = 0;

    private static LinuxWebKitSnapshotLibrary? _instance;

    private readonly WebKitGetSnapshotDelegate _getSnapshot;
    private readonly WebKitSnapshotFinishDelegate _snapshotFinish;
    private readonly CairoWriteToPngStreamDelegate _writeToPngStream;
    private readonly CairoSurfaceDestroyDelegate _surfaceDestroy;
    private readonly GAsyncReadyCallback _callbackThunk;
    private readonly IntPtr _callbackPointer;

    private LinuxWebKitSnapshotLibrary(
        WebKitGetSnapshotDelegate getSnapshot,
        WebKitSnapshotFinishDelegate snapshotFinish,
        CairoWriteToPngStreamDelegate writeToPngStream,
        CairoSurfaceDestroyDelegate surfaceDestroy,
        GAsyncReadyCallback callbackThunk,
        IntPtr callbackPointer)
    {
        _getSnapshot = getSnapshot;
        _snapshotFinish = snapshotFinish;
        _writeToPngStream = writeToPngStream;
        _surfaceDestroy = surfaceDestroy;
        _callbackThunk = callbackThunk;
        _callbackPointer = callbackPointer;
    }

    public static LinuxWebKitSnapshotLibrary? Instance => _instance ??= TryCreate();

    /// <summary>
    /// Issues an asynchronous snapshot of the visible viewport. The request
    /// must be issued from the thread that owns the WebKitWebView; WebKit
    /// later invokes the rooted callback on its main context with userData.
    /// </summary>
    public void BeginCapture(IntPtr webView, IntPtr userData)
    {
        _getSnapshot(
            webView, SnapshotRegionVisible, SnapshotOptionsNone, IntPtr.Zero, _callbackPointer, userData);
    }

    /// <summary>
    /// Completes a snapshot request started by BeginCapture, encodes the
    /// returned surface as PNG and destroys the surface.
    /// </summary>
    public byte[]? FinishCapture(IntPtr webView, IntPtr asyncResult)
    {
        var surface = _snapshotFinish(webView, asyncResult);
        if (surface == IntPtr.Zero)
            return null;
        try
        {
            using var png = new MemoryStream();
            var pinnedPng = GCHandle.Alloc(png);
            try
            {
                var writeThunk = new CairoWriteFunc(WritePngChunk);
                var writePointer = Marshal.GetFunctionPointerForDelegate(writeThunk);
                // writeThunk is invoked synchronously inside the cairo call;
                // the local reference keeps it alive for the call duration.
                var status = _writeToPngStream(surface, writePointer, GCHandle.ToIntPtr(pinnedPng));
                return status == 0 ? png.ToArray() : null;
            }
            finally
            {
                if (pinnedPng.IsAllocated) pinnedPng.Free();
            }
        }
        finally
        {
            _surfaceDestroy(surface);
        }
    }

    private static int WritePngChunk(IntPtr closure, IntPtr data, UIntPtr length)
    {
        try
        {
            if (GCHandle.FromIntPtr(closure).Target is not MemoryStream png || data == IntPtr.Zero)
                return 1;
            var byteCount = checked((int)length);
            var buffer = new byte[byteCount];
            Marshal.Copy(data, buffer, 0, byteCount);
            png.Write(buffer, 0, byteCount);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static LinuxWebKitSnapshotLibrary? TryCreate()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        // The WPE adapter ships on fewer distributions; Avalonia falls back to
        // the WebKitGTK adapter, so both sonames are valid entry points. The
        // libraries are already loaded by the running adapter and stay alive
        // for the process lifetime; the handles are released here only to drop
        // this resolver's extra dlopen reference.
        foreach (var soname in new[]
                 {
                     "libWPEWebKit-2.0.so.1",
                     "libwebkit2gtk-4.1.so.0",
                     "libwebkit2gtk-4.0.so.0"
                 })
        {
            if (!NativeLibrary.TryLoad(soname, out var webkit))
                continue;
            try
            {
                var getSnapshot = TryGetDelegate<WebKitGetSnapshotDelegate>(
                    webkit, "webkit_web_view_get_snapshot");
                var snapshotFinish = TryGetDelegate<WebKitSnapshotFinishDelegate>(
                    webkit, "webkit_web_view_snapshot_finish")
                    ?? TryGetDelegate<WebKitSnapshotFinishDelegate>(
                        webkit, "webkit_web_view_get_snapshot_finish");
                if (getSnapshot is null || snapshotFinish is null)
                    continue;

                if (!NativeLibrary.TryLoad("libcairo.so.2", out var cairo))
                    continue;
                try
                {
                    var writeToPngStream = TryGetDelegate<CairoWriteToPngStreamDelegate>(
                        cairo, "cairo_surface_write_to_png_stream");
                    var surfaceDestroy = TryGetDelegate<CairoSurfaceDestroyDelegate>(
                        cairo, "cairo_surface_destroy");
                    if (writeToPngStream is null || surfaceDestroy is null)
                        continue;

                    var callbackThunk = new GAsyncReadyCallback(DispatchSnapshotReady);
                    return new LinuxWebKitSnapshotLibrary(
                        getSnapshot,
                        snapshotFinish,
                        writeToPngStream,
                        surfaceDestroy,
                        callbackThunk,
                        Marshal.GetFunctionPointerForDelegate(callbackThunk));
                }
                finally
                {
                    NativeLibrary.Free(cairo);
                }
            }
            finally
            {
                NativeLibrary.Free(webkit);
            }
        }

        return null;
    }

    private static void DispatchSnapshotReady(IntPtr sourceObject, IntPtr asyncResult, IntPtr userData)
    {
        LinuxWebKitPageSnapshotRequest.Complete(sourceObject, asyncResult, userData);
    }

    private static T? TryGetDelegate<T>(IntPtr library, string export)
        where T : class
    {
        try
        {
            if (!NativeLibrary.TryGetExport(library, export, out var pointer) || pointer == IntPtr.Zero)
                return null;
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Marshals one pending Linux snapshot request between the WebKit
/// main-context callback and the awaiting reader transition. The GCHandle
/// keeps this instance alive while WebKit owns the user_data pointer.
/// </summary>
internal sealed class LinuxWebKitPageSnapshotRequest
{
    private readonly IntPtr _webView;
    private readonly TaskCompletionSource<byte[]?> _completion;
    private GCHandle _handle;

    private LinuxWebKitPageSnapshotRequest(IntPtr webView)
    {
        _webView = webView;
        _completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static Task<byte[]?> StartAsync(IntPtr webView, TimeSpan timeout)
    {
        if (webView == IntPtr.Zero || LinuxWebKitSnapshotLibrary.Instance is not { } library)
            return Task.FromResult<byte[]?>(null);

        var request = new LinuxWebKitPageSnapshotRequest(webView);
        request._handle = GCHandle.Alloc(request);
        try
        {
            library.BeginCapture(webView, GCHandle.ToIntPtr(request._handle));
        }
        catch
        {
            if (request._handle.IsAllocated)
                request._handle.Free();
            return Task.FromResult<byte[]?>(null);
        }

        return request.AwaitAsync(timeout);
    }

    private async Task<byte[]?> AwaitAsync(TimeSpan timeout)
    {
        try
        {
            var completed = await Task.WhenAny(_completion.Task, Task.Delay(timeout)).ConfigureAwait(true);
            return completed == _completion.Task
                ? await _completion.Task.ConfigureAwait(true)
                : null;
        }
        finally
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }

    internal static void Complete(IntPtr sourceObject, IntPtr asyncResult, IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not LinuxWebKitPageSnapshotRequest request)
            return;
        byte[]? png = null;
        try
        {
            if (LinuxWebKitSnapshotLibrary.Instance is { } library)
                png = library.FinishCapture(request._webView, asyncResult);
        }
        catch
        {
            png = null;
        }
        request._completion.TrySetResult(png);
    }
}
