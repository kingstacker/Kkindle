using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData(1024, 664, "zh-CN")]
    [InlineData(1294, 804, "zh-CN")]
    [InlineData(1920, 1080, "zh-CN")]
    [InlineData(1024, 664, "en-US")]
    public Task KindleToolbarMatchesComputerLibrary(int width, int height, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = width;
        scope.Window.Height = height;
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        foreach (var (title, index) in new[] { "Alpha · 阅读与思考", "Middle · A Reader's Notes", "Zulu · The Art of Reading" }.Select((title, index) => (title, index)))
            scope.Window.DeviceBooks.Add(new KindleBookCardViewModel(new KindleBook
            {
                Title = title, Authors = new[] { "Zulu", "Middle", "Alpha" }[index],
                Format = index == 2 ? "pdf" : "azw3", RelativePath = title + (index == 2 ? ".pdf" : ".azw3"),
                Size = 1_600_000, ModifiedAt = DateTimeOffset.UtcNow.AddDays(-index)
            }));
        scope.Get<TextBox>("DeviceBookSearchBox").Text = " ";
        scope.Call("ShowStage3Page", scope.Get<Grid>("DevicePage"), null);
        await Render();
        Assert.True(scope.Get<ScrollViewer>("DeviceBookGridScroll").IsVisible);
        Assert.False(scope.Get<ScrollViewer>("DeviceBookListScroll").IsVisible);
        Assert.Equal(3, scope.Window.VisibleDeviceBooks.Count);
        AssertKindleToolbar(scope);
        var gridToolbarBounds = GetToolbarControlBounds(scope.Get<StackPanel>("DeviceToolbarActions"), scope.Window);
        Capture(scope.Window, $"{language}-{width}-kindle-grid");

        var viewButton = scope.Get<Button>("DeviceViewToggleButton");
        viewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(viewButton.ContextMenu!.IsOpen);
        Assert.True(scope.Get<MenuItem>("DeviceGridViewMenuItem").IsChecked);
        await Render();
        var selectedView = scope.Get<MenuItem>("DeviceGridViewMenuItem");
        var selectedViewText = selectedView.GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.NotEqual(
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(selectedView.Background).Color,
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(selectedViewText.Foreground).Color);
        Capture(scope.Window, $"{language}-{width}-kindle-view-menu");
        viewButton.ContextMenu.Close();
        scope.Get<MenuItem>("DeviceListViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(scope.Get<ScrollViewer>("DeviceBookListScroll").IsVisible);
        Assert.Equal(3, scope.Window.VisibleDeviceBooks.Count);
        await Render();
        Assert.Equal(gridToolbarBounds, GetToolbarControlBounds(scope.Get<StackPanel>("DeviceToolbarActions"), scope.Window));

        // Sorting is always available, while opening filters leaves room for every control.
        scope.Get<ComboBox>("DeviceBookSortBox").SelectedIndex = 1;
        Assert.Equal("Zulu · The Art of Reading", scope.Window.VisibleDeviceBooks[0].Title);
        scope.Get<ComboBox>("DeviceBookSortBox").SelectedIndex = 2;
        scope.Get<Button>("DeviceBookFilterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        AssertKindleToolbar(scope);
        AssertWithinWindow(scope.Get<ComboBox>("DeviceBookPresenceFilterBox"), scope.Window);
        Capture(scope.Window, $"{language}-{width}-kindle-list-filters");
        scope.Get<ComboBox>("DeviceBookFormatFilterBox").SelectedIndex = 2;
        Assert.Single(scope.Window.VisibleDeviceBooks);
        Assert.Equal("pdf", scope.Window.VisibleDeviceBooks[0].Book.Format);
        scope.Get<MenuItem>("DeviceGridViewMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(scope.Get<ScrollViewer>("DeviceBookGridScroll").IsVisible);
        Assert.Single(scope.Window.VisibleDeviceBooks);
        scope.Get<Button>("DeviceBookFilterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("active", scope.Get<Button>("DeviceBookFilterButton").Classes);
    });

    private static void AssertKindleToolbar(TestWindow scope)
    {
        var toolbar = scope.Get<Grid>("DeviceToolbar");
        var search = scope.Get<TextBox>("DeviceBookSearchBox");
        var actions = scope.Get<StackPanel>("DeviceToolbarActions");
        AssertWithinWindow(search, scope.Window);
        AssertWithinWindow(actions, scope.Window);
        foreach (var action in actions.GetVisualDescendants().OfType<Control>().Where(control => control is Button or ComboBox))
            AssertWithinWindow(action, scope.Window);
        var searchPosition = search.TranslatePoint(default, scope.Window)!.Value;
        var actionsPosition = actions.TranslatePoint(default, scope.Window)!.Value;
        Assert.True(search.Bounds.Width >= 239);
        Assert.True(actionsPosition.Y >= 38);
        Assert.True(searchPosition.X + search.Bounds.Width + 20 <= actionsPosition.X
                    || searchPosition.Y + search.Bounds.Height <= actionsPosition.Y,
            "Search and toolbar actions must not overlap.");
        var toolbarRight = toolbar.TranslatePoint(default, scope.Window)!.Value.X + toolbar.Bounds.Width;
        Assert.InRange(Math.Abs(toolbarRight - actionsPosition.X - actions.Bounds.Width), 0, 1);
    }
}
