using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class App : Application
{
    private readonly AppServices? _services;
    private UiLanguageService? _uiLanguageService;
    private MainWindow? _mainWindow;
    private IActivatableLifetime? _activatableLifetime;

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
        FontManager.Current.AddFontCollection(new BundledAppFontCollection());
        AvaloniaXamlLoader.Load(this);
        ApplyTheme(AppTheme.Classic);
        ReaderAppearanceResources.Populate(Resources, new ReaderAppearanceSettings());
        _uiLanguageService = new UiLanguageService(this);
        // macOS can deliver Finder "Open With" files as an application
        // activation before the main window exists. Subscribe as early as the
        // platform lifetime is available so those files are queued, not lost.
        InitializeActivatableLifetime();
    }

    public void ApplyLanguage(string? language) => _uiLanguageService?.Apply(language);

    public void ApplyTheme(AppTheme theme)
    {
        AppAppearanceResources.Populate(Resources, AppPalette.For(theme));
        RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += Dispatcher_UnhandledException;
        InitializeActivatableLifetime();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var modules = DesktopApplicationBootstrap.CreateForStartup(_services);
            var startupSettings = modules.StartupSettings;
            ApplyLanguage(startupSettings.UiLanguage);
            ApplyTheme(startupSettings.MainTheme);
            var window = new MainWindow(modules, _services);
            desktop.MainWindow = window;
            _mainWindow = window;
            window.InitializeMacMenu();
            // Prepare TTS in the background while the library opens. If the
            // user enters a reader before setup finishes, the reader awaits
            // this same task instead of starting a second installer.
            _ = window.InitializeTtsEnvironmentAsync();
            var libraryInitialization = window.InitializeLibraryAsync();

            // A document opened when the app was not yet running arrives in
            // the startup arguments (command line) or through activation
            // (Finder). Both paths feed the same import queue.
            var startupFiles = (desktop.Args ?? [])
                .Where(argument => !argument.StartsWith('-') && File.Exists(argument));
            var files = startupFiles
                .Concat(ExternalFileOpenQueue.Drain())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length > 0)
                _ = window.OpenExternalFilesAsync(files, libraryInitialization);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeActivatableLifetime()
    {
        if (_activatableLifetime is not null) return;
        if (this.TryGetFeature<IActivatableLifetime>() is not { } lifetime) return;
        _activatableLifetime = lifetime;
        lifetime.Activated += ApplicationActivated;
    }

    private void ApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        // Clicking the Dock icon while the window is hidden should bring the
        // menu-bar-resident app back, matching every other Mac application.
        if (e.Kind == ActivationKind.Reopen && _mainWindow is { } reopened)
        {
            if (!reopened.IsVisible) reopened.Show();
            if (reopened.WindowState == WindowState.Minimized)
                reopened.WindowState = WindowState.Normal;
            reopened.Activate();
            return;
        }

        if (e is not FileActivatedEventArgs { Files.Count: > 0 } fileActivation) return;
        var paths = fileActivation.Files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0) return;

        if (_mainWindow is { } window)
            _ = window.OpenExternalFilesAsync(paths);
        else
            ExternalFileOpenQueue.Enqueue(paths);
    }

    internal static bool IsInsideMacAppBundle(string directory) =>
        OperatingSystem.IsMacOS()
        && directory.Replace('\\', '/').Contains(".app/Contents/", StringComparison.Ordinal);

    private void Dispatcher_UnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var payload = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] UnhandledUiException{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}";
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Writing into Kkindle.app/Contents/MacOS invalidates the bundle's
            // code signature (a sealed directory gains an unsealed file), so
            // macOS bundles only log to the data directory.
            if (!IsInsideMacAppBundle(AppContext.BaseDirectory))
                targets.Add(Path.Combine(AppContext.BaseDirectory, "kkindle-crash.log"));
            var paths = _services?.Paths
                ?? new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
            targets.Add(Path.Combine(paths.Logs, "crash.log"));
            foreach (var target in targets)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.AppendAllText(target, payload, new System.Text.UTF8Encoding(false));
                }
                catch
                {
                    // A crash log must not cause a second dispatcher failure.
                }
            }
        }
        catch
        {
            // Keep the dispatcher handler itself exception-safe.
        }

        // Event handlers that perform I/O and reader navigation already
        // report recoverable failures locally. This last-resort handler keeps
        // an escaped UI exception from terminating the entire application.
        e.Handled = true;
    }
}
