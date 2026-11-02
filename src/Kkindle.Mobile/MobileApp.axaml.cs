using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Kkindle.Mobile;

public partial class MobileApp : Application
{
    static MobileApp()
    {
        // RenderTransform has no default keyframe animator in Avalonia 12; the
        // glass backdrop's drifting colour fields need one.
        Animation.RegisterCustomAnimator<ITransform, TransformAnimator>();
    }

    /// <summary>
    /// Platform adapters published by the head before Avalonia creates the
    /// application. <c>AvaloniaAndroidApplication&lt;TApp&gt;</c> requires a
    /// parameterless constructor, so the head cannot pass them through one.
    /// </summary>
    public static MobileServices? Services { get; set; }

    private ResourceDictionary? _glassTokens;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsIOS())
            UseLiquidGlass();
    }

    /// <summary>
    /// iOS 26 Liquid Glass look: translucent glass tokens over an ambient
    /// gradient, plus floating, edge-lit chrome.
    /// </summary>
    private void UseLiquidGlass()
    {
        _glassTokens = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Kkindle.Mobile/Styles/MobileLiquidGlass.axaml"), null);
        ApplyGlassTokens();
        ActualThemeVariantChanged += (_, _) => ApplyGlassTokens();

        Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://Kkindle.Mobile/Styles/MobileLiquidGlassTheme.axaml"), null));
    }

    /// <summary>
    /// Copies the glass values into the application's own resources. Values
    /// defined directly on the dictionary take precedence over the merged flat
    /// tokens, which is the only reliable override at this level.
    /// </summary>
    private void ApplyGlassTokens()
    {
        if (_glassTokens is null) return;
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (!_glassTokens.ThemeDictionaries.TryGetValue(variant, out var provider)
            || provider is not ResourceDictionary values)
        {
            return;
        }

        if (Resources is not ResourceDictionary resources) return;
        foreach (var (key, value) in values)
            resources[key] = value;
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
