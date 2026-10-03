using Avalonia;
using Avalonia.Controls;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task OnboardingFillsCompactWindowAndRestoresMainWindow(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        var originalMinimum = new Size(scope.Window.MinWidth, scope.Window.MinHeight);
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { OnboardingCompleted = false });
        scope.Call("ShowOnboardingIfNeeded");
        await Render();

        var frame = scope.Get<Border>("OnboardingFrame");
        Assert.Equal(scope.Window.ClientSize, frame.Bounds.Size);
        Assert.Equal(new Thickness(0), frame.Margin);
        Assert.InRange(scope.Window.Width, 1, 760);
        Assert.InRange(scope.Window.Height, 1, 576);
        Assert.False(scope.Window.CanResize);
        for (var step = 1; step <= 3; step++)
        {
            scope.Set("_onboardingStep", step);
            scope.Call("UpdateOnboardingPage");
            await Render();
            AssertWithinWindow(scope.Get<Button>(step == 3 ? "OnboardingFinishButton" : "OnboardingNextButton"), scope.Window);
            Capture(scope.Window, $"{language}-onboarding-{step}");
        }

        await scope.Call<Task>("CompleteOnboardingAsync", (object?)null);
        await Render();
        Assert.False(scope.Get<Control>("OnboardingOverlay").IsVisible);
        // Native windows round dimensions to physical pixels.
        Assert.InRange(Math.Abs(scope.Window.Height - scope.Window.Width / ((1 + Math.Sqrt(5)) / 2)), 0, 1);
        Assert.Equal(WindowState.Normal, scope.Window.WindowState);
        if (scope.Window.Screens.ScreenFromWindow(scope.Window) is { } screen)
        {
            var scale = screen.Scaling;
            Assert.InRange(Math.Abs(scope.Window.Position.X + scope.Window.Width * scale / 2
                - (screen.WorkingArea.X + screen.WorkingArea.Width / 2d)), 0, 1);
            Assert.InRange(Math.Abs(scope.Window.Position.Y + scope.Window.Height * scale / 2
                - (screen.WorkingArea.Y + screen.WorkingArea.Height / 2d)), 0, 1);
        }
        Assert.Equal(originalMinimum, new Size(scope.Window.MinWidth, scope.Window.MinHeight));
        Assert.True(scope.Window.CanResize);
    });
}
