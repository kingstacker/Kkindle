using Kkindle.ApplicationServices;
using Kkindle.Infrastructure;
using Kkindle.Mobile;

namespace Kkindle.iOSHead;

/// <summary>Chooses the iOS implementations of the Core platform contracts.</summary>
internal static class IOSMobileHost
{
    public static MobileServices Create(IMobileTabBar? tabBar = null)
    {
        // Library is backed up by iCloud and invisible to the Files app; the
        // sandbox keeps it private to Kkindle.
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kkindle");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();

        return new MobileServices(
            paths,
            new IOSSecretProtector(Path.Combine(root, "keys")),
            new BookMetadataService(),
            // Calibre is an external process; conversion is unavailable on iOS
            // until replaced by an in-process converter.
            new UnavailableBookFormatConverter(),
            new IOSFileImportSource(),
            TabBar: tabBar);
    }
}
