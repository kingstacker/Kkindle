using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN", 1024, AppTheme.Classic)]
    [InlineData("en-US", 1294, AppTheme.Night)]
    public Task ReadingBadges_ShowProgressFiltersDetailsAndPersistNotificationChoice(string language, int width, AppTheme theme) => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = language, MainTheme = theme, OnboardingCompleted = true, NetworkEnabled = false,
            AutoUpdateCheckEnabled = false, AutoConnectDevice = false, GridGalleryDisplay = false
        });
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        var (book, file) = await SeedSettingsReadingResetAsync(scope);
        var reader = scope.Field<ReaderDataService>("_readerData");
        await reader.AddReadingTimeAsync(book, file, 3480, 60, 6, 10);
        scope.Window.Width = width;
        scope.Window.Height = 818;
        scope.Call("ShowStage3Page", scope.Get<Control>("ReadingDashboardPage"), null);
        await scope.Call<Task>("RefreshReadingDashboardAsync");
        await Render();
        Assert.Single(scope.Window.RecentAchievements);
        Assert.Equal(language == "zh-CN" ? "已获得 1 / 12" : "1 / 12 earned", scope.Get<TextBlock>("AchievementCountText").Text);
        Assert.False(scope.Get<TextBlock>("DashboardStatusText").IsVisible);
        AssertWithinWindow(scope.Get<Button>("AchievementViewAllButton"), scope.Window);
        Capture(scope.Window, $"{language}-badges-overview");

        scope.Get<Button>("AchievementViewAllButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.True(scope.Get<Control>("AchievementWall").IsVisible);
        Assert.Equal(12, scope.Window.VisibleAchievements.Count);
        Capture(scope.Window, $"{language}-badges-wall");
        scope.Get<ComboBox>("AchievementFilterBox").SelectedIndex = 1;
        await Render();
        Assert.Single(scope.Window.VisibleAchievements);
        var button = scope.Get<Control>("AchievementWall").GetVisualDescendants().OfType<Button>()
            .First(b => b.DataContext is ReadingAchievementViewModel);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.True(scope.Get<Control>("AchievementDetails").IsVisible);
        Assert.Equal(language == "zh-CN" ? "累计阅读 1 小时" : "Read for 1 hours in total", scope.Get<TextBlock>("AchievementDetailRule").Text);
        Capture(scope.Window, $"{language}-badges-detail");

        scope.Get<ComboBox>("AchievementFilterBox").SelectedIndex = 2;
        Assert.Equal(11, scope.Window.VisibleAchievements.Count);
        scope.Get<CheckBox>("AchievementNotificationsCheckBox").IsChecked = false;
        await Until(() => !scope.Field<AppSettings>("_appSettings").ReadingAchievementNotificationsEnabled);
        await Until(() => !new AppSettingsStore(scope.Paths).LoadSynchronously().ReadingAchievementNotificationsEnabled);
        await reader.InitializeAsync();
        Assert.True((await reader.GetReadingAchievementsAsync()).Items.Single(i => i.Definition.Id == "time-1").Award!.Seen);
    });

    [Fact]
    public Task ReadingBadges_ResetSelectionIsExplicitAndClearsAwardsWhenConfirmed() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var (book, file) = await SeedSettingsReadingResetAsync(scope);
        var reader = scope.Field<ReaderDataService>("_readerData");
        await reader.AddReadingTimeAsync(book, file, 3600, 90, 9, 10);
        Assert.Single((await reader.GetReadingAchievementsAsync()).Items, i => i.IsEarned);
        Assert.False(scope.Get<CheckBox>("ResetReadingAchievementsCheckBox").IsChecked);
        scope.Get<CheckBox>("ResetReadingAchievementsCheckBox").IsChecked = true;
        var reset = scope.Call<Task>("ResetReadingDataFromSettingsAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Contains("勋章和完读记录也将清除", scope.Get<TextBlock>("ConfirmationMessageText").Text!);
        Assert.False(scope.Get<CheckBox>("ResetReadingAchievementsCheckBox").IsEnabled);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain((await reader.GetReadingAchievementsAsync()).Items, i => i.IsEarned);
        Assert.True(scope.Get<CheckBox>("ResetReadingAchievementsCheckBox").IsEnabled);
    });
}
