using Avalonia;
using Avalonia.Controls;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private void ApplyMainAppearance()
    {
        if (Application.Current is App app) app.ApplyTheme(_appSettings.MainTheme);
    }

    private void MainThemeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave || !_appSettingsAutoSaveConfigured
            || MainThemeBox.SelectedItem is not ComboBoxItem { Tag: not null } item
            || !Enum.TryParse<AppTheme>(item.Tag.ToString(), out var theme)
            || !Enum.IsDefined(theme)) return;

        // Preview immediately while the normal settings debounce and shutdown
        // flush persist the selection, together with other pending preferences.
        if (Application.Current is App app) app.ApplyTheme(theme);
        ScheduleAppSettingsAutoSave();
        _appSettings = _appSettings with { MainTheme = theme };
    }

    private AppTheme ReadMainThemeFromControls() =>
        MainThemeBox.SelectedItem is ComboBoxItem item
        && Enum.TryParse<AppTheme>(item.Tag?.ToString(), out var theme)
        && Enum.IsDefined(theme) ? theme : _appSettings.MainTheme;
}
