using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task TaskStatusAppearsOnlyWhileRunningOrFailed() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
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
        await Until(() => button.IsVisible && button.Content?.ToString()?.Contains("3/10") == true);
        finish.SetResult();
        await handle.Completion;
        await Until(() => !button.IsVisible);
        var failed = center.Start("失败导入", BackgroundTaskKind.Import, (_, _) => Task.FromException(new IOException("test")));
        await failed.Completion;
        await Until(() => button.IsVisible && button.Content?.ToString()?.Contains("失败") == true);
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
        Capture(scope.Window, "settings-search-keyboard");
        scope.Call("SettingsSearchClearButton_Click", null, new RoutedEventArgs());
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
