using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class App : Application
{
    private readonly AppServices? _services;
    private UiLanguageService? _uiLanguageService;

    /// <summary>
    /// Parameterless constructor for the XAML previewer and designer, which
    /// instantiate the application without a composition root.
    /// </summary>
    public App()
    {
    }

    /// <summary>
    /// Used by the platform head projects, which build the platform-specific
    /// services and hand them over before the framework starts.
    /// </summary>
    public App(AppServices services)
    {
        _services = services;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _uiLanguageService = new UiLanguageService(this);
    }

    public void ApplyLanguage(string? language) => _uiLanguageService?.Apply(language);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var applicationDirectory = AppContext.BaseDirectory;
            var paths = _services?.Paths
                ?? new AppPaths(AppRootConfiguration.ResolveRoot(applicationDirectory));
            var startupSettings = new AppSettingsStore(paths).LoadSynchronously();
            ApplyLanguage(startupSettings.UiLanguage);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            var window = new MainWindow(
                paths,
                library,
                services: _services,
                startupSettings: startupSettings);
            desktop.MainWindow = window;
            // Prepare TTS in the background while the library opens. If the
            // user enters a reader before setup finishes, the reader awaits
            // this same task instead of starting a second installer.
            _ = window.InitializeTtsEnvironmentAsync();
            _ = window.InitializeLibraryAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Everything the UI needs that only a platform head can build. Kept as a
/// record so adding a service does not ripple through every head.
/// </summary>
/// <param name="SecretProtector">Machine-bound encryption for stored secrets.</param>
/// <param name="CreateDeviceChangeNotifier">
/// Builds a removable-storage watcher for a native window handle. Windows needs
/// the handle to subclass the window procedure; other platforms may ignore it.
/// Returns null when the platform has no notifier, in which case callers fall
/// back to polling.
/// </param>
/// <param name="ReaderHostFactory">
/// Creates a reader webview host. A platform head may replace the default
/// Avalonia NativeWebView implementation when it needs a different engine.
/// </param>
/// <param name="TtsEngine">Optional platform-independent TTS engine.</param>
/// <param name="TtsAudioPlayer">Optional platform audio output.</param>
/// <param name="TtsEnvironmentSetup">
/// Optional platform-specific dependency bootstrapper. It may install the
/// engine and audio prerequisites before the first utterance.
/// </param>
public sealed record AppServices(
    ISecretProtector SecretProtector,
    Func<IntPtr, IDeviceChangeNotifier?> CreateDeviceChangeNotifier,
    IKindleDeviceService? KindleDeviceService = null,
    Func<IReaderHost>? ReaderHostFactory = null,
    AppPaths? Paths = null,
    string? RootConfigurationDirectory = null,
    IAppUpdateInstaller? UpdateInstaller = null,
    ITtsEngine? TtsEngine = null,
    ITtsAudioPlayer? TtsAudioPlayer = null,
    ITtsEnvironmentSetup? TtsEnvironmentSetup = null);
