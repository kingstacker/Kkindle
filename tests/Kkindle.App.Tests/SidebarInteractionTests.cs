using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("BookManagementSectionButton")]
    [InlineData("SettingsNavigationButton")]
    [InlineData("AllBooksButton")]
    public Task NightSidebarKeepsOneBackgroundThroughoutClick(string name) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)AppTheme.Night;
        await Render();
        var button = scope.Get<Button>(name);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseMove(point);
        await Render();
        var expected = ColorOf(button.Background);
        var border = button.GetVisualDescendants().OfType<Border>().First();
        Assert.Null(button.Transitions);
        Assert.Null(border.Transitions);

        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        foreach (var delay in new[] { 0, 30, 140 })
        {
            if (delay > 0) await Task.Delay(delay);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Equal(expected, ColorOf(button.Background));
            Assert.Equal(expected, ColorOf(border.Background));
            Assert.Equal(Colors.Transparent, ColorOf(button.BorderBrush));
        }
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await Render();
        Assert.Equal(expected, ColorOf(button.Background));
        Assert.Equal(expected, ColorOf(border.Background));
    });

    [Theory]
    [InlineData(AppTheme.Classic)]
    [InlineData(AppTheme.Night)]
    [InlineData(AppTheme.Green)]
    [InlineData(AppTheme.MistBlue)]
    [InlineData(AppTheme.Ivory)]
    public Task SidebarPointerExitImmediatelyRestoresThemeBackground(AppTheme theme) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)theme;
        await Render();
        foreach (var name in new[] { "ReadingSectionButton", "SettingsNavigationButton", "AllBooksButton" })
        {
            var button = scope.Get<Button>(name);
            var border = button.GetVisualDescendants().OfType<Border>().First();
            var resting = ColorOf(border.Background);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), scope.Window)!.Value;
            scope.Window.MouseMove(point);
            await Render();
            scope.Window.MouseMove(new Point(500, 20));
            foreach (var delay in new[] { 0, 30, 140 })
            {
                if (delay > 0) await Task.Delay(delay);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.Equal(resting, ColorOf(border.Background));
                Assert.Null(border.Transitions);
            }
        }
    });
}
