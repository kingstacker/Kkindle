using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ResourceOperationsRecordNamesFailuresAndCancellation() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("BackgroundTasksButton_Click", null, new RoutedEventArgs());
        var center = scope.Field<Kkindle.Infrastructure.BackgroundTaskCenterService>("_backgroundTasks");
        Func<CancellationToken, Task> success = _ => Task.CompletedTask;
        await scope.Call<Task>("RecordResourceActionAsync", "导入设备字体", "思源宋体", success, CancellationToken.None);
        Func<CancellationToken, Task> failure = _ => Task.FromException(new IOException("设备已断开"));
        await Assert.ThrowsAsync<IOException>(() => scope.Call<Task>("RecordResourceActionAsync", "删除设备词典", "英汉词典", failure, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        Func<CancellationToken, Task> cancel = token => { cancellation.Cancel(); return Task.FromCanceled(token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Call<Task>("RecordResourceActionAsync", "发送书籍到设备", "三体", cancel, cancellation.Token));
        var tasks = center.GetSnapshot();
        Assert.Contains(tasks, task => task.Status == BackgroundTaskStatus.Succeeded && task.BookTitles!.Contains("思源宋体"));
        Assert.Contains(tasks, task => task.Status == BackgroundTaskStatus.Failed && task.Error == "设备已断开" && task.BookTitles!.Contains("英汉词典") && !task.CanRetry);
        Assert.Contains(tasks, task => task.Status == BackgroundTaskStatus.Canceled && task.BookTitles!.Contains("三体") && !task.CanRetry);
        scope.Call("RefreshBackgroundTaskRows");
        await Render();
        var texts = scope.Field<StackPanel>("_backgroundTaskRows").GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text ?? "").ToArray();
        Assert.Contains(texts, text => text.Contains("思源宋体"));
        Assert.Contains(texts, text => text.Contains("英汉词典"));
        Assert.Contains(texts, text => text.Contains("设备已断开"));
        Capture(scope.Window, "resource-task-history");
        scope.Field<Flyout>("_backgroundTaskFlyout").Hide();
    });

    [Fact]
    public Task TaskHistoryShowsImportIdentityResultAndTimes() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("BackgroundTasksButton_Click", null, new RoutedEventArgs());
        var center = scope.Field<Kkindle.Infrastructure.BackgroundTaskCenterService>("_backgroundTasks");
        var handle = center.Start("书库导入", BackgroundTaskKind.Import, (context, _) =>
        {
            context.ReportBooks(["三体", "活着", "人类简史"]);
            context.ReportProgress("正在补齐阅读格式", 40, 100);
            context.ReportProgress("导入完成：新增 2 个文件，跳过 1 个重复或已有文件；已补齐 2 个 EPUB/AZW3 文件。");
            return Task.CompletedTask;
        });
        await handle.Completion;
        scope.Call("RefreshBackgroundTaskRows");
        await Render();
        var rows = scope.Field<StackPanel>("_backgroundTaskRows");
        var texts = rows.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text ?? "").ToArray();
        Assert.Contains(texts, text => text.Contains(center.GetSnapshot().Single().UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")));
        Assert.Contains("批量导入", texts);
        Assert.DoesNotContain(texts, text => text.Contains("三体"));
        Assert.DoesNotContain(texts, text => text.Contains("新增 2 个文件"));
        Assert.DoesNotContain(texts, text => text.Contains("完成："));
        Assert.DoesNotContain(texts, text => text.Contains("开始：") || text.Contains("结束："));
        Assert.DoesNotContain(texts, text => text.Contains("正在补齐"));
        Assert.Empty(rows.GetVisualDescendants().OfType<ProgressBar>());
        var heading = Assert.Single(rows.GetVisualDescendants().OfType<Grid>());
        var statusIcon = Assert.IsType<Avalonia.Controls.Shapes.Path>(heading.Children[0]);
        Assert.Equal("已完成", Avalonia.Automation.AutomationProperties.GetName(statusIcon));
        Assert.Equal(0, Grid.GetColumn(statusIcon));
        Assert.DoesNotContain(texts, text => text.Contains("已完成"));
        var headingTexts = heading.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(2, headingTexts.Length);
        Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, headingTexts[0].TextWrapping);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, headingTexts[0].TextTrimming);
        Assert.Equal(headingTexts[0].Bounds.Y, headingTexts[1].Bounds.Y);
        var delete = Assert.Single(heading.Children.OfType<Button>());
        Assert.Equal(4, Grid.GetColumn(delete));
        Assert.Equal(heading.Bounds.Width, delete.Bounds.Right, precision: 1);

        var output = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var panel = (Border)scope.Field<Flyout>("_backgroundTaskFlyout").Content!;
            var presenter = panel.GetVisualAncestors().OfType<FlyoutPresenter>().First();
            Directory.CreateDirectory(output);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)Math.Ceiling(presenter.Bounds.Width), (int)Math.Ceiling(presenter.Bounds.Height)),
                new Avalonia.Vector(96, 96));
            bitmap.Render(presenter);
            bitmap.Save(Path.Combine(output, "task-history-details.png"));
        }
        delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => center.GetSnapshot().Count == 0);
        scope.Field<Flyout>("_backgroundTaskFlyout").Hide();
    });

    [Fact]
    public Task TaskHistoryAnimatesAndShowsActualProgress() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        Assert.Contains(scope.Get<Button>("BackgroundTaskHistoryButton").GetVisualAncestors(),
            ancestor => ReferenceEquals(ancestor, scope.Get<Border>("DeviceStatusBox")));
        Assert.Equal(2, Grid.GetColumn(scope.Get<Button>("BackgroundTaskHistoryButton")));
        var button = scope.Get<Button>("BackgroundTaskStatusButton");
        Assert.False(button.IsVisible);
        scope.Call("BackgroundTasksButton_Click", null, new RoutedEventArgs());
        var center = scope.Field<Kkindle.Infrastructure.BackgroundTaskCenterService>("_backgroundTasks");
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = center.Start("测试导入", BackgroundTaskKind.Import, async (context, _) =>
        {
            context.ReportProgress("正在导入", 3, 10);
            await finish.Task;
        });
        await Until(() => scope.Get<Control>("TaskHistoryActivityIcon").IsVisible);
        Assert.False(button.IsVisible);
        scope.Call("RefreshBackgroundTaskRows");
        await Render();
        var bar = Assert.Single(scope.Field<StackPanel>("_backgroundTaskRows").GetVisualDescendants().OfType<ProgressBar>());
        Assert.Equal(3, bar.Value);
        Assert.Equal(10, bar.Maximum);
        var icon = scope.Get<Control>("TaskHistoryActivityIcon");
        var rotation = Assert.IsType<Avalonia.Media.RotateTransform>(icon.RenderTransform);
        var angle = rotation.Angle;
        await Until(() => rotation.Angle != angle);
        Capture(scope.Window, "task-history-running");
        finish.SetResult();
        await handle.Completion;
        await Until(() => scope.Get<Control>("TaskHistoryIdleIcon").IsVisible);
        var failed = center.Start("失败导入", BackgroundTaskKind.Import, (_, _) => Task.FromException(new IOException("test")));
        await failed.Completion;
        await Until(() => scope.Get<Control>("TaskHistoryIdleIcon").IsVisible);
        Assert.False(button.IsVisible);
        scope.Field<Flyout>("_backgroundTaskFlyout").Hide();
    });

    [Fact]
    public Task TaskCenterShowsRunningTaskAndCancellation() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("BackgroundTasksButton_Click", null, new RoutedEventArgs());
        await Render();
        var output = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var panel = (Border)scope.Field<Flyout>("_backgroundTaskFlyout").Content!;
            var presenter = panel.GetVisualAncestors().OfType<FlyoutPresenter>().First();
            Directory.CreateDirectory(output);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)Math.Ceiling(presenter.Bounds.Width), (int)Math.Ceiling(presenter.Bounds.Height)),
                new Avalonia.Vector(96, 96));
            bitmap.Render(presenter);
            bitmap.Save(Path.Combine(output, "task-flyout-empty.png"));
        }
        var center = scope.Field<Kkindle.Infrastructure.BackgroundTaskCenterService>("_backgroundTasks");
        var handle = center.Start("测试导入", BackgroundTaskKind.Import,
            async (_, token) => await Task.Delay(Timeout.Infinite, token));
        Assert.True(center.GetSnapshot().Single().CanCancel);
        Assert.True(center.Cancel(handle.Id));
        await handle.Completion;
        Assert.Equal(BackgroundTaskStatus.Canceled, center.GetSnapshot().Single().Status);
        await Render();
        var flyout = scope.Field<Flyout>("_backgroundTaskFlyout");
        Assert.True(flyout.IsOpen);
        Assert.IsType<Border>(flyout.Content);
        Capture(scope.Window, "task-center");
        scope.Call("BackgroundTasksButton_Click", scope.Get<Button>("BackgroundTaskHistoryButton"), new RoutedEventArgs());
        Assert.False(flyout.IsOpen);
    });

    [Fact]
    public Task SettingsSearchSupportsKeyboardSelectionAndClear() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        scope.Get<TextBox>("SettingsSearchBox").Text = "字体";
        await Until(() => scope.Get<ItemsControl>("SettingsSearchResults").Items.Count > 0);
        await Render();
        scope.Call("SettingsSearchBox_KeyDown", null, new Avalonia.Input.KeyEventArgs
        { Key = Avalonia.Input.Key.Down });
        Assert.True(scope.Field<int>("_settingsSearchSelectedIndex") >= 0);
        await Render();
        Capture(scope.Window, "settings-search-keyboard");
        var selected = Assert.Single(scope.Get<ItemsControl>("SettingsSearchResults").GetVisualDescendants()
            .OfType<Button>().Where(button => button.Classes.Contains("selected")));
        Assert.Equal(new Avalonia.Thickness(0), selected.BorderThickness);
        scope.Call("SettingsSearchBox_KeyDown", null, new Avalonia.Input.KeyEventArgs
        { Key = Avalonia.Input.Key.Escape });
        await Until(() => scope.Get<ScrollViewer>("SettingsScrollViewer").IsVisible);
        Assert.True(scope.Get<ScrollViewer>("SettingsScrollViewer").IsVisible);
        Assert.True(scope.Get<WrapPanel>("SettingsCategoryButtons").IsVisible);
    });

    [Fact]
    public Task TxtImportCanCancelWhilePreviewIsPending() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var path = Path.Combine(scope.Paths.Data, "cancel.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文");
        var result = scope.Call<Task<IReadOnlyDictionary<string, TxtImportRules>?>>(
            "ChooseTxtImportRulesAsync", new[] { path }, CancellationToken.None);
        scope.Call("TxtImportCancelButton_Click", null, new RoutedEventArgs());
        Assert.Null(await result);
        Assert.False(scope.Get<Grid>("TxtImportRulesOverlay").IsVisible);
    });

    [Fact]
    public Task TxtImportKeepsRulesSeparateAndRejectsInvalidRegex() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var first = Path.Combine(scope.Paths.Data, "first.txt");
        var second = Path.Combine(scope.Paths.Data, "second.txt");
        await File.WriteAllTextAsync(first, "第一章 开始\n正文");
        await File.WriteAllTextAsync(second, "Chapter 1 Start\nBody");
        var result = scope.Call<Task<IReadOnlyDictionary<string, TxtImportRules>?>>(
            "ChooseTxtImportRulesAsync", new[] { first, second }, CancellationToken.None);
        await Until(() => scope.Get<ItemsControl>("TxtImportPreviewItems").Items.Count > 0);
        await Render();
        Capture(scope.Window, "txt-import-rules");
        scope.Get<ComboBox>("TxtImportEncodingBox").SelectedIndex = 1;
        scope.Get<Button>("TxtImportNextFileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, scope.Get<ComboBox>("TxtImportEncodingBox").SelectedIndex);
        scope.Get<ComboBox>("TxtImportHeadingRuleBox").SelectedIndex = 5;
        scope.Get<TextBox>("TxtImportCustomPatternBox").Text = "[";
        scope.Call("TxtImportConfirmButton_Click", null, new RoutedEventArgs());
        Assert.False(result.IsCompleted);
        Assert.Contains("无效", scope.Get<TextBlock>("TxtImportPreviewStatusText").Text);
        scope.Get<TextBox>("TxtImportCustomPatternBox").Text = "^Chapter ";
        scope.Call("TxtImportConfirmButton_Click", null, new RoutedEventArgs());
        var rules = (await result)!;
        Assert.Equal(TxtTextEncoding.Utf8, rules[first].Encoding);
        Assert.Equal(TxtTextEncoding.Auto, rules[second].Encoding);
        Assert.Equal(TxtHeadingRulePreset.CustomRegex, rules[second].HeadingPreset);
        Assert.Equal("^Chapter", rules[second].CustomHeadingPattern);
    });
}
