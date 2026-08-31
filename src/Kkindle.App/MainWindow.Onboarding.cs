using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private bool _onboardingUpdatingLanguage;
    private bool _onboardingUpdatingChoices;
    private bool _onboardingSaving;
    private int _onboardingStep;
    private string? _onboardingSelectedDeviceModel;

    private void ShowOnboardingIfNeeded()
    {
        if (_appSettings.OnboardingCompleted) return;

        _onboardingStep = 1;
        _onboardingSelectedDeviceModel = _appSettings.DefaultDeviceModel;
        _onboardingUpdatingLanguage = true;
        try
        {
            OnboardingLanguageBox.SelectedIndex = UiText.IsEnglish ? 1 : 0;
        }
        finally
        {
            _onboardingUpdatingLanguage = false;
        }

        UpdateOnboardingLanguageLabels();
        RefreshOnboardingDeviceChoices();
        UpdateOnboardingPage();
        OnboardingOverlay.IsVisible = true;
        OnboardingOverlay.Opacity = 1;
        OnboardingLanguageBox.Focus();
    }

    private void UpdateOnboardingPage()
    {
        var isWelcome = _onboardingStep == 1;
        OnboardingWelcomePage.IsVisible = isWelcome;
        OnboardingDevicePage.IsVisible = !isWelcome;
        OnboardingStepText.Text = $"{_onboardingStep} / 2";
        OnboardingBackButton.IsVisible = !isWelcome;
        OnboardingSkipButton.IsVisible = !isWelcome;
        OnboardingNextButton.IsVisible = isWelcome;
        OnboardingFinishButton.IsVisible = !isWelcome;
        OnboardingDeviceStatusText.IsVisible = false;

        if (!isWelcome)
        {
            RefreshOnboardingDeviceChoices();
            OnboardingVendorBox.Focus();
        }
    }

    private void RefreshOnboardingLocalizedChoices()
    {
        if (!OnboardingOverlay.IsVisible) return;
        UpdateOnboardingLanguageLabels();
        RefreshOnboardingDeviceChoices();
        UpdateOnboardingSelectedDeviceText();
    }

    private void UpdateOnboardingLanguageLabels()
    {
        if (OnboardingLanguageBox.Items.Count < 2) return;
        if (OnboardingLanguageBox.Items[0] is ComboBoxItem chineseItem)
            chineseItem.Content = "简体中文";
        if (OnboardingLanguageBox.Items[1] is ComboBoxItem englishItem)
            englishItem.Content = "English";
    }

    private void RefreshOnboardingDeviceChoices()
    {
        if (_onboardingUpdatingChoices) return;

        var selectedVendor = GetComboBoxTag(OnboardingVendorBox.SelectedItem);
        var selectedModel = _onboardingSelectedDeviceModel
            ?? GetComboBoxTag(OnboardingDeviceModelBox.SelectedItem);
        var matchingVendor = DeviceModelCatalog.Vendors.FirstOrDefault(vendor =>
            string.Equals(vendor.Name, selectedVendor, StringComparison.Ordinal)
            || vendor.Models.Contains(selectedModel ?? string.Empty, StringComparer.Ordinal));
        matchingVendor ??= DeviceModelCatalog.Vendors.FirstOrDefault();

        _onboardingUpdatingChoices = true;
        try
        {
            var vendorItems = DeviceModelCatalog.Vendors
                .Select(vendor => new ComboBoxItem
                {
                    Content = LocalizeDeviceModelLabel(vendor.Name),
                    Tag = vendor.Name
                })
                .ToArray();
            OnboardingVendorBox.ItemsSource = vendorItems;
            OnboardingVendorBox.SelectedItem = vendorItems.FirstOrDefault(item =>
                string.Equals(item.Tag as string, matchingVendor?.Name, StringComparison.Ordinal));

            var models = matchingVendor?.Models ?? [];
            var modelItems = new List<ComboBoxItem>
            {
                new()
                {
                    Content = GetOnboardingResource(
                        "Ui.Onboarding.SelectModelPlaceholder",
                        "选择设备型号"),
                    Tag = null
                }
            };
            modelItems.AddRange(models.Select(model => new ComboBoxItem
            {
                Content = LocalizeDeviceModelLabel(model),
                Tag = model
            }));
            OnboardingDeviceModelBox.ItemsSource = modelItems.ToArray();
            OnboardingDeviceModelBox.IsEnabled = true;
            OnboardingDeviceModelBox.SelectedItem = modelItems.FirstOrDefault(item =>
                string.Equals(item.Tag as string, selectedModel, StringComparison.Ordinal))
                ?? modelItems[0];

            _onboardingSelectedDeviceModel = GetComboBoxTag(OnboardingDeviceModelBox.SelectedItem);
        }
        finally
        {
            _onboardingUpdatingChoices = false;
        }

        UpdateOnboardingSelectedDeviceText();
    }

    private void UpdateOnboardingSelectedDeviceText()
    {
        var model = _onboardingSelectedDeviceModel;
        OnboardingSelectedDeviceText.Text = model is null
            ? GetOnboardingResource("Ui.Onboarding.NoModelSelected", "尚未选择设备型号")
            : LocalizeDeviceModelLabel(model);
    }

    private static string? GetComboBoxTag(object? item) => item is ComboBoxItem { Tag: string value }
        ? value
        : null;

    private static string GetOnboardingResource(string key, string fallback)
    {
        if (Application.Current?.TryGetResource(
                key,
                Application.Current.ActualThemeVariant,
                out var value) == true
            && value is not null)
        {
            return value.ToString() ?? fallback;
        }

        return fallback;
    }

    private void OnboardingLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_onboardingUpdatingLanguage
            || OnboardingLanguageBox is null
            || OnboardingLanguageBox.SelectedItem is not ComboBoxItem { Tag: string language })
        {
            return;
        }

        var normalized = UiText.NormalizeLanguage(language);
        if (Application.Current is App app)
            app.ApplyLanguage(normalized);
        _appSettings = AppSettings.Normalize(_appSettings with { UiLanguage = normalized });
        _suppressAppSettingsAutoSave = true;
        try
        {
            UiLanguageBox.SelectedIndex = normalized.Equals("en-US", StringComparison.Ordinal)
                ? 1
                : 0;
        }
        finally
        {
            _suppressAppSettingsAutoSave = false;
        }
        UpdateOnboardingLanguageLabels();
        RefreshOnboardingLocalizedChoices();
    }

    private void OnboardingVendorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_onboardingUpdatingChoices) return;

        var vendorName = GetComboBoxTag(OnboardingVendorBox.SelectedItem);
        var vendor = DeviceModelCatalog.Vendors.FirstOrDefault(item =>
            string.Equals(item.Name, vendorName, StringComparison.Ordinal));
        _onboardingSelectedDeviceModel = null;

        _onboardingUpdatingChoices = true;
        try
        {
            var modelItems = new List<ComboBoxItem>
            {
                new()
                {
                    Content = GetOnboardingResource(
                        "Ui.Onboarding.SelectModelPlaceholder",
                        "选择设备型号"),
                    Tag = null
                }
            };
            modelItems.AddRange((vendor?.Models ?? []).Select(model => new ComboBoxItem
            {
                Content = LocalizeDeviceModelLabel(model),
                Tag = model
            }));
            OnboardingDeviceModelBox.ItemsSource = modelItems.ToArray();
            OnboardingDeviceModelBox.IsEnabled = vendor is not null;
            OnboardingDeviceModelBox.SelectedIndex = 0;
        }
        finally
        {
            _onboardingUpdatingChoices = false;
        }

        UpdateOnboardingSelectedDeviceText();
    }

    private void OnboardingDeviceModelBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_onboardingUpdatingChoices) return;
        _onboardingSelectedDeviceModel = GetComboBoxTag(OnboardingDeviceModelBox.SelectedItem);
        OnboardingDeviceStatusText.IsVisible = false;
        UpdateOnboardingSelectedDeviceText();
    }

    private void OnboardingNextButton_Click(object? sender, RoutedEventArgs e)
    {
        _onboardingStep = 2;
        UpdateOnboardingPage();
    }

    private void OnboardingBackButton_Click(object? sender, RoutedEventArgs e)
    {
        _onboardingStep = 1;
        UpdateOnboardingPage();
        OnboardingLanguageBox.Focus();
    }

    private async void OnboardingSkipButton_Click(object? sender, RoutedEventArgs e)
        => await CompleteOnboardingAsync(_appSettings.DefaultDeviceModel);

    private async void OnboardingFinishButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_onboardingSelectedDeviceModel))
        {
            OnboardingDeviceStatusText.IsVisible = true;
            OnboardingDeviceModelBox.Focus();
            return;
        }

        await CompleteOnboardingAsync(_onboardingSelectedDeviceModel);
    }

    private async Task CompleteOnboardingAsync(string? selectedModel)
    {
        if (_onboardingSaving) return;
        _onboardingSaving = true;
        OnboardingBackButton.IsEnabled = false;
        OnboardingSkipButton.IsEnabled = false;
        OnboardingFinishButton.IsEnabled = false;
        OnboardingNextButton.IsEnabled = false;

        var normalizedModel = string.IsNullOrWhiteSpace(selectedModel)
            ? null
            : selectedModel.Trim();
        try
        {
            if (normalizedModel is not null && CurrentDevice is { } device)
                await _deviceModelStore.SetModelAsync(device.Identity, normalizedModel, _lifetimeCancellation.Token);

            _appSettings = AppSettings.Normalize(_appSettings with
            {
                OnboardingCompleted = true,
                DefaultDeviceModel = normalizedModel
            });
            await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);

            if (normalizedModel is not null && CurrentDevice is { } currentDevice)
            {
                _deviceDisplayName = normalizedModel;
                KindleStatusText.Text = normalizedModel;
                KindleConnectionText.Text = T("{0} · 已连接", currentDevice.ConnectionLabel);
                KindleConnectionText.IsVisible = true;
                DevicePageDeviceText.Text = $"{normalizedModel} · {currentDevice.ConnectionLabel}";
                DeviceNameButton.IsEnabled = true;
            }

            _suppressAppSettingsAutoSave = true;
            try
            {
                UiLanguageBox.SelectedIndex = UiText.IsEnglish ? 1 : 0;
            }
            finally
            {
                _suppressAppSettingsAutoSave = false;
            }

            OnboardingOverlay.IsVisible = false;
            LibraryRoot.IsVisible = true;
            UpdateLibraryUi();
            StartAutomaticUpdateCheck();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("保存失败：{0}", UiText.Localize(exception.Message)), 4000);
        }
        finally
        {
            _onboardingSaving = false;
            OnboardingBackButton.IsEnabled = true;
            OnboardingSkipButton.IsEnabled = true;
            OnboardingFinishButton.IsEnabled = true;
            OnboardingNextButton.IsEnabled = true;
        }
    }

    private void OnboardingOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _onboardingStep == 2)
        {
            e.Handled = true;
            _onboardingStep = 1;
            UpdateOnboardingPage();
            OnboardingLanguageBox.Focus();
        }
    }
}
