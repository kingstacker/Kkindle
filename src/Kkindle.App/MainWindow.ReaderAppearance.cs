using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private bool _suppressReaderAppearanceChange = true;
    private Task _readerAppearanceSaveTask = Task.CompletedTask;

    private void InitializeReaderAppearance()
    {
        ApplyReaderAppearance();
        ReaderRoot.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) UpdateReaderTitleAppearance();
        };
        ReaderRoot.SizeChanged += (_, _) =>
        {
            if (ReaderLayoutSettingsPopup.IsOpen) UpdateReaderSettingsPopupSize();
        };
        UpdateReaderTitleAppearance();
        SyncReaderAppearanceControls();
    }

    private void ApplyReaderAppearance()
    {
        var appearance = ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance);
        ReaderAppearanceResources.Populate(Resources, appearance);
        ReaderAppearanceResources.PopulateControls(ReaderThemeScope.Resources, appearance);
        ReaderThemeScope.RequestedThemeVariant = appearance.Theme == ReaderTheme.Night
            ? ThemeVariant.Dark : ThemeVariant.Light;
        (_readerActiveHost as NativeReaderHost)?.SetAppearance(appearance);
        (_readerPreloadHost as NativeReaderHost)?.SetAppearance(appearance);
        (_readerActiveHost as NativePdfReaderHost)?.SetAppearance(appearance);
        UpdateReaderTitleAppearance();
        // Updating marker brushes leaves the rail's scroll offset and hover
        // wave intact; rebuilding its ItemsSource would move the chapter map.
        foreach (var item in ReaderTocCompactList.ItemsSource?.OfType<ReaderTocMarker>() ?? [])
            item.Palette = ReaderPalette.For(appearance.Theme);
        UpdateReaderCompactMarkerWave();
    }

    private void UpdateReaderTitleAppearance()
    {
        var visible = ReaderRoot.IsVisible;
        ReaderThemeScope.IsVisible = visible;
        ReaderWindowTitleBar.Classes.Set("readerChrome", visible);
        ReaderWindowTitleBar.Background = visible ? (IBrush)Resources["ReaderPageBrush"]! : Brushes.Transparent;
    }

    private void SyncReaderAppearanceControls()
    {
        _suppressReaderAppearanceChange = true;
        try
        {
            var appearance = _appSettings.ReaderAppearance;
            foreach (var option in new[] { ReaderClassicThemeOption, ReaderNightThemeOption, ReaderGreenThemeOption, ReaderBrownThemeOption, ReaderIvoryThemeOption })
                option.IsChecked = Enum.TryParse<ReaderTheme>(option.Tag?.ToString(), out var theme) && theme == appearance.Theme;
            ReaderPaperEnabledCheck.IsChecked = appearance.PaperEnabled;
            ReaderPaperStrengthSlider.Value = appearance.PaperStrength * 100;
            ReaderFibersEnabledCheck.IsChecked = appearance.FibersEnabled;
            UpdateReaderAppearanceLabels();
        }
        finally { _suppressReaderAppearanceChange = false; }
    }

    private void UpdateReaderSettingsPopupSize()
    {
        var width = Math.Max(0, ReaderRoot.Bounds.Width);
        var height = Math.Max(0, ReaderRoot.Bounds.Height);
        ReaderLayoutSettingsOverlay.Width = width;
        ReaderLayoutSettingsOverlay.Height = height;
        ReaderLayoutSettingsCard.Width = Math.Min(580, Math.Max(1, width - 32));
        ReaderLayoutSettingsCard.MaxHeight = Math.Min(760, Math.Max(1, height - 32));
    }

    private void UpdateReaderAppearanceLabels()
    {
        ReaderPaperStrengthValueText.Text = $"{_appSettings.ReaderAppearance.PaperStrength * 100:0}%";
        ReaderPaperOptions.IsEnabled = _appSettings.ReaderAppearance.PaperEnabled;
        ReaderNightPaperHint.IsVisible = _appSettings.ReaderAppearance.Theme == ReaderTheme.Night;
    }

    private void ReaderThemeOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange || sender is not RadioButton { IsChecked: true } option
            || !Enum.TryParse<ReaderTheme>(option.Tag?.ToString(), out var theme)) return;
        ChangeReaderAppearance(_appSettings.ReaderAppearance with { Theme = theme });
    }

    private void ReaderPaperCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(_appSettings.ReaderAppearance with
        {
            PaperEnabled = ReaderPaperEnabledCheck.IsChecked == true,
            FibersEnabled = ReaderFibersEnabledCheck.IsChecked == true
        });
    }

    private void ReaderPaperStrength_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(_appSettings.ReaderAppearance with { PaperStrength = ReaderPaperStrengthSlider.Value / 100 });
    }

    private void ChangeReaderAppearance(ReaderAppearanceSettings appearance)
    {
        appearance = ReaderAppearanceSettings.Normalize(appearance);
        if (_appSettings.ReaderAppearance == appearance) return;
        // Update shared settings before yielding, so a concurrent layout or
        // general-settings save always sees the newest reader appearance.
        _appSettings = _appSettings with { ReaderAppearance = appearance };
        ApplyReaderAppearance();
        UpdateReaderAppearanceLabels();
        _readerAppearanceSaveTask = SaveReaderAppearanceAsync();
    }

    private async Task SaveReaderAppearanceAsync()
    {
        // Share the normal settings gate: shutdown's flush waits for this write,
        // even when the user closes immediately after changing a radio/slider.
        await _appSettingsSaveGate.WaitAsync();
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings, CancellationToken.None);
            ReaderAppearanceSaveStatus.IsVisible = false;
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
        }
        catch (Exception exception)
        {
            ReaderAppearanceSaveStatus.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
            ReaderAppearanceSaveStatus.IsVisible = true;
        }
        finally { _appSettingsSaveGate.Release(); }
    }
}
