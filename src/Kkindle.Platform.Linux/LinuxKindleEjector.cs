using System.Diagnostics;
using Kkindle.Core;

namespace Kkindle.Platform.Linux;

public static class LinuxKindleEjector
{
    public static async Task EjectAsync(KindleDevice device, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var source = await RunAsync(
            "findmnt",
            ["--noheadings", "--output", "SOURCE", "--target", device.RootPath],
            cancellationToken,
            allowFailure: true);
        if (source.ExitCode == 0 && !string.IsNullOrWhiteSpace(source.Output))
        {
            var unmount = await RunAsync(
                "udisksctl",
                ["unmount", "--block-device", source.Output.Trim()],
                cancellationToken,
                allowFailure: true);
            if (unmount.ExitCode == 0) return;
        }

        var fallback = await RunAsync("umount", [device.RootPath], cancellationToken, allowFailure: true);
        if (fallback.ExitCode != 0)
            throw new IOException($"无法安全卸载设备：{fallback.Error}");
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {executable}。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var result = new CommandResult(process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
        if (!allowFailure && result.ExitCode != 0)
            throw new IOException($"{executable} 执行失败：{result.Error}");
        return result;
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
