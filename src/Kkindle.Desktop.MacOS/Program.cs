using Avalonia;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Kkindle.Platform.MacOS;

namespace Kkindle.Desktop.MacOS;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This Kkindle head is for macOS.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new Kkindle.App(BuildServices()))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var configurationDirectory = MacOSAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, MacOSAppData.ResolveRoot()));
        var metadata = new BookMetadataService();
        var massStorage = new MassStorageKindleDeviceService(
            paths,
            metadata,
            eject: MacOSKindleEjector.EjectAsync);
        // Newer Kindles only expose MTP, which macOS cannot mount; libmtp
        // provides the same access path calibre uses on this platform.
        var mtp = new MtpKindleDeviceService(paths, metadata);
        return new AppServices(
            SecretProtector: new MacOSSecretProtector(),
            CreateDeviceChangeNotifier: _ => null,
            KindleDeviceService: new MacOSKindleDeviceService(massStorage, mtp),
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }
}
