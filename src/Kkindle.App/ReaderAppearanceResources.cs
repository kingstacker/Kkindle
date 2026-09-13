using Avalonia.Controls;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

internal static class ReaderAppearanceResources
{
    public static void Populate(IResourceDictionary resources, ReaderAppearanceSettings appearance)
    {
        var palette = ReaderPalette.For(appearance.Theme);
        Set("Page", palette.Page);
        Set("Chrome", palette.Chrome);
        Set("Sidebar", palette.Sidebar);
        Set("Ink", palette.Ink);
        Set("Muted", palette.Muted);
        Set("Border", palette.Border);
        Set("Hover", palette.Hover);
        Set("Selected", palette.Selected);
        Set("Pressed", palette.Selected);
        Set("Accent", palette.Accent);
        Set("OnAccent", palette.OnAccent);
        Set("Input", palette.Page);
        resources["ReaderPageBrush"] = ReaderPaperTexture.CreateBrush(palette.Page, appearance);
        resources["ReaderChromeBrush"] = ReaderPaperTexture.CreateBrush(palette.Chrome, appearance, 0.65);
        resources["ReaderSidebarBrush"] = ReaderPaperTexture.CreateBrush(palette.Sidebar, appearance, 0.8);

        void Set(string name, Color color)
        {
            resources[$"Reader{name}Color"] = color;
            resources[$"Reader{name}Brush"] = new SolidColorBrush(color);
        }
    }

    // Fluent resources live inside the reader scope, never in the library.
    public static void PopulateControls(IResourceDictionary resources, ReaderAppearanceSettings appearance)
    {
        var palette = ReaderPalette.For(appearance.Theme);
        AppAppearanceResources.Populate(resources, AppPalette.FromReader(palette));
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
                     "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            resources[key] = palette.Accent;
        foreach (var key in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
                     "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                     "KkindleScrollBarThumbPointerOverBrush", "KkindleScrollBarThumbPressedBrush" })
            resources[key] = new SolidColorBrush(palette.Accent);
        foreach (var key in new[] { "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed",
                     "SliderTrackValueFillDisabled", "SliderThumbBackgroundDisabled", "KkindleScrollBarThumbBrush",
                     "KkindleScrollBarThumbDisabledBrush" })
            resources[key] = new SolidColorBrush(palette.Border);
        foreach (var key in new[] { "KkindleScrollBarBackgroundBrush", "KkindleScrollBarExpandedBackgroundBrush",
                     "KkindleScrollBarBorderBrush" })
            resources[key] = Brushes.Transparent;
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused",
                     "TextControlPlaceholderForegroundPointerOver" })
            resources[key] = new SolidColorBrush(palette.Muted);
    }
}
