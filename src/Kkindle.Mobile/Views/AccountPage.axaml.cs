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
    private bool _presentingTrial;
    private bool _resetMode;
    private bool _emailCodeSent;
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
        {
            ClearActivationCode();
            _memberStatus = null;
            TrialCard.IsVisible = false;
            _presentingTrial = true;
            try { TrialClaimSwitch.IsChecked = false; } finally { _presentingTrial = false; }
        }
        _status = status;
        if (status.User is null || status.User.EmailVerified) _emailCodeSent = false;
        ChinaRegionButton.Classes.Set("on", status.Region == AccountRegion.China);
        InternationalRegionButton.Classes.Set("on", status.Region == AccountRegion.International);
        RestartText.IsVisible = status.RequiresDataRestart;
        ServerUrlBox.Text = status.ServerUrl;
        AccountPanel.IsVisible = status.IsLoggedIn;
        SignInPanel.IsVisible = !status.IsLoggedIn;
        RegionPanel.IsVisible = !status.IsLoggedIn;
        ApplyResetMode();
        SignOutButton.IsVisible = status.IsLoggedIn;
        MembershipPanel.IsVisible = status.IsLoggedIn && _membership is not null;
        var internationalPlans = status.Region == AccountRegion.International;
        PlansRegionText.Text = "会员方案 · " + AccountRegions.DisplayName(status.Region);
        PlansUnavailableText.IsVisible = internationalPlans;
        PurchasePanel.IsVisible = !internationalPlans;
        UsernameText.Text = status.User?.Username;
        ServerText.Text = AccountRegions.DisplayName(status.Region) + " · " + status.ServerUrl;
        ExpiryText.Text = status.ExpiresAt is { } expiry ? $"会话有效至 {expiry.LocalDateTime:yyyy/MM/dd}" : "";
        if (status.IsLoggedIn) ClearPasswords();
        PresentEmailVerification();
    }

    private void ApplyResetMode()
    {
        ResetPanel.IsVisible = _resetMode;
        SubmitButton.IsVisible = _status?.IsLoggedIn != true && !_resetMode;
        ForgotPasswordButton.IsVisible = _status?.IsLoggedIn != true && !_register && !_resetMode;
        ConfirmPasswordPanel.IsVisible = _register && !_resetMode;
        RegistrationEmailPanel.IsVisible = _register && !_resetMode;
    }

    private void PresentEmailVerification()
    {
        var user = _status?.User;
        var available = _status?.IsLoggedIn == true && user?.Email is not null;
        var verified = user?.EmailVerified == true;
        EmailVerifiedText.Text = available
            ? (verified ? "注册邮箱已验证。" : "注册邮箱尚未验证。验证后可找回密码。")
            : "此账号未设置注册邮箱，无法验证或找回密码。";
        SendEmailCodeButton.IsVisible = available && !verified && !_emailCodeSent;
        EmailCodePanel.IsVisible = available && !verified && _emailCodeSent;
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
        _resetMode = false;
        _emailCodeSent = false;
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
        _resetMode = false;
        LoginModeButton.Classes.Set("on", !_register);
        RegisterModeButton.Classes.Set("on", _register);
        ApplyResetMode();
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

    private async void OnForgotPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null || _register) return;
        var identifier = UsernameBox.Text?.Trim() ?? "";
        if (!AccountValidation.IsLogin(identifier)) { ShowStatus("请输入有效的用户名或邮箱。"); return; }
        SetBusy(true);
        ShowStatus("正在发送验证码…");
        try
        {
            await _accounts.RequestPasswordResetAsync(ServerUrlBox.Text ?? "", identifier, _lifetime.Token);
            _resetMode = true;
            ApplyResetMode();
            ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
            ShowStatus("若该账号已绑定邮箱，验证码已发送（15 分钟内有效）。请查收邮件并设置新密码。");
            ResetCodeBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnResetSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if (ResetPasswordBox.Text != ResetConfirmPasswordBox.Text)
        { ShowStatus("两次输入的密码不一致。"); return; }
        SetBusy(true);
        ShowStatus("正在重置密码…");
        try
        {
            var user = await _accounts.ResetPasswordAsync(ServerUrlBox.Text ?? "", UsernameBox.Text ?? "",
                ResetCodeBox.Text ?? "", ResetPasswordBox.Text ?? "", ResetConfirmPasswordBox.Text ?? "", _lifetime.Token);
            UsernameBox.Text = user.Username;
            _resetMode = false;
            ApplyResetMode();
            ClearPasswords();
            ShowStatus("密码已重置，请使用新密码登录。");
            PasswordBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { SetBusy(false); }
    }

    private void OnResetCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _resetMode = false;
        ApplyResetMode();
        ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
        ShowStatus("已返回登录。");
    }

    private async void OnSendEmailCodeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        ShowStatus("正在发送邮箱验证码…");
        try
        {
            await _accounts.RequestEmailVerificationAsync(_lifetime.Token);
            _emailCodeSent = true;
            EmailCodeBox.Text = "";
            PresentEmailVerification();
            EmailCodeBox.Focus();
            ShowStatus("验证码已发送至注册邮箱（15 分钟内有效）。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnConfirmEmailCodeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        ShowStatus("正在验证邮箱…");
        try
        {
            Present(await _accounts.ConfirmEmailVerificationAsync(EmailCodeBox.Text ?? "", _lifetime.Token));
            EmailCodeBox.Text = "";
            ShowStatus("邮箱验证成功。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowStatus(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ClearPasswords()
    {
        PasswordBox.Text = ""; ConfirmPasswordBox.Text = "";
        ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
    }
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
                        + (member.PlusDownloadQuotaBytes is { } annualQuota && member.PlusDownloadUsedBytes is { } annualUsed
                            ? $"\n本会员年度 {MobileFormat.Size(annualUsed)} / {MobileFormat.Size(annualQuota)}"
                                + $" · 剩余 {MobileFormat.Size(Math.Max(0, annualQuota - annualUsed))}"
                            : "")
                        + (member.PlusEarlyDownloadQuotaBytes is { } earlyQuota
                            && member.PlusEarlyDownloadEndsAt is { } earlyEnd && earlyEnd > DateTimeOffset.UtcNow
                            ? $"\n新用户限额 {MobileFormat.Size(member.PlusEarlyDownloadUsedBytes ?? 0)} / {MobileFormat.Size(earlyQuota)}"
                                + $" · 截止 {earlyEnd.LocalDateTime:yyyy/MM/dd HH:mm}"
                            : "")
                        + (member.MetadataDownloadQuotaBytes is { } metadataQuota && member.MetadataDownloadUsedBytes is { } metadataUsed
                            ? $"\n今日元数据流量 {MobileFormat.Size(metadataUsed)} / {MobileFormat.Size(metadataQuota)}（UTC 零点重置）"
                            : "")
                    : "\n官方云空间与下载：未开通（需要 Plus）")
                + (member.ProExpiresAt is { } expiry
                    ? member.Tier == "pro"
                        ? $"\nPlus 有效至 {expiry.LocalDateTime:yyyy/MM/dd}"
                            + (MemberEntitlements.PlusRenewalReminder(expiry, DateTimeOffset.UtcNow) is not null
                                ? "\n" + MemberEntitlements.PlusRenewalMessage(expiry, DateTimeOffset.UtcNow, member.TrialExpiresAt) : "")
                        : "\n" + MemberEntitlements.PlusExpiryMessage(expiry, DateTimeOffset.UtcNow, member.TrialExpiresAt)
                    : "");
            PresentTrial(member);
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
    private void PresentTrial(AccountMembership member)
    {
        var active = member.TrialClaimed && member.TrialExpiresAt is { } until && until > DateTimeOffset.UtcNow;
        TrialCard.IsVisible = member.TrialAvailable || active;
        _presentingTrial = true;
        try
        {
            TrialClaimSwitch.IsChecked = member.TrialClaimed;
            TrialClaimSwitch.IsEnabled = member.TrialAvailable;
        }
        finally { _presentingTrial = false; }
        TrialStatusText.IsVisible = active;
        TrialStatusText.Text = active
            ? $"试用期至 {member.TrialExpiresAt!.Value.LocalDateTime:yyyy/MM/dd}，到期后云端数据保留 7 天；可随时开通正式 Plus 继续使用。"
            : "";
    }

    private async void OnTrialClaimChanged(object? sender, RoutedEventArgs e)
    {
        if (_presentingTrial || _busy || _membership is null || _status?.IsLoggedIn != true
            || TrialClaimSwitch.IsChecked != true) return;
        SetBusy(true);
        MembershipStatus.Text = "正在领取免费试用…";
        try
        {
            var member = await _membership.ClaimTrialAsync(_lifetime.Token);
            if (_status?.User?.Id != member.AccountId || _status.Region != member.Region) return;
            await LoadMembershipAsync();
            MembershipStatus.Text = "Plus 免费试用已开通，7 天内可使用云同步、下载和自备存储同步。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _presentingTrial = true;
            try { TrialClaimSwitch.IsChecked = false; } finally { _presentingTrial = false; }
            MembershipStatus.Text = UiText.Localize(error.Message);
        }
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
