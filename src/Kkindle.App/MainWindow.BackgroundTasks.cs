using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private BackgroundTaskCenterService? _backgroundTasks;
    private Flyout? _backgroundTaskFlyout;
    private StackPanel? _backgroundTaskRows;

    private BackgroundTaskCenterService BackgroundTasks => _backgroundTasks ??= CreateBackgroundTasks();

    private BackgroundTaskCenterService CreateBackgroundTasks()
    {
        var service = new BackgroundTaskCenterService(Path.Combine(_paths.Data, "background-tasks.json"));
        service.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshBackgroundTaskStatus();
            RefreshBackgroundTaskRows();
        });
        return service;
    }

    private void RefreshBackgroundTaskStatus()
    {
        var tasks = BackgroundTasks.GetSnapshot();
        var running = tasks.Where(task => task.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued).ToArray();
        var failed = tasks.Count(task => !task.IsFromPreviousRun && task.Status == BackgroundTaskStatus.Failed);
        BackgroundTaskStatusButton.IsVisible = running.Length > 0 || failed > 0;
        if (running.Length > 0)
        {
            var task = running[0];
            var label = string.IsNullOrWhiteSpace(task.Stage) ? task.Title : task.Stage;
            BackgroundTaskStatusButton.Content = running.Length > 1
                ? label + " · " + running.Length
                : task.ProgressTotal is > 0
                    ? label + $" {task.ProgressCurrent ?? 0}/{task.ProgressTotal}"
                    : label;
        }
        else if (failed > 0)
            BackgroundTaskStatusButton.Content = T("任务失败") + $" ({failed}) · " + T("查看详情");
    }

    private async Task<T> RunTrackedTaskAsync<T>(string title, BackgroundTaskKind kind,
        Func<BackgroundTaskContext, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var handle = BackgroundTasks.Start(title, kind, async (context, token) =>
        {
            failure = null;
            try { result = await operation(context, token); }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }, cancellationToken);
        await handle.Completion;
        failure?.Throw();
        return result;
    }

    private void BackgroundTasksButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backgroundTaskFlyout?.IsOpen == true)
        {
            _backgroundTaskFlyout.Hide();
            return;
        }
        if (_backgroundTaskFlyout is null)
        {
            _backgroundTaskRows = new StackPanel { Spacing = 0 };
            var content = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    new ScrollViewer { Content = _backgroundTaskRows, MaxHeight = Math.Clamp(Bounds.Height - 200, 160, 460) }
                }
            };
            _backgroundTaskFlyout = new Flyout
            {
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                VerticalOffset = 0,
                FlyoutPresenterClasses = { "backgroundTaskFlyout" },
                Content = new Border
                {
                    Width = Math.Clamp(Bounds.Width - 80, 240, 340),
                    Child = content
                }
            };
        }
        RefreshBackgroundTaskRows();
        _backgroundTaskFlyout.ShowAt(BackgroundTaskHistoryButton);
    }

    private void RefreshBackgroundTaskRows()
    {
        if (_backgroundTaskRows is null) return;
        _backgroundTaskRows.Children.Clear();
        var tasks = BackgroundTasks.GetSnapshot();
        if (tasks.Count == 0) _backgroundTaskRows.Children.Add(new TextBlock
        {
            Text = T("暂无任务记录。"), FontSize = 13,
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
            Margin = new Thickness(16, 22), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });
        foreach (var task in tasks)
        {
            var status = task.Status switch
            {
                BackgroundTaskStatus.Running => T("进行中"),
                BackgroundTaskStatus.Succeeded => T("已完成"),
                BackgroundTaskStatus.Failed => T("失败"),
                BackgroundTaskStatus.Canceled => T("已取消"),
                BackgroundTaskStatus.Interrupted => T("已中断"),
                _ => T("等待中")
            };
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock { Text = task.Title + " · " + status, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(task.Stage))
                row.Children.Add(new TextBlock { Text = task.Stage, FontSize = 12,
                    Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (task.ProgressTotal is > 0)
                row.Children.Add(new ProgressBar { Maximum = task.ProgressTotal.Value, Value = task.ProgressCurrent ?? 0 });
            if (!string.IsNullOrWhiteSpace(task.Error))
                row.Children.Add(new TextBlock { Text = task.Error, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            if (task.CanCancel)
            {
                var cancel = new Button { Content = T("取消") };
                cancel.Click += (_, _) => BackgroundTasks.Cancel(task.Id);
                actions.Children.Add(cancel);
            }
            if (task.CanRetry)
            {
                var retry = new Button { Content = T("重试") };
                retry.Click += (_, _) => BackgroundTasks.Retry(task.Id, _lifetimeCancellation.Token);
                actions.Children.Add(retry);
            }
            if (actions.Children.Count > 0) row.Children.Add(actions);
            _backgroundTaskRows.Children.Add(new Border
            {
                Padding = new Thickness(16, 12), BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"), Child = row
            });
        }
    }
}
