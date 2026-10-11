using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task MedalCountsFilterAndPersistWithoutCheckbox(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = language, OnboardingCompleted = true, ShowUnearnedReadingMedals = false
        });
        var dashboard = new ReadingDashboard(1, 0, 7200, 5, 0, 5, [], []);
        scope.Call("PopulateReadingMedals", dashboard);
        var panel = scope.Get<WrapPanel>("DashboardMedalsPanel");
        var unlocked = scope.Get<Button>("DashboardUnlockedMedalsButton");
        var all = scope.Get<Button>("DashboardAllMedalsButton");
        Assert.Equal("5", unlocked.Content);
        Assert.Equal("40", all.Content);
        Assert.Equal(5, panel.Children.Count);
        all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.Equal(40, panel.Children.Count);
        Assert.Contains("active", all.Classes);
        Assert.False(scope.Field<bool>("_appSettingsAutoSaveShowStatus"));
        await scope.Call<Task<bool>>("FlushAppSettingsAsync");
        Assert.True(scope.Field<AppSettings>("_appSettings").ShowUnearnedReadingMedals);
        unlocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.Equal(5, panel.Children.Count);
        Assert.Contains("active", unlocked.Classes);
        Assert.False(scope.Field<bool>("_appSettingsAutoSaveShowStatus"));
        await scope.Call<Task<bool>>("FlushAppSettingsAsync");
        Assert.False(scope.Field<AppSettings>("_appSettings").ShowUnearnedReadingMedals);
        scope.Call("PopulateReadingMedals", new ReadingDashboard(0, 0, 0, 0, 0, 0, [], []));
        Assert.Empty(panel.Children);
        Assert.True(scope.Get<TextBlock>("DashboardMedalsEmptyText").IsVisible);
        all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(40, panel.Children.Count);
    });
}
