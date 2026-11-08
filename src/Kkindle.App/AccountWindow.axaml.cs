using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class AccountWindow : Window
{
    private readonly AccountService? _accounts;
    private readonly DeviceMembershipService? _membership;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _presenting;

    /// <summary>Design-time constructor; the form stays disabled without an account service.</summary>
    public AccountWindow() => InitializeComponent();

    public AccountWindow(AccountService accounts, DeviceMembershipService? membership = null) : this()
    {
        _accounts = accounts;
        _membership = membership;
        accounts.DeviceInvalidated += OnDeviceInvalidated;
        ProductsBox.SelectionChanged += (_, _) => ProductDescription.Text = ProductsBox.SelectedItem is MembershipProduct product
            ? $"{product.PriceMinor/100m:0.00} {product.Currency} · {product.Description}" : "";
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => { accounts.DeviceInvalidated -= OnDeviceInvalidated; _lifetime.Cancel(); ClearPasswords(); };
    }

    private void OnDeviceInvalidated(object? sender, AccountDeviceInvalidatedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetime.IsCancellationRequested) return;
            try { Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); StatusText.Text = UiText.Localize(e.Message); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });

    private bool Register => ModeBox.SelectedIndex == 1;

    private async Task LoadAsync()
    {
        if (_accounts is null) return;
        SetBusy(true);
        try { Present(await _accounts.GetStatusAsync(true, _lifetime.Token)); await LoadMembershipAsync(); }
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
        MembershipPanel.IsVisible = status.IsLoggedIn && _membership is not null;
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
        MembershipPanel.IsEnabled = !value;
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
            await LoadMembershipAsync();
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
            await LoadMembershipAsync();
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

    private async Task LoadMembershipAsync()
    {
        if (_membership is null || !MembershipPanel.IsVisible) return;
        try
        {
            var status = await _membership.RefreshAsync(_lifetime.Token);
            var member = status.Membership;
            MembershipSummary.Text = $"{MemberEntitlements.TierName(member.Tier)} · 已登录 {member.LoggedInDeviceCount}/5 台\n"
                + (member.HasLifetimeFeatures ? "已永久拥有自定义 S3 和自定义 AI 功能" : "自定义 S3 和自定义 AI 需购买功能会员；其他功能免费")
                + $"\n空间 {MemberSize(member.StorageBytes)} / {MemberSize(member.StorageQuota)}"
                + $"\n上传 {MemberSize(member.UploadBytes)} / {MemberSize(member.UploadQuota)} · 已下载 {MemberSize(member.DownloadBytes)}"
                + $"\n下载剩余 {MemberSize(member.DownloadRemainingBytes)} · 购买余额 {MemberSize(member.DownloadCreditBytes)}"
                + (member.ProExpiresAt is { } expiry ? $"\nPro 有效至 {expiry.LocalDateTime:yyyy/MM/dd}" : "");
            DeviceListPanel.Children.Clear();
            foreach (var device in status.Devices.Where(item => item.State == "active"))
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = device.Name + (device.DeviceId == status.CurrentDeviceId ? "（当前设备）" : ""), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = $"{device.Platform} · 最近访问 {device.LastSeenAt.LocalDateTime:MM/dd HH:mm}", FontSize = 11 });
                var button = new Button { Content = "踢出设备", Tag = device.DeviceId };
                button.Click += OnKickDeviceClick; row.Children.Add(button); DeviceListPanel.Children.Add(row);
            }
            var products = await _membership.ProductsAsync(_lifetime.Token);
            ProductsBox.ItemsSource = products;
            ProductsBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(MembershipProduct.Name));
            ProductsBox.SelectedIndex = products.Length > 0 ? 0 : -1; PurchaseButton.IsEnabled = products.Length > 0;
            OrderListPanel.Children.Clear();
            foreach (var order in await _membership.OrdersAsync(_lifetime.Token))
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = $"{order.ProductName} · {order.PriceMinor/100m:0.00} {order.Currency}\n"
                    + (order.State == "fulfilled" ? "已到账" : order.State == "cancelled" ? "已取消" : order.ExpiresAt <= DateTimeOffset.UtcNow ? "已过期" : "待确认付款 · " + order.Id), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 });
                if (order.State == "pending" && order.ExpiresAt > DateTimeOffset.UtcNow) { var cancel = new Button { Content = "取消订单", Tag = order.Id }; cancel.Click += OnCancelOrderClick; row.Children.Add(cancel); }
                OrderListPanel.Children.Add(row);
            }
            MembershipStatus.Text = "";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { MembershipStatus.Text = UiText.Localize(error.Message); }
    }

    private static string MemberSize(long bytes) => bytes >= 1073741824 ? $"{bytes/1073741824d:0.##} GiB" : bytes >= 1048576 ? $"{bytes/1048576d:0.##} MiB" : $"{bytes} B";
    private async void OnMembershipRefreshClick(object? sender, RoutedEventArgs e)
    { if (_busy) return; SetBusy(true); try { await LoadMembershipAsync(); } finally { SetBusy(false); } }
    private async void OnKickDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { Tag: Guid device }) return;
        SetBusy(true);
        try { await _membership.KickAsync(device, _lifetime.Token); Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); await LoadMembershipAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { MembershipStatus.Text = UiText.Localize(error.Message); }
        finally { SetBusy(false); }
    }
    private async void OnPurchaseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || ProductsBox.SelectedItem is not MembershipProduct product) return;
        SetBusy(true);
        try { await _membership.PurchaseAsync(product.Id, Guid.NewGuid(), _lifetime.Token); await LoadMembershipAsync(); MembershipStatus.Text = "订单已创建，等待确认付款。权益到账后刷新即可使用。"; }
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
