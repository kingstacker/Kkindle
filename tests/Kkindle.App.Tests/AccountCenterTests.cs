using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    private static void MountAccountCenter(TestWindow scope, AccountCenterView page)
    {
        page.AccountStateChanged += (_, state) => scope.Call("PresentAccountSummary", state.Account, state.Membership);
        scope.Set("_accountCenterView", page);
        var host = scope.Get<ContentControl>("AccountCenterHost");
        host.Content = page;
        scope.Call("ShowStage3Page", host, scope.Get<Button>("AccountNavigationButton"));
    }

    private static void UnmountAccountCenter(TestWindow scope, AccountCenterView page)
    {
        scope.Call("ShowLibraryPage");
        page.Dispose();
        scope.Get<ContentControl>("AccountCenterHost").Content = null;
        scope.Set("_accountCenterView", null);
    }

    [Fact]
    public Task AccountCenterViewRegistersAndSignsOutThroughItsControls() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = 1024;
        scope.Window.Height = 664;
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(service);
        try
        {
            MountAccountCenter(scope, dialog);
            T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("RegionBox").SelectedIndex = 1;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Assert.Null(dialog.FindControl<TextBox>("ServerUrlBox"));
            Get<ComboBox>("ModeBox").SelectedIndex = 1;
            Assert.True(Get<StackPanel>("RegistrationEmailPanel").IsVisible);
            Assert.True(Get<TextBlock>("RegisterHeading").IsVisible);
            Assert.False(Get<TextBlock>("SignInHeading").IsVisible);
            await Render();
            Capture(scope.Window, "account-registration");
            Get<Button>("SubmitButton").BringIntoView();
            await Render();
            AssertWithinAccountViewport(Get<TextBox>("ConfirmPasswordBox"), Get<ScrollViewer>("AccountScrollViewer"));
            AssertWithinAccountViewport(Get<Button>("SubmitButton"), Get<ScrollViewer>("AccountScrollViewer"));
            Capture(scope.Window, "account-registration-action");
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible && Get<Button>("SignOutButton").IsEnabled);
            Assert.Equal("test_reader", Get<TextBlock>("UsernameText").Text);
            Assert.Equal(Kkindle.Core.AccountRegion.International, (await service.GetStatusAsync()).Region);
            Assert.Equal(Kkindle.Core.UiText.Get("国际区"), Get<TextBlock>("AccountRegionText").Text);
            Assert.Equal("reader@example.invalid", (await service.GetStatusAsync()).User!.Email);
            Assert.True(Get<TextBox>("ProfileEmailBox").IsReadOnly);
            Assert.Equal(Kkindle.Core.AccountRegion.International, handler.LastRequestRegion);
            Assert.Equal("", Get<TextBox>("PasswordBox").Text);
            Assert.Equal("", Get<TextBox>("ConfirmPasswordBox").Text);
            await Render();
            AssertWithinAccountViewport(Get<Button>("SignOutButton"), Get<ScrollViewer>("AccountScrollViewer"));
            Capture(scope.Window, "account-signed-in");
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible);
            Assert.True(handler.Revoked);
            Assert.False((await service.GetStatusAsync()).IsLoggedIn);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task AccountRegistrationRequiresEmailAndMatchingPasswordBeforeSending() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(service);
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("ModeBox").SelectedIndex = 1;
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("SubmitButton").IsEnabled && Get<TextBlock>("StatusText").Text!.Contains("必须填写邮箱"));
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("RegistrationEmailBox").Text = "invalid email";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("SubmitButton").IsEnabled && Get<TextBlock>("StatusText").Text!.Contains("有效的邮箱"));
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = "fictional-mismatch-password";
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("密码不一致", Get<TextBlock>("StatusText").Text);
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible && Get<Button>("SignOutButton").IsEnabled);
            Assert.Equal("reader@example.invalid", (await service.GetStatusAsync()).User!.Email);
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("ModeBox").SelectedIndex = 0;
            Get<TextBox>("UsernameBox").Text = "READER@EXAMPLE.INVALID";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible);
            Assert.Equal("test_reader", (await service.GetStatusAsync()).User!.Username);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task SidebarAccountButtonShowsTheLoginPageInsideTheMainWindowAndKeepsNavigationAvailable() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = scope.Get<ContentControl>("AccountCenterHost");
        await Until(() => host.Content is AccountCenterView && host.IsVisible);
        var page = Assert.IsType<AccountCenterView>(host.Content);
        await Until(() => page.FindControl<Button>("SubmitButton")!.IsEnabled);
        await Render();
        Assert.Same(scope.Window, TopLevel.GetTopLevel(page));
        Assert.Empty(scope.Window.OwnedWindows);
        Assert.True(page.FindControl<StackPanel>("SignInPanel")!.IsVisible);
        Assert.Contains("active", scope.Get<Button>("AccountNavigationButton").Classes);
        Assert.DoesNotContain("active", scope.Get<Button>("AllBooksButton").Classes);
        Assert.False(scope.Get<Grid>("LibraryWorkspace").IsVisible);
        Assert.False(scope.Get<Grid>("SettingsPage").IsVisible);
        page.FindControl<TextBox>("PasswordBox")!.Text = "fictional-unsent-password";
        page.FindControl<TextBox>("ActivationCodeBox")!.Text = "fictional-unsent-activation-code";
        scope.Get<Button>("SettingsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(host.IsVisible); Assert.True(scope.Get<Grid>("SettingsPage").IsVisible);
        Assert.DoesNotContain("active", scope.Get<Button>("AccountNavigationButton").Classes);
        Assert.Equal("", page.FindControl<TextBox>("PasswordBox")!.Text);
        Assert.Equal("", page.FindControl<TextBox>("ActivationCodeBox")!.Text);
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => host.IsVisible && page.FindControl<Button>("SubmitButton")!.IsEnabled);
        Assert.Same(page, host.Content); Assert.Empty(scope.Window.OwnedWindows);
        scope.Call("ShowLibraryPage");
        Assert.False(host.IsVisible); Assert.True(scope.Get<Grid>("LibraryWorkspace").IsVisible);
        Assert.Contains("active", scope.Get<Button>("AllBooksButton").Classes);

        scope.Set("_isTransferring", true);
        scope.Call("SetTransferNavigationEnabled", false);
        try
        {
            Assert.False(scope.Get<Button>("AccountNavigationButton").IsEnabled);
            scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(host.IsVisible);
            Assert.True(scope.Get<Grid>("LibraryWorkspace").IsVisible);
            Assert.Empty(scope.Window.OwnedWindows);
        }
        finally
        {
            scope.Set("_isTransferring", false);
            scope.Call("SetTransferNavigationEnabled", true);
        }
        Assert.True(scope.Get<Button>("AccountNavigationButton").IsEnabled);
    });

    [Fact]
    public Task MemberCanListAndKickDevicesAndCreateThenCancelAnOrder() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false }; using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Contains("Free", Get<TextBlock>("MembershipSummary").Text);
            Assert.Equal("2 / 6 台", Get<TextBlock>("DeviceCountText").Text);
            Assert.Equal("¥88", Get<TextBlock>("LifetimePriceText").Text);
            Assert.Contains("演示价", Get<TextBlock>("PlanPurchaseHintText").Text);
            var kick = Get<StackPanel>("DeviceListPanel").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Tag is Guid id && id == handler.OtherDeviceId);
            kick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal(handler.OtherDeviceId, handler.LastKickedDevice);
            Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            Get<Button>("LifetimePurchaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("OrderListPanel").Children.Count == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal("fictional-lifetime", handler.LastPurchase!.ProductId);
            Assert.True(Get<Expander>("OrdersExpander").IsExpanded);
            var cancel = Get<StackPanel>("OrderListPanel").GetVisualDescendants().OfType<Button>().Single();
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.Orders[0].State == "cancelled" && Get<StackPanel>("MembershipPanel").IsEnabled);
            Capture(scope.Window, "membership-devices-and-order");
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData("free")]
    [InlineData("lifetime")]
    [InlineData("plus")]
    public Task AccountLoginAndDeviceSignOutUpdateTheSidebarImmediately(string tier) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = tier == "lifetime",
            ProExpiresAt = tier == "plus" ? DateTimeOffset.UtcNow.AddDays(30) : null
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Assert.Equal(UiText.Get("登录"), scope.Get<TextBlock>("AccountNavigationText").Text);
            Get<TextBox>("UsernameBox").Text = handler.User.Username;
            Get<TextBox>("PasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal("已登录", scope.Get<TextBlock>("AccountNavigationText").Text);
            Assert.Contains(tier, scope.Get<ShapePath>("AccountNavigationIcon").Classes);
            Assert.Contains(tier, Get<ShapePath>("MembershipAvatarIcon").Classes);
            Assert.Contains(handler.User.Username, ToolTip.GetTip(scope.Get<Button>("AccountNavigationButton"))!.ToString());
            Assert.Equal(tier == "plus", Get<Grid>("CloudUsagePanel").IsVisible);

            var current = Get<StackPanel>("DeviceListPanel").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Tag is Guid id && id == handler.CurrentDeviceId);
            current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Assert.Equal(UiText.Get("登录"), scope.Get<TextBlock>("AccountNavigationText").Text);
            Assert.Contains("free", scope.Get<ShapePath>("AccountNavigationIcon").Classes);
            Assert.False(Get<Grid>("CloudUsagePanel").IsVisible);
            Assert.Empty(Get<StackPanel>("DeviceListPanel").Children);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData("zh-CN", 1294, 818, AppTheme.Classic)]
    [InlineData("zh-CN", 1024, 664, AppTheme.Green)]
    [InlineData("en-US", 1294, 818, AppTheme.Beige)]
    [InlineData("en-US", 1024, 664, AppTheme.Classic)]
    public Task AccountDashboardShowsAnnualDownloadAllowanceAndServerPricedPlanCards(string language, int width, int height, AppTheme theme) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var app = (Kkindle.App)Avalonia.Application.Current!;
        app.ApplyLanguage(language);
        app.ApplyTheme(theme);
        const long gib = 1024L * 1024 * 1024;
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = false,
            ProExpiresAt = DateTimeOffset.UtcNow.AddDays(365),
            Products =
            [
                new("fictional-lifetime", "Feature Unlock", "lifetime", 8800, "CNY", 0, 0, "Fictional permanent unlock."),
                new("fictional-plus", "Plus", "pro", 12800, "CNY", 365, 0, "Fictional annual membership.")
            ],
            TransformStatus = status => status with
            {
                Membership = status.Membership with
                {
                    StorageBytes = 8 * gib, StorageQuota = 10 * gib,
                    UploadBytes = gib, DownloadBytes = 3 * gib,
                    DownloadRemainingBytes = 137 * gib, DownloadCreditBytes = 2 * gib,
                    PlusDownloadQuotaBytes = 150 * gib, PlusDownloadUsedBytes = 15 * gib
                }
            }
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        handler.Devices[0] = handler.Devices[0] with { Name = "MacBook Pro" };
        handler.Devices[1] = handler.Devices[1] with { Name = "iPhone 15", LastSeenAt = DateTimeOffset.UtcNow.AddHours(-3) };
        scope.Window.Width = width;
        scope.Window.Height = height;
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.True(Get<Grid>("CloudUsagePanel").IsVisible);
            Assert.Equal("8 GiB", Get<TextBlock>("StorageUsedText").Text);
            Assert.Equal("80%", Get<TextBlock>("StoragePercentText").Text);
            Assert.Equal(0.8, Get<AccountUsageRing>("StorageUsageRing").Ratio, 6);
            Assert.Equal("15 GiB", Get<TextBlock>("AnnualDownloadUsedText").Text);
            Assert.Equal("10%", Get<TextBlock>("TrafficPercentText").Text);
            Assert.Equal(0.1, Get<AccountUsageRing>("TrafficUsageRing").Ratio, 6);
            Assert.Equal("135 GiB", Get<TextBlock>("DownloadRemainingText").Text);
            Assert.Equal("3 GiB", Get<TextBlock>("DownloadUsedText").Text);
            Assert.Equal(UiText.Get("{0} / {1} 台", 2, 6), Get<TextBlock>("DeviceCountText").Text);
            Assert.Equal(6, Get<StackPanel>("DeviceSlotsPanel").Children.Count);
            Assert.True(Get<TextBlock>("DownloadCreditText").IsVisible);
            Assert.Equal("¥88", Get<TextBlock>("LifetimePriceText").Text);
            Assert.Equal("¥128", Get<TextBlock>("PlusPriceText").Text);
            Assert.Equal(UiText.Get("续费 Plus"), Get<Button>("PlusPurchaseButton").Content);
            Assert.False(Get<Expander>("OrdersExpander").IsExpanded);
            await Render();
            Assert.Equal(UiText.Get("已登录"), scope.Get<TextBlock>("AccountNavigationText").Text);
            var viewport = Get<ScrollViewer>("AccountScrollViewer");
            var settingsTitle = scope.Get<Grid>("SettingsPage").GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Classes.Contains("pageTitle"));
            Assert.Equal(settingsTitle.FontFamily, Get<TextBlock>("AccountPageTitle").FontFamily);
            Assert.Equal(settingsTitle.FontSize, Get<TextBlock>("AccountPageTitle").FontSize);
            Assert.Equal(settingsTitle.FontWeight, Get<TextBlock>("AccountPageTitle").FontWeight);
            Assert.Equal(ResourceColor("PaperBrush"), ColorOf(dialog.Background));
            Assert.Contains(Get<Border>("AccountCard"), Get<Button>("SignOutButton").GetVisualAncestors());
            AssertWithinAccountViewport(Get<Button>("SignOutButton"), viewport);
            AssertWithinAccountViewport(Get<Grid>("CloudUsagePanel"), viewport);
            Assert.False(Get<Expander>("ProfileExpander").IsExpanded);
            Assert.False(Get<Expander>("PasswordExpander").IsExpanded);
            if (height >= 800) AssertWithinAccountViewport(Get<Border>("DevicesCard"), viewport);
            else AssertWithinAccountViewport((Control)Get<StackPanel>("DeviceListPanel").Children[0], viewport);
            Capture(scope.Window, $"account-dashboard-{language}-{width}x{height}-{theme}");

            Get<StackPanel>("MembershipPlansPanel").BringIntoView();
            await Render();
            var refundPolicy = Get<TextBlock>("NoRefundPolicyText");
            Assert.Contains(language == "en-US" ? "non-refundable" : "均不支持退款", refundPolicy.Text);
            AssertWithinAccountViewport(refundPolicy, viewport);
            Assert.Equal(Get<TextBlock>("TierBadgeText").FontFamily, refundPolicy.FontFamily);
            foreach (var name in new[] { "FreePlanCard", "LifetimePlanCard", "PlusPlanCard" })
            {
                var card = Get<Border>(name);
                var position = card.TranslatePoint(default, viewport)!.Value;
                Assert.True(position.X >= 0 && position.X + card.Bounds.Width <= viewport.Bounds.Width + 1, name);
            }
            var freeBenefits = Get<StackPanel>("FreeBenefitsPanel").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
            foreach (var icon in Get<StackPanel>("FreeBenefitsPanel").GetVisualDescendants().OfType<ShapePath>())
                Assert.NotNull(icon.Data);
            Assert.DoesNotContain(freeBenefits, text => text.Contains("100M", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(freeBenefits, text => text.Contains("S3", StringComparison.OrdinalIgnoreCase));
            var unlockBenefits = Get<StackPanel>("LifetimeBenefitsPanel").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
            Assert.Contains(UiText.Get("进度与批注完整同步"), unlockBenefits);
            Assert.Contains(UiText.Get("最多 6 台设备登录"), unlockBenefits);
            var plusBenefits = Get<StackPanel>("PlusBenefitsPanel").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
            Assert.Contains(UiText.Get("书籍与阅读数据自动同步"), plusBenefits);
            Capture(scope.Window, $"account-plans-{language}-{width}x{height}-{theme}");
            Get<Button>("PlusPurchaseButton").BringIntoView();
            await Render();
            foreach (var name in new[] { "FreePlanButton", "LifetimePurchaseButton", "PlusPurchaseButton" })
                AssertWithinAccountViewport(Get<Button>(name), viewport);
            Capture(scope.Window, $"account-plan-actions-{language}-{width}x{height}-{theme}");
            Assert.Null(dialog.FindControl<Border>("AccountDetailsCard"));
            Get<Expander>("ProfileExpander").IsExpanded = true;
            await Render();
            Get<Button>("ProfileSaveButton").BringIntoView();
            await Render();
            foreach (var name in new[] { "ProfileUsernameBox", "ProfileEmailBox", "ProfilePasswordBox", "ProfileSaveButton" })
                AssertWithinAccountViewport(Get<Control>(name), viewport);
            Capture(scope.Window, $"account-profile-{language}-{width}x{height}-{theme}");
        }
        finally
        {
            UnmountAccountCenter(scope, dialog);
            app.ApplyLanguage("zh-CN");
            app.ApplyTheme(AppTheme.Classic);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AccountAnnualDownloadRingExcludesOldCreditAndHandlesLegacyReplies(bool legacy) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        const long gib = 1024L * 1024 * 1024;
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(365),
            TransformStatus = status => status with { Membership = status.Membership with
            {
                PlusDownloadQuotaBytes = legacy ? null : 150 * gib, PlusDownloadUsedBytes = legacy ? null : 150 * gib,
                DownloadBytes = gib, UploadBytes = 19 * gib, DownloadRemainingBytes = 5 * gib, DownloadCreditBytes = 5 * gib
            } }
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Email!, MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled && dialog.FindControl<Grid>("CloudUsagePanel")!.IsVisible);
            Assert.Equal(legacy ? "—" : "100%", dialog.FindControl<TextBlock>("TrafficPercentText")!.Text);
            Assert.Equal(legacy ? 0 : 1, dialog.FindControl<AccountUsageRing>("TrafficUsageRing")!.Ratio);
            Assert.Equal(legacy ? "—" : "0 B", dialog.FindControl<TextBlock>("DownloadRemainingText")!.Text);
            Assert.True(dialog.FindControl<TextBlock>("DownloadCreditText")!.IsVisible);
            Assert.Contains("5 GiB", dialog.FindControl<TextBlock>("DownloadCreditText")!.Text);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task AccountProfilePasswordAndClosureWorkThroughTheirControls() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler();
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Email!, MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<StackPanel>("MembershipPanel").IsEnabled);
            Get<TextBox>("ProfileUsernameBox").Text = handler.UnavailableUsername;
            Get<TextBox>("ProfilePasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("ProfileSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Border>("AccountSettingsCard").IsEnabled && Get<TextBlock>("StatusText").Text!.Contains("已注册"));
            Assert.Equal("fictional_member", (await accounts.GetStatusAsync()).User!.Username);
            Get<TextBox>("ProfileUsernameBox").Text = "renamed_reader";
            Assert.True(Get<TextBox>("ProfileEmailBox").IsReadOnly);
            Get<TextBox>("ProfilePasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("ProfileSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Border>("AccountSettingsCard").IsEnabled && Get<TextBlock>("UsernameText").Text == "renamed_reader");
            Assert.Equal("member@example.invalid", (await accounts.GetStatusAsync()).User!.Email);
            Assert.Equal("member@example.invalid", Get<TextBox>("ProfileEmailBox").Text);
            Assert.Equal("", Get<TextBox>("ProfilePasswordBox").Text);
            Get<TextBox>("CurrentPasswordBox").Text = MembershipHttpHandler.Password;
            Get<TextBox>("NewPasswordBox").Text = "fictional-new-reader-password";
            Get<TextBox>("ConfirmNewPasswordBox").Text = "fictional-new-reader-password";
            Get<Button>("PasswordSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Border>("AccountSettingsCard").IsEnabled && Get<StackPanel>("DeviceListPanel").Children.Count == 1);
            Assert.Equal("", Get<TextBox>("CurrentPasswordBox").Text);
            Get<Button>("CloseAccountButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Any());
            var confirmation = scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Single();
            await Render();
            Capture(confirmation, "account-closure-confirmation");
            confirmation.FindControl<TextBox>("ClosurePasswordBox")!.Text = "fictional-new-reader-password";
            confirmation.FindControl<TextBox>("ClosureConfirmationBox")!.Text = "注销";
            Assert.False(confirmation.FindControl<Button>("ClosureConfirmButton")!.IsEnabled);
            Assert.False(handler.AccountClosed);
            confirmation.FindControl<Button>("ClosureCancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !scope.Window.OwnedWindows.Any());
            Assert.False(handler.AccountClosed);
            Get<Button>("CloseAccountButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Any());
            confirmation = scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Single();
            confirmation.FindControl<TextBox>("ClosurePasswordBox")!.Text = "fictional-new-reader-password";
            confirmation.FindControl<TextBox>("ClosureConfirmationBox")!.Text = "注销账号";
            await Until(() => confirmation.FindControl<Button>("ClosureConfirmButton")!.IsEnabled);
            Assert.True(confirmation.FindControl<Button>("ClosureConfirmButton")!.IsEnabled);
            confirmation.FindControl<Button>("ClosureConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.AccountClosed && Get<StackPanel>("SignInPanel").IsVisible);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
            Assert.Equal("", confirmation.FindControl<TextBox>("ClosurePasswordBox")!.Text);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task PlusLoginSelectsKkindleCloudAndPreservesLaterManualProviderChoice(bool hasSyncKey, bool syncInProgress) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(365) };
        using var http = new HttpClient(handler);
        var accounts = scope.Field<Kkindle.ApplicationServices.ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(accounts, http);
        var saved = await scope.Sync.LoadSettingsAsync();
        var settings = saved.Settings with { Provider = SyncProvider.WebDav, WebDavEndpoint = "https://dav.example.invalid/dav",
            WebDavUsername = "fictional-dav-reader", WebDavPassword = "fictional-dav-password",
            EncryptionKey = hasSyncKey ? "fictional-sync-key" : "", Enabled = true, AutomaticSyncEnabled = false };
        await scope.Sync.SaveSettingsAsync(saved.DeviceId, settings);
        scope.Set("_s3SyncStoredSettings", new S3SyncStoredSettings(saved.DeviceId, settings));
        scope.Call("PopulateS3SyncControls", (object?)null);
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = scope.Get<ContentControl>("AccountCenterHost");
        await Until(() => host.Content is AccountCenterView && host.IsVisible);
        var dialog = Assert.IsType<AccountCenterView>(host.Content);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Until(() => dialog.FindControl<Button>("SubmitButton")!.IsEnabled);
            if (syncInProgress)
            {
                scope.Set("_s3SyncBusy", true);
                scope.Set("_s3SyncCompletion", completion);
            }
            dialog.FindControl<TextBox>("UsernameBox")!.Text = handler.User.Email;
            dialog.FindControl<TextBox>("PasswordBox")!.Text = MembershipHttpHandler.Password;
            dialog.FindControl<Button>("SubmitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (syncInProgress)
            {
                await Until(() => dialog.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2
                    && dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
                Assert.Equal(SyncProvider.WebDav, (await scope.Sync.LoadSettingsAsync()).Settings.Provider);
                scope.Set("_s3SyncBusy", false);
                scope.Set("_s3SyncCompletion", (object?)null);
                completion.TrySetResult(true);
            }
            await Until(() => scope.Get<ComboBox>("SyncProviderBox").SelectedIndex == (int)SyncProvider.AccountServer);
            var selected = (await scope.Sync.LoadSettingsAsync()).Settings;
            Assert.Equal(SyncProvider.AccountServer, selected.Provider);
            Assert.Equal(handler.User.Id, selected.AccountUserId);
            Assert.Equal(hasSyncKey, selected.Enabled);
            Assert.Equal(settings.EncryptionKey, selected.EncryptionKey);
            Assert.Equal(settings.WebDavPassword, selected.WebDavPassword);
            Assert.Equal("Kkindle 云同步", selected.ProviderName);
            scope.Get<Button>("SettingsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(host.IsVisible); Assert.Empty(scope.Window.OwnedWindows);
            scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)SyncProvider.WebDav;
            scope.Get<ToggleSwitch>("S3SyncEnabledCheck").IsChecked = true;
            Assert.True(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));
            await scope.Call<Task>("RefreshAccountSummaryAsync");
            Assert.Equal(SyncProvider.WebDav, (await scope.Sync.LoadSettingsAsync()).Settings.Provider);
            Assert.Equal((int)SyncProvider.WebDav, scope.Get<ComboBox>("SyncProviderBox").SelectedIndex);
        }
        finally
        {
            scope.Set("_s3SyncBusy", false);
            scope.Set("_s3SyncCompletion", (object?)null);
            completion.TrySetResult(false);
            UnmountAccountCenter(scope, dialog);
        }
    });

    [Fact]
    public Task ExpiredPlusRemovesCloudChartsAndShowsTheRetentionDeadline() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled && dialog.FindControl<Grid>("CloudUsagePanel")!.IsVisible);
            var expired = DateTimeOffset.UtcNow.AddDays(-5);
            handler.ProExpiresAt = expired;
            scope.Get<Button>("SettingsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(scope.Get<ContentControl>("AccountCenterHost").IsVisible);
            scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => dialog.FindControl<Border>("ExpiryNoticeBorder")!.IsVisible && dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.Same(dialog, scope.Get<ContentControl>("AccountCenterHost").Content);
            Assert.Empty(scope.Window.OwnedWindows);
            Assert.False(dialog.FindControl<Grid>("CloudUsagePanel")!.IsVisible);
            Assert.True(dialog.FindControl<Border>("CloudLockedPanel")!.IsVisible);
            Assert.Contains(MemberEntitlements.CloudRetentionEndsAt(expired).LocalDateTime.ToString("yyyy/MM/dd"), dialog.FindControl<TextBlock>("ExpiryNoticeText")!.Text);
            Assert.Contains("free", dialog.FindControl<ShapePath>("MembershipAvatarIcon")!.Classes);
            Assert.Equal("续费 Plus", dialog.FindControl<Button>("ViewPlusButton")!.Content);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task PurchaseServiceFailureKeepsTheCloudUsageAndDeviceDashboard() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(30), ProductsUnavailable = true,
            TransformStatus = status => status with { Membership = status.Membership with { StorageBytes = 12L * 1024 * 1024 * 1024 } }
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.True(dialog.FindControl<Grid>("CloudUsagePanel")!.IsVisible);
            Assert.Equal("120%", dialog.FindControl<TextBlock>("StoragePercentText")!.Text);
            Assert.Equal(1, dialog.FindControl<AccountUsageRing>("StorageUsageRing")!.Ratio);
            Assert.Equal("空间已满", dialog.FindControl<TextBlock>("StorageRemainingText")!.Text);
            Assert.False(dialog.FindControl<Button>("LifetimePurchaseButton")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("PlusPurchaseButton")!.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("MembershipStatus")!.IsVisible);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task DailyInvalidationRestoresTheAccountCenterViewsLoginForm() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler(); using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        using var monitor = new AccountDeviceMonitor(accounts, scope.Paths);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("MembershipPanel")!.IsVisible);
            handler.Revoked = true; await monitor.CheckIfDueAsync();
            await Until(() => dialog.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False(dialog.FindControl<StackPanel>("MembershipPanel")!.IsVisible);
            Assert.Contains("重新登录", dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task ClosingTheMainWindowCancelsTheAccountRefreshAndClearsSensitiveInputs() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var membership = new MembershipHttpHandler();
        using var handler = new DeferredAccountRefreshHandler(membership);
        using var http = new HttpClient(handler);
        var accounts = scope.Field<Kkindle.ApplicationServices.ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(accounts, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, membership.User.Username, MembershipHttpHandler.Password);
        handler.DeferSession = true;
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => handler.Started.Task.IsCompleted);
        var page = Assert.IsType<AccountCenterView>(scope.Get<ContentControl>("AccountCenterHost").Content);
        page.FindControl<TextBox>("PasswordBox")!.Text = "fictional-unsent-password";
        page.FindControl<TextBox>("ActivationCodeBox")!.Text = "fictional-unsent-activation-code";
        scope.RequestQuit();
        await Until(() => handler.Cancelled.Task.IsCompleted);
        Assert.Equal("", page.FindControl<TextBox>("PasswordBox")!.Text);
        Assert.Equal("", page.FindControl<TextBox>("ActivationCodeBox")!.Text);
    });

    private static void AssertWithinAccountViewport(Control control, ScrollViewer viewport)
    {
        var position = control.TranslatePoint(default, viewport)!.Value;
        var message = $"{control.Name}: position {position}, size {control.Bounds.Size}, viewport {viewport.Bounds.Size}";
        Assert.True(position.X >= -1 && position.Y >= -1, message);
        Assert.True(position.X + control.Bounds.Width <= viewport.Bounds.Width + 1, message);
        Assert.True(position.Y + control.Bounds.Height <= viewport.Bounds.Height + 1, message);
    }

    private sealed class DeferredAccountRefreshHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool DeferSession { get; set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (DeferSession && request.RequestUri!.AbsolutePath.EndsWith("/session", StringComparison.Ordinal))
            {
                Started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { Cancelled.TrySetResult(true); throw; }
            }
            return await base.SendAsync(request, token);
        }
    }

    private sealed class AccountTestProtector : Kkindle.Core.ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
