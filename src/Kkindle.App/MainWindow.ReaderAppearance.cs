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
    private ReaderBookLayoutProfile? _readerBookLayoutProfile;

    private bool CanUseReaderBookProfile =>
        !_readerIsPdf && _readerBookCard is not null && _readerBookFile is not null;

    private bool ReaderUsesBookOverride =>
        CanUseReaderBookProfile && _readerBookLayoutProfile?.UseBookOverride == true;

    private ReaderAppearanceSettings CurrentReaderAppearance =>
        ReaderUsesBookOverride
            ? ReaderAppearanceSettings.Normalize(_readerBookLayoutProfile!.Appearance)
            : ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance);

    private void InitializeReaderAppearance()
    {
        ReaderBookInfoText.LayoutUpdated += (_, _) => UpdateReaderBookTitleToolTip();
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

    private void UpdateReaderBookTitleToolTip()
    {
        var title = ReaderBookInfoText.Text;
        var trimmed = !string.IsNullOrWhiteSpace(title)
            && ReaderBookInfoText.TextLayout.TextLines.Any(line => line.HasCollapsed);
        if (!trimmed)
        {
            ToolTip.SetIsOpen(ReaderBookInfoText, false);
            ToolTip.SetTip(ReaderBookInfoText, null);
            return;
        }

        if (ToolTip.GetTip(ReaderBookInfoText) is TextBlock tip && tip.Text == title) return;
        ToolTip.SetTip(ReaderBookInfoText, new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            // The title is a single-line, trimmed TextBlock. Override these
            // inherited text settings on the popup so it shows the whole title.
            MaxLines = 0,
            TextTrimming = TextTrimming.None,
            MaxWidth = 480
        });
    }

    private void ApplyReaderAppearance()
    {
        var appearance = CurrentReaderAppearance;
        ReaderAppearanceResources.Populate(Resources, appearance);
        ReaderAppearanceResources.PopulateControls(ReaderThemeScope.Resources, appearance);
        ReaderThemeScope.RequestedThemeVariant = ThemeVariant.Light;
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
            var appearance = CurrentReaderAppearance;
            foreach (var option in new[] { ReaderClassicThemeOption, ReaderBeigeThemeOption, ReaderGreenThemeOption })
                option.IsChecked = Enum.TryParse<ReaderTheme>(option.Tag?.ToString(), out var theme) && theme == appearance.Theme;
            ReaderPaperEnabledCheck.IsChecked = appearance.PaperEnabled;
            ReaderPaperStrengthSlider.Value = appearance.PaperStrength * 100;
            ReaderFibersEnabledCheck.IsChecked = appearance.FibersEnabled;
            ReaderPdfAnnotationDisplayModePanel.IsVisible = _readerIsPdf;
            foreach (var option in new[] { ReaderPdfAnnotationPinnedOption, ReaderPdfAnnotationClickOption })
            {
                option.IsChecked = Enum.TryParse<ReaderAnnotationDisplayMode>(option.Tag?.ToString(), out var mode)
                    && mode == appearance.AnnotationDisplayMode;
            }
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
        var appearance = CurrentReaderAppearance;
        ReaderPaperStrengthValueText.Text = $"{appearance.PaperStrength * 100:0}%";
        ReaderPaperOptions.IsEnabled = appearance.PaperEnabled;
    }

    private void ReaderThemeOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange || sender is not RadioButton { IsChecked: true } option
            || !Enum.TryParse<ReaderTheme>(option.Tag?.ToString(), out var theme)) return;
        ChangeReaderAppearance(CurrentReaderAppearance with { Theme = theme });
    }

    private void ReaderPaperCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(CurrentReaderAppearance with
        {
            PaperEnabled = ReaderPaperEnabledCheck.IsChecked == true,
            FibersEnabled = ReaderFibersEnabledCheck.IsChecked == true
        });
    }

    private void ReaderPaperStrength_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(CurrentReaderAppearance with { PaperStrength = ReaderPaperStrengthSlider.Value / 100 });
    }

    private void ReaderPdfAnnotationDisplayModeOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange
            || sender is not RadioButton { IsChecked: true } option
            || !Enum.TryParse<ReaderAnnotationDisplayMode>(option.Tag?.ToString(), out var mode)) return;
        if (CurrentReaderAppearance.AnnotationDisplayMode == mode) return;
        HideReaderAnnotationHoverPopup();
        ChangeReaderAppearance(CurrentReaderAppearance with { AnnotationDisplayMode = mode });
        ShowCurrentReaderPdfAnnotation();
    }

    private void ChangeReaderAppearance(ReaderAppearanceSettings appearance)
    {
        appearance = ReaderAppearanceSettings.Normalize(appearance);
        if (CurrentReaderAppearance == appearance) return;
        if (ReaderUsesBookOverride)
        {
            _readerBookLayoutProfile = _readerBookLayoutProfile! with { Appearance = appearance };
        }
        else
        {
            // Global appearance lives in the settings file. A per-book
            // override updates only that book's synced reader row.
            _appSettings = _appSettings with { ReaderAppearance = appearance };
        }
        ApplyReaderAppearance();
        UpdateReaderAppearanceLabels();
        _readerAppearanceSaveTask = SaveReaderAppearanceAsync();
    }

    private async Task SaveReaderAppearanceAsync()
    {
        if (ReaderUsesBookOverride)
        {
            try
            {
                await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
                ReaderAppearanceSaveStatus.IsVisible = false;
            }
            catch (Exception exception)
            {
                ReaderAppearanceSaveStatus.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
                ReaderAppearanceSaveStatus.IsVisible = true;
            }
            return;
        }

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

    private async Task LoadReaderBookProfileAsync(CancellationToken cancellationToken)
    {
        _readerBookLayoutProfile = null;
        if (!CanUseReaderBookProfile) return;

        var profile = await _readerData.GetBookLayoutProfileAsync(
            _readerBookCard!.Book.Id,
            _readerBookFile!.Id,
            cancellationToken);
        _readerBookLayoutProfile = profile ?? new ReaderBookLayoutProfile(
            _readerBookCard.Book.Id,
            _readerBookFile.Id,
            false,
            NormalizeReaderLayoutForPlatform(_appSettings.DefaultReaderLayout),
            ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance));
    }

    private async Task SetReaderBookOverrideAsync(bool useBookOverride)
    {
        if (!CanUseReaderBookProfile) return;

        var globalLayout = NormalizeReaderLayoutForPlatform(_appSettings.DefaultReaderLayout);
        var globalAppearance = ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance);
        var profile = _readerBookLayoutProfile ?? new ReaderBookLayoutProfile(
            _readerBookCard!.Book.Id,
            _readerBookFile!.Id,
            false,
            globalLayout,
            globalAppearance);

        if (useBookOverride && !profile.UseBookOverride)
        {
            // Enabling an override starts from the currently visible settings,
            // so the toggle itself never causes an unexpected visual jump.
            profile = profile with
            {
                UseBookOverride = true,
                Layout = NormalizeReaderLayoutForPlatform(_readerLayout),
                Appearance = CurrentReaderAppearance
            };
        }
        else if (!useBookOverride)
        {
            profile = profile with
            {
                UseBookOverride = false,
                Layout = globalLayout,
                Appearance = globalAppearance
            };
        }
        else
        {
            profile = profile with { UseBookOverride = true };
        }

        _readerBookLayoutProfile = ReaderBookLayoutProfile.Normalize(profile);
        _readerLayout = ReaderLayoutDefaults.ApplyPreferences(
            _readerBookLayoutProfile.Layout,
            globalLayout,
            _readerBookLayoutProfile.UseBookOverride);
        ApplyReaderAppearance();
        SyncReaderAppearanceControls();
        SyncReaderBookScopeControls();
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
    }

    private async Task SaveCurrentReaderPreferencesAsync(
        CancellationToken cancellationToken)
    {
        if (_readerIsPdf) return;

        var layout = NormalizeReaderLayoutForPlatform(_readerLayout);
        _readerLayout = layout;
        if (ReaderUsesBookOverride)
        {
            _readerBookLayoutProfile = ReaderBookLayoutProfile.Normalize(
                _readerBookLayoutProfile! with { Layout = layout });
            await _readerData.SaveBookLayoutProfileAsync(
                _readerBookLayoutProfile,
                cancellationToken);
            return;
        }

        _appSettings = AppSettings.Normalize(_appSettings with
        {
            DefaultReaderLayout = layout
        });
        await _appSettingsStore.SaveAsync(_appSettings, cancellationToken);
        HandleLocalDataChanged(LocalDataChangeKind.Settings);
    }

    private void SyncReaderBookScopeControls()
    {
        if (ReaderBookScopeGlobalOption is null || ReaderBookScopeBookOption is null) return;
        var previous = _suppressReaderAppearanceChange;
        _suppressReaderAppearanceChange = true;
        try
        {
            ReaderBookScopeGlobalOption.IsChecked = !ReaderUsesBookOverride;
            ReaderBookScopeBookOption.IsChecked = ReaderUsesBookOverride;
            var canChooseScope = CanUseReaderBookProfile;
            ReaderBookScopeGlobalOption.IsVisible = canChooseScope;
            ReaderBookScopeBookOption.IsVisible = canChooseScope;
            if (ReaderBookScopeHint is not null)
                ReaderBookScopeHint.IsVisible =
                    canChooseScope && ReaderUsesBookOverride;
        }
        finally
        {
            _suppressReaderAppearanceChange = previous;
        }
    }

    private async void ReaderBookScopeOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange
            || sender is not RadioButton { IsChecked: true, Tag: string scope })
            return;
        await SetReaderBookOverrideAsync(
            string.Equals(scope, "book", StringComparison.Ordinal));
    }
}
