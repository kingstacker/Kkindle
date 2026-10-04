using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task SettingsCategoriesGroupGlobalOptionsAndIntegrations() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.True(scope.Get<Control>("SettingsGeneralSection").IsEffectivelyVisible);
        foreach (var (control, category) in new[]
        {
            ("UiLanguageBox", "General"), ("MainThemeBox", "General"),
            ("NetworkEnabledCheck", "General"), ("TranslationGoogleProxyPane", "General"),
            ("SettingsAccountExpander", "Library"), ("SettingsCalibreExpander", "Library"),
            ("SettingsMcpExpander", "Advanced"), ("SettingsDiagnosticsExpander", "Advanced"),
            ("ExportSoftwareLogsButton", "Advanced"), ("SettingsSendToKindleExpander", "Kindle")
        })
            Assert.Contains(scope.Get<Control>(control).GetVisualAncestors(),
                ancestor => ReferenceEquals(ancestor, scope.Get<Control>($"Settings{category}Section")));

        var data = scope.Get<StackPanel>("SettingsDataSection");
        Assert.Same(scope.Get<Expander>("SettingsReadingDataExpander"), data.Children.Last());
        scope.Call("ZLibraryAccountButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.True(scope.Get<Control>("SettingsLibrarySection").IsEffectivelyVisible);
        Assert.True(scope.Get<Expander>("SettingsAccountExpander").IsExpanded);
    });
}
