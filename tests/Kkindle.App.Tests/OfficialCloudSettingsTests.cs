using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task OfficialCloudSettingsKeepTheKeyEditableAndPreserveCustomConnectionsWhenSaved() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { ProExpiresAt = DateTimeOffset.UtcNow.AddDays(365) };
        using var http = new HttpClient(handler);
        var accounts = scope.Field<Kkindle.ApplicationServices.ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(accounts, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password,
            true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        await scope.Call<Task>("RefreshAccountSummaryAsync");

        var stored = await scope.Sync.LoadSettingsAsync();
        var custom = stored.Settings with
        {
            Enabled = true, AutomaticSyncEnabled = false, DownloadBookFilesOnSync = true,
            Endpoint = "https://s3.example.invalid", AccessKey = "fictional-access-key", SecretKey = "fictional-secret-key",
            Bucket = "fictional-bucket", Region = "fictional-region", Prefix = "fictional-library",
            WebDavEndpoint = "https://dav.example.invalid/dav", WebDavUsername = "fictional-dav-user",
            WebDavPassword = "fictional-dav-password", EncryptionKey = "fictional-initial-sync-key",
            IntervalMinutes = 75, TimeoutSeconds = 120, ConcurrentRequests = 6, PathStyle = true, SkipTlsVerify = true
        };
        await scope.Sync.SaveSettingsAsync(stored.DeviceId, custom);
        scope.Set("_s3SyncStoredSettings", new S3SyncStoredSettings(stored.DeviceId, custom));
        scope.Call("PopulateS3SyncControls", (object?)null);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = true;
        scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)SyncProvider.AccountServer;
        await Render();

        foreach (var name in new[]
        {
            "S3ConnectionPanel", "WebDavConnectionPanel", "S3CustomSettingsHelpText", "S3DownloadBookFilesHelpText",
            "S3SyncIntervalRow", "SettingsS3AdvancedExpander", "SettingsS3ProfileExpander", "S3TestConnectionButton"
        })
            Assert.False(scope.Get<Control>(name).IsVisible, name);
        Assert.True(scope.Get<TextBox>("AccountSyncEncryptionKeyBox").IsEffectivelyVisible);
        Assert.False(scope.Get<TextBox>("S3EncryptionKeyBox").IsEffectivelyVisible);
        Assert.Equal(custom.EncryptionKey, scope.Get<TextBox>("AccountSyncEncryptionKeyBox").Text);
        scope.Get<TextBox>("AccountSyncEncryptionKeyBox").Text = "fictional-updated-sync-key";
        await Render();
        Assert.Equal("fictional-updated-sync-key", scope.Get<TextBox>("S3EncryptionKeyBox").Text);
        Assert.Equal(custom, (await scope.Sync.LoadSettingsAsync()).Settings);
        Assert.True(scope.Get<Button>("S3SaveSettingsButton").IsVisible);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.True(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));

        var saved = (await scope.Sync.LoadSettingsAsync()).Settings;
        Assert.Equal(SyncProvider.AccountServer, saved.Provider);
        Assert.Equal(handler.User.Id, saved.AccountUserId);
        Assert.Equal("fictional-updated-sync-key", saved.EncryptionKey);
        Assert.Equal(custom, saved with
        {
            Provider = custom.Provider, AccountRegion = custom.AccountRegion, AccountServerUrl = custom.AccountServerUrl,
            AccountUserId = custom.AccountUserId, EncryptionKey = custom.EncryptionKey
        });
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsVisible);
        Assert.False(scope.Get<Button>("S3DiscardSettingsButton").IsVisible);
        Assert.False(scope.Get<TextBlock>("S3DraftStatusText").IsVisible);
        Assert.True(scope.Get<Button>("S3SyncNowButton").IsEnabled);

        scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)SyncProvider.WebDav;
        Assert.True(scope.Get<Control>("WebDavConnectionPanel").IsVisible);
        Assert.True(scope.Get<Control>("SettingsS3AdvancedExpander").IsVisible);
        Assert.True(scope.Get<Control>("SettingsS3ProfileExpander").IsVisible);
        Assert.Equal(custom.WebDavPassword, scope.Get<TextBox>("WebDavPasswordBox").Text);
        Assert.Equal(saved.EncryptionKey, scope.Get<TextBox>("S3EncryptionKeyBox").Text);
        scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)SyncProvider.S3;
        Assert.Equal(custom.SecretKey, scope.Get<TextBox>("S3SecretKeyBox").Text);
        Assert.Equal(custom.Prefix, scope.Get<TextBox>("S3PrefixBox").Text);
        scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)SyncProvider.AccountServer;
        Assert.Equal(saved, scope.Call<S3SyncSettings>("ReadS3SyncSettingsFromControls"));
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsVisible);
    });

    [Theory]
    [InlineData("signed-out", "登录 Plus")]
    [InlineData("free", "需要 Plus")]
    [InlineData("lifetime", "需要 Plus")]
    [InlineData("expired-retained", "届时自动销毁")]
    [InlineData("expired-past", "保留期已结束")]
    [InlineData("region-switch", "重新打开应用")]
    public Task OfficialCloudSettingsKeepNecessaryAccountWarningsAndDisableUnavailableSync(string state, string expected) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var region = state == "region-switch" ? AccountRegion.International : AccountRegion.China;
        var user = new AccountUser(Guid.NewGuid(), "fictional_cloud_reader", "cloud@example.invalid");
        var expiry = state switch
        {
            "expired-retained" => DateTimeOffset.UtcNow.AddDays(-5),
            "expired-past" => DateTimeOffset.UtcNow.AddDays(-40),
            _ => DateTimeOffset.UtcNow.AddDays(365)
        };
        var member = state == "signed-out" ? null : CloudSettingsMembership(user, region,
            state is "free" or "lifetime" ? state : "pro", state is "free" or "lifetime" ? null : expiry);
        var status = new AccountStatus(AccountRegions.DefaultServerUrl(region), state == "signed-out" ? null : user,
            DateTimeOffset.UtcNow.AddDays(30), region, state == "region-switch");
        PresentOfficialCloudSettings(scope, status, member, user.Id);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();

        var notice = scope.Get<TextBlock>("AccountSyncNoticeText");
        Assert.True(notice.IsEffectivelyVisible);
        Assert.Contains(expected, notice.Text);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.True(scope.Get<Button>("AccountNavigationButton").IsEnabled);
        Assert.False(scope.Get<Button>("S3TestConnectionButton").IsVisible);
        var indicator = scope.Get<ComboBox>("SyncProviderBox").GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Ellipse>().Single(control => control.Name == "AccountSyncSelectedLoginIndicator");
        Assert.Equal(status.IsLoggedIn, indicator.IsVisible);
        if (state == "expired-retained")
        {
            Assert.Contains(MemberEntitlements.CloudRetentionEndsAt(expiry).LocalDateTime.ToString("yyyy/MM/dd"), notice.Text);
            Assert.Contains("续费", ToolTip.GetTip(notice)?.ToString());
        }
        scope.Call("PopulateS3SyncControls", scope.Field<S3SyncStoredSettings>("_s3SyncStoredSettings").Settings with { Provider = SyncProvider.WebDav });
        await Render();
        Assert.False(notice.IsVisible);
        indicator = scope.Get<ComboBox>("SyncProviderBox").GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Ellipse>().Single(control => control.Name == "AccountSyncSelectedLoginIndicator");
        Assert.False(indicator.IsVisible);
        scope.Call("PopulateS3SyncControls", (object?)null);
        await Render();
        indicator = scope.Get<ComboBox>("SyncProviderBox").GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Ellipse>().Single(control => control.Name == "AccountSyncSelectedLoginIndicator");
        Assert.Equal(status.IsLoggedIn, indicator.IsVisible);
    });

    [Theory]
    [InlineData(1024, 664, "zh-CN")]
    [InlineData(1294, 818, "zh-CN")]
    [InlineData(1024, 664, "en-US")]
    [InlineData(1294, 818, "en-US")]
    public Task OfficialCloudSettingsShowCompactActionsAndAnAccessibleKeyAtSmallWindowSizes(int width, int height, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = width;
        scope.Window.Height = height;
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        var user = new AccountUser(Guid.NewGuid(), "fictional_cloud_reader", "cloud@example.invalid");
        var status = new AccountStatus(AccountService.DefaultServerUrl, user, DateTimeOffset.UtcNow.AddDays(30));
        PresentOfficialCloudSettings(scope, status, CloudSettingsMembership(user, AccountRegion.China, "pro", DateTimeOffset.UtcNow.AddDays(365)), user.Id);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();

        Assert.False(scope.Get<TextBlock>("AccountSyncNoticeText").IsVisible);
        Assert.Null(scope.Window.FindControl<Control>("AccountSyncNavigationButton"));
        var indicator = scope.Get<ComboBox>("SyncProviderBox").GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Ellipse>().Single(control => control.Name == "AccountSyncSelectedLoginIndicator");
        Assert.True(indicator.IsEffectivelyVisible);
        foreach (var name in new[] { "SyncProviderBox", "S3AutomaticSyncCheck", "S3DownloadBookFilesCheck", "AccountSyncEncryptionKeyBox", "S3SyncNowButton" })
        {
            var control = scope.Get<Control>(name);
            Assert.True(control.IsEffectivelyVisible, name);
            AssertWithinWindow(control, scope.Window);
        }
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsVisible);
        Assert.False(scope.Get<Button>("S3TestConnectionButton").IsVisible);
        Capture(scope.Window, $"{language}-{width}-kkindle-cloud");
        scope.Get<TextBox>("AccountSyncEncryptionKeyBox").BringIntoView();
        await Render();
        var viewport = scope.Get<ScrollViewer>("SettingsScrollViewer");
        var key = scope.Get<TextBox>("AccountSyncEncryptionKeyBox");
        var position = key.TranslatePoint(default, viewport)!.Value;
        Assert.True(position.Y >= 0 && position.Y + key.Bounds.Height <= viewport.Bounds.Height + 1);
        var help = scope.Get<TextBlock>("AccountSyncEncryptionKeyHelpText");
        var helpPosition = help.TranslatePoint(default, viewport)!.Value;
        Assert.True(helpPosition.Y >= 0 && helpPosition.Y + help.Bounds.Height <= viewport.Bounds.Height + 1);
        Capture(scope.Window, $"{language}-{width}-kkindle-cloud-key");

        scope.Get<ComboBox>("SyncProviderBox").IsDropDownOpen = true;
        await Render();
        var menuIndicator = scope.Get<ComboBoxItem>("AccountSyncProviderItem").GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Ellipse>().Single(control => control.Name == "AccountSyncMenuLoginIndicator");
        Assert.True(menuIndicator.IsEffectivelyVisible);
        scope.Call("PresentAccountSummary", status with { User = null, ExpiresAt = null }, null);
        await Render();
        Assert.False(indicator.IsVisible);
        Assert.False(menuIndicator.IsVisible);
        scope.Call("PresentAccountSummary", status, CloudSettingsMembership(user, AccountRegion.China, "pro", DateTimeOffset.UtcNow.AddDays(365)));
        await Render();
        Assert.True(indicator.IsVisible);
        Assert.True(menuIndicator.IsVisible);
        scope.Get<ComboBox>("SyncProviderBox").IsDropDownOpen = false;
    });

    [Fact]
    public Task OfficialCloudSettingsRemoveHiddenConnectionOptionsFromSearchAndInvalidateOldResults() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var search = scope.Get<TextBox>("SettingsSearchBox");
        search.Text = "Endpoint";
        await Until(() => scope.Get<Border>("SettingsSearchResultsPanel").IsVisible);
        Assert.Contains(scope.Call<List<SettingsSearchResult>>("BuildSettingsSearchResults", "Endpoint"),
            result => ReferenceEquals(result.Target, scope.Get<Expander>("SettingsS3Expander")));

        var user = new AccountUser(Guid.NewGuid(), "fictional_cloud_reader", "cloud@example.invalid");
        PresentOfficialCloudSettings(scope, new AccountStatus(AccountService.DefaultServerUrl, user, DateTimeOffset.UtcNow.AddDays(30)),
            CloudSettingsMembership(user, AccountRegion.China, "pro", DateTimeOffset.UtcNow.AddDays(365)), user.Id);
        await Until(() => scope.Get<TextBlock>("SettingsSearchNoResultsText").IsVisible);
        foreach (var query in new[] { "Endpoint", "自动同步间隔", "并发请求" })
        {
            var results = scope.Call<List<SettingsSearchResult>>("BuildSettingsSearchResults", query);
            Assert.DoesNotContain(results,
                result => ReferenceEquals(result.Target, scope.Get<Expander>("SettingsS3AdvancedExpander"))
                    || ReferenceEquals(result.Target, scope.Get<Expander>("SettingsS3ProfileExpander")));
            foreach (var result in results.Where(result => ReferenceEquals(result.Target, scope.Get<Expander>("SettingsS3Expander"))))
                Assert.DoesNotContain(query, result.SearchCorpus, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(scope.Call<List<SettingsSearchResult>>("BuildSettingsSearchResults", "Calibre"), result => result.Category == "Library");

        scope.Call("PopulateS3SyncControls", scope.Field<S3SyncStoredSettings>("_s3SyncStoredSettings").Settings with { Provider = SyncProvider.S3 });
        await Until(() => scope.Get<Border>("SettingsSearchResultsPanel").IsVisible);
        Assert.Contains(scope.Call<List<SettingsSearchResult>>("BuildSettingsSearchResults", "Endpoint"),
            result => ReferenceEquals(result.Target, scope.Get<Expander>("SettingsS3Expander")));
    });

    private static AccountMembership CloudSettingsMembership(AccountUser user, AccountRegion region, string tier, DateTimeOffset? expiry) =>
        new(user.Id, region, tier, tier is "lifetime" or "pro", expiry, 6, 2, 2, 0, 0, 0, 0, 0, 0, 0, null);

    private static void PresentOfficialCloudSettings(TestWindow scope, AccountStatus status, AccountMembership? membership, Guid userId)
    {
        scope.Call("PresentAccountSummary", status, membership);
        var stored = scope.Field<S3SyncStoredSettings>("_s3SyncStoredSettings");
        var settings = stored.Settings with
        {
            Provider = SyncProvider.AccountServer, Enabled = true, AutomaticSyncEnabled = false,
            AccountRegion = status.Region, AccountServerUrl = status.ServerUrl, AccountUserId = userId,
            EncryptionKey = "fictional-cloud-settings-key"
        };
        scope.Set("_s3SyncStoredSettings", stored with { Settings = settings });
        scope.Call("PopulateS3SyncControls", (object?)null);
    }
}
