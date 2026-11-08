using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

public partial class AccountPage : UserControl, IShellPage
{
    private readonly AccountService? _accounts;
    private readonly DeviceMembershipService? _membership;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _register;
    private bool _busy;

    public AccountPage() => InitializeComponent();

    public AccountPage(AccountService accounts, MobileShell shell, DeviceMembershipService? membership = null)
    {
        _accounts = accounts;
        _membership = membership;
        _shell = shell;
        InitializeComponent();
        accounts.DeviceInvalidated += OnDeviceInvalidated;
        ProductsBox.SelectionChanged += (_, _) => ProductDescription.Text = ProductsBox.SelectedItem is MembershipProduct product
            ? $"{product.PriceMinor/100m:0.00} {product.Currency} · {product.Description}" : "";
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        Loaded += async (_, _) =>
        {
            SetBusy(true);
            try { Present(await accounts.GetStatusAsync(validateSession: true, cancellationToken: _lifetime.Token)); await LoadMembershipAsync(); }
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

    private void OnDeviceInvalidated(object? sender, AccountDeviceInvalidatedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetime.IsCancellationRequested) return;
            try { Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); ShowStatus(e.Message); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });

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
        MembershipPanel.IsVisible = status.IsLoggedIn && _membership is not null;
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
        MembershipPanel.IsEnabled = !value;
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
            await LoadMembershipAsync();
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
            await LoadMembershipAsync();
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
    public void OnClosed() { if (_accounts is not null) _accounts.DeviceInvalidated -= OnDeviceInvalidated; _lifetime.Cancel(); ClearPasswords(); }

    private async Task LoadMembershipAsync()
    {
        if (_membership is null || !MembershipPanel.IsVisible) return;
        try
        {
            var status = await _membership.RefreshAsync(_lifetime.Token); var member = status.Membership;
            MembershipSummary.Text = $"{MemberEntitlements.TierName(member.Tier)} · 已登录 {member.LoggedInDeviceCount}/5 台\n"
                + (member.HasLifetimeFeatures ? "已永久拥有自定义 S3 和自定义 AI 功能" : "自定义 S3 和自定义 AI 需功能会员；其他功能免费")
                + $"\n空间 {MobileFormat.Size(member.StorageBytes)} / {MobileFormat.Size(member.StorageQuota)}"
                + $"\n上传 {MobileFormat.Size(member.UploadBytes)} / {MobileFormat.Size(member.UploadQuota)} · 已下载 {MobileFormat.Size(member.DownloadBytes)}"
                + $"\n下载剩余 {MobileFormat.Size(member.DownloadRemainingBytes)} · 购买余额 {MobileFormat.Size(member.DownloadCreditBytes)}"
                + (member.ProExpiresAt is { } expiry ? $"\nPro 有效至 {expiry.LocalDateTime:yyyy/MM/dd}" : "");
            DeviceListPanel.Children.Clear();
            foreach (var device in status.Devices.Where(item => item.State == "active"))
            {
                var row = new StackPanel { Spacing = 5 };
                row.Children.Add(new TextBlock { Text = device.Name + (device.DeviceId == status.CurrentDeviceId ? "（当前设备）" : ""), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = $"{device.Platform} · 最近访问 {device.LastSeenAt.LocalDateTime:MM/dd HH:mm}", FontSize = 11 });
                var button = new Button { Content = "踢出设备", Tag = device.DeviceId }; button.Classes.Add("secondary"); button.Click += OnKickDeviceClick;
                row.Children.Add(button); DeviceListPanel.Children.Add(row);
            }
            var products = await _membership.ProductsAsync(_lifetime.Token);
            ProductsBox.ItemsSource = products;
            ProductsBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MembershipProduct>((product, _) =>
                new TextBlock { Text = product?.Name, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            ProductsBox.SelectedIndex = products.Length > 0 ? 0 : -1; PurchaseButton.IsEnabled = products.Length > 0;
            OrderListPanel.Children.Clear();
            foreach (var order in await _membership.OrdersAsync(_lifetime.Token))
            {
                var row = new StackPanel { Spacing = 5 };
                row.Children.Add(new TextBlock { Text = $"{order.ProductName} · {order.PriceMinor/100m:0.00} {order.Currency}\n"
                    + (order.State == "fulfilled" ? "已到账" : order.State == "cancelled" ? "已取消" : order.ExpiresAt <= DateTimeOffset.UtcNow ? "已过期" : "待确认付款 · " + order.Id), FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                if (order.State == "pending" && order.ExpiresAt > DateTimeOffset.UtcNow) { var button = new Button { Content = "取消订单", Tag = order.Id }; button.Classes.Add("secondary"); button.Click += OnCancelOrderClick; row.Children.Add(button); }
                OrderListPanel.Children.Add(row);
            }
            MembershipStatus.Text = "";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { MembershipStatus.Text = UiText.Localize(error.Message); }
    }

    private async void OnMembershipRefreshClick(object? sender, RoutedEventArgs e)
    { if (_busy) return; SetBusy(true); try { await LoadMembershipAsync(); } finally { SetBusy(false); } }
    private async void OnKickDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { Tag: Guid device }) return;
        if (_shell is not null && !await _shell.ConfirmAsync("踢出设备", "此设备将退出登录并释放名额。", "踢出")) return;
        SetBusy(true);
        try { await _membership.KickAsync(device, _lifetime.Token); Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); await LoadMembershipAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { MembershipStatus.Text = UiText.Localize(error.Message); }
        finally { SetBusy(false); }
    }
    private async void OnPurchaseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || ProductsBox.SelectedItem is not MembershipProduct product) return;
        SetBusy(true);
        try { await _membership.PurchaseAsync(product.Id, Guid.NewGuid(), _lifetime.Token); await LoadMembershipAsync(); MembershipStatus.Text = "订单已创建，等待确认付款。到账后刷新即可使用。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { MembershipStatus.Text = UiText.Localize(error.Message); }
        finally { SetBusy(false); }
    }
    private async void OnCancelOrderClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { Tag: Guid order }) return;
        SetBusy(true);
        try { await _membership.CancelOrderAsync(order, _lifetime.Token); await LoadMembershipAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { MembershipStatus.Text = UiText.Localize(error.Message); }
        finally { SetBusy(false); }
    }
}
