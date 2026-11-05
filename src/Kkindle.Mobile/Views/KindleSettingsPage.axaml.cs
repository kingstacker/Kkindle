using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

/// <summary>E-mail delivery to a Kindle, sharing the desktop app's protected settings file.</summary>
public partial class KindleSettingsPage : UserControl, IShellPage
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Design-time only.</summary>
    public KindleSettingsPage() => InitializeComponent();

    public KindleSettingsPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        Loaded += async (_, _) =>
        {
            try { Present(await _modules.Integrations.KindleEmailSettings.LoadAsync(_lifetime.Token)); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                StatusText.Text = "读取设置失败：" + UiText.Localize(exception.Message);
                StatusText.IsVisible = true;
            }
        };
    }

    private void Present(KindleEmailSettings settings)
    {
        KindleAddressBox.Text = settings.KindleEmailAddress;
        SenderBox.Text = settings.SenderEmailAddress;
        HostBox.Text = settings.SmtpHost;
        PortBox.Text = settings.SmtpPort.ToString();
        UserBox.Text = settings.SmtpUsername;
        PasswordBox.Text = settings.SmtpPassword;
        SslSwitch.IsChecked = settings.EnableSsl;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var settings = KindleEmailSettings.Normalize(new KindleEmailSettings
        {
            KindleEmailAddress = KindleAddressBox.Text ?? string.Empty,
            SenderEmailAddress = SenderBox.Text ?? string.Empty,
            SmtpHost = HostBox.Text ?? string.Empty,
            SmtpPort = int.TryParse(PortBox.Text, out var port) ? port : 0,
            SmtpUsername = UserBox.Text ?? string.Empty,
            SmtpPassword = PasswordBox.Text ?? string.Empty,
            EnableSsl = SslSwitch.IsChecked == true
        });
        if (settings.Validate() is { } invalid)
        {
            StatusText.Text = UiText.Localize(invalid);
            StatusText.IsVisible = true;
            return;
        }
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null)
        {
            StatusText.Text = "正在恢复数据，请稍后重试。";
            StatusText.IsVisible = true;
            return;
        }
        SaveButton.IsEnabled = false;
        try
        {
            await _modules!.Integrations.KindleEmailSettings.SaveAsync(settings, _lifetime.Token);
            StatusText.IsVisible = false;
            _shell?.ShowToast("已保存");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = "保存失败：" + UiText.Localize(exception.Message);
            StatusText.IsVisible = true;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();

    /// <summary>Pushed pages only detach this one; work ends when it leaves history.</summary>
    public void OnClosed() => _lifetime.Cancel();
}
