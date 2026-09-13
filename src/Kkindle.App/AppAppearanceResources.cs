using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kkindle;

internal static class AppAppearanceResources
{
    // Code-built windows share these mutable brushes so existing progress
    // windows and status glyphs update with the rest of the application.
    public static IBrush GetBrush(string key) =>
        Application.Current?.Resources[key] as IBrush ?? Avalonia.Media.Brushes.Black;

    public static void Populate(IResourceDictionary resources, AppPalette palette)
    {
        Set("Paper", palette.Paper);
        Set("Sidebar", palette.Sidebar);
        Set("Panel", palette.Panel);
        Set("Ink", palette.Ink);
        Set("MutedInk", palette.Muted);
        Set("SubtleInk", palette.Subtle);
        Set("Hairline", palette.Hairline);
        Set("SidebarIndicator", palette.StrongBorder);
        Set("CardBorder", palette.StrongBorder);
        Set("SoftHover", palette.Hover);
        Set("Pressed", palette.Pressed);
        Set("Cover", palette.Cover);
        Set("Accent", palette.Accent);
        Set("AccentHover", palette.AccentHover);
        Set("AccentPressed", palette.AccentPressed);
        Set("OnAccent", palette.OnAccent);
        var dark = palette.Paper.R < 90;
        Set("Success", Color.Parse(dark ? "#9BC797" : "#2E754D"));
        Set("Warning", Color.Parse(dark ? "#E2C178" : "#8D6400"));
        Set("Danger", Color.Parse(dark ? "#EAA59B" : "#A7342F"));
        resources["CardHoverShadow"] = new BoxShadows(new BoxShadow
        {
            IsInset = true, Spread = 1, Color = palette.Ink
        });

        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
                     "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            resources[key] = palette.Accent;

        Brushes(palette.Paper,
            "MenuFlyoutPresenterBackground", "MenuFlyoutItemBackground", "ComboBoxItemBackground",
            "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
            "ComboBoxBackgroundUnfocused", "ComboBoxDropDownBackground",
            "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver");
        Brushes(palette.Ink,
            "MenuFlyoutPresenterBorderBrush", "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPressed",
            "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened",
            "MenuFlyoutItemKeyboardAcceleratorTextForeground", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed",
            "ComboBoxItemForeground", "ComboBoxItemForegroundPressed", "ComboBoxItemBorderBrushPressed",
            "ComboBoxItemBorderBrushPointerOver", "ComboBoxItemBorderBrushSelected", "ComboBoxItemBorderBrushSelectedPressed",
            "ComboBoxItemBorderBrushSelectedPointerOver", "ComboBoxBackgroundBorderBrushFocused",
            "ComboBoxBackgroundBorderBrushUnfocused", "ComboBoxForeground", "ComboBoxForegroundFocused",
            "ComboBoxForegroundFocusedPressed", "ComboBoxPlaceHolderForegroundFocusedPressed", "ComboBoxBorderBrush",
            "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxDropDownGlyphForeground",
            "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed", "ComboBoxDropDownBorderBrush",
            "KkindleScrollBarArrowBrush", "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver");
        Brushes(palette.Accent,
            "MenuFlyoutItemBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundSelectedPressed", "ComboBoxItemBackgroundSelectedPointerOver",
            "TextControlBorderBrushFocused", "KkindleScrollBarButtonPointerOverBrush", "KkindleScrollBarButtonPressedBrush",
            "KkindleScrollBarButtonPointerOverBorderBrush", "KkindleScrollBarButtonPressedBorderBrush",
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed");
        Brushes(palette.OnAccent,
            "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutSubItemChevronPointerOver",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "ComboBoxItemForegroundPointerOver",
            "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPressed", "ComboBoxItemForegroundSelectedPointerOver",
            "KkindleScrollBarArrowPointerOverBrush", "KkindleScrollBarArrowPressedBrush");
        Brushes(palette.Pressed, "MenuFlyoutItemBackgroundPressed", "ComboBoxItemBackgroundPressed");
        Brushes(palette.Hover,
            "MenuFlyoutItemBackgroundDisabled", "ComboBoxItemBackgroundDisabled", "ComboBoxItemBackgroundSelectedDisabled",
            "ComboBoxBackgroundDisabled", "TextControlBackgroundDisabled");
        Brushes(palette.Muted,
            "MenuFlyoutItemForegroundDisabled", "MenuFlyoutSubItemChevronDisabled", "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled",
            "ComboBoxItemForegroundDisabled", "ComboBoxItemForegroundSelectedDisabled", "ComboBoxForegroundDisabled",
            "ComboBoxPlaceHolderForeground", "ComboBoxDropDownGlyphForegroundDisabled", "TextControlForegroundDisabled",
            "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundDisabled", "KkindleScrollBarArrowDisabledBrush");
        Brushes(palette.StrongBorder,
            "ComboBoxItemBorderBrushDisabled", "ComboBoxItemBorderBrushSelectedDisabled", "ComboBoxBorderBrushDisabled",
            "KkindleScrollBarThumbBrush", "KkindleScrollBarThumbDisabledBrush");
        Brushes(palette.Muted, "KkindleScrollBarThumbPointerOverBrush", "KkindleScrollBarThumbPressedBrush");
        Brushes(palette.Hairline,
            "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed", "SliderTrackValueFillDisabled",
            "SliderThumbBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushDisabled");

        void Set(string name, Color color)
        {
            resources[name + "Color"] = color;
            SetBrush(name + "Brush", color);
        }

        void Brushes(Color color, params string[] keys)
        {
            foreach (var key in keys) SetBrush(key, color);
        }

        void SetBrush(string key, Color color)
        {
            if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
                brush.Color = color;
            else
                resources[key] = new SolidColorBrush(color);
        }
    }
}
