using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

public partial class AccountPage : UserControl, IShellPage
{
    private readonly AccountService? _accounts;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _register;
    private bool _busy;

    public AccountPage() => InitializeComponent();

    public AccountPage(AccountService accounts, MobileShell shell)
    {
        _accounts = accounts;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        Loaded += async (_, _) =>
        {
            SetBusy(true);
            try { Present(await accounts.GetStatusAsync(validateSession: true, cancellationToken: _lifetime.Token)); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    try { Present(await accounts.GetStatusAsync(cancellationToken: _lifetime.Token)); }
                    catch (Exception fallback) when (fallback is not OutOfMemoryException) { }
                    ShowStatus(exception.Message);
                }
            }
            finally { SetBusy(false); }
        };
    }

    private void Present(AccountStatus status)
    {
        ChinaRegionButton.Classes.Set("on", status.Region == AccountRegion.China);
        InternationalRegionButton.Classes.Set("on", status.Region == AccountRegion.International);
        RestartText.IsVisible = status.RequiresDataRestart;
        ServerUrlBox.Text = status.ServerUrl;
        AccountPanel.IsVisible = status.IsLoggedIn;
        SignInPanel.IsVisible = !status.IsLoggedIn;
        SubmitButton.IsVisible = !status.IsLoggedIn;
        SignOutButton.IsVisible = status.IsLoggedIn;
        UsernameText.Text = status.User?.Username;
        ServerText.Text = AccountRegions.DisplayName(status.Region) + " · " + status.ServerUrl;
        ExpiryText.Text = status.ExpiresAt is { } expiry ? $"会话有效至 {expiry.LocalDateTime:yyyy/MM/dd}" : "";
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

    private async void OnRegionClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        var region = sender is Button { Name: nameof(InternationalRegionButton) } ? AccountRegion.International : AccountRegion.China;
        SetBusy(true);
        ClearPasswords();
        UsernameBox.Text = "";
        ShowStatus(null);
        try
        {
            Present(await _accounts.SelectRegionAsync(region, _lifetime.Token));
            Present(await _accounts.GetStatusAsync(validateSession: true, cancellationToken: _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = !string.IsNullOrEmpty(message);
    }

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _register = sender is Button { Name: nameof(RegisterModeButton) };
        LoginModeButton.Classes.Set("on", !_register);
        RegisterModeButton.Classes.Set("on", _register);
        ConfirmPasswordPanel.IsVisible = _register;
        SubmitButton.Content = _register ? "注册并登录" : "登录";
        ClearPasswords();
        ShowStatus(null);
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if (_register && PasswordBox.Text != ConfirmPasswordBox.Text)
        { ShowStatus("两次输入的密码不一致。"); return; }
        SetBusy(true);
        ShowStatus(_register ? "正在注册…" : "正在登录…");
        try
        {
            Present(await _accounts.SignInAsync(ServerUrlBox.Text ?? "", UsernameBox.Text ?? "", PasswordBox.Text ?? "",
                _register, _lifetime.Token));
            ShowStatus("登录成功。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        ShowStatus("正在退出…");
        try
        {
            var revoked = await _accounts.SignOutAsync(_lifetime.Token);
            Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token));
            ShowStatus(revoked ? "已退出登录。" : "已在本机退出；服务器暂时不可用，远端会话将在到期后失效。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ClearPasswords() { PasswordBox.Text = ""; ConfirmPasswordBox.Text = ""; }
    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();
    public void OnClosed() { _lifetime.Cancel(); ClearPasswords(); }
}
