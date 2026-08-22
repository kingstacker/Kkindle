namespace Kkindle.Core;

public sealed record AppUpdateAsset(string Name, Uri DownloadUrl, long Size);

public sealed record AppUpdateInfo(
    string Version,
    string TagName,
    string ReleaseNotes,
    Uri ReleasePage,
    AppUpdateAsset Package,
    AppUpdateAsset Checksums);

public sealed record AppUpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
        : 0;
}

public interface IAppUpdateInstaller
{
    bool CanInstall { get; }
    string UnavailableReason { get; }
    string GetPackageAssetName(string version);
    void LaunchInstaller(string packagePath);
}
