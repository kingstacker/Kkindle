using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class BookTranslationProgressTests(SettingsUiSession session)
{
    [Fact]
    public Task QueuedTranslationProgressYieldsToUserInput() => session.Session.Dispatch(async () =>
    {
        var windowType = typeof(MainWindow).Assembly.GetType("Kkindle.EpubTranslationProgressWindow")!;
        var window = (Window)Activator.CreateInstance(windowType,
            "Translation test", BookTranslationProvider.GoogleFree, "English", "中文", Path.GetTempPath())!;
        window.Show();
        try
        {
            var reporterType = typeof(MainWindow).GetNestedType("BookTranslationProgressReporter", BindingFlags.NonPublic)!;
            var reporter = Activator.CreateInstance(reporterType, window)!;
            var progress = (IProgress<BookTranslationProgress>)reporter;
            var pending = reporterType.GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var index = 0; index < 200; index++)
                progress.Report(new BookTranslationProgress("正在翻译", "chapter.xhtml", index, 200));

            var input = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => input.SetResult((int)pending.GetValue(reporter)!), DispatcherPriority.Input);
            var flushed = (Task)reporterType.GetMethod("FlushAsync")!.Invoke(reporter, null)!;
            await Task.WhenAll(input.Task, flushed).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(await input.Task > 0, "Input must run before the translation progress queue drains.");
            Assert.Equal(0, (int)pending.GetValue(reporter)!);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
