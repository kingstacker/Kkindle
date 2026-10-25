using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData(BackgroundTaskStatus.Succeeded)]
    [InlineData(BackgroundTaskStatus.Failed)]
    [InlineData(BackgroundTaskStatus.Canceled)]
    public Task ConversionUsesOnlyTaskHistory(BackgroundTaskStatus outcome) => Run(async () =>
    {
        var converter = new GatedHistoryConverter();
        await using var scope = await TestWindow.Create(formatConverter: converter);
        var library = scope.Field<IBookLibraryService>("_library");
        await library.ImportAsync([PdfFixture.Write(scope.Paths.ReaderCache)]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        var conversion = scope.Call<Task>("ConvertBookAsync", card, "epub", null);
        await Until(() => converter.Progress is not null);
        converter.Progress!.Report(new FormatConversionProgress(37, "正在转换章节"));
        var center = scope.Field<BackgroundTaskCenterService>("_backgroundTasks");
        await Until(() => center.GetSnapshot().Single().ProgressCurrent == 37);
        var task = Assert.Single(center.GetSnapshot());
        Assert.Equal(BackgroundTaskKind.FormatConversion, task.Kind);
        Assert.Contains(book.Title, task.BookTitles!);
        Assert.Contains("PDF → EPUB", task.Stage);
        Assert.Contains("正在转换章节", task.Stage);
        Assert.True(task.CanCancel);
        Assert.Null(scope.Window.FindControl<Control>("BookConversionPopup"));
        Assert.False(scope.Get<Control>("TaskProgressPopup").IsVisible);
        scope.Call("BackgroundTasksButton_Click", null, new RoutedEventArgs());
        await Render();
        var rows = scope.Field<StackPanel>("_backgroundTaskRows");
        var progress = Assert.Single(rows.GetVisualDescendants().OfType<ProgressBar>());
        Assert.Equal(37, progress.Value);
        Assert.Equal(100, progress.Maximum);
        if (outcome == BackgroundTaskStatus.Canceled)
        {
            var cancel = Assert.Single(rows.GetVisualDescendants().OfType<Button>());
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else if (outcome == BackgroundTaskStatus.Failed)
            converter.Finish.SetException(new IOException("转换测试失败"));
        else
            converter.Finish.SetResult();
        await conversion;
        task = Assert.Single(center.GetSnapshot());
        Assert.Equal(outcome, task.Status);
        Assert.False(scope.Field<bool>("_conversionInProgress"));
        Assert.False(scope.Get<Control>("TaskProgressPopup").IsVisible);
        Assert.False(scope.Get<Control>("MessageOverlay").IsVisible);
        if (outcome == BackgroundTaskStatus.Failed) Assert.Equal("转换测试失败", task.Error);
        if (outcome == BackgroundTaskStatus.Succeeded)
            Assert.Contains((await library.SearchAsync()).Single().Files, file => file.Format == "epub");
        scope.Field<Flyout>("_backgroundTaskFlyout").Hide();
    });

    private sealed class GatedHistoryConverter : IBookFormatConverter
    {
        public IProgress<FormatConversionProgress>? Progress { get; private set; }
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConvertAsync(string sourcePath, string destinationPath,
            IProgress<FormatConversionProgress>? progress = null,
            CancellationToken cancellationToken = default, FormatConversionMetadata? metadata = null)
        {
            Progress = progress;
            await Finish.Task.WaitAsync(cancellationToken);
            await File.WriteAllTextAsync(destinationPath, "converted test book", cancellationToken);
        }
    }
}
