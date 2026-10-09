using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private bool _onboardingUpdatingLanguage;
    private bool _onboardingUpdatingChoices;
    private bool _onboardingSaving;
    private bool _onboardingInstallingCalibre;

    private void OnboardingSkipCalibreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_onboardingInstallingCalibre) return;
        OnboardingCalibreStatus.Text = GetOnboardingResource("Ui.Onboarding.CalibreSkipped", "暂不安装，可稍后在设置中安装");
        OnboardingCalibreStatus.IsVisible = true;
    }

    private async void OnboardingInstallCalibreButton_Click(object? sender, RoutedEventArgs e)
        => await InstallOnboardingCalibreAsync();

    private async Task InstallOnboardingCalibreAsync()
    {
        if (_onboardingInstallingCalibre || _calibreSetupBusy) return;
        _onboardingInstallingCalibre = true;
        _calibreSetupBusy = true;
        OnboardingInstallCalibreButton.IsEnabled = false;
        OnboardingSkipCalibreButton.IsEnabled = false;
        OnboardingNextButton.IsEnabled = false;
        OnboardingLanguageBox.IsEnabled = false;
        OnboardingCalibreProgress.IsVisible = true;
        OnboardingCalibreProgress.IsIndeterminate = true;
        OnboardingCalibreStatus.IsVisible = true;
        OnboardingCalibreStatus.Text = UiText.IsEnglish ? "Checking Calibre and KFX Input" : "正在检查 Calibre 和 KFX Input";
        var progress = new Progress<CalibreSetupProgress>(value =>
        {
            if (!_onboardingInstallingCalibre) return;
            OnboardingCalibreStatus.Text = UiText.Localize(value.Message);
            OnboardingCalibreProgress.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage) OnboardingCalibreProgress.Value = percentage;
        });
        try
        {
            using var setup = new CalibreSetupService(proxyAddress: TranslationGoogleProxyBox.Text);
            var executable = setup.LocateCalibre(CalibrePathBox.Text);
            if (executable is null)
                executable = (await setup.InstallCalibreAsync(progress, _lifetimeCancellation.Token)).ExecutablePath;
            if (!await setup.IsKfxInputInstalledAsync(executable, _lifetimeCancellation.Token))
                executable = await setup.InstallKfxInputAsync(executable, progress, _lifetimeCancellation.Token);
            if (setup.LocateCalibre(executable) is null ||
                !await setup.IsKfxInputInstalledAsync(executable, _lifetimeCancellation.Token))
                throw new InvalidOperationException(UiText.IsEnglish ? "Component verification failed" : "组件验证失败");
            CalibrePathBox.Text = executable;
            _appSettings = _appSettings with { CalibrePath = executable };
            await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
            OnboardingCalibreStatus.Text = GetOnboardingResource("Ui.Onboarding.CalibreDone", "Calibre 和 KFX Input 插件均已安装并验证");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            OnboardingCalibreStatus.Text = string.Format(GetOnboardingResource("Ui.Onboarding.CalibreFailed", "安装失败，可点击自动安装重试：{0}"), UiText.Localize(exception.Message));
        }
        finally
        {
            _onboardingInstallingCalibre = false;
            _calibreSetupBusy = false;
            OnboardingInstallCalibreButton.IsEnabled = true;
            OnboardingSkipCalibreButton.IsEnabled = true;
            OnboardingNextButton.IsEnabled = !_onboardingSaving;
            OnboardingLanguageBox.IsEnabled = true;
            OnboardingCalibreProgress.IsVisible = false;
            UpdateCalibreDetectionStatus();
        }
    }
    private bool _onboardingDisclaimerAccepted;
    private int _onboardingStep;
    private string? _onboardingSelectedDeviceModel;
    private (double Width, double Height, double MinWidth, double MinHeight,
        bool CanResize)? _onboardingWindowLayout;

    private void UseOnboardingWindowLayout()
    {
        if (_onboardingWindowLayout is not null) return;
        _onboardingWindowLayout = (Width, Height, MinWidth, MinHeight, CanResize);
        WindowState = WindowState.Normal;
        MinWidth = 0;
        MinHeight = 0;
        CanResize = false;
        var screen = Screens.ScreenFromWindow(this);
        var scale = screen?.Scaling ?? 1;
        Width = Math.Min(760, (screen?.WorkingArea.Width ?? 760 * scale) / scale);
        Height = Math.Min(576, (screen?.WorkingArea.Height ?? 576 * scale) / scale);
        if (screen is not null)
            Position = new PixelPoint(
                screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * scale) / 2),
                screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * scale) / 2));
    }

    private void RestoreMainWindowLayout()
    {
        if (_onboardingWindowLayout is not { } layout) return;
        _onboardingWindowLayout = null;
        var goldenRatio = (1 + Math.Sqrt(5)) / 2;
        var screen = Screens.ScreenFromWindow(this);
        var scale = screen?.Scaling ?? 1;
        var width = Math.Max(layout.Width, Math.Max(layout.MinWidth, layout.MinHeight * goldenRatio));
        if (screen is not null)
            width = Math.Min(width, Math.Min(screen.WorkingArea.Width / scale,
                screen.WorkingArea.Height / scale * goldenRatio));
        var height = width / goldenRatio;
        WindowState = WindowState.Normal;
        CanResize = layout.CanResize;
        MinWidth = Math.Min(layout.MinWidth, width);
        MinHeight = Math.Min(layout.MinHeight, height);
        Width = width;
        Height = height;
        if (screen is not null)
            Position = new PixelPoint(
                screen.WorkingArea.X + (int)Math.Round((screen.WorkingArea.Width - width * scale) / 2),
                screen.WorkingArea.Y + (int)Math.Round((screen.WorkingArea.Height - height * scale) / 2));
    }

    private void OnboardingHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ShowOnboardingIfNeeded()
    {
        if (_appSettings.OnboardingCompleted) return;
        UseOnboardingWindowLayout();

        _onboardingStep = 1;
        _onboardingDisclaimerAccepted = false;
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

        OnboardingDisclaimerCheckBox.IsChecked = false;
        OnboardingDisclaimerStatusText.IsVisible = false;
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
        var isDisclaimer = _onboardingStep == 2;
        var isDevice = _onboardingStep == 3;
        OnboardingWelcomePage.IsVisible = isWelcome;
        OnboardingDisclaimerPage.IsVisible = isDisclaimer;
        OnboardingDevicePage.IsVisible = isDevice;
        OnboardingStepText.Text = $"{_onboardingStep} / 3";
        OnboardingBackButton.IsVisible = !isWelcome;
        OnboardingSkipButton.IsVisible = isDevice;
        OnboardingNextButton.IsVisible = isWelcome || isDisclaimer;
        OnboardingNextButton.IsEnabled = !_onboardingSaving;
        OnboardingFinishButton.IsVisible = isDevice;
        OnboardingDisclaimerStatusText.IsVisible = false;
        OnboardingDeviceStatusText.IsVisible = false;

        if (isWelcome)
        {
            OnboardingLanguageBox.Focus();
        }
        else if (isDisclaimer)
        {
            OnboardingDisclaimerCheckBox.Focus();
        }
        else
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

    private void OnboardingDisclaimerCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _onboardingDisclaimerAccepted = sender is CheckBox { IsChecked: true };
        if (OnboardingDisclaimerStatusText is not null)
            OnboardingDisclaimerStatusText.IsVisible = false;
    }

    private void OnboardingNextButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_onboardingInstallingCalibre) return;
        if (_onboardingStep == 1)
        {
            _onboardingStep = 2;
            UpdateOnboardingPage();
            return;
        }

        if (_onboardingStep != 2)
            return;

        if (!_onboardingDisclaimerAccepted)
        {
            OnboardingDisclaimerStatusText.IsVisible = true;
            OnboardingDisclaimerCheckBox.Focus();
            return;
        }

        _onboardingStep = 3;
        UpdateOnboardingPage();
    }

    private void OnboardingBackButton_Click(object? sender, RoutedEventArgs e)
    {
        _onboardingStep = Math.Max(1, _onboardingStep - 1);
        UpdateOnboardingPage();
        if (_onboardingStep == 1)
            OnboardingLanguageBox.Focus();
        else
            OnboardingDisclaimerCheckBox.Focus();
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
        if (_onboardingSaving || _onboardingInstallingCalibre) return;
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
            RestoreMainWindowLayout();
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
            OnboardingNextButton.IsEnabled = !_onboardingSaving;
        }
    }

    private void OnboardingOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_onboardingInstallingCalibre) { e.Handled = true; return; }
        if (e.Key == Key.Escape && _onboardingStep > 1)
        {
            e.Handled = true;
            _onboardingStep--;
            UpdateOnboardingPage();
            if (_onboardingStep == 1)
                OnboardingLanguageBox.Focus();
            else
                OnboardingDisclaimerCheckBox.Focus();
        }
    }
}
