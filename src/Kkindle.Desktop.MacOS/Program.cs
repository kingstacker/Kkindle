using System.Diagnostics;
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

        // Mirror the Windows head: any unhandled exception is written next to
        // the executable and in the data logs directory before the process
        // dies, so startup failures on a user's Mac are diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // A second launch only asks the running instance to come forward.
        // The DEBUG validation processes stay isolated so they can run next to
        // the user's copy, matching the Windows head.
#if DEBUG
        var isolateValidation = string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_AI_PREVIEW"),
                "1",
                StringComparison.Ordinal)
            || string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE"),
                "1",
                StringComparison.Ordinal)
            || string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_ANIMATION_PROBE"),
                "1",
                StringComparison.Ordinal);
#else
        const bool isolateValidation = false;
#endif
        FileStream? instanceLock = null;
        MacOSInstanceActivationServer? activationServer = null;
        if (!isolateValidation)
        {
            if (!MacOSSingleInstanceGuard.TryAcquire(out instanceLock))
            {
                if (!MacOSInstanceActivationServer.RequestActivate())
                    ActivateWithLaunchServices();
                instanceLock?.Dispose();
                return;
            }
            activationServer = MacOSInstanceActivationServer.TryStart();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            activationServer?.Dispose();
            instanceLock?.Dispose();
        }
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
        // Read-aloud uses the same Microsoft Edge TTS service as the Windows
        // and Linux heads. The Python dependency is installed into Kkindle's
        // own data directory on first use; afplay ships with macOS.
        var ttsEngine = new EdgeTtsEngine(
            executablePath: TtsRuntimePaths.PreferredEdgeTtsPath(paths));
        var ttsPlayer = new MacOSTtsAudioPlayer();
        return new AppServices(
            SecretProtector: new MacOSSecretProtector(),
            // IOKit USB notifications refresh the device list on plug/unplug.
            CreateDeviceChangeNotifier: _ => new MacOSDeviceChangeNotifier(),
            KindleDeviceService: new MacOSKindleDeviceService(massStorage, mtp),
            UpdateInstaller: new MacOSAppUpdateInstaller(),
            TtsEngine: ttsEngine,
            TtsAudioPlayer: ttsPlayer,
            TtsEnvironmentSetup: new TtsEnvironmentSetupService(
                paths,
                ttsEngine,
                ttsPlayer),
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }

    private static void ActivateWithLaunchServices()
    {
        if (MacOSAppUpdateInstaller.BundlePath is not { } bundle) return;
        try
        {
            var info = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-a");
            info.ArgumentList.Add(bundle);
            using var process = Process.Start(info);
        }
        catch
        {
            // Activation is best effort.
        }
    }

    private static void WriteCrashLog(string kind, Exception? exception)
    {
        var payload = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        var targets = new List<string>();
        try
        {
            targets.Add(Path.Combine(AppContext.BaseDirectory, "kkindle-crash.log"));
        }
        catch
        {
        }
        try
        {
            var paths = new AppPaths(AppRootConfiguration.ResolveRoot(
                MacOSAppData.ResolveConfigurationDirectory(),
                MacOSAppData.ResolveRoot()));
            targets.Add(Path.Combine(paths.Logs, "crash.log"));
        }
        catch
        {
        }

        foreach (var target in targets.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(target, payload, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // Logging must never mask the original failure.
            }
        }
    }
}
