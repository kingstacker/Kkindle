using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private string _activeSettingsCategory = "Library";
    private bool _mainReaderAiSettingsLoaded;
    private Task? _mainReaderAiSettingsLoadTask;
    private readonly SemaphoreSlim _appSettingsSaveGate = new(1, 1);
    private long _appSettingsEditVersion;
    private long _appSettingsSavedVersion;
    private bool _skipS3SyncOnExit;
    private bool _s3SettingsDirty;
    private bool _s3SettingsSaving;
    private long _s3SettingsEditVersion;
    private Task<bool>? _s3SettingsSaveTask;

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        SettingsDataPathText.Text = _paths.Data;
        ShowSettingsSection(_activeSettingsCategory);
    }

    private void SystemBackupNavigationButton_Click(object? sender, RoutedEventArgs e) =>
        OpenSettingsExpander("Data", SettingsBackupExpander);

    private void SystemS3SyncNavigationButton_Click(object? sender, RoutedEventArgs e) =>
        OpenSettingsExpander("Data", SettingsS3Expander);

    private void KindleEmailSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        OpenSettingsExpander("Kindle", SettingsEmailExpander);
        FocusSettingsControl(KindleEmailRecipientBox);
    }

    private async void ReaderAiSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        OpenSettingsExpander("Reading", SettingsAiExpander);
        await EnsureMainReaderAiSettingsLoadedAsync();
        if (SettingsReadingSection.IsVisible && SettingsAiExpander.IsExpanded)
            FocusSettingsControl(MainReaderAiBaseUrlBox);
    }

    private void SettingsCategoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) ShowSettingsSection(tag);
    }

    private void ShowSettingsSection(string tag)
    {
        var sections = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            ["Library"] = SettingsLibrarySection,
            ["Reading"] = SettingsReadingSection,
            ["Kindle"] = SettingsKindleSection,
            ["Data"] = SettingsDataSection,
            ["About"] = SettingsAboutSection
        };
        if (!sections.TryGetValue(tag, out var activeSection))
        {
            tag = "Library";
            activeSection = SettingsLibrarySection;
        }

        var focused = FocusManager?.GetFocusedElement() as Control;
        var focusWasInContent = focused is not null && focused.GetVisualAncestors()
            .Any(ancestor => ReferenceEquals(ancestor, SettingsScrollViewer) || ReferenceEquals(ancestor, SettingsS3ActionBar));
        foreach (var section in sections.Values)
            section.IsVisible = ReferenceEquals(section, activeSection);
        if (!string.Equals(_activeSettingsCategory, tag, StringComparison.OrdinalIgnoreCase))
            SettingsScrollViewer.Offset = default;
        _activeSettingsCategory = tag;
        UpdateS3SettingsActions();
        Button[] buttons = [SettingsLibraryButton, SettingsReadingButton, SettingsKindleButton, SettingsDataButton, SettingsAboutButton];
        foreach (var button in buttons)
        {
            var active = string.Equals(button.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("active", active);
            if (active && focusWasInContent && focused?.IsEffectivelyVisible == false) button.Focus();
        }

    }

    private static void FocusSettingsControl(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (control.IsEffectivelyVisible && TopLevel.GetTopLevel(control) is not null) control.Focus();
    }, DispatcherPriority.Loaded);

    private void OpenSettingsExpander(string category, Expander expander)
    {
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        ShowSettingsSection(category);
        expander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (expander.IsEffectivelyVisible) expander.BringIntoView();
        }, DispatcherPriority.Loaded);
        UpdateS3SettingsActions();
    }

    private async void SettingsExpander_Expanded(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source)) return;
        UpdateS3SettingsActions();
        try
        {
            if (ReferenceEquals(sender, SettingsAiExpander))
                await EnsureMainReaderAiSettingsLoadedAsync();
            else if (ReferenceEquals(sender, SettingsTrashExpander))
                await RefreshTrashItemsAsync();
            else if (ReferenceEquals(sender, SettingsDiagnosticsExpander))
                await RefreshPlatformDiagnosticsAsync();
            else if (ReferenceEquals(sender, SettingsLocalResourcesExpander))
                await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SettingsStatusText.Text = T("读取设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void SettingsExpander_Collapsed(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !ReferenceEquals(sender, e.Source)) return;
        if (FocusManager?.GetFocusedElement() is Control focused
            && (!focused.IsEffectivelyVisible
                || (ReferenceEquals(expander, SettingsS3Expander) && focused.GetVisualAncestors().Contains(SettingsS3ActionBar))))
            expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()?.Focus();
        UpdateS3SettingsActions();
    }

    private async Task EnsureMainReaderAiSettingsLoadedAsync()
    {
        if (_mainReaderAiSettingsLoaded) return;
        if (_mainReaderAiSettingsLoadTask is { IsCompleted: false } pending)
        {
            await pending;
            return;
        }
        _mainReaderAiSettingsLoadTask = LoadMainReaderAiSettingsAsync();
        await _mainReaderAiSettingsLoadTask;
    }

    private void ConfigureS3SettingsDraftTracking()
    {
        ToggleSwitch[] switches = [S3SyncEnabledCheck, S3AutomaticSyncCheck, S3PathStyleCheck, S3SkipTlsVerifyCheck];
        foreach (var control in switches) control.IsCheckedChanged += (_, _) => S3SettingsFieldChanged();
        TextBox[] fields = [S3EndpointBox, S3AccessKeyBox, S3SecretKeyBox, S3BucketBox, S3RegionBox, S3PrefixBox, S3EncryptionKeyBox];
        foreach (var control in fields)
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) S3SettingsFieldChanged();
            };
        NumericUpDown[] numbers = [S3SyncIntervalBox, S3TimeoutBox, S3ConcurrencyBox];
        foreach (var control in numbers) control.ValueChanged += (_, _) => S3SettingsFieldChanged();
        UpdateS3SettingsDraftState();
    }

    private void S3SettingsFieldChanged()
    {
        if (_suppressS3SettingsDraftTracking) return;
        _s3SettingsEditVersion++;
        S3SyncStatusText.Text = string.Empty;
        UpdateS3SettingsDraftState();
    }

    private void UpdateS3SettingsDraftState()
    {
        _s3SettingsDirty = ReadS3SyncSettingsFromControls() != S3SyncSettings.Normalize(_s3SyncStoredSettings.Settings);
        UpdateS3SettingsActions();
    }

    private void UpdateS3SettingsActions()
    {
        if (SettingsS3ActionBar is null) return;
        SettingsS3ActionBar.IsVisible = _activeSettingsCategory.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && SettingsS3Expander.IsExpanded;
        S3DraftStatusText.Text = "S3 · " + (_s3SettingsSaving ? T("正在保存 S3 设置…")
            : _s3SettingsDirty ? T("有未保存的修改 · 保存后生效")
            : _s3SyncStoredSettings.Settings.IsConfigured ? T("配置已保存 · 立即同步使用当前保存的配置")
            : T("尚未配置 · 填写连接信息后保存"));
        S3SaveSettingsButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3SettingsDirty;
        S3DiscardSettingsButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3SettingsDirty;
        S3TestConnectionButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3TestConnectionCancellation is null;
        S3SyncNowButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && !_s3SettingsDirty
            && _s3SyncStoredSettings.Settings.IsConfigured;
    }

    private void S3DiscardSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_s3SyncBusy || _s3SettingsSaving) return;
        PopulateS3SyncControls();
        S3ConnectionProfileStatusText.Text = string.Empty;
        S3SyncStatusText.Text = T("已放弃修改，恢复为上次保存的配置。");
    }

    private void CancelAppSettingsDebounce()
    {
        var pending = _appSettingsAutoSaveCancellation;
        _appSettingsAutoSaveCancellation = null;
        pending?.Cancel();
        // The debounce owns disposal, including when its save is already running.
    }

    private async Task DebounceAppSettingsAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(600, cancellation.Token);
            if (!cancellation.IsCancellationRequested) await SaveAppSettingsCoreAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_appSettingsAutoSaveCancellation, cancellation))
                _appSettingsAutoSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> FlushAppSettingsAsync()
    {
        do
        {
            CancelAppSettingsDebounce();
            // This also waits for any save already holding the gate. Cancelling
            // the debounce never cancels an in-flight disk write.
            if (!await SaveAppSettingsCoreAsync()) return false;
        } while (_appSettingsSavedVersion < _appSettingsEditVersion);
        return true;
    }

    private async Task<bool> PrepareSettingsForExitAsync()
    {
        if (!await FlushAppSettingsAsync()) return false;
        if (_s3SettingsSaveTask is { IsCompleted: false } saving && !await saving) return false;
        UpdateS3SettingsDraftState();
        if (!_s3SettingsDirty) return true;
        OpenSettingsExpander("Data", SettingsS3Expander);
        if (!await ConfirmAsync(T("S3 设置尚未保存"),
                T("S3 配置中有未保存的修改。返回设置可保存；继续退出将放弃这些修改。"), T("放弃修改并退出")))
            return false;
        PopulateS3SyncControls();
        return true;
    }

    private KindleEmailSettings ReadKindleEmailDraft() => KindleEmailSettings.Normalize(new KindleEmailSettings
    {
        KindleEmailAddress = KindleEmailRecipientBox.Text ?? string.Empty,
        SenderEmailAddress = KindleEmailSenderBox.Text ?? string.Empty,
        SmtpHost = KindleEmailSmtpHostBox.Text ?? string.Empty,
        SmtpPort = int.TryParse(KindleEmailSmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 587,
        SmtpUsername = KindleEmailUsernameBox.Text ?? string.Empty,
        SmtpPassword = KindleEmailPasswordBox.Text ?? string.Empty,
        EnableSsl = KindleEmailSslCheck.IsChecked != false
    });

    private ZLibrarySettings ReadZLibraryDraft() => ZLibrarySettings.Normalize(new ZLibrarySettings
    {
        Email = ZLibraryEmailBox.Text ?? string.Empty,
        Password = ZLibraryPasswordBox.Text ?? string.Empty,
        BaseUrl = ZLibraryBaseUrlBox.Text ?? string.Empty
    });

    private static bool KindleEmailSettingsEqual(KindleEmailSettings left, KindleEmailSettings right) =>
        (left.KindleEmailAddress, left.SenderEmailAddress, left.SmtpHost, left.SmtpPort, left.SmtpUsername, left.SmtpPassword, left.EnableSsl)
        == (right.KindleEmailAddress, right.SenderEmailAddress, right.SmtpHost, right.SmtpPort, right.SmtpUsername, right.SmtpPassword, right.EnableSsl);

    private static bool ZLibrarySettingsEqual(ZLibrarySettings left, ZLibrarySettings right) =>
        (left.Email, left.Password, left.BaseUrl) == (right.Email, right.Password, right.BaseUrl);

    private void PopulateKindleEmailControls()
    {
        KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
        KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
        KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
        KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
        KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
        KindleEmailPasswordBox.Text = _kindleEmailSettings.SmtpPassword;
        KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
    }

    private void PopulateZLibraryControls()
    {
        ZLibraryEmailBox.Text = _zLibrarySettings.Email;
        ZLibraryPasswordBox.Text = _zLibrarySettings.Password;
        ZLibraryBaseUrlBox.Text = _zLibrarySettings.BaseUrl;
    }
}
