using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
        FontManager.Current.AddFontCollection(new BundledAppFontCollection());
        AvaloniaXamlLoader.Load(this);
        ApplyTheme(AppTheme.Classic);
        ReaderAppearanceResources.Populate(Resources, new ReaderAppearanceSettings());
        _uiLanguageService = new UiLanguageService(this);
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var modules = DesktopApplicationBootstrap.CreateForStartup(_services);
            var startupSettings = modules.StartupSettings;
            ApplyLanguage(startupSettings.UiLanguage);
            ApplyTheme(startupSettings.MainTheme);
            var window = new MainWindow(modules, _services);
            desktop.MainWindow = window;
            // Prepare TTS in the background while the library opens. If the
            // user enters a reader before setup finishes, the reader awaits
            // this same task instead of starting a second installer.
            _ = window.InitializeTtsEnvironmentAsync();
            _ = window.InitializeLibraryAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Dispatcher_UnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var payload = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] UnhandledUiException{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}";
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(AppContext.BaseDirectory, "kkindle-crash.log")
            };
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
