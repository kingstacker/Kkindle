using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Kkindle;

public partial class AccountCenterView : UserControl, IDisposable
{
    private readonly AccountService? _accounts;
    private readonly DeviceMembershipService? _membership;
    private readonly CancellationTokenSource _lifetime = new();
    private AccountStatus? _status;
    private DeviceFeatureStatus? _memberStatus;
    private MembershipProduct[] _products = [];
    private PurchaseOrder[] _orders = [];
    private bool _busy;
    private bool _presenting;
    private bool _resetMode;
    private bool _emailCodeSent;
    private Guid _activationRequestId;
    private string? _activationAttemptCode;
    private Task? _refreshTask;

    internal event EventHandler<AccountStateChangedEventArgs>? AccountStateChanged;
    internal event EventHandler<AccountStateChangedEventArgs>? AccountSignedIn;

    /// <summary>Design-time constructor; the form stays disabled without an account service.</summary>
    public AccountCenterView()
    {
        InitializeComponent();
        PresentPlanBenefits();
        MembershipAvatarIcon.Data = AccountVisuals.User;
        MembershipStatus.IsVisible = false;
    }

    public AccountCenterView(AccountService accounts, DeviceMembershipService? membership = null) : this()
    {
        _accounts = accounts;
        _membership = membership;
        accounts.DeviceInvalidated += OnDeviceInvalidated;
        UiText.LanguageChanged += OnLanguageChanged;
        AttachedToVisualTree += async (_, _) => await RefreshAsync();
        DetachedFromVisualTree += (_, _) => Deactivate();
    }

    internal Task RefreshAsync()
    {
        if (_lifetime.IsCancellationRequested || _accounts is null) return Task.CompletedTask;
        if (_busy) return _refreshTask is { IsCompleted: false } refreshing ? refreshing : Task.CompletedTask;
        return _refreshTask = LoadAsync();
    }

    internal void Deactivate()
    {
        ClearPasswords();
        ClearActivationCode();
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_accounts is not null) _accounts.DeviceInvalidated -= OnDeviceInvalidated;
        UiText.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        Deactivate();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime.IsCancellationRequested) return;
        PresentPlanBenefits();
        if (_status is { } status) Present(status);
        if (_memberStatus is { } member) PresentMembership(member);
        PresentLoginMode();
        PresentProducts();
        PresentOrders();
    });

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
        if (_status?.User?.Id != status.User?.Id || _status?.Region != status.Region)
        {
            _memberStatus = null;
            _products = [];
            _orders = [];
            CloudUsagePanel.IsVisible = false;
            CloudLockedPanel.IsVisible = false;
            ExpiryNoticeBorder.IsVisible = false;
            DeviceListPanel.Children.Clear();
            DeviceSlotsPanel.Children.Clear();
            DeviceCountText.Text = "—";
            DeviceCapacityText.Text = "";
            TrialCard.IsVisible = false;
            _presenting = true;
            try { TrialClaimToggle.IsChecked = false; } finally { _presenting = false; }
            SetMembershipMessage("");
            ClearPasswords();
            ClearActivationCode();
            ProfileExpander.IsExpanded = false;
            PasswordExpander.IsExpanded = false;
            OrdersExpander.IsExpanded = false;
            AccountScrollViewer.ScrollToHome();
            PresentProducts();
            PresentOrders();
        }
        _status = status;
        if (status.IsLoggedIn) _resetMode = false;
        if (status.User is null || status.User.EmailVerified) _emailCodeSent = false;
        _presenting = true;
        try { RegionBox.SelectedIndex = (int)status.Region; }
        finally { _presenting = false; }
        RestartText.IsVisible = status.RequiresDataRestart;
        AccountCard.IsVisible = status.IsLoggedIn;
        AccountPanel.IsVisible = status.IsLoggedIn;
        AccountSettingsCard.IsVisible = status.IsLoggedIn;
        SignInPanel.IsVisible = !status.IsLoggedIn;
        ApplyResetMode();
        SignOutButton.IsVisible = status.IsLoggedIn;
        MembershipPanel.IsVisible = status.IsLoggedIn && _membership is not null;
        UsernameText.Text = status.User?.Username;
        AccountRegionText.Text = UiText.Get(AccountRegions.DisplayName(status.Region));
        ProfileUsernameBox.Text = status.User?.Username ?? "";
        ProfileEmailBox.Text = status.User?.Email ?? "";
        if (_memberStatus is null)
        {
            SetMembershipAppearance("free");
            TierBadgeText.Text = UiText.Get("已登录");
            MembershipExpiryText.Text = "";
            MembershipSummary.Text = UiText.Get(_membership is null ? "账户已登录" : "正在读取会员信息…");
            MembershipHintText.Text = "";
        }
        if (status.IsLoggedIn) ClearPasswords();
        PresentEmailVerification();
        PublishAccountState();
    }

    private void ApplyResetMode()
    {
        ResetPanel.IsVisible = _resetMode;
        SubmitButton.IsVisible = !_resetMode && _status?.IsLoggedIn != true;
        ForgotPasswordButton.IsVisible = !_resetMode;
        LoginPasswordPanel.IsVisible = !_resetMode;
        ConfirmPasswordPanel.IsVisible = !_resetMode && Register;
        RegistrationEmailPanel.IsVisible = !_resetMode && Register;
        ModeBox.IsEnabled = !_resetMode;
    }

    private void PresentEmailVerification()
    {
        var user = _status?.User;
        var available = _status?.IsLoggedIn == true && user?.Email is not null;
        var verified = user?.EmailVerified == true;
        EmailVerifiedText.Text = available
            ? UiText.Get(verified ? "注册邮箱已验证。" : "注册邮箱尚未验证。验证后可找回密码。")
            : UiText.Get("此账号未设置注册邮箱，无法验证或找回密码。");
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
        AccountSettingsCard.IsEnabled = !value;
        MembershipRefreshButton.IsEnabled = !value && _accounts is not null;
        LoadingIndicator.IsVisible = value;
        UpdateActivationButton();
    }

    private async void OnRegionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_presenting || _busy || RegionPanel is null || _accounts is null || RegionBox.SelectedIndex < 0) return;
        SetBusy(true);
        _resetMode = false;
        _emailCodeSent = false;
        ClearPasswords();
        ClearActivationCode();
        UsernameBox.Text = "";
        RegistrationEmailBox.Text = "";
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
        PresentLoginMode();
        ApplyResetMode();
        SubmitButton.Content = UiText.Get(Register ? "注册并登录" : "登录");
        ClearPasswords();
        StatusText.Text = "";
    }

    private void PresentLoginMode()
    {
        SignInHeading.IsVisible = !Register;
        SignInSubtitle.IsVisible = !Register;
        RegisterHeading.IsVisible = Register;
        RegisterSubtitle.IsVisible = Register;
        RegistrationEmailPanel.IsVisible = Register;
        LoginIdentifierLabel.Text = UiText.Get(Register ? "用户名" : "用户名或邮箱");
        UsernameHintText.Text = UiText.Get(Register ? "3–32 位字母、数字、点、下划线或连字符" : "使用用户名或邮箱登录同一个账号");
        UsernameBox.MaxLength = Register ? 32 : 254;
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
            Present(await _accounts.SignInAsync(_status?.ServerUrl ?? "", UsernameBox.Text ?? "", PasswordBox.Text ?? "", Register, _lifetime.Token,
                Register ? RegistrationEmailBox.Text : null, Register ? ConfirmPasswordBox.Text : null));
            StatusText.Text = UiText.Get("登录成功。");
            await LoadMembershipAsync();
            if (_status?.IsLoggedIn == true && _memberStatus is { } member)
                AccountSignedIn?.Invoke(this, new(_status, member.Membership));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnForgotPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null || Register) return;
        var identifier = (UsernameBox.Text ?? "").Trim();
        if (!AccountValidation.IsLogin(identifier))
        { StatusText.Text = UiText.Get("请输入有效的用户名或邮箱。"); return; }
        SetBusy(true);
        StatusText.Text = UiText.Get("正在发送验证码…");
        try
        {
            await _accounts.RequestPasswordResetAsync(_status?.ServerUrl ?? "", identifier, _lifetime.Token);
            _resetMode = true;
            ApplyResetMode();
            ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
            StatusText.Text = UiText.Get("若该账号已绑定邮箱，验证码已发送（15 分钟内有效）。请查收邮件并设置新密码。");
            ResetCodeBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnResetSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if ((ResetPasswordBox.Text ?? "") != (ResetConfirmPasswordBox.Text ?? ""))
        { StatusText.Text = UiText.Get("两次输入的密码不一致。"); return; }
        SetBusy(true);
        StatusText.Text = UiText.Get("正在重置密码…");
        try
        {
            var user = await _accounts.ResetPasswordAsync(_status?.ServerUrl ?? "", UsernameBox.Text ?? "",
                ResetCodeBox.Text ?? "", ResetPasswordBox.Text ?? "", ResetConfirmPasswordBox.Text ?? "", _lifetime.Token);
            UsernameBox.Text = user.Username;
            _resetMode = false;
            ApplyResetMode();
            ClearPasswords();
            StatusText.Text = UiText.Get("密码已重置，请使用新密码登录。");
            PasswordBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private void OnResetCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _resetMode = false;
        ApplyResetMode();
        ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
        StatusText.Text = UiText.Get("已返回登录。");
    }

    private async void OnSendEmailCodeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        StatusText.Text = UiText.Get("正在发送邮箱验证码…");
        try
        {
            await _accounts.RequestEmailVerificationAsync(_lifetime.Token);
            _emailCodeSent = true;
            EmailCodeBox.Text = "";
            PresentEmailVerification();
            EmailCodeBox.Focus();
            StatusText.Text = UiText.Get("验证码已发送至注册邮箱（15 分钟内有效）。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnConfirmEmailCodeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        StatusText.Text = UiText.Get("正在验证邮箱…");
        try
        {
            Present(await _accounts.ConfirmEmailVerificationAsync(EmailCodeBox.Text ?? "", _lifetime.Token));
            EmailCodeBox.Text = "";
            StatusText.Text = UiText.Get("邮箱验证成功。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
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

    private void ClearPasswords()
    {
        PasswordBox.Text = ""; ConfirmPasswordBox.Text = ""; ProfilePasswordBox.Text = "";
        CurrentPasswordBox.Text = ""; NewPasswordBox.Text = ""; ConfirmNewPasswordBox.Text = "";
        ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
    }

    private async void OnProfileSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        try
        {
            Present(await _accounts.UpdateProfileAsync(ProfileUsernameBox.Text ?? "", ProfilePasswordBox.Text ?? "", _lifetime.Token));
            StatusText.Text = UiText.Get("账号资料已更新。");
            ProfileExpander.IsExpanded = false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { StatusText.Text = UiText.Localize(error.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnPasswordSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if (NewPasswordBox.Text != ConfirmNewPasswordBox.Text) { StatusText.Text = UiText.Get("两次输入的密码不一致。"); return; }
        SetBusy(true);
        try
        {
            Present(await _accounts.ChangePasswordAsync(CurrentPasswordBox.Text ?? "", NewPasswordBox.Text ?? "", _lifetime.Token));
            await LoadMembershipAsync();
            PasswordExpander.IsExpanded = false;
            StatusText.Text = UiText.Get("密码已修改，其他设备已退出登录。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { StatusText.Text = UiText.Localize(error.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnCloseAccountClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new AccountClosureWindow();
        if (!await dialog.ShowDialog<bool>(owner)) return;
        SetBusy(true);
        try
        {
            await _accounts.CloseAccountAsync(dialog.CurrentPassword, "注销账号", _lifetime.Token);
            Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token));
            StatusText.Text = UiText.Get("注销申请已提交，账号和云数据正在删除；本地书库保留。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { StatusText.Text = UiText.Localize(error.Message); }
        finally { dialog.ClearPassword(); ClearPasswords(); SetBusy(false); }
    }

    private async Task LoadMembershipAsync()
    {
        if (_membership is null || !MembershipPanel.IsVisible) return;
        try
        {
            var status = await _membership.RefreshAsync(_lifetime.Token);
            if (_status?.User?.Id != status.Membership.AccountId || _status.Region != status.Membership.Region) return;
            _memberStatus = status;
            PresentMembership(status);
            PublishAccountState();
            SetMembershipMessage("");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (_memberStatus is { } known && _status?.IsLoggedIn == true)
            {
                PresentMembership(known);
                PublishAccountState();
            }
            SetMembershipMessage(UiText.Localize(error.Message));
            return;
        }

        // Usage and devices stay useful even if the separate purchase or order
        // endpoint is unavailable. An unavailable product cannot be ordered.
        try
        {
            _products = await _membership.ProductsAsync(_lifetime.Token);
            PresentProducts();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _products = [];
            PresentProducts();
            SetMembershipMessage(UiText.Localize(error.Message));
        }
        if (!MembershipPanel.IsVisible || _lifetime.IsCancellationRequested) return;
        try
        {
            _orders = await _membership.OrdersAsync(_lifetime.Token);
            PresentOrders();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetMembershipMessage(UiText.Localize(error.Message)); }
    }

    private void PresentMembership(DeviceFeatureStatus status)
    {
        var member = status.Membership;
        var tier = AccountVisuals.Tier(member);
        var cloud = tier == "plus" && status.CanUseAccountCloudSync;
        var now = DateTimeOffset.UtcNow;
        var expired = member.ProExpiresAt is { } expiry && expiry <= now;
        var expiring = tier == "plus" && member.ProExpiresAt is { } renewalAt && renewalAt > now
            && MemberEntitlements.PlusRenewalReminder(renewalAt, now) is not null;
        SetMembershipAppearance(tier);
        TierBadgeText.Text = UiText.Get(tier switch { "plus" => "Plus 会员", "lifetime" => "功能解锁", _ => "Free" });
        MembershipSummary.Text = UiText.Get(tier switch
        {
            "plus" => "Plus 会员 · Kkindle 云同步权益已开通",
            "lifetime" => "功能解锁 · 自备存储同步已解锁",
            _ => "Free · 畅享本地阅读"
        });
        MembershipHintText.Text = UiText.Get(tier switch
        {
            "plus" => "书籍、阅读进度、书签与批注，可通过 Kkindle 云同步在设备之间同步。",
            "lifetime" => "使用自己的 S3 / WebDAV 同步书籍与阅读数据，存储与流量由你掌控。",
            _ => "本地阅读、USB Kindle 同步、插件与自定义 AI 接口永久免费。Free 不含多端同步。"
        });
        MembershipExpiryText.Text = tier == "plus" && member.ProExpiresAt is { } until
            ? UiText.Get("有效至 {0:yyyy/MM/dd}", until.LocalDateTime)
            : UiText.Get(tier == "lifetime" ? "永久有效" : expired ? "Plus 已到期" : "永久免费");
        ExpiryNoticeBorder.IsVisible = expired || expiring;
        ExpiryNoticeText.Text = expired
            ? MemberEntitlements.PlusExpiryMessage(member.ProExpiresAt!.Value, now, member.TrialExpiresAt)
            : expiring ? MemberEntitlements.PlusRenewalMessage(member.ProExpiresAt!.Value, now, member.TrialExpiresAt) : "";
        CloudUsagePanel.IsVisible = cloud;
        CloudLockedPanel.IsVisible = !cloud;
        CloudLockedTitleText.Text = UiText.Get(expired ? "Kkindle 云同步已停用" : "Kkindle 云同步未开通");
        CloudLockedHintText.Text = UiText.Get(tier == "lifetime"
            ? "功能解锁使用自备 S3 / WebDAV；Kkindle 云空间与下载需要 Plus。"
            : "开通 Plus 即可同步书籍与阅读数据，无需配置自备存储。");
        ViewPlusButton.Content = UiText.Get(expired ? "续费 Plus" : "了解 Plus");
        if (cloud) PresentUsage(member);
        PresentDevices(status);
        PresentTrial(member);
        PresentProducts();
    }

    private void PresentTrial(AccountMembership member)
    {
        var active = member.TrialClaimed && member.TrialExpiresAt is { } until && until > DateTimeOffset.UtcNow;
        TrialCard.IsVisible = member.TrialAvailable || active;
        _presenting = true;
        try
        {
            TrialClaimToggle.IsChecked = member.TrialClaimed;
            TrialClaimToggle.IsEnabled = member.TrialAvailable;
        }
        finally { _presenting = false; }
        TrialStatusText.IsVisible = active;
        TrialStatusText.Text = active
            ? UiText.Get("试用期至 {0:yyyy/MM/dd}，到期后云端数据保留 7 天；可随时开通正式 Plus 继续使用。", member.TrialExpiresAt!.Value.LocalDateTime)
            : "";
    }

    private void SetMembershipAppearance(string tier)
    {
        foreach (var control in new Control[] { AvatarBorder, MembershipAvatarIcon, TierBadgeBorder, TierBadgeText })
            AccountVisuals.SetTier(control, tier);
        MembershipAvatarIcon.Data = AccountVisuals.MembershipIcon(tier);
    }

    private void PresentUsage(AccountMembership member)
    {
        var used = Math.Max(0, member.StorageBytes);
        var quota = Math.Max(0, member.StorageQuota);
        var ratio = quota > 0 ? used / (double)quota : used > 0 ? 1 : 0;
        StorageUsageRing.Ratio = Math.Clamp(ratio, 0, 1);
        StorageUsedText.Text = MemberSize(used);
        StorageQuotaText.Text = UiText.Get("共 {0}", MemberSize(quota));
        StoragePercentText.Text = quota > 0 ? Percentage(ratio) : "—";
        StorageRemainingText.Text = used >= quota ? UiText.Get("空间已满") : MemberSize(quota - used);
        Avalonia.Automation.AutomationProperties.SetName(StorageCard,
            UiText.Get("云端空间：已使用 {0}，总空间 {1}", MemberSize(used), MemberSize(quota)));

        var download = Math.Max(0, member.DownloadBytes);
        // Annual usage comes from the active Plus grant, independently of
        // monthly transfer statistics and old credit. Admin adjustments update the same grant total.
        var hasAnnualUsage = member.PlusDownloadQuotaBytes is >= 0 && member.PlusDownloadUsedBytes is >= 0;
        var annualQuota = Math.Max(0, member.PlusDownloadQuotaBytes ?? 0);
        var annualUsed = Math.Max(0, member.PlusDownloadUsedBytes ?? 0);
        var annualRemaining = Math.Max(0, annualQuota - annualUsed);
        var annualRatio = annualQuota > 0 ? annualUsed / (double)annualQuota : annualUsed > 0 ? 1 : 0;
        TrafficUsageRing.Ratio = hasAnnualUsage ? Math.Clamp(annualRatio, 0, 1) : 0;
        TrafficPercentText.Text = hasAnnualUsage ? Percentage(annualRatio) : "—";
        AnnualDownloadUsedText.Text = hasAnnualUsage ? MemberSize(annualUsed) : "—";
        AnnualDownloadQuotaText.Text = hasAnnualUsage ? UiText.Get("共 {0}", MemberSize(annualQuota)) : "—";
        TrafficAllowancePeriodText.Text = !hasAnnualUsage ? UiText.Get("年度用量暂不可用")
            : member.PlusDownloadExpiresAt is { } expires ? UiText.Get("本期有效至 {0:yyyy/MM/dd}", expires.LocalDateTime) : "";
        TrafficAllowancePeriodText.IsVisible = TrafficAllowancePeriodText.Text.Length > 0;
        ToolTip.SetTip(TrafficPeriodText, member.PlusDownloadStartsAt is { } starts && member.PlusDownloadExpiresAt is { } ends
            ? UiText.Get("本期下载额度：{0:yyyy/MM/dd} – {1:yyyy/MM/dd}", starts.LocalDateTime, ends.LocalDateTime) : null);
        DownloadUsedText.Text = MemberSize(download);
        ToolTip.SetTip(DownloadUsedText, UiText.Get("本月下载 {0}", MemberSize(download)));
        DownloadRemainingText.Text = hasAnnualUsage ? MemberSize(annualRemaining) : "—";
        var additional = hasAnnualUsage ? Math.Max(0, member.DownloadRemainingBytes - annualRemaining)
            : Math.Max(0, member.DownloadCreditBytes);
        DownloadCreditText.IsVisible = additional > 0;
        DownloadCreditText.Text = member.DownloadCreditBytes > 0
            ? UiText.Get("额外可用流量 {0}，含旧版购买余额 {1}；不计入年度限额。", MemberSize(additional), MemberSize(Math.Max(0, member.DownloadCreditBytes)))
            : UiText.Get("额外可用流量 {0}，不计入年度限额。", MemberSize(additional));
        var earlyEnds = member.PlusEarlyDownloadEndsAt;
        var earlyQuota = member.PlusEarlyDownloadQuotaBytes;
        var earlyUsed = member.PlusEarlyDownloadUsedBytes ?? 0;
        var earlyActive = earlyEnds is { } end && end > DateTimeOffset.UtcNow && earlyQuota is >= 0;
        EarlyLimitText.IsVisible = earlyActive;
        if (earlyActive)
            EarlyLimitText.Text = UiText.Get("新用户下载限额：已用 {0} / {1}，截止 {2:yyyy/MM/dd HH:mm}",
                MemberSize(Math.Max(0, earlyUsed)), MemberSize(Math.Max(0, earlyQuota!.Value)), earlyEnds!.Value.LocalDateTime);
        var metadataQuota = member.MetadataDownloadQuotaBytes;
        var metadataUsed = member.MetadataDownloadUsedBytes;
        var metadataVisible = metadataQuota is >= 0 && metadataUsed is > 0;
        MetadataUsageText.IsVisible = metadataVisible;
        if (metadataVisible)
            MetadataUsageText.Text = UiText.Get("今日元数据流量：已用 {0} / {1}，UTC 零点重置",
                MemberSize(Math.Max(0, metadataUsed!.Value)), MemberSize(Math.Max(0, metadataQuota!.Value)));
        Avalonia.Automation.AutomationProperties.SetName(TrafficCard,
            UiText.Get("年度下载：已用 {0}，限额 {1}，剩余 {2}；本月下载 {3}。",
                AnnualDownloadUsedText.Text, hasAnnualUsage ? MemberSize(annualQuota) : "—", DownloadRemainingText.Text, MemberSize(download)));
    }

    private void PresentDevices(DeviceFeatureStatus status)
    {
        var member = status.Membership;
        DeviceCountText.Text = UiText.Get("{0} / {1} 台", member.LoggedInDeviceCount, member.DeviceLimit);
        var remaining = Math.Max(0, member.DeviceLimit - member.LoggedInDeviceCount);
        DeviceCapacityText.Text = UiText.Get(remaining > 0 ? "还可登录 {0} 台设备" : "设备名额已满，可退出旧设备后登录新设备。", remaining);
        DeviceSlotsPanel.Children.Clear();
        for (var index = 0; index < member.DeviceLimit; index++)
        {
            var slot = new Border { Width = 16, Height = 4, CornerRadius = new CornerRadius(2) };
            slot.Bind(Border.BackgroundProperty, this.GetResourceObservable(index < member.LoggedInDeviceCount ? "SuccessBrush" : "SoftHoverBrush"));
            DeviceSlotsPanel.Children.Add(slot);
        }
        DeviceListPanel.Children.Clear();
        foreach (var device in status.Devices.Where(item => item.State == "active")
                     .OrderByDescending(item => item.DeviceId == status.CurrentDeviceId).ThenByDescending(item => item.LastSeenAt))
        {
            var current = device.DeviceId == status.CurrentDeviceId;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,Auto,Auto"), ColumnSpacing = 12 };
            var icon = new ShapePath { Data = AccountVisuals.DeviceIcon(device.Platform), Width = 18, Height = 18, Classes = { "accountDeviceIcon" } };
            icon.Classes.Set("current", current);
            var avatar = new Border { Width = 36, Height = 36, Child = icon, Classes = { "accountDeviceAvatar" } };
            avatar.Classes.Set("current", current);
            row.Children.Add(avatar);
            var description = new StackPanel { Spacing = 3, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var name = new TextBlock { Text = device.Name, FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, device.Name);
            description.Children.Add(name);
            if (current)
            {
                var badge = new TextBlock { Text = UiText.Get("当前设备"), FontSize = 10 };
                badge.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SuccessBrush"));
                var badgeBorder = new Border
                {
                    Child = badge, Padding = new Thickness(6, 2),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                badgeBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccountSuccessTintBrush"));
                Grid.SetColumn(badgeBorder, 2);
                row.Children.Add(badgeBorder);
            }
            var activity = new TextBlock { Text = UiText.Get("{0} · 最近活跃 {1}", device.Platform, LastActivity(device.LastSeenAt)), Classes = { "accountMuted" }, FontSize = 11 };
            ToolTip.SetTip(activity, device.LastSeenAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss", UiText.CurrentCulture));
            description.Children.Add(activity);
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            var button = new Button
            {
                Content = UiText.Get(current ? "退出此设备" : "退出设备"), Tag = device.DeviceId,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "accountTextAction" }
            };
            Avalonia.Automation.AutomationProperties.SetName(button, UiText.Get("退出设备 {0}", device.Name));
            button.Click += OnKickDeviceClick;
            Grid.SetColumn(button, 3);
            row.Children.Add(button);
            DeviceListPanel.Children.Add(new Border { Child = row, Classes = { "flatRow", "accountDeviceRow" } });
        }
        DeviceEmptyText.IsVisible = DeviceListPanel.Children.Count == 0;
    }

    private void PresentPlanBenefits()
    {
        Benefits(FreeBenefitsPanel, ["本地书库管理与阅读", "USB Kindle 书籍与批注双向同步", "插件功能", "自定义 AI 接口", "加密备份"]);
        Benefits(LifetimeBenefitsPanel, ["包含全部 Free 权益", "自备 S3 / WebDAV 多端同步", "进度与批注完整同步", "最多 6 台设备登录", "一次购买，永久使用"]);
        Benefits(PlusBenefitsPanel, ["订阅期内包含功能解锁的同步权益", "10GB Kkindle 私人云空间", "书籍与阅读数据自动同步", "每会员年度 50GB 下载额度", "最多 6 台设备同时云同步"]);
    }

    private void Benefits(StackPanel panel, string[] benefits)
    {
        panel.Children.Clear();
        foreach (var benefit in benefits)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,8,*") };
            var check = new ShapePath { Classes = { "accountBenefitCheck" } };
            check.Bind(ShapePath.DataProperty, this.GetResourceObservable("AccountCheckGeometry"));
            row.Children.Add(check);
            var text = new TextBlock { Text = UiText.Get(benefit), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
    }

    private void PresentProducts()
    {
        var tier = _memberStatus is null ? "" : AccountVisuals.Tier(_memberStatus.Membership);
        var lifetime = _products.FirstOrDefault(product => product.Kind == "lifetime");
        var plus = _products.FirstOrDefault(product => product.Kind is "plus" or "pro");
        FreePlanCard.Classes.Set("current", tier == "free");
        LifetimePlanCard.Classes.Set("current", tier == "lifetime");
        PlusPlanCard.Classes.Set("current", tier == "plus");
        FreePlanButton.Content = UiText.Get(tier == "free" ? "当前方案" : "永久免费");
        LifetimePriceText.Text = lifetime is null ? "—" : Price(lifetime.PriceMinor, lifetime.Currency);
        PlusPriceText.Text = plus is null ? "—" : Price(plus.PriceMinor, plus.Currency);
        LifetimePurchaseButton.Tag = lifetime;
        PlusPurchaseButton.Tag = plus;
        var hasLifetime = _memberStatus?.Membership.HasLifetimeFeatures == true;
        LifetimePurchaseButton.IsEnabled = lifetime is not null && !hasLifetime;
        var canRenew = plus is not null && _memberStatus is { } status
            && MemberEntitlements.CanExtendPlus(status.Membership, plus.DurationDays, DateTimeOffset.UtcNow);
        PlusPurchaseButton.IsEnabled = canRenew;
        LifetimePurchaseButton.Content = UiText.Get(hasLifetime ? "已永久解锁" : lifetime is null ? "暂未开放" : "解锁功能");
        PlusPurchaseButton.Content = UiText.Get(plus is null ? "暂未开放" : !canRenew ? "暂不可续费" : tier == "plus" ? "续费 Plus" : "开通 Plus");
        ToolTip.SetTip(PlusPurchaseButton, UiText.Get("Plus 最多可持有 5 年有效期，付费和激活码共用上限。"));
        var demo = Uri.TryCreate(_status?.ServerUrl, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback;
        PlanPurchaseHintText.Text = UiText.Get(_products.Length == 0 ? "购买服务暂未开放，请稍后刷新。"
            : demo ? "本机演示价：创建订单不会实际扣款。" : "创建订单后，确认付款并到账即可开通权益。")
            + " " + UiText.Get("Plus 最多可持有 5 年有效期，付费和激活码共用上限。");
        var international = _status?.Region == AccountRegion.International;
        PlansRegionText.Text = UiText.Get(AccountRegions.DisplayName(_status?.Region ?? AccountRegion.China));
        PlansUnavailableText.IsVisible = international;
        NoRefundPolicyText.IsVisible = !international;
        PlanCardsGrid.IsVisible = !international;
        PlanPurchaseHintText.IsVisible = !international;
        ActivationCard.IsVisible = !international;
        OrdersCard.IsVisible = !international;
    }

    private void PresentOrders()
    {
        OrderListPanel.Children.Clear();
        OrderEmptyText.IsVisible = _orders.Length == 0;
        foreach (var order in _orders)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 14 };
            var info = new StackPanel { Spacing = 5 };
            info.Children.Add(new TextBlock { Text = order.ProductName, FontSize = 13, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap });
            var state = UiText.Get(order.State == "fulfilled" ? "已到账" : order.State == "cancelled" ? "已取消" : order.ExpiresAt <= DateTimeOffset.UtcNow ? "已过期" : "待确认付款");
            info.Children.Add(new TextBlock { Text = order.CreatedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm", UiText.CurrentCulture) + " · " + state, Classes = { "accountMuted" }, FontSize = 11 });
            ToolTip.SetTip(info, UiText.Get("订单号：{0}", order.Id));
            row.Children.Add(info);
            var amount = new TextBlock { Text = Price(order.PriceMinor, order.Currency), FontSize = 14, FontWeight = FontWeight.Medium, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(amount, 1);
            row.Children.Add(amount);
            if (order.State == "pending" && order.ExpiresAt > DateTimeOffset.UtcNow)
            {
                var cancel = new Button { Content = UiText.Get("取消订单"), Tag = order.Id, Classes = { "quiet" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                cancel.Click += OnCancelOrderClick;
                Grid.SetColumn(cancel, 2);
                row.Children.Add(cancel);
            }
            OrderListPanel.Children.Add(new Border { Child = row, Padding = new Thickness(0, 8) });
        }
    }

    private void PublishAccountState()
    {
        if (_status is { } status) AccountStateChanged?.Invoke(this, new(status, _memberStatus?.Membership));
    }

    private void SetMembershipMessage(string message)
    {
        MembershipStatus.Text = message;
        MembershipStatus.IsVisible = !string.IsNullOrWhiteSpace(message);
        if (MembershipStatus.IsVisible && _status?.IsLoggedIn == true) StatusText.Text = "";
    }

    private static string Percentage(double ratio) => ratio is > 0 and < 0.001
        ? "<0.1%" : (ratio * 100).ToString("0.#", UiText.CurrentCulture) + "%";

    private static string MemberSize(decimal bytes)
    {
        var (value, unit) = bytes >= 1073741824 ? (bytes / 1073741824, "GB") : bytes >= 1048576
            ? (bytes / 1048576, "MB") : bytes >= 1024 ? (bytes / 1024, "KB") : (bytes, "B");
        return value.ToString("0.##", UiText.CurrentCulture) + " " + unit;
    }

    private static string Price(int minor, string currency)
    {
        var amount = (minor / 100m).ToString("0.##", UiText.CurrentCulture);
        return currency switch { "CNY" => "¥" + amount, "USD" => "$" + amount, "EUR" => "€" + amount, _ => amount + " " + currency };
    }

    private static string LastActivity(DateTimeOffset seen)
    {
        var elapsed = DateTimeOffset.UtcNow - seen;
        return elapsed.TotalMinutes < 1 ? UiText.Get("刚刚") : elapsed.TotalHours < 1 ? UiText.Get("{0} 分钟前", (int)elapsed.TotalMinutes)
            : elapsed.TotalDays < 1 ? UiText.Get("{0} 小时前", (int)elapsed.TotalHours) : elapsed.TotalDays < 7
                ? UiText.Get("{0} 天前", (int)elapsed.TotalDays) : seen.LocalDateTime.ToString("MM/dd HH:mm", UiText.CurrentCulture);
    }

    private async void OnMembershipRefreshClick(object? sender, RoutedEventArgs e)
    { if (_busy) return; await RefreshAsync(); }
    private void OnViewPlansClick(object? sender, RoutedEventArgs e) => MembershipPlansPanel.BringIntoView();
    private async void OnKickDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { Tag: Guid device }) return;
        SetBusy(true);
        try { await _membership.KickAsync(device, _lifetime.Token); Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); await LoadMembershipAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetMembershipMessage(UiText.Localize(error.Message)); }
        finally { SetBusy(false); }
    }
    private async void OnTrialClaimToggle(object? sender, RoutedEventArgs e)
    {
        if (_presenting || _busy || _membership is null || _status?.IsLoggedIn != true
            || TrialClaimToggle.IsChecked != true) return;
        SetBusy(true);
        SetMembershipMessage(UiText.Get("正在领取免费试用…"));
        try
        {
            var member = await _membership.ClaimTrialAsync(_lifetime.Token);
            if (_status?.User?.Id != member.AccountId || _status.Region != member.Region) return;
            await LoadMembershipAsync();
            SetMembershipMessage(UiText.Get("Plus 免费试用已开通，7 天内可使用云同步、下载和自备存储同步。"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _presenting = true;
            try { TrialClaimToggle.IsChecked = false; } finally { _presenting = false; }
            SetMembershipMessage(UiText.Localize(error.Message));
        }
        finally { SetBusy(false); }
    }
    private async void OnPlanPurchaseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { IsEnabled: true, Tag: MembershipProduct product }) return;
        SetBusy(true);
        try
        {
            await _membership.PurchaseAsync(product.Id, Guid.NewGuid(), _lifetime.Token);
            await LoadMembershipAsync();
            OrdersExpander.IsExpanded = true;
            OrdersExpander.BringIntoView();
            SetMembershipMessage(UiText.Get("订单已创建，等待确认付款。权益到账后刷新即可使用。"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetMembershipMessage(UiText.Localize(error.Message)); }
        finally { SetBusy(false); }
    }
    private async void OnCancelOrderClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { Tag: Guid order }) return;
        SetBusy(true);
        try { await _membership.CancelOrderAsync(order, _lifetime.Token); await LoadMembershipAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetMembershipMessage(UiText.Localize(error.Message)); }
        finally { SetBusy(false); }
    }

    private void OnActivationCodeChanged(object? sender, TextChangedEventArgs e)
    {
        if (ActivationRedeemButton is null) return;
        UpdateActivationButton();
    }

    private void UpdateActivationButton() => ActivationRedeemButton.IsEnabled = !_busy && _membership is not null
        && _status?.IsLoggedIn == true && !string.IsNullOrWhiteSpace(ActivationCodeBox.Text);

    private void ClearActivationCode()
    {
        ActivationCodeBox.Text = "";
        _activationAttemptCode = null; _activationRequestId = Guid.Empty;
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
        var wasPlus = _memberStatus?.CanUseAccountCloudSync == true;
        SetBusy(true);
        ActivationStatusText.Text = UiText.Get("正在兑换激活码…"); ActivationStatusText.IsVisible = true;
        try
        {
            var result = await _membership.RedeemActivationCodeAsync(code, _activationRequestId, _lifetime.Token);
            if (_status?.User?.Id != result.Status.Membership.AccountId || _status.Region != result.Status.Membership.Region) return;
            _memberStatus = result.Status;
            PresentMembership(result.Status); PublishAccountState();
            ClearActivationCode();
            await LoadMembershipAsync();
            ActivationStatusText.Text = MemberEntitlements.ActivationMessage(result);
            ActivationStatusText.IsVisible = true;
            if (!wasPlus && result.Status.CanUseAccountCloudSync && _status?.IsLoggedIn == true)
                AccountSignedIn?.Invoke(this, new(_status, result.Status.Membership));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ActivationStatusText.Text = UiText.Localize(error.Message); ActivationStatusText.IsVisible = true; }
        finally { SetBusy(false); }
    }
}

internal sealed record AccountStateChangedEventArgs(AccountStatus Account, AccountMembership? Membership);
