using Kkindle.Platform.Windows;

namespace Kkindle.Tests.Windows;

public sealed class WindowsAppUpdateInstallerTests
{
    [Fact]
    public void UpdateShowsProgressAndErrorsAndTargetsTheRunningInstallation()
    {
        var package = Path.Combine(Path.GetTempPath(), "更新 包", "setup.exe");
        var destination = Path.Combine(Path.GetTempPath(), "Kkindle custom");
        var info = WindowsAppUpdateInstaller.CreateInstallerStartInfo(package, destination);

        Assert.True(info.UseShellExecute);
        Assert.Equal(package, info.FileName);
        Assert.Contains("/SILENT", info.ArgumentList);
        Assert.DoesNotContain("/VERYSILENT", info.ArgumentList);
        Assert.DoesNotContain("/SUPPRESSMSGBOXES", info.ArgumentList);
        Assert.Contains($"/DIR={destination}", info.ArgumentList);
        Assert.Contains($"/LOG={package}.install.log", info.ArgumentList);
        Assert.Equal(Path.GetDirectoryName(package), info.WorkingDirectory);
    }

    [Fact]
    public void MissingPackageFailsBeforeLaunchingAnything()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new WindowsAppUpdateInstaller().LaunchInstaller(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }
}
