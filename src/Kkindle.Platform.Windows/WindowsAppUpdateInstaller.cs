using System.Diagnostics;
using Kkindle.Core;

namespace Kkindle.Platform.Windows;

public sealed class WindowsAppUpdateInstaller : IAppUpdateInstaller
{
    private string UninstallerPath => Path.Combine(AppContext.BaseDirectory, "unins000.exe");

    public bool CanInstall => File.Exists(UninstallerPath);

    public string UnavailableReason =>
        "当前不是通过 Kkindle Windows 安装程序运行，暂不支持应用内安装。";

    public string GetPackageAssetName(string version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("版本号不能用于生成安装包文件名。", nameof(version));
        return $"Kkindle-{version}-win-x64-setup.exe";
    }

    public void LaunchInstaller(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath)
            || !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("找不到已下载的 Kkindle 安装程序。", fullPath);
        if (!CanInstall) throw new InvalidOperationException(UnavailableReason);

        using var process = Process.Start(CreateInstallerStartInfo(fullPath, AppContext.BaseDirectory));
        if (process is null) throw new InvalidOperationException("无法启动 Kkindle 安装程序。");
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string packagePath, string installDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = packagePath,
            WorkingDirectory = Path.GetDirectoryName(packagePath)!,
            UseShellExecute = true
        };
        // SILENT keeps the progress window and installation errors visible.
        // Pin the destination to this installation, even when multiple copies exist.
        foreach (var argument in new[] { "/SILENT", "/NORESTART", "/CLOSEAPPLICATIONS",
                     $"/DIR={Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar)}",
                     $"/LOG={packagePath}.install.log" })
            info.ArgumentList.Add(argument);
        return info;
    }
}
