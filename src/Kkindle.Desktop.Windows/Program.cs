using Avalonia;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Windows;
using System.Runtime.InteropServices;

namespace Kkindle.Desktop.Windows;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Inno Setup invokes this hidden command before removing the install
        // directory. It handles relocated data and temporary update files that
        // the installer cannot describe with a fixed [UninstallDelete] path.
        if (args.Any(argument => string.Equals(
                argument,
                "/cleanup-uninstall",
                StringComparison.OrdinalIgnoreCase)))
        {
            AppDataCleanup.RemoveForUninstall(AppContext.BaseDirectory);
            return;
        }

        // Any unhandled exception is written to a crash log next to the exe
        // (and in the data logs directory) before the process dies, so remote
        // startup failures can be diagnosed from the user machine.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Single instance: a second launch brings the running window back
        // (even from the tray) instead of starting another copy of the app.
        // The DEBUG validation process is deliberately isolated so it can
        // exercise resize/maximize regressions while the user's app stays open.
#if DEBUG
        var isolateValidation = string.Equals(
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
        Mutex? singleInstanceMutex = null;
        if (!isolateValidation)
        {
            if (!SingleInstanceGuard.TryAcquire(out var acquiredMutex))
            {
                SingleInstanceGuard.ActivateExistingInstance();
                acquiredMutex.Dispose();
                return;
            }
            singleInstanceMutex = acquiredMutex;
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstanceMutex?.Dispose();
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
            var paths = new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
            targets.Add(Path.Combine(paths.Logs, "crash.log"));
        }
        catch
        {
        }

        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
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

    // Also used by the Avalonia visual designer, which calls it without ever
    // reaching Main — so the App must stay constructible from here alone.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new Kkindle.App(BuildServices()))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        var ttsEngine = new EdgeTtsEngine(
            executablePath: TtsRuntimePaths.PreferredEdgeTtsPath(paths));
        var ttsPlayer = new WindowsTtsAudioPlayer();
        return new AppServices(
            SecretProtector: new WindowsSecretProtector(),
            CreateDeviceChangeNotifier: handle => new WindowsDeviceChangeNotifier(handle),
            KindleDeviceService: new KindleDeviceService(paths, new BookMetadataService()),
            ReaderHostFactory: () => new NativeWebViewReaderHost(ConfigureWebView2),
            UpdateInstaller: new WindowsAppUpdateInstaller(),
            TtsEngine: ttsEngine,
            TtsAudioPlayer: ttsPlayer,
            TtsEnvironmentSetup: new TtsEnvironmentSetupService(
                paths,
                ttsEngine,
                ttsPlayer));
    }

    private static void ConfigureWebView2(IntPtr coreWebView2Pointer)
    {
        var coreWebView = (ICoreWebView2)Marshal.GetTypedObjectForIUnknown(
            coreWebView2Pointer,
            typeof(ICoreWebView2));
        Marshal.ThrowExceptionForHR(coreWebView.GetSettings(out var settings));
        Marshal.ThrowExceptionForHR(settings.SetIsStatusBarEnabled(0));
    }

    [ComImport]
    [Guid("76ECEACB-0462-4D94-AC83-423A6793775E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2
    {
        [PreserveSig]
        int GetSettings(out ICoreWebView2Settings settings);
    }

    [ComImport]
    [Guid("E562E4F0-D7FA-43AC-8D71-C05150499F00")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2Settings
    {
        [PreserveSig]
        int GetIsScriptEnabled(out int enabled);

        [PreserveSig]
        int SetIsScriptEnabled(int enabled);

        [PreserveSig]
        int GetIsWebMessageEnabled(out int enabled);

        [PreserveSig]
        int SetIsWebMessageEnabled(int enabled);

        [PreserveSig]
        int GetAreDefaultScriptDialogsEnabled(out int enabled);

        [PreserveSig]
        int SetAreDefaultScriptDialogsEnabled(int enabled);

        [PreserveSig]
        int GetIsStatusBarEnabled(out int enabled);

        [PreserveSig]
        int SetIsStatusBarEnabled(int enabled);
    }
}
