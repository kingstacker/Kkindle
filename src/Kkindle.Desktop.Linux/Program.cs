using Avalonia;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Kkindle.Platform.Linux;

namespace Kkindle.Desktop.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This Kkindle head is for Linux.");
        ConfigureLinuxWebViewRendering();
        var configurationDirectory = LinuxAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, LinuxAppData.ResolveRoot()));
        BuildAvaloniaApp(BuildServices(paths, configurationDirectory)).StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigureLinuxWebViewRendering()
    {
        SetDefaultEnvironment("GDK_BACKEND", "x11");
        // Keep Linux on Avalonia NativeWebView's WebKit path. The shared-memory
        // frame transport is still WebKit rendering; it avoids depending on
        // DMA-BUF import support in the host compositor while keeping the DOM,
        // CSS layout, font shaping and pagination inside WebKit.
        SetDefaultEnvironment("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
    }

    private static void SetDefaultEnvironment(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    public static AppBuilder BuildAvaloniaApp(AppServices? services = null) =>
        AppBuilder.Configure(() => new Kkindle.App(services ?? BuildServices()))
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "kkindle" })
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var configurationDirectory = LinuxAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, LinuxAppData.ResolveRoot()));
        return BuildServices(paths, configurationDirectory);
    }

    private static AppServices BuildServices(AppPaths paths, string configurationDirectory)
    {
        return new AppServices(
            SecretProtector: new LinuxSecretProtector(),
            CreateDeviceChangeNotifier: _ => null,
            KindleDeviceService: new MassStorageKindleDeviceService(
                paths,
                new BookMetadataService(),
                eject: LinuxKindleEjector.EjectAsync),
            ReaderHostFactory: () => new NativeWebViewReaderHost(),
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }
}
