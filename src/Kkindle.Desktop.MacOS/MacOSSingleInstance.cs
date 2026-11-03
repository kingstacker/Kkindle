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
    /// Asks the already-running instance to come to the front. Returns false
    /// when the socket is unavailable, so the caller can fall back to
    /// LaunchServices activation.
    /// </summary>
    public static bool RequestActivate()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(ActivateMessage));
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
                var buffer = new byte[64];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, _cancellation.Token)
                    .ConfigureAwait(false);
                if (read > 0
                    && Encoding.UTF8.GetString(buffer, 0, read).Trim() == ActivateMessage)
                    await ShowMainWindowAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Activation is best effort and must never take the app down.
        }
    }

    private async Task ShowMainWindowAsync()
    {
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
