using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task AccountWindowRegistersAndSignsOutThroughItsControls() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountWindow(service);
        try
        {
            dialog.Show(scope.Window);
            T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("RegionBox").SelectedIndex = 1;
            await Until(() => Get<Button>("SubmitButton").IsEnabled && Get<TextBox>("ServerUrlBox").Text == "http://127.0.0.1:5089");
            Get<ComboBox>("ModeBox").SelectedIndex = 1;
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible);
            Assert.Equal("test_reader", Get<TextBlock>("UsernameText").Text);
            Assert.Equal(Kkindle.Core.AccountRegion.International, (await service.GetStatusAsync()).Region);
            Assert.Contains(Kkindle.Core.UiText.Get("国际区"), Get<TextBlock>("ServerText").Text);
            Assert.Equal(Kkindle.Core.AccountRegion.International, handler.LastRequestRegion);
            Assert.Equal("", Get<TextBox>("PasswordBox").Text);
            Assert.Equal("", Get<TextBox>("ConfirmPasswordBox").Text);
            Capture(dialog, "account-signed-in");
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible);
            Assert.True(handler.Revoked);
            Assert.False((await service.GetStatusAsync()).IsLoggedIn);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task SidebarAccountButtonOpensTheLoginWindow() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Window.OwnedWindows.OfType<AccountWindow>().Any());
        var dialog = scope.Window.OwnedWindows.OfType<AccountWindow>().Single();
        Assert.True(dialog.FindControl<StackPanel>("SignInPanel")!.IsVisible);
        dialog.Close();
        await Until(() => !scope.Field<bool>("_accountWindowOpen"));
    });

    [Fact]
    public Task MemberCanListAndKickDevicesAndCreateThenCancelAnOrder() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler(); using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true);
        var dialog = new AccountWindow(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            dialog.Show(scope.Window);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Contains("终生功能会员", Get<TextBlock>("MembershipSummary").Text);
            Assert.Contains("2/5", Get<TextBlock>("MembershipSummary").Text);
            Assert.Contains("0.01 CNY", Get<TextBlock>("ProductDescription").Text);
            var kick = Get<StackPanel>("DeviceListPanel").Children.OfType<StackPanel>().SelectMany(row => row.Children).OfType<Button>()
                .Single(button => button.Tag is Guid id && id == handler.OtherDeviceId);
            kick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal(handler.OtherDeviceId, handler.LastKickedDevice);
            Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            Get<Button>("PurchaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("OrderListPanel").Children.Count == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal("fictional-lifetime", handler.LastPurchase!.ProductId);
            var cancel = ((StackPanel)Get<StackPanel>("OrderListPanel").Children[0]).Children.OfType<Button>().Single();
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.Orders[0].State == "cancelled" && Get<StackPanel>("MembershipPanel").IsEnabled);
            Capture(dialog, "membership-devices-and-order");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task DailyInvalidationRestoresTheAccountWindowsLoginForm() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler(); using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true);
        using var monitor = new AccountDeviceMonitor(accounts, scope.Paths);
        var dialog = new AccountWindow(accounts, new DeviceMembershipService(accounts));
        try
        {
            dialog.Show(scope.Window);
            await Until(() => dialog.FindControl<StackPanel>("MembershipPanel")!.IsVisible);
            handler.Revoked = true; await monitor.CheckIfDueAsync();
            await Until(() => dialog.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False(dialog.FindControl<StackPanel>("MembershipPanel")!.IsVisible);
            Assert.Contains("重新登录", dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
        }
        finally { dialog.Close(); }
    });

    private sealed class AccountTestProtector : Kkindle.Core.ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
