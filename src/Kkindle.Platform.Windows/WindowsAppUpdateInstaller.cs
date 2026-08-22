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

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = true
        });
        if (process is null) throw new InvalidOperationException("无法启动 Kkindle 安装程序。");
    }
}
