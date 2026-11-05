using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

/// <summary>The AI connection, stored in the same protected file as the desktop app's.</summary>
public partial class AiSettingsPage : UserControl, IShellPage
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private string _provider = "deepseek";

    /// <summary>Design-time only.</summary>
    public AiSettingsPage() => InitializeComponent();

    public AiSettingsPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        Loaded += async (_, _) =>
        {
            try { Present(await _modules.Ai.Settings.LoadAsync(_lifetime.Token)); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { ShowStatus("读取设置失败：" + UiText.Localize(exception.Message)); }
        };
    }

    private void Present(AiConnectionSettings settings)
    {
        _provider = settings.Provider;
        PresentProvider();
        BaseUrlBox.Text = settings.BaseUrl;
        ModelBox.Text = settings.Model;
        ApiKeyBox.Text = settings.ApiKey;
    }

    private void PresentProvider()
    {
        ProviderDeepSeek.Classes.Set("on", _provider == "deepseek");
        ProviderOpenAi.Classes.Set("on", _provider == "openai");
        ProviderCustom.Classes.Set("on", _provider == "custom");
    }

    private AiConnectionSettings ReadSettings() => new()
    {
        Provider = _provider,
        BaseUrl = BaseUrlBox.Text?.Trim() ?? string.Empty,
        Model = ModelBox.Text?.Trim() ?? string.Empty,
        ApiKey = ApiKeyBox.Text?.Trim() ?? string.Empty
    };

    private void OnProviderClick(object? sender, RoutedEventArgs e)
    {
        _provider = sender switch
        {
            Button { Name: nameof(ProviderOpenAi) } => "openai",
            Button { Name: nameof(ProviderCustom) } => "custom",
            _ => "deepseek"
        };
        var defaults = AiConnectionSettings.GetDefaults(_provider);
        BaseUrlBox.Text = defaults.BaseUrl;
        ModelBox.Text = defaults.Model;
        PresentProvider();
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        TestButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ShowStatus("正在测试连接…");
        try
        {
            await _modules!.Ai.ChatClient.TestConnectionAsync(ReadSettings(), _lifetime.Token);
            ShowStatus("连接成功");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("连接失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
        {
            ShowStatus("请输入有效的接口地址");
            return;
        }
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) { ShowStatus("正在恢复数据，请稍后重试。"); return; }
        SetBusy(true);
        try
        {
            await _modules!.Ai.Settings.SaveAsync(settings, _lifetime.Token);
            ShowStatus(null);
            _shell?.ShowToast("已保存");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("保存失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();

    /// <summary>Pushed pages only detach this one; work ends when it leaves history.</summary>
    public void OnClosed() => _lifetime.Cancel();
}
