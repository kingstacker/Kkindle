using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task AccountActivationRequiresLoginThenRefreshesTierAndClearsTheCode() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false }; using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        var upgrades = 0;
        dialog.AccountSignedIn += (_, args) => { if (args.Membership?.Tier == "pro") upgrades++; };
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Assert.False(Get<StackPanel>("MembershipPanel").IsVisible);
            Get<TextBox>("ActivationCodeBox").Text = "fictional-plus-code";
            Assert.False(Get<Button>("ActivationRedeemButton").IsEnabled);
            Get<Button>("ActivationRedeemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, handler.ActivationAttempts);
            Get<TextBox>("UsernameBox").Text = handler.User.Username;
            Get<TextBox>("PasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("MembershipPanel").IsVisible && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal("", Get<TextBox>("ActivationCodeBox").Text);
            Assert.Equal(0, upgrades);
            Get<TextBox>("ActivationCodeBox").Text = "fictional-plus-code";
            await Until(() => Get<Button>("ActivationRedeemButton").IsEnabled);
            Assert.Equal(dialog.FontFamily, Get<TextBox>("ActivationCodeBox").FontFamily);
            Get<Button>("ActivationRedeemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<TextBlock>("ActivationStatusText").Text?.Contains("已增加 1 年") == true);
            Assert.Contains("Plus", Get<TextBlock>("TierBadgeText").Text);
            Assert.True(Get<Grid>("CloudUsagePanel").IsVisible);
            Assert.Equal("", Get<TextBox>("ActivationCodeBox").Text); Assert.Equal(1, upgrades);
            Assert.False(Get<Button>("ActivationRedeemButton").IsEnabled);
            Get<TextBox>("ActivationCodeBox").Text = "fictional-lifetime-code";
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible);
            Assert.Equal("", Get<TextBox>("ActivationCodeBox").Text); Assert.False(Get<TextBlock>("ActivationStatusText").IsVisible);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task AccountActivationRetriesALostResponseWithTheSameRequest() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, LoseNextActivationResponse = true }; using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Get<TextBox>("ActivationCodeBox").Text = "fictional-plus-code";
            Get<Button>("ActivationRedeemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.ActivationAttempts == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            var firstRequest = handler.LastActivationRequest!.RequestId;
            Assert.Equal("fictional-plus-code", Get<TextBox>("ActivationCodeBox").Text);
            Assert.True(Get<TextBlock>("ActivationStatusText").IsVisible);
            Get<Button>("ActivationRedeemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<TextBlock>("ActivationStatusText").Text?.Contains("当前会员状态已刷新") == true);
            Assert.Equal(firstRequest, handler.LastActivationRequest!.RequestId);
            Assert.Equal(handler.Now.AddDays(365), handler.ProExpiresAt); Assert.Equal(2, handler.ActivationAttempts);
            Assert.Equal("", Get<TextBox>("ActivationCodeBox").Text);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task AccountRenewalHonorsFiveYearCapacityIncludingPendingOrders(bool pendingYear) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false }; using var http = new HttpClient(handler);
        handler.ProExpiresAt = handler.Now.AddDays(pendingYear ? 1460 : 1825);
        handler.Products = [new("fictional-plus-year", "Fictional Plus year", "pro", 12800, "CNY", 365, 0, "Fictional annual plan")];
        if (pendingYear) handler.Orders.Add(new(Guid.NewGuid(), handler.User.Id, "fictional-plus-year", "Fictional Plus year", "pro", 12800, "CNY", 365, 0, "pending", handler.Now, handler.Now.AddDays(2), null));
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("PlusPurchaseButton").Tag is MembershipProduct && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.False(Get<Button>("PlusPurchaseButton").IsEnabled);
            Assert.Contains("5 年", Get<TextBlock>("PlanPurchaseHintText").Text);
            Get<Button>("PlusPurchaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(handler.LastPurchase);
            var before = handler.ProExpiresAt;
            Get<TextBox>("ActivationCodeBox").Text = "fictional-plus-code";
            Get<Button>("ActivationRedeemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<TextBlock>("ActivationStatusText").Text?.Contains("5 年") == true);
            Assert.Equal(before, handler.ProExpiresAt);
            Assert.Equal("fictional-plus-code", Get<TextBox>("ActivationCodeBox").Text);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task PlusActivationSelectsKkindleCloudAndLaterCodeRenewalPreservesManualProvider() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false }; using var http = new HttpClient(handler);
        handler.ActivationCodes["fictionalrenewalcode"] = "pro";
        var accounts = scope.Field<Kkindle.ApplicationServices.ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(accounts, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var saved = await scope.Sync.LoadSettingsAsync();
        var settings = saved.Settings with { Provider = SyncProvider.WebDav, WebDavEndpoint = "https://dav.example.invalid/dav",
            WebDavUsername = "fictional-dav-reader", WebDavPassword = "fictional-dav-password", EncryptionKey = "", Enabled = false, AutomaticSyncEnabled = false };
        await scope.Sync.SaveSettingsAsync(saved.DeviceId, settings);
        scope.Set("_s3SyncStoredSettings", new S3SyncStoredSettings(saved.DeviceId, settings));
        scope.Call("PopulateS3SyncControls", (object?)null);
        AccountCenterView? dialog = null;
        try
        {
            await OpenAsync();
            var upgradeDialog = dialog!;
            upgradeDialog.FindControl<TextBox>("ActivationCodeBox")!.Text = "fictional-plus-code";
            upgradeDialog.FindControl<Button>("ActivationRedeemButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => scope.Get<ComboBox>("SyncProviderBox").SelectedIndex == (int)SyncProvider.AccountServer);
            Assert.Equal(handler.User.Id, (await scope.Sync.LoadSettingsAsync()).Settings.AccountUserId);
            scope.Get<Button>("SettingsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(scope.Get<ContentControl>("AccountCenterHost").IsVisible);
            Assert.Empty(scope.Window.OwnedWindows);
            scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)SyncProvider.WebDav;
            Assert.True(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));
            await OpenAsync();
            var renewalDialog = dialog!;
            renewalDialog.FindControl<TextBox>("ActivationCodeBox")!.Text = "fictional-renewal-code";
            renewalDialog.FindControl<Button>("ActivationRedeemButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => renewalDialog.FindControl<TextBlock>("ActivationStatusText")!.Text?.Contains("已增加 1 年") == true);
            Assert.Equal(SyncProvider.WebDav, (await scope.Sync.LoadSettingsAsync()).Settings.Provider);
        }
        finally { if (dialog is not null) UnmountAccountCenter(scope, dialog); }
        async Task OpenAsync()
        {
            scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var host = scope.Get<ContentControl>("AccountCenterHost");
            await Until(() => host.Content is AccountCenterView && host.IsVisible);
            dialog = Assert.IsType<AccountCenterView>(host.Content);
            await Until(() => dialog.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
        }
    });
}
