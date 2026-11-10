using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("ReadingMaterialsSearchBox")]
    [InlineData("CancelReadingMaterialsExportButton")]
    public Task ReadingMaterialExportEscapeHidesActionsWithFocusedControl(string focus) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await LoadExportNotesAsync(scope);
        scope.Get<Button>("ExportReadingMaterialsToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var search = scope.Get<TextBox>("ReadingMaterialsSearchBox");
        search.Text = "第一条";
        Assert.True(scope.Get<Control>(focus).Focus());
        scope.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        scope.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Render();

        AssertExportModeClosed(scope);
        Assert.Equal("第一条", search.Text);
        Assert.Single(scope.Window.ReadingMaterials);
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
    });

    [Fact]
    public Task ReadingMaterialExportSelectAllUsesCurrentFilterAndRefreshPreservesSelection() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await LoadExportNotesAsync(scope);
        var search = scope.Get<TextBox>("ReadingMaterialsSearchBox");
        search.Text = "第一条";
        await Until(() => scope.Window.ReadingMaterials.Count == 1);
        scope.Get<Button>("ExportReadingMaterialsToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Assert.Single(scope.Window.ReadingMaterials).IsSelected);
        search.Text = null;
        await Until(() => scope.Window.ReadingMaterials.Count == 2);
        Assert.Equal(1, scope.Window.ReadingMaterials.Count(item => item.IsSelected));

        var selectAll = scope.Get<Button>("SelectAllReadingMaterialsButton");
        Assert.Equal("全选", selectAll.Content);
        selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(scope.Window.ReadingMaterials, item => Assert.True(item.IsSelected));
        Assert.Equal("取消全选", selectAll.Content);
        selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
        Assert.False(scope.Get<Button>("ExportReadingMaterialsMarkdownButton").IsEnabled);

        scope.Window.ReadingMaterialGroups[0].IsSelected = true;
        scope.Call("UpdateReadingMaterialsActionState");
        var selectedId = Assert.Single(scope.Window.ReadingMaterials, item => item.IsSelected).KindleClipping!.Id;
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Equal(selectedId, Assert.Single(scope.Window.ReadingMaterials, item => item.IsSelected).KindleClipping!.Id);
        Assert.True(scope.Get<Button>("ExportReadingMaterialsMarkdownButton").IsEnabled);
        Assert.True(scope.Get<Button>("ExportReadingMaterialsTextButton").IsEnabled);

        search.Text = "无匹配结果";
        await Until(() => scope.Window.ReadingMaterials.Count == 0);
        Assert.False(selectAll.IsEnabled);
        Assert.False(scope.Get<Button>("ExportReadingMaterialsTextButton").IsEnabled);
        Assert.True(scope.Get<Button>("CancelReadingMaterialsExportButton").IsEnabled);
        scope.Get<Button>("CancelReadingMaterialsExportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        search.Text = null;
        await Until(() => scope.Window.ReadingMaterials.Count == 2);
        AssertExportModeClosed(scope);
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
    });

    [Fact]
    public Task ReadingMaterialExportEscapeClosesMessageBeforeLeavingExport() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await LoadExportNotesAsync(scope);
        scope.Get<Button>("ExportReadingMaterialsToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var message = scope.Call<Task>("ShowMessageAsync", "测试提示", "虚构提示内容", null);
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        scope.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        scope.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await message;
        Assert.True(scope.Get<Border>("ReadingMaterialsExportPanel").IsVisible);
        scope.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        scope.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        AssertExportModeClosed(scope);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReadingMaterialExportNavigationClearsModeAndSelection(bool library) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await LoadExportNotesAsync(scope);
        scope.Get<Button>("ExportReadingMaterialsToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (library) scope.Call("ShowLibraryPage");
        else scope.Call("ShowStage3Page", scope.Get<Grid>("SettingsPage"), null);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        AssertExportModeClosed(scope);
        Assert.All(scope.Window.ReadingMaterials, item => Assert.False(item.IsSelected));
    });

    [Theory]
    [InlineData("zh-CN", 1024)]
    [InlineData("en-US", 1024)]
    [InlineData("zh-CN", 1294)]
    [InlineData("en-US", 1294)]
    public Task ReadingMaterialExportToolbarFitsAndAligns(string language, int width) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await LoadExportNotesAsync(scope);
        scope.Window.Width = width;
        scope.Get<ComboBox>("UiLanguageBox").SelectedIndex = language == "en-US" ? 1 : 0;
        scope.Call("ApplyReadingMaterialsFilter");
        await Render();
        AssertWithinWindow(scope.Get<Button>("RefreshReadingMaterialsButton"), scope.Window);
        scope.Get<Button>("ExportReadingMaterialsToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        var search = scope.Get<TextBox>("ReadingMaterialsSearchBox");
        var origin = search.TranslatePoint(default, scope.Window)!.Value;
        foreach (var name in new[] { "ReadingMaterialsSourceBox", "SelectAllReadingMaterialsButton",
                     "ExportReadingMaterialsMarkdownButton", "ExportReadingMaterialsTextButton", "CancelReadingMaterialsExportButton" })
        {
            var control = scope.Get<Control>(name);
            Assert.True(control.IsEffectivelyVisible, name);
            AssertWithinWindow(control, scope.Window);
            Assert.Equal(origin.Y, control.TranslatePoint(default, scope.Window)!.Value.Y, 1);
            Assert.Equal(search.Bounds.Height, control.Bounds.Height, 1);
        }
        Assert.True(search.Bounds.Width >= 120);
        Assert.Contains(scope.Get<Border>("ReadingMaterialsExportPanel").GetVisualAncestors(),
            ancestor => ReferenceEquals(ancestor, scope.Get<Grid>("ReadingMaterialsToolbar")));
        Capture(scope.Window, $"notes-export-{language}-{width}");
    });

    private static async Task LoadExportNotesAsync(TestWindow scope)
    {
        var reader = Reader(scope, "KOBO-EXPORT-UI", ReaderDeviceProfiles.Kobo);
        var service = new ReaderService
        {
            Devices = [reader],
            Notes = [
                new KindleClipping { Id = "export:one", BookTitle = "测试书籍一", Content = "第一条摘录", Type = KindleClippingType.Highlight },
                new KindleClipping { Id = "export:two", BookTitle = "测试书籍二", Content = "第二条摘录", Type = KindleClippingType.Highlight }
            ]
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
    }

    private static void AssertExportModeClosed(TestWindow scope)
    {
        Assert.False(scope.Field<bool>("_readingMaterialsExportMode"));
        Assert.False(scope.Get<Border>("ReadingMaterialsExportPanel").IsVisible);
        Assert.False(scope.Get<Button>("SelectAllReadingMaterialsButton").IsVisible);
        foreach (var name in new[] { "ExportReadingMaterialsMarkdownButton", "ExportReadingMaterialsTextButton", "CancelReadingMaterialsExportButton" })
            Assert.False(scope.Get<Button>(name).IsEffectivelyVisible);
        Assert.True(scope.Get<Button>("ExportReadingMaterialsToggleButton").IsEffectivelyVisible);
        Assert.True(scope.Get<Button>("RefreshReadingMaterialsButton").IsEffectivelyVisible);
        Assert.All(scope.Window.ReadingMaterialGroups, group => Assert.False(group.IsExportMode));
    }
}
