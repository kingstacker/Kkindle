namespace Kkindle.Core;

/// <summary>
/// Encrypts small secrets (API keys and SMTP passwords) with a
/// key that belongs to the current OS user, so a copied settings file is
/// useless on another machine or account.
///
/// Protected values are inherently machine-bound: backups never carry them
/// (see <c>AppBackupService</c>), and a failed <see cref="Unprotect"/> is
/// treated as "no stored secret" rather than an error.
///
/// Windows implementation uses DPAPI. A Linux implementation should use
/// libsecret / Secret Service, macOS should use the Keychain.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="value"/>. Empty input returns empty.</summary>
    byte[] Protect(byte[] value);

    /// <summary>
    /// Decrypts a blob produced by <see cref="Protect"/> on this machine and
    /// user account. Throws when the blob was produced elsewhere.
    /// </summary>
    byte[] Unprotect(byte[] value);
}

/// <summary>
/// Raises an event whenever removable storage is attached or detached, so the
/// Kindle device list can refresh without polling.
///
/// This only signals "something changed" — enumeration stays in
/// <see cref="IKindleDeviceService.DetectDevicesAsync"/>. Callers must keep a
/// polling fallback: the notification is best-effort and platform specific.
///
/// Windows implementation subclasses the window procedure and listens for
/// WM_DEVICECHANGE. A Linux implementation should use udev, macOS IOKit.
/// </summary>
public interface IDeviceChangeNotifier : IDisposable
{
    event EventHandler? DeviceChanged;
}

/// <summary>
/// Portable boundary around the embedded web reader. The UI only depends on
/// navigation, script execution and web messages; the concrete control stays
/// in the Avalonia application layer so a different native webview can be
/// supplied by another platform head later.
/// </summary>
public interface IReaderHost : IDisposable
{
    /// <summary>
    /// The native UI control to place in an Avalonia visual tree. It is kept as
    /// <see cref="object"/> here so Core remains free of UI-framework types.
    /// </summary>
    object View { get; }

    Uri? Source { get; }

    /// <summary>Completes after the native webview adapter is attached.</summary>
    Task ReadyTask { get; }

    event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
    event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
    event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    void Navigate(Uri uri);

    Task<string?> InvokeScriptAsync(string script);

    void Stop();
}

public interface IReaderHtmlHost
{
    void NavigateToString(string html, Uri baseUri);
}

/// <summary>
/// Optional capability for reader hosts that can capture the currently
/// visible browser viewport. Page-transition effects use this when available
/// and fall back to a regular fade when the platform cannot provide it.
/// </summary>
public interface IReaderPageSnapshotProvider
{
    Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken);
}

public sealed class ReaderNavigationStartingEventArgs(Uri? request) : EventArgs
{
    public Uri? Request { get; } = request;

    public bool Cancel { get; set; }
}

public sealed class ReaderNavigationCompletedEventArgs(Uri? request, bool isSuccess) : EventArgs
{
    public Uri? Request { get; } = request;

    public bool IsSuccess { get; } = isSuccess;
}

public sealed class ReaderWebMessageReceivedEventArgs(string? body) : EventArgs
{
    public string? Body { get; } = body;
}
