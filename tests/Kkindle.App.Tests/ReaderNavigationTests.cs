using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ReaderNavigationSingleClickShowsDetailsAndCoverReadAction() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        Assert.Null(scope.Window.FindControl<Button>("DetailCoverReadButton"));
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await Task.Delay(600);
        await Render();
        Assert.True(scope.Get<Control>("LibraryDetailPane").IsVisible);
        var coverRead = Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);
        var readPoint = coverRead.TranslatePoint(new Point(coverRead.Bounds.Width / 2, coverRead.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(readPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(readPoint, MouseButton.Left, RawInputModifiers.None);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);
        await Until(() => scope.Field<object?>("_readerDocument") is not null);
        await scope.Call<Task>("ReturnFromReaderAsync");
        Assert.True(scope.Get<Control>("LibraryRoot").IsVisible);
        Assert.False(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.Single(scope.Window.ViewModel.Books, card => card.IsSelected);
        await scope.Call<Task>("OpenApplicationSettingsAsync");
        scope.Call("ShowLibraryPage");
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
    });

    [Theory]
    [InlineData("ReadingMaterialsPage", "ReaderNotesNavigationButton", "返回笔记")]
    [InlineData("ReadingDashboardPage", "ReadingDashboardButton", "返回阅读数据")]
    public Task ReaderNavigationReturnsToOriginAfterToolbarAutoHide(string page, string navigation, string label) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>(page), scope.Get<Button>(navigation));
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Set("_readerTocExpanded", true);
        scope.Call("ApplyReaderPanelLayout");
        Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(scope.Get<Button>("ReaderBackButton")));
        await Render();
        var back = scope.Get<Button>("ReaderBackButton");
        Assert.True(back.Bounds.Width >= back.DesiredSize.Width - 1);
        Assert.InRange(back.Bounds.Height, 34, 35);
        scope.Call("SetReaderToolbarsVisible", false);
        Assert.True(scope.Get<Button>("ReaderBackButton").IsEffectivelyVisible);
        scope.Get<Button>("ReaderBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>(page).IsEffectivelyVisible);
        Assert.True(scope.Window.IsVisible);
    });

    [Fact]
    public Task ReaderNavigationUsesOneBackArrowAndOneTocToggle() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Set("_readerTocExpanded", true);
        scope.Set("_readerTocMinimal", false);
        scope.Call("ApplyReaderPanelLayout");
        await Render();
        var expandedBack = scope.Get<Button>("ReaderBackButton");
        var collapsedBack = scope.Get<Button>("ReaderCollapsedBackButton");
        var toc = scope.Get<Button>("ReaderTocToggleButton");
        Assert.True(expandedBack.IsEffectivelyVisible);
        Assert.False(collapsedBack.IsEffectivelyVisible);
        Assert.True(toc.IsEffectivelyVisible);
        Assert.Null(scope.Window.FindControl<Button>("ReaderTocCloseButton"));
        Assert.True(expandedBack.TranslatePoint(default, scope.Window)!.Value.X <
                    scope.Get<TextBlock>("ReaderBookInfoText").TranslatePoint(default, scope.Window)!.Value.X);
        toc.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.False(expandedBack.IsEffectivelyVisible);
        Assert.True(collapsedBack.IsEffectivelyVisible);
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible);
        collapsedBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>("LibraryRoot").IsVisible);
    });

    [Fact]
    public Task ReaderNavigationSettingsClosesReaderBeforeShowingSettings() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await scope.Call<Task>("OpenApplicationSettingsAsync");
        Assert.False(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>("SettingsPage").IsEffectivelyVisible);
        Assert.True(scope.Window.IsVisible);
    });
}
