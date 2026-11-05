using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Kkindle.Platform.MacOS;

namespace Kkindle.Desktop.MacOS;

/// <summary>
/// Guarantees one Kkindle instance per user. A second launch connects to the
/// running instance over a Unix domain socket and asks it to show its main
/// window instead of starting another copy. The lock file serializes startup
/// so two simultaneous launches cannot both become the primary instance.
/// </summary>
internal static class MacOSSingleInstanceGuard
{
    private const string LockFileName = "kkindle.instance.lock";

    public static bool TryAcquire(out FileStream? lockFile)
    {
        lockFile = null;
        try
        {
            var directory = MacOSAppData.ResolveRoot();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, LockFileName);
            // FileShare.None maps to an exclusive flock on Unix, so the handle
            // is released automatically when the process exits.
            lockFile = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Primary-instance activation endpoint. The second instance only sends one
/// message; showing the window happens on the Avalonia UI thread of the
/// primary instance, which also restores a window parked in the menu bar.
/// </summary>
internal sealed class MacOSInstanceActivationServer : IDisposable
{
    private const string ActivateMessage = "activate";
    private const int MaximumPayloadBytes = 256 * 1024;

    // Set by Program once Avalonia has initialized the UI thread. Touching
    // Dispatcher.UIThread earlier from a thread-pool thread makes that thread
    // the dispatcher owner, and Avalonia's own setup on the main thread then
    // fails its thread-affinity check and aborts the first instance.
    private static readonly TaskCompletionSource UiReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void SignalUiReady() => UiReady.TrySetResult();

    private readonly Socket _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    private MacOSInstanceActivationServer(Socket listener)
    {
        _listener = listener;
        _acceptLoop = AcceptLoopAsync();
    }

    public static string SocketPath =>
        Path.Combine(MacOSAppData.ResolveRoot(), "kkindle.instance.sock");

    public static MacOSInstanceActivationServer? TryStart()
    {
        var path = SocketPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // The lock file is held, so a leftover socket can only come from
            // an instance that exited without cleaning up.
            if (File.Exists(path)) File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(backlog: 4);
            return new MacOSInstanceActivationServer(listener);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the already-running instance to come to the front, optionally
    /// passing files for import. Returns false when the socket is unavailable,
    /// so the caller can fall back to LaunchServices activation.
    /// </summary>
    public static bool RequestActivate(IReadOnlyList<string>? files = null)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            var payload = new StringBuilder(ActivateMessage);
            foreach (var file in files ?? [])
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                // Base64 keeps newline-containing paths from breaking the
                // simple line protocol.
                payload.Append('\n').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(file)));
            }
            var bytes = Encoding.UTF8.GetBytes(payload.ToString());
            var sent = 0;
            while (sent < bytes.Length)
                sent += socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
            socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A client that disconnects during accept must not stop the
                // listener; retry until the server is disposed.
                try
                {
                    await Task.Delay(100, _cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        try
        {
            using (client)
            {
                using var payload = new MemoryStream();
                var buffer = new byte[8192];
                while (payload.Length < MaximumPayloadBytes)
                {
                    var read = await client.ReceiveAsync(buffer, SocketFlags.None, _cancellation.Token)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    payload.Write(buffer, 0, read);
                }

                var lines = Encoding.UTF8.GetString(payload.ToArray())
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 0 || lines[0] != ActivateMessage) return;

                var files = new List<string>();
                foreach (var line in lines.Skip(1))
                {
                    try
                    {
                        var path = Encoding.UTF8.GetString(Convert.FromBase64String(line));
                        if (!string.IsNullOrWhiteSpace(path)) files.Add(path);
                    }
                    catch (FormatException)
                    {
                        // Ignore malformed path entries instead of dropping the
                        // whole activation request.
                    }
                }

                await ShowMainWindowAsync(files).ConfigureAwait(false);
            }
        }
        catch
        {
            // Activation is best effort and must never take the app down.
        }
    }

    private async Task ShowMainWindowAsync(IReadOnlyList<string> files)
    {
        try
        {
            await UiReady.Task.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The second launch can arrive before the main window exists.
        for (var attempt = 0; attempt < 40 && !_cancellation.IsCancellationRequested; attempt++)
        {
            try
            {
                var shown = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Application.Current?.ApplicationLifetime
                        is not IClassicDesktopStyleApplicationLifetime desktop) return false;
                    if (desktop.MainWindow is not { } window) return false;
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                    if (files.Count > 0 && window is MainWindow mainWindow)
                        _ = mainWindow.OpenExternalFilesAsync(files);
                    return true;
                });
                if (shown) return;
            }
            catch
            {
                // Avalonia may not be initialized yet.
            }

            try
            {
                await Task.Delay(250, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        try { _listener.Dispose(); } catch { }
        try { _acceptLoop.GetAwaiter().GetResult(); } catch { }
        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
        }
        catch
        {
        }
        _cancellation.Dispose();
    }
}
