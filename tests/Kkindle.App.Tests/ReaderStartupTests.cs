using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ReaderTtsSettingsDoNotWaitForEnvironmentBootstrap() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Set("_readerTtsEnvironmentTask", pending.Task);
        scope.Set("_readerTtsEnvironmentChecked", false);
        try
        {
            await scope.Call<Task>("InitializeReaderTtsAsync", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.Task.IsCompleted);
        }
        finally { pending.TrySetResult(); }
    });
}
