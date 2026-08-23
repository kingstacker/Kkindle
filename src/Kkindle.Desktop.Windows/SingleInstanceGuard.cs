using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kkindle.Desktop.Windows;

// Guarantees a single Kkindle instance per user session. A second launch
// focuses the existing window — restoring it even when parked in the tray —
// and exits before any Avalonia setup runs.
internal static class SingleInstanceGuard
{
    private const string MutexName = @"Local\Kkindle-SingleInstance";
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    public static bool TryAcquire(out Mutex mutex)
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    public static void ActivateExistingInstance()
    {
        var currentPid = Environment.ProcessId;
        var processName = Process.GetCurrentProcess().ProcessName;
        var candidates = new List<IntPtr>();
        var visibleCandidates = new List<IntPtr>();

        EnumWindows((hWnd, _) =>
        {
            if (GetWindowThreadProcessId(hWnd, out var pid) != 0
                && pid != currentPid
                && IsOwnedByKkindle(pid, processName))
            {
                candidates.Add(hWnd);
                if (IsWindowVisible(hWnd)) visibleCandidates.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);

        // Prefer a visible window; a hidden one means the app is parked in the
        // tray and still needs to come back.
        var target = visibleCandidates.FirstOrDefault() != IntPtr.Zero
            ? visibleCandidates[0]
            : candidates.FirstOrDefault();
        if (target == IntPtr.Zero) return;

        ShowWindow(target, IsIconic(target) ? SW_RESTORE : SW_SHOW);
        SetForegroundWindow(target);
    }

    private static bool IsOwnedByKkindle(uint pid, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(unchecked((int)pid));
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
