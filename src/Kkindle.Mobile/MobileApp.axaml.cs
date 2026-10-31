using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Kkindle.Mobile;

public partial class MobileApp : Application
{
    /// <summary>
    /// Platform adapters published by the head before Avalonia creates the
    /// application. <c>AvaloniaAndroidApplication&lt;TApp&gt;</c> requires a
    /// parameterless constructor, so the head cannot pass them through one.
    /// </summary>
    public static MobileServices? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = Services
            ?? throw new InvalidOperationException(
                "MobileApp.Services must be set by the platform head before Avalonia starts.");
        var modules = MobileComposition.Create(services);
        var preferences = new MobilePreferencesStore(services.Paths);
        preferences.Load();
        var shell = new MobileShell(modules, services, preferences);

        if (this.TryGetFeature<IActivatableLifetime>() is { } activation)
        {
            activation.Activated += (_, _) => shell.OnAppResumed();
            activation.Deactivated += (_, _) => shell.OnAppPaused();
        }

        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = shell;
                break;
            // Desktop preview harness: show the phone-sized shell in a window so
            // layout and navigation can be iterated without an Android device.
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "Kkindle · Mobile Preview",
                    Width = 411,
                    Height = 880,
                    Content = shell
                };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
