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

    private sealed class AccountTestProtector : Kkindle.Core.ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
