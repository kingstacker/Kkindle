using System.Runtime.InteropServices;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Platform.Windows;

public sealed class WindowsKindleWebFileInput : IKindleWebFileInput, IKindleWebBrowserSettings
{
    public bool DisableCredentialSaving(nint webViewHandle)
    {
        if (webViewHandle == nint.Zero) return false;
        var view = (ICoreWebView2)Marshal.GetTypedObjectForIUnknown(webViewHandle, typeof(ICoreWebView2));
        view.GetSettings(out var settingsObject);
        var settings = (ICoreWebView2Settings4)settingsObject;
        settings.SetPasswordAutosaveEnabled(0);
        settings.SetGeneralAutofillEnabled(0);
        settings.GetPasswordAutosaveEnabled(out var passwords);
        settings.GetGeneralAutofillEnabled(out var autofill);
        GC.KeepAlive(settingsObject);
        GC.KeepAlive(view);
        return passwords == 0 && autofill == 0;
    }

    public async Task SetFilesAsync(nint webViewHandle, string elementId, IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (webViewHandle == nint.Zero || paths.Count == 0 || elementId != "kkindle-send-files")
            throw new InvalidOperationException("Invalid Kindle file selection.");
        foreach (var path in paths) KindleWebFilePolicy.Inspect(path);

        // This RCW borrows Avalonia's WebView. Never release the native pointer,
        // dispose its controller, or use reflection to access the SDK internals.
        var view = (ICoreWebView2)Marshal.GetTypedObjectForIUnknown(webViewHandle, typeof(ICoreWebView2));
        using var document = await CallAsync(view, "DOM.getDocument", "{}", cancellationToken);
        var rootId = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
        using var element = await CallAsync(view, "DOM.querySelector",
            JsonSerializer.Serialize(new { nodeId = rootId, selector = "#kkindle-send-files" }), cancellationToken);
        var nodeId = element.RootElement.GetProperty("nodeId").GetInt32();
        if (nodeId == 0) throw new InvalidOperationException("The Kindle file input is unavailable.");
        using var result = await CallAsync(view, "DOM.setFileInputFiles",
            JsonSerializer.Serialize(new { nodeId, files = paths }), cancellationToken);
        GC.KeepAlive(view);
    }

    private static async Task<JsonDocument> CallAsync(ICoreWebView2 view, string method, string parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        view.GetSource(out var source);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !KindleWebNavigationPolicy.IsUploadPage(uri))
            throw new InvalidOperationException("Files can only be selected on the official Kindle upload page.");
        var handler = new CompletedHandler();
        view.CallDevToolsProtocolMethod(method, parameters, handler);
        var json = await handler.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        GC.KeepAlive(handler);
        return JsonDocument.Parse(json);
    }

    [ComVisible(true)]
    [Guid("5C4889F0-5EF6-4C5A-952C-D8F1B92D0574")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICompletedHandler
    {
        void Invoke(int errorCode, [MarshalAs(UnmanagedType.LPWStr)] string result);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletedHandler : ICompletedHandler
    {
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Invoke(int errorCode, string result)
        {
            if (errorCode < 0)
                Completion.TrySetException(new InvalidOperationException("The browser could not select the files."));
            else
                Completion.TrySetResult(result);
        }
    }

    // ICoreWebView2 ABI prefix, in the order published in WebView2.h.
    // Only settings, source and file-selection CDP are used by this bridge.
    [ComImport]
    [Guid("76ECEACB-0462-4D94-AC83-423A6793775E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2
    {
        void GetSettings([MarshalAs(UnmanagedType.Interface)] out object settings);
        void GetSource([MarshalAs(UnmanagedType.LPWStr)] out string uri);
        void Navigate([MarshalAs(UnmanagedType.LPWStr)] string uri);
        void NavigateToString([MarshalAs(UnmanagedType.LPWStr)] string html);
        void AddNavigationStarting(nint handler, out long token);
        void RemoveNavigationStarting(long token);
        void AddContentLoading(nint handler, out long token);
        void RemoveContentLoading(long token);
        void AddSourceChanged(nint handler, out long token);
        void RemoveSourceChanged(long token);
        void AddHistoryChanged(nint handler, out long token);
        void RemoveHistoryChanged(long token);
        void AddNavigationCompleted(nint handler, out long token);
        void RemoveNavigationCompleted(long token);
        void AddFrameNavigationStarting(nint handler, out long token);
        void RemoveFrameNavigationStarting(long token);
        void AddFrameNavigationCompleted(nint handler, out long token);
        void RemoveFrameNavigationCompleted(long token);
        void AddScriptDialogOpening(nint handler, out long token);
        void RemoveScriptDialogOpening(long token);
        void AddPermissionRequested(nint handler, out long token);
        void RemovePermissionRequested(long token);
        void AddProcessFailed(nint handler, out long token);
        void RemoveProcessFailed(long token);
        void AddScriptToExecuteOnDocumentCreated([MarshalAs(UnmanagedType.LPWStr)] string script, nint handler);
        void RemoveScriptToExecuteOnDocumentCreated([MarshalAs(UnmanagedType.LPWStr)] string id);
        void ExecuteScript([MarshalAs(UnmanagedType.LPWStr)] string script, nint handler);
        void CapturePreview(int format, nint stream, nint handler);
        void Reload();
        void PostWebMessageAsJson([MarshalAs(UnmanagedType.LPWStr)] string json);
        void PostWebMessageAsString([MarshalAs(UnmanagedType.LPWStr)] string message);
        void AddWebMessageReceived(nint handler, out long token);
        void RemoveWebMessageReceived(long token);
        void CallDevToolsProtocolMethod([MarshalAs(UnmanagedType.LPWStr)] string method,
            [MarshalAs(UnmanagedType.LPWStr)] string parameters, ICompletedHandler handler);
    }

    // Published ICoreWebView2Settings4 ABI. The RCW owns its references; no
    // Avalonia pointer is manually released and no SDK internals are reflected.
    [ComImport]
    [Guid("CB56846C-4168-4D53-B04F-03B6D6796FF2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2Settings4
    {
        void GetScriptEnabled(out int value);
        void SetScriptEnabled(int value);
        void GetWebMessageEnabled(out int value);
        void SetWebMessageEnabled(int value);
        void GetDefaultScriptDialogsEnabled(out int value);
        void SetDefaultScriptDialogsEnabled(int value);
        void GetStatusBarEnabled(out int value);
        void SetStatusBarEnabled(int value);
        void GetDevToolsEnabled(out int value);
        void SetDevToolsEnabled(int value);
        void GetDefaultContextMenusEnabled(out int value);
        void SetDefaultContextMenusEnabled(int value);
        void GetHostObjectsAllowed(out int value);
        void SetHostObjectsAllowed(int value);
        void GetZoomControlEnabled(out int value);
        void SetZoomControlEnabled(int value);
        void GetBuiltInErrorPageEnabled(out int value);
        void SetBuiltInErrorPageEnabled(int value);
        void GetUserAgent([MarshalAs(UnmanagedType.LPWStr)] out string value);
        void SetUserAgent([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetBrowserAcceleratorKeysEnabled(out int value);
        void SetBrowserAcceleratorKeysEnabled(int value);
        void GetPasswordAutosaveEnabled(out int value);
        void SetPasswordAutosaveEnabled(int value);
        void GetGeneralAutofillEnabled(out int value);
        void SetGeneralAutofillEnabled(int value);
    }
}
