using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task NotesGroupTitleTogglesReadableExcerptLayout() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        using var item = new Stage3ReadingMaterialViewModel(Kkindle.Core.ReadingMaterialSource.Local,
            "源泉", "划线", "第一章", "位置 1280", "独立思考，是阅读带给我们的重要收获。\n在文字里寻找自己的答案。",
            "这段话让我想到：阅读之后，还要留出自己的思考时间。", DateTimeOffset.Now, null, null);
        using var group = new Stage3ReadingMaterialGroupViewModel(Kkindle.Core.ReadingMaterialSource.Local,
            "源泉", [item], null, isExpanded: false);
        scope.Window.ReadingMaterialGroups.Clear();
        scope.Window.ReadingMaterialGroups.Add(group);
        scope.Get<TextBlock>("ReadingMaterialsEmptyText").IsVisible = false;
        await Render();
        var titleButton = scope.Get<Grid>("ReadingMaterialsPage").GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.Tag, group) && button.Content is StackPanel);
        titleButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.True(group.IsExpanded);
        Assert.Contains(scope.Window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == item.QuoteLabel && text.Bounds.Height > 0);
        Capture(scope.Window, "notes-refined-expanded");
    });

    [Fact]
    public Task DashboardFiveRecentBooksFitWithinHeightLimit() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingDashboardPage"), null);
        await scope.Call<Task>("RefreshReadingDashboardAsync");
        scope.Window.DashboardRecentItems.Clear();
        for (var i = 0; i < 5; i++)
            scope.Window.DashboardRecentItems.Add(new Stage3DashboardRecentViewModel(
                $"最近阅读书籍 {i + 1}：完整显示标题与阅读时间", 33, 80, DateTimeOffset.Now));
        await Render();
        var scroll = scope.Get<ScrollViewer>("DashboardRecentScrollViewer");
        Assert.Equal(220, scroll.MaxHeight);
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height,
            $"Recent books extent {scroll.Extent.Height} exceeds viewport {scroll.Viewport.Height}");
        var items = Assert.IsType<ItemsControl>(scroll.Content);
        Assert.Equal(5, items.ItemCount);
        Assert.Equal(220, scroll.Extent.Height);
        scope.Get<TextBlock>("DashboardRecentEmptyText").IsVisible = false;
        scope.Window.DashboardRecentItems.Add(new Stage3DashboardRecentViewModel(
            "第六本仍可滚动查看", 33, 80, DateTimeOffset.Now));
        await Render();
        Assert.Equal(220, scroll.Viewport.Height);
        Assert.Equal(264, scroll.Extent.Height);
        Capture(scope.Window, "dashboard-five-recent-books");
    });

    [Theory]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(30)]
    public Task DashboardDaysStayOnOneRow(int days) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingDashboardPage"), null);
        scope.Set("_readingDashboardDayCount", days);
        await scope.Call<Task>("RefreshReadingDashboardAsync");
        await Render();
        var charts = scope.Get<Grid>("DashboardChartsGrid").Children.OfType<Border>().ToArray();
        Assert.Equal(4, charts.Length);
        Assert.True(charts[0].Bounds.Height >= 250);
        foreach (var block in charts)
            Assert.Equal(charts[0].Bounds.Height, block.Bounds.Height);
        var progress = scope.Get<ItemsControl>("DashboardProgressChart");
        var progressPanel = Assert.Single(progress.GetVisualDescendants().OfType<UniformGrid>());
        Assert.Equal(5, progressPanel.Children.Count);
        Assert.True(progressPanel.Bounds.Height >= 220);
        Assert.True(progressPanel.Children[0].Bounds.Height >= 40);
        var chart = scope.Get<ItemsControl>("DashboardDaysChart");
        Assert.Equal(days, chart.Items.Count);
        var panel = Assert.Single(chart.GetVisualDescendants().OfType<UniformGrid>());
        Assert.Equal(1, panel.Rows);
        Assert.Equal(days, panel.Children.Count);
        var first = panel.Children[0].Bounds;
        Assert.True(first.Width > 0);
        foreach (var child in panel.Children)
        {
            Assert.Equal(first.Y, child.Bounds.Y);
            Assert.Equal(first.Height, child.Bounds.Height);
            Assert.True(child.Bounds.Right <= panel.Bounds.Width + 1, $"Child {child.Bounds}; panel {panel.Bounds}");
        }
        Assert.Contains(days.ToString(), scope.Get<TextBlock>("DashboardDailyTitleText").Text);
        for (var i = 0; i < scope.Window.DashboardDays.Count; i++)
            scope.Window.DashboardDays[i] = scope.Window.DashboardDays[i] with { BarHeight = 4 + i % 6 * 35 };
        for (var i = 0; i < scope.Window.DashboardProgressBuckets.Count; i++)
            scope.Window.DashboardProgressBuckets[i] = scope.Window.DashboardProgressBuckets[i] with
            {
                BarWidth = 210 - i * 40,
                ValueLabel = $"{26 - i * 5}"
            };
        for (var i = 0; i < 8; i++)
            scope.Window.DashboardBookTimes.Add(new Stage3DashboardBarViewModel($"Book {i + 1}", $"{56 - i * 6} min", 210 - i * 25));
        await Render();
        Capture(scope.Window, $"dashboard-single-row-{days}");
    });

    [Fact]
    public Task ReadingMaterialFiltersHaveAlignedBottomEdges() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        await Render();
        var search = scope.Get<TextBox>("ReadingMaterialsSearchBox");
        var source = scope.Get<ComboBox>("ReadingMaterialsSourceBox");
        Assert.True(search.Bounds.Height > 0);
        Assert.Equal(search.Bounds.Height, source.Bounds.Height);
        Assert.Equal(search.Bounds.Bottom, source.Bounds.Bottom);
        Capture(scope.Window, "reading-material-filters-aligned");
    });

    [Fact]
    public Task DashboardTitleClickCyclesDayRange() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingDashboardPage"), null);
        await Render();
        Assert.Null(scope.Window.FindControl<ComboBox>("DashboardRangeBox"));
        await scope.Call<Task>("RefreshReadingDashboardAsync");
        var button = scope.Get<Button>("DashboardDailyRangeButton");
        foreach (var days in new[] { 30, 7, 14 })
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => scope.Window.DashboardDays.Count == days
                && scope.Get<TextBlock>("DashboardDailyTitleText").Text!.Contains(days.ToString()));
        }
    });
}
