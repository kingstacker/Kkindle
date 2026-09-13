using System.Diagnostics;
using Kkindle.Core;

namespace Kkindle.Platform.MacOS;

public static class MacOSKindleEjector
{
    public static async Task EjectAsync(KindleDevice device, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/sbin/diskutil",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("eject");
        startInfo.ArgumentList.Add(device.RootPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 diskutil。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new IOException($"无法安全弹出设备：{(await errorTask).Trim()}");
        await outputTask;
    }
}
