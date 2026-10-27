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
    private TextBlock? _taskHistoryTooltipText;
    private DispatcherTimer? _taskHistoryAnimation;
    private readonly Avalonia.Media.RotateTransform _taskHistoryRotation = new();
    private readonly AsyncLocal<BackgroundTaskContext?> _resourceTaskContext = new();
    private BackgroundTaskContext? _genericTaskContext;
    private TaskCompletionSource? _genericTaskCompletion;
    private BackgroundTaskContext? _transferTaskContext;
    private TaskCompletionSource? _transferTaskCompletion;
    private string? _transferTaskTitle;
    private Guid? _transferTaskId;
    private int? _transferTaskProgress;
    private int? _transferTaskProgressTotal;
    private bool _backgroundTaskFlyoutAutoOpened;
    private bool _genericTaskActive;
    private bool _progressSubscriptionsAdded;
    private readonly HashSet<Guid> _displayOnlyTaskIds = [];

    private void BeginGenericTaskRecord()
    {
        _genericTaskActive = true;
        if (!_progressSubscriptionsAdded)
        {
            _progressSubscriptionsAdded = true;
            TaskProgressPopupText.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty) UpdateGenericTaskProgress();
            };
            TaskProgressPopupBar.PropertyChanged += (_, _) => UpdateGenericTaskProgress();
        }
        // Import attaches its own context synchronously after showing progress.
        // Defer fallback creation so that it has only one history entry.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_genericTaskActive || _genericTaskContext is not null) return;
            _genericTaskCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = _genericTaskCompletion;
            var handle = BackgroundTasks.Start(TaskProgressPopupText.Text ?? T("后台任务"), BackgroundTaskKind.Other,
                async (context, token) =>
                {
                    _genericTaskContext = context;
                    UpdateGenericTaskProgress();
                    await completion.Task.WaitAsync(token);
                }, _lifetimeCancellation.Token, allowRetry: false);
            _displayOnlyTaskIds.Add(handle.Id);
        });
    }

    private void UpdateGenericTaskProgress()
    {
        if (!_genericTaskActive) return;
        _genericTaskContext?.ReportProgress(TaskProgressPopupText.Text,
            TaskProgressPopupBar.IsIndeterminate ? null : (int)TaskProgressPopupBar.Value,
            TaskProgressPopupBar.IsIndeterminate ? null : (int)TaskProgressPopupBar.Maximum);
    }

    private void EndGenericTaskRecord()
    {
        _genericTaskActive = false;
        _genericTaskCompletion?.TrySetResult();
        _genericTaskCompletion = null;
        _genericTaskContext = null;
    }

    private void UpdateTransferTaskRecord(string title, string message, double? progress,
        bool isIndeterminate, bool autoHide, bool completed)
    {
        if (_transferTaskCompletion is not null && _transferTaskTitle != title)
            EndTransferTaskRecord(new OperationCanceledException(T("任务已切换")));
        if (_transferTaskCompletion is null && !autoHide && !completed)
        {
            _transferTaskTitle = title;
            _transferTaskCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = _transferTaskCompletion;
            var handle = BackgroundTasks.Start(title, BackgroundTaskKind.Other, async (context, token) =>
            {
                _transferTaskContext = context;
                await completion.Task.WaitAsync(token);
            }, _lifetimeCancellation.Token, allowRetry: false);
            _transferTaskId = handle.Id;
            _displayOnlyTaskIds.Add(handle.Id);
        }
        _transferTaskProgress = !isIndeterminate && progress.HasValue
            ? (int)Math.Clamp(progress.Value, 0, 100)
            : null;
        _transferTaskProgressTotal = _transferTaskProgress.HasValue ? 100 : null;
        _transferTaskContext?.ReportProgress(message, _transferTaskProgress, _transferTaskProgressTotal);
        if (completed || autoHide)
            EndTransferTaskRecord(completed ? null : new InvalidOperationException(message));
    }

    private void EndTransferTaskRecord(Exception? error = null)
    {
        if (error is null) _transferTaskCompletion?.TrySetResult();
        else _transferTaskCompletion?.TrySetException(error);
        _transferTaskCompletion = null;
        _transferTaskContext = null;
        _transferTaskTitle = null;
        _transferTaskId = null;
        _transferTaskProgress = null;
        _transferTaskProgressTotal = null;
        HideBackgroundTaskFlyoutIfAutoOpened();
    }

    private void ReportTransferTaskStage(string stage)
    {
        _transferTaskContext?.ReportProgress(stage, _transferTaskProgress, _transferTaskProgressTotal);
    }

    private IProgress<TransferProgress> CreateRecordedTransferProgress(IProgress<TransferProgress>? original)
    {
        var context = _resourceTaskContext.Value;
        return new RecordedTransferProgress(value =>
        {
            context?.ReportProgress(UiText.Localize(value.Message), (int)value.Percentage, 100);
            original?.Report(value);
        });
    }

    private sealed class RecordedTransferProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private BackgroundTaskCenterService BackgroundTasks => _backgroundTasks ??= CreateBackgroundTasks();

    private BackgroundTaskCenterService CreateBackgroundTasks()
    {
        var service = new BackgroundTaskCenterService(
            Path.Combine(_paths.Data, "background-tasks.json"),
            clearHistoryOnStartup: true);
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
        BackgroundTaskStatusButton.IsVisible = false;
        TaskHistoryIdleIcon.IsVisible = running.Length == 0;
        TaskHistoryActivityIcon.IsVisible = running.Length > 0;
        // Update the existing tooltip content so an already-open tooltip keeps
        // showing live stages and progress without closing on every report.
        if (_taskHistoryTooltipText is null)
        {
            _taskHistoryTooltipText = new TextBlock
            {
                FontSize = 12,
                LineHeight = 18,
                MaxWidth = 420,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            ToolTip.SetTip(BackgroundTaskHistoryButton, new ScrollViewer
            {
                Content = _taskHistoryTooltipText,
                MaxHeight = 360,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            });
        }
        var tooltipSections = new List<string>
        {
            T("任务记录") + (running.Length > 0 ? T(" · 进行中 {0}", running.Length)
                : failed > 0 ? T(" · 失败 {0}", failed) : string.Empty)
        };
        foreach (var task in running)
        {
            var details = new List<string>
            {
                (task.Status == BackgroundTaskStatus.Queued ? T("等待中") : T("进行中")) + " · " + task.Title
            };
            if (!string.IsNullOrWhiteSpace(task.Stage) && task.Stage != task.Title)
                details.Add(task.Stage);
            if (task.BookTitles is { Count: > 0 })
                details.Add(string.Join("、", task.BookTitles.Take(3).Select(title => "《" + title + "》"))
                    + (task.BookTitles.Count > 3 ? " …" : string.Empty));
            if (task.ProgressTotal is > 0)
            {
                var current = Math.Clamp(task.ProgressCurrent ?? 0, 0, task.ProgressTotal.Value);
                details.Add(T("进度：{0}/{1}", current, task.ProgressTotal)
                    + $" · {100d * current / task.ProgressTotal.Value:0}%");
            }
            tooltipSections.Add(string.Join(Environment.NewLine, details));
        }
        _taskHistoryTooltipText.Text = string.Join(Environment.NewLine + Environment.NewLine, tooltipSections);
        if (_taskHistoryAnimation is null)
        {
            TaskHistoryActivityIcon.RenderTransform = _taskHistoryRotation;
            _taskHistoryAnimation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _taskHistoryAnimation.Tick += (_, _) => _taskHistoryRotation.Angle = (_taskHistoryRotation.Angle + 12) % 360;
            Closed += (_, _) => _taskHistoryAnimation.Stop();
        }
        if (running.Length > 0) _taskHistoryAnimation.Start();
        else { _taskHistoryAnimation.Stop(); _taskHistoryRotation.Angle = 0; }
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
        Func<BackgroundTaskContext, CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        bool allowRetry = true)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var handle = BackgroundTasks.Start(title, kind, async (context, token) =>
        {
            failure = null;
            if (_genericTaskActive && _genericTaskContext is null)
            {
                _genericTaskContext = context;
                UpdateGenericTaskProgress();
            }
            try { result = await operation(context, token); }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }, cancellationToken, allowRetry);
        await handle.Completion;
        failure?.Throw();
        return result;
    }

    private Task<T> RecordResourceOperationAsync<T>(string action, string name,
        Func<CancellationToken, Task<T>> operation, CancellationToken token) =>
        RunTrackedTaskAsync(action, BackgroundTaskKind.Other, async (context, cancellationToken) =>
        {
            context.ReportBooks([name]);
            var previous = _resourceTaskContext.Value;
            _resourceTaskContext.Value = context;
            try { return await operation(cancellationToken); }
            finally { _resourceTaskContext.Value = previous; }
        }, token, allowRetry: false);

    private async Task RecordResourceActionAsync(string action, string name,
        Func<CancellationToken, Task> operation, CancellationToken token) =>
        await RecordResourceOperationAsync(action, name, async cancellationToken =>
        {
            await operation(cancellationToken);
            return true;
        }, token);

    private void BackgroundTasksButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backgroundTaskFlyout?.IsOpen == true)
        {
            DismissBackgroundTaskFlyout();
            return;
        }
        ShowBackgroundTaskFlyout(autoOpened: false);
    }

    private void DismissBackgroundTaskFlyout()
    {
        if (_backgroundTaskFlyout?.IsOpen != true) return;
        _backgroundTaskFlyout.Hide();
        _backgroundTaskFlyoutAutoOpened = false;
    }

    private void EnsureBackgroundTaskFlyout()
    {
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
                Placement = PlacementMode.TopEdgeAlignedLeft,
                VerticalOffset = 4,
                FlyoutPresenterClasses = { "backgroundTaskFlyout" },
                Content = new Border
                {
                    MinWidth = 240, MaxWidth = Math.Clamp(Bounds.Width - 80, 240, 560),
                    Child = content
                }
            };
        }
    }

    private void ShowBackgroundTaskFlyout(bool autoOpened)
    {
        EnsureBackgroundTaskFlyout();
        if (_backgroundTaskFlyout?.IsOpen == true)
        {
            if (autoOpened) _backgroundTaskFlyoutAutoOpened = true;
            return;
        }
        RefreshBackgroundTaskRows();
        _backgroundTaskFlyout?.ShowAt(BackgroundTaskHistoryButton);
        _backgroundTaskFlyoutAutoOpened = autoOpened;
    }

    private void HideBackgroundTaskFlyoutIfAutoOpened()
    {
        if (!_backgroundTaskFlyoutAutoOpened) return;
        _backgroundTaskFlyout?.Hide();
        _backgroundTaskFlyoutAutoOpened = false;
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
            Margin = new Thickness(16, 10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
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
            var row = new StackPanel { Spacing = 4 };
            var isBatchImport = task.Kind == BackgroundTaskKind.Import && task.BookTitles is { Count: > 1 };
            var names = !isBatchImport && task.BookTitles is { Count: > 0 }
                ? string.Join("、", task.BookTitles.Select(title => "《" + title + "》")) : string.Empty;
            var identity = isBatchImport ? T("批量导入") : task.Title + (names.Length > 0 ? " · " + names : string.Empty);
            var result = task.Status == BackgroundTaskStatus.Succeeded
                ? task.UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") : string.Empty;
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 6 };
            ToolTip.SetTip(heading, status + " · " + identity + (result.Length > 0 ? " · " + result : string.Empty));
            var statusIcon = CreateTaskHistoryIcon(task.Status switch
            {
                BackgroundTaskStatus.Succeeded => "M 3,8 L 6,11 L 13,4",
                BackgroundTaskStatus.Failed => "M 8,2 A 6,6 0 1 1 7.99,2 M 8,5 L 8,8 M 8,10 L 8,11",
                BackgroundTaskStatus.Canceled => "M 3,3 L 13,13 M 13,3 L 3,13",
                BackgroundTaskStatus.Interrupted => "M 5,3 L 5,13 M 11,3 L 11,13",
                _ => "M 8,2 A 6,6 0 1 1 7.99,2 M 8,4 L 8,8 L 11,10"
            });
            statusIcon.Margin = new Thickness(4, 0);
            ToolTip.SetTip(statusIcon, status);
            Avalonia.Automation.AutomationProperties.SetName(statusIcon, status);
            heading.Children.Add(statusIcon);
            var identityText = new TextBlock
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Text = identity, MaxWidth = Math.Clamp(Bounds.Width - 300, 60, 300), FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(identityText, 1);
            heading.Children.Add(identityText);
            var resultText = new TextBlock { Text = result, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(resultText, 2);
            heading.Children.Add(resultText);
            var isTransferTask = task.Id == _transferTaskId;
            var canCancel = task.Status is (BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued)
                && ((isTransferTask && _isTransferring)
                    || (task.CanCancel && !_displayOnlyTaskIds.Contains(task.Id)));
            if (canCancel || task.Status is not (BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued))
            {
                var actionLabel = canCancel ? T("取消") : T("删除");
                var delete = CreateTaskHistoryButton(actionLabel, canCancel
                    ? "M 3,3 L 13,13 M 13,3 L 3,13"
                    : "M 2,4 L 14,4 M 6,4 L 6,2 L 10,2 L 10,4 M 4,4 L 5,14 L 11,14 L 12,4 M 7,6 L 7,12 M 9,6 L 9,12");
                delete.Click += async (_, _) =>
                {
                    if (canCancel)
                    {
                        if (isTransferTask) RequestTransferStop();
                        else BackgroundTasks.Cancel(task.Id);
                    }
                    else await BackgroundTasks.RemoveCompletedAsync(task.Id);
                };
                Grid.SetColumn(delete, 4);
                heading.Children.Add(delete);
            }
            row.Children.Add(heading);
            var stage = task.Status != BackgroundTaskStatus.Succeeded && task.Stage != task.Title
                ? task.Stage : null;
            var hasProgress = task.ProgressTotal is > 0
                && task.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued;
            if (hasProgress)
            {
                var current = Math.Clamp(task.ProgressCurrent ?? 0, 0, task.ProgressTotal!.Value);
                var progressText = task.ProgressTotal == 100
                    ? $"{current}%"
                    : T("进度：{0}/{1}", current, task.ProgressTotal);
                stage = string.IsNullOrWhiteSpace(stage) ? progressText : stage + " · " + progressText;
            }
            if (!string.IsNullOrWhiteSpace(stage))
                row.Children.Add(new TextBlock { Text = stage, FontSize = 12,
                    Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (hasProgress)
            {
                row.Children.Add(new ProgressBar { Maximum = task.ProgressTotal!.Value, Value = task.ProgressCurrent ?? 0 });
            }
            else if (task.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued)
                row.Children.Add(new ProgressBar { IsIndeterminate = true });
            if (!string.IsNullOrWhiteSpace(task.Error))
                row.Children.Add(new TextBlock { Text = task.Error, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (task.CanRetry)
            {
                var retry = CreateTaskHistoryButton(T("重试"),
                    "M 13,7 A 5,5 0 1 0 12,11 M 13,2 L 13,7 L 8,7");
                retry.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                retry.Click += (_, _) => BackgroundTasks.Retry(task.Id, _lifetimeCancellation.Token);
                Grid.SetColumn(retry, 3);
                heading.Children.Add(retry);
            }
            _backgroundTaskRows.Children.Add(new Border
            {
                Padding = new Thickness(12, 4), BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"), Child = row
            });
        }
    }

    private static Avalonia.Controls.Shapes.Path CreateTaskHistoryIcon(string geometry) => new()
    {
        Width = 12, Height = 12, Stretch = Avalonia.Media.Stretch.Uniform,
        Data = Avalonia.Media.Geometry.Parse(geometry),
        Stroke = AppAppearanceResources.GetBrush("MutedInkBrush"), StrokeThickness = 1,
        Fill = Avalonia.Media.Brushes.Transparent,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private static Button CreateTaskHistoryButton(string label, string geometry)
    {
        var button = new Button
        {
            Classes = { "eject" }, Width = 25, MinWidth = 25, Height = 23, MinHeight = 23,
            Padding = new Thickness(0), Margin = new Thickness(0),
            Content = CreateTaskHistoryIcon(geometry)
        };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
}
