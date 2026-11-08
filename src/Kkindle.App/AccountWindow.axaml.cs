using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class AccountWindow : Window
{
    private readonly AccountService? _accounts;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _presenting;

    /// <summary>Design-time constructor; the form stays disabled without an account service.</summary>
    public AccountWindow() => InitializeComponent();

    public AccountWindow(AccountService accounts) : this()
    {
        _accounts = accounts;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _lifetime.Cancel(); ClearPasswords(); };
    }

    private bool Register => ModeBox.SelectedIndex == 1;

    private async Task LoadAsync()
    {
        if (_accounts is null) return;
        SetBusy(true);
        try { Present(await _accounts.GetStatusAsync(true, _lifetime.Token)); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token)); }
            catch (Exception fallback) when (fallback is not OutOfMemoryException) { }
            StatusText.Text = UiText.Localize(exception.Message);
        }
        finally { SetBusy(false); }
    }

    private void Present(AccountStatus status)
    {
        _presenting = true;
        try { RegionBox.SelectedIndex = (int)status.Region; }
        finally { _presenting = false; }
        RestartText.IsVisible = status.RequiresDataRestart;
        ServerUrlBox.Text = status.ServerUrl;
        AccountPanel.IsVisible = status.IsLoggedIn;
        SignInPanel.IsVisible = !status.IsLoggedIn;
        SubmitButton.IsVisible = !status.IsLoggedIn;
        SignOutButton.IsVisible = status.IsLoggedIn;
        UsernameText.Text = status.User?.Username;
        ServerText.Text = UiText.Get(AccountRegions.DisplayName(status.Region)) + " · " + status.ServerUrl;
        ExpiryText.Text = status.ExpiresAt is { } expiry ? UiText.Get("会话有效至 {0:yyyy/MM/dd}", expiry.LocalDateTime) : "";
        if (status.IsLoggedIn) ClearPasswords();
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        RegionPanel.IsEnabled = !value;
        SignInPanel.IsEnabled = !value;
        SubmitButton.IsEnabled = !value;
        SignOutButton.IsEnabled = !value;
    }

    private async void OnRegionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_presenting || _busy || RegionPanel is null || _accounts is null || RegionBox.SelectedIndex < 0) return;
        SetBusy(true);
        ClearPasswords();
        UsernameBox.Text = "";
        StatusText.Text = "";
        try
        {
            Present(await _accounts.SelectRegionAsync((AccountRegion)RegionBox.SelectedIndex, _lifetime.Token));
            Present(await _accounts.GetStatusAsync(true, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConfirmPasswordPanel is null) return;
        ConfirmPasswordPanel.IsVisible = Register;
        SubmitButton.Content = UiText.Get(Register ? "注册并登录" : "登录");
        ClearPasswords();
        StatusText.Text = "";
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if (Register && PasswordBox.Text != ConfirmPasswordBox.Text)
        { StatusText.Text = UiText.Get("两次输入的密码不一致。"); return; }
        SetBusy(true);
        StatusText.Text = UiText.Get(Register ? "正在注册…" : "正在登录…");
        try
        {
            Present(await _accounts.SignInAsync(ServerUrlBox.Text ?? "", UsernameBox.Text ?? "", PasswordBox.Text ?? "", Register, _lifetime.Token));
            StatusText.Text = UiText.Get("登录成功。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        try
        {
            var revoked = await _accounts.SignOutAsync(_lifetime.Token);
            Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token));
            StatusText.Text = UiText.Get(revoked ? "已退出登录。" : "已在本机退出；服务器暂时不可用，远端会话将在到期后失效。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ClearPasswords() { PasswordBox.Text = ""; ConfirmPasswordBox.Text = ""; }
}
