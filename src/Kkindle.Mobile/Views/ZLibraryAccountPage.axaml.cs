using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

/// <summary>Z-Library account, stored in the desktop app's protected settings file.</summary>
public partial class ZLibraryAccountPage : UserControl
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Design-time only.</summary>
    public ZLibraryAccountPage() => InitializeComponent();

    public ZLibraryAccountPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        Loaded += async (_, _) =>
        {
            var settings = await _modules.Integrations.ZLibrarySettings.LoadAsync(_lifetime.Token);
            EmailBox.Text = settings.Email;
            PasswordBox.Text = settings.Password;
            BaseUrlBox.Text = settings.BaseUrl;
        };
        DetachedFromVisualTree += (_, _) => _lifetime.Cancel();
    }

    private ZLibrarySettings ReadSettings() => ZLibrarySettings.Normalize(new ZLibrarySettings
    {
        Email = EmailBox.Text ?? string.Empty,
        Password = PasswordBox.Text ?? string.Empty,
        BaseUrl = BaseUrlBox.Text ?? string.Empty
    });

    private void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }
        SetBusy(true);
        ShowStatus("正在登录…");
        try
        {
            await _modules!.Integrations.ZLibrary.LoginAsync(settings.Email, settings.Password, settings.BaseUrl, _lifetime.Token);
            ShowStatus("登录成功");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("登录失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (settings.Validate() is { } invalid)
        {
            ShowStatus(UiText.Localize(invalid));
            return;
        }
        SetBusy(true);
        try
        {
            await _modules!.Integrations.ZLibrarySettings.SaveAsync(settings, _lifetime.Token);
            ShowStatus(null);
            _shell?.ShowToast("已保存");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("保存失败：" + exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();
}
