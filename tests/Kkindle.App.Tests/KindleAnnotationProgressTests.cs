using System.Reflection;
using Avalonia.Controls;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task AnnotationWorkIsRecordedAsTaskInsteadOfInlinePanel() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingMaterialsPage"), null);
        Assert.Null(scope.Window.FindControl<Border>("KindleAnnotationProgressPanel"));

        var release = new TaskCompletionSource();
        Func<IProgress<KindleAnnotationSyncProgress>, CancellationToken, Task<bool>> operation =
            async (progress, _) =>
            {
                progress.Report(new(KindleAnnotationSyncStage.ProcessingBooks, 1, 2, "规模"));
                await release.Task;
                return true;
            };
        var run = (Task<bool>)typeof(MainWindow)
            .GetMethod("RunKindleAnnotationTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(bool))
            .Invoke(scope.Window, ["同步 Kindle 批注", false, operation, CancellationToken.None])!;
        await Render();

        var tasks = (BackgroundTaskCenterService)typeof(MainWindow)
            .GetProperty("BackgroundTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scope.Window)!;
        var running = Assert.Single(tasks.GetSnapshot(), task => task.Title == "同步 Kindle 批注");
        Assert.Equal(BackgroundTaskStatus.Running, running.Status);
        Assert.Contains("规模", running.Stage);
        Assert.Equal(1, running.ProgressCurrent);
        Assert.Equal(2, running.ProgressTotal);
        // Device writes must not offer a cancel action midway.
        Assert.Contains(running.Id, scope.Field<HashSet<Guid>>("_displayOnlyTaskIds"));

        release.SetResult();
        Assert.True(await run);
        await Render();
        Assert.Equal(BackgroundTaskStatus.Succeeded,
            Assert.Single(tasks.GetSnapshot(), task => task.Id == running.Id).Status);
    });
}
