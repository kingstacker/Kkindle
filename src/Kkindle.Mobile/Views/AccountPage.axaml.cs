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
    private AccountStatus? _status;
    private DeviceFeatureStatus? _memberStatus;
    private Guid _activationRequestId;
    private string? _activationAttemptCode;

    public AccountPage() => InitializeComponent();

    public AccountPage(AccountService accounts, MobileShell shell, DeviceMembershipService? membership = null)
    {
        _accounts = accounts;
        _membership = membership;
        _shell = shell;
        InitializeComponent();
        accounts.DeviceInvalidated += OnDeviceInvalidated;
        ProductsBox.SelectionChanged += (_, _) => PresentProduct();
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
        if (_status?.User?.Id != status.User?.Id || _status?.Region != status.Region)
        { ClearActivationCode(); _memberStatus = null; }
        _status = status;
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
        UpdateActivationButton();
    }

    private async void OnRegionClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        var region = sender is Button { Name: nameof(InternationalRegionButton) } ? AccountRegion.International : AccountRegion.China;
        SetBusy(true);
        ClearPasswords();
        ClearActivationCode();
        UsernameBox.Text = "";
        RegistrationEmailBox.Text = "";
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
        RegistrationEmailPanel.IsVisible = _register;
        LoginIdentifierLabel.Text = _register ? "用户名" : "用户名或邮箱";
        UsernameHintText.Text = _register ? "3–32 位字母、数字、点、下划线或连字符" : "使用用户名或邮箱登录同一个账号";
        UsernameBox.MaxLength = _register ? 32 : 254;
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
                _register, _lifetime.Token, _register ? RegistrationEmailBox.Text : null, _register ? ConfirmPasswordBox.Text : null));
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
    public void OnClosed() { if (_accounts is not null) _accounts.DeviceInvalidated -= OnDeviceInvalidated; _lifetime.Cancel(); ClearPasswords(); ClearActivationCode(); }

    private async Task LoadMembershipAsync()
    {
        if (_membership is null || !MembershipPanel.IsVisible) return;
        try
        {
            var status = await _membership.RefreshAsync(_lifetime.Token); var member = status.Membership;
            if (_status?.User?.Id != member.AccountId || _status.Region != member.Region) return;
            _memberStatus = status;
            MembershipSummary.Text = $"{MemberEntitlements.TierName(member.Tier)} · 已登录 {member.LoggedInDeviceCount}/{member.DeviceLimit} 台\n"
                + (status.CanUseFeatures ? "自备 S3 / WebDAV 多端同步已解锁" : "Free 不支持多端同步")
                + (status.CanUseAccountCloudSync
                    ? $"\n官方云空间 {MobileFormat.Size(member.StorageBytes)} / {MobileFormat.Size(member.StorageQuota)}"
                        + $"\n已下载 {MobileFormat.Size(member.DownloadBytes)} · 下载剩余 {MobileFormat.Size(member.DownloadRemainingBytes)}"
                        + (member.DownloadCreditBytes > 0 ? $" · 购买余额 {MobileFormat.Size(member.DownloadCreditBytes)}" : "")
                    : "\n官方云空间与下载：未开通（需要 Plus）")
                + (member.ProExpiresAt is { } expiry
                    ? member.Tier == "pro" ? $"\nPlus 有效至 {expiry.LocalDateTime:yyyy/MM/dd}"
                        : "\n" + MemberEntitlements.PlusExpiryMessage(expiry, DateTimeOffset.UtcNow)
                    : "");
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
            ProductsBox.SelectedIndex = products.Length > 0 ? 0 : -1; PresentProduct();
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
        if (_busy || _membership is null || !PurchaseButton.IsEnabled || ProductsBox.SelectedItem is not MembershipProduct product) return;
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

    private void PresentProduct()
    {
        var product = ProductsBox.SelectedItem as MembershipProduct;
        ProductDescription.Text = product is null ? "" : $"{product.PriceMinor/100m:0.00} {product.Currency} · {product.Description}";
        PurchaseButton.IsEnabled = product is not null && (product.Kind != "pro" || _memberStatus is { } status
            && MemberEntitlements.CanExtendPlus(status.Membership, product.DurationDays, DateTimeOffset.UtcNow));
    }

    private void OnActivationCodeChanged(object? sender, TextChangedEventArgs e)
    { if (ActivationRedeemButton is not null) UpdateActivationButton(); }

    private void UpdateActivationButton() => ActivationRedeemButton.IsEnabled = !_busy && _membership is not null
        && _status?.IsLoggedIn == true && !string.IsNullOrWhiteSpace(ActivationCodeBox.Text);

    private void ClearActivationCode()
    {
        ActivationCodeBox.Text = ""; _activationAttemptCode = null; _activationRequestId = Guid.Empty;
        ActivationStatusText.Text = ""; ActivationStatusText.IsVisible = false;
    }

    private async void OnActivationRedeemClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || _status?.IsLoggedIn != true) return;
        var code = ActivationCodeBox.Text?.Trim() ?? "";
        if (code.Length == 0) return;
        var normalized = code.ToUpperInvariant().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (_activationAttemptCode != normalized || _activationRequestId == Guid.Empty)
        { _activationAttemptCode = normalized; _activationRequestId = Guid.NewGuid(); }
        SetBusy(true);
        ActivationStatusText.Text = UiText.Get("正在兑换激活码…"); ActivationStatusText.IsVisible = true;
        try
        {
            var result = await _membership.RedeemActivationCodeAsync(code, _activationRequestId, _lifetime.Token);
            if (_status?.User?.Id != result.Status.Membership.AccountId || _status.Region != result.Status.Membership.Region) return;
            _memberStatus = result.Status;
            ClearActivationCode(); await LoadMembershipAsync();
            ActivationStatusText.Text = MemberEntitlements.ActivationMessage(result);
            ActivationStatusText.IsVisible = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ActivationStatusText.Text = UiText.Localize(error.Message); ActivationStatusText.IsVisible = true; }
        finally { SetBusy(false); }
    }
}
