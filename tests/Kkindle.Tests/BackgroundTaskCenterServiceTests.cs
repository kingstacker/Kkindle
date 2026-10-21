using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class BackgroundTaskCenterServiceTests
{
    [Fact]
    public async Task RemovingCompletedTaskPersistsAndRejectsRunningTask()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var history = Path.Combine(root, "tasks.json");
            var service = new BackgroundTaskCenterService(history);
            var completed = service.Start("完成", BackgroundTaskKind.Other, (_, _) => Task.CompletedTask);
            await completed.Completion;
            var running = service.Start("运行", BackgroundTaskKind.Other, async (_, token) => await Task.Delay(Timeout.Infinite, token));
            Assert.False(await service.RemoveCompletedAsync(running.Id));
            service.Cancel(running.Id);
            await running.Completion;
            Assert.True(await service.RemoveCompletedAsync(completed.Id));
            Assert.DoesNotContain(new BackgroundTaskCenterService(history).GetSnapshot(), task => task.Id == completed.Id);
            Assert.Contains(service.GetSnapshot(), task => task.Id == running.Id);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BookTitlesSurviveRestartWithoutPersistingSourcePaths()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var history = Path.Combine(root, "tasks.json");
            var service = new BackgroundTaskCenterService(history);
            var handle = service.Start("书库导入", BackgroundTaskKind.Import, (context, _) =>
            {
                context.ReportBooks(["三体", "活着", @"C:\private\book.epub"]);
                return Task.CompletedTask;
            });
            await handle.Completion;
            var restored = Assert.Single(new BackgroundTaskCenterService(history).GetSnapshot());
            Assert.Contains("三体", restored.BookTitles!);
            Assert.Contains("活着", restored.BookTitles!);
            Assert.DoesNotContain("private", await File.ReadAllTextAsync(history));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RetryIsExclusiveAndLateProgressFromPreviousAttemptIsIgnored()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new BackgroundTaskCenterService(Path.Combine(root, "tasks.json"));
            var attempt = 0;
            BackgroundTaskContext? firstAttemptContext = null;
            var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = service.Start("翻译单本书", BackgroundTaskKind.BookTranslation, async (context, cancellationToken) =>
            {
                var currentAttempt = Interlocked.Increment(ref attempt);
                if (currentAttempt == 1)
                {
                    firstAttemptContext = context;
                    throw new InvalidOperationException("first attempt failed");
                }

                context.ReportProgress("当前重试", 2, 4);
                retryStarted.TrySetResult();
                await finishRetry.Task.WaitAsync(cancellationToken);
            });
            await handle.Completion;

            var first = Assert.Single(service.GetSnapshot());
            Assert.Equal(BackgroundTaskStatus.Failed, first.Status);
            Assert.True(first.CanRetry);

            var retry = service.Retry(handle.Id);
            Assert.NotNull(retry);
            await retryStarted.Task;
            firstAttemptContext!.ReportProgress("过期进度", 99, 99);
            Assert.Null(service.Retry(handle.Id));
            var running = Assert.Single(service.GetSnapshot());
            Assert.Equal(BackgroundTaskStatus.Running, running.Status);
            Assert.Equal("当前重试", running.Stage);
            Assert.Equal(2, running.ProgressCurrent);
            Assert.Equal(4, running.ProgressTotal);
            Assert.Equal(2, Volatile.Read(ref attempt));

            Assert.True(service.Cancel(handle.Id));
            await retry.Completion;
            var canceled = Assert.Single(service.GetSnapshot());
            Assert.Equal(BackgroundTaskStatus.Canceled, canceled.Status);
            Assert.False(canceled.CanCancel);
            Assert.True(canceled.CanRetry);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OperationStartsOnCallerContextAndCancellationStopsIt()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new BackgroundTaskCenterService(Path.Combine(root, "tasks.json"));
            var callerThread = Environment.CurrentManagedThreadId;
            var operationThread = -1;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = service.Start("下载书籍", BackgroundTaskKind.BookDownload, async (_, cancellationToken) =>
            {
                operationThread = Environment.CurrentManagedThreadId;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

            await entered.Task;
            Assert.Equal(callerThread, operationThread);
            Assert.True(service.Cancel(handle.Id));
            await handle.Completion;
            Assert.Equal(BackgroundTaskStatus.Canceled, Assert.Single(service.GetSnapshot()).Status);
            Assert.False(service.Cancel(handle.Id));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PersistedHistoryRedactsSecretsAndCannotRetryAfterRestart()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var history = Path.Combine(root, "tasks.json");
            var service = new BackgroundTaskCenterService(history);
            var handle = service.Start("导入测试书", BackgroundTaskKind.Import, (_, _) =>
                Task.FromException(new InvalidOperationException(
                    "Authorization: Bearer top-secret C:\\private\\book.txt https://example.test/?token=hidden")));
            await handle.Completion;

            var contents = await File.ReadAllTextAsync(history);
            Assert.DoesNotContain("top-secret", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("private", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("example.test", contents, StringComparison.OrdinalIgnoreCase);

            var restored = new BackgroundTaskCenterService(history);
            var entry = Assert.Single(restored.GetSnapshot());
            Assert.Equal(BackgroundTaskStatus.Failed, entry.Status);
            Assert.True(entry.IsFromPreviousRun);
            Assert.False(entry.CanRetry);
            Assert.Contains("已隐藏", entry.Error, StringComparison.Ordinal);
            Assert.Null(restored.Retry(entry.Id));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
