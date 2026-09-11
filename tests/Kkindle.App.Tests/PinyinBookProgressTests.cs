using System.IO.Compression;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class PinyinBookProgressTests(SettingsUiSession session)
{
    [Fact]
    public Task GenerationShowsActiveSourceBeforeInferenceFinishes() => Run(async () =>
    {
        using var scope = new ProgressWindow();
        using var engine = new GatedPinyinEngine();
        var directory = Path.Combine(Path.GetTempPath(), "Kkindle-pinyin-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.epub");
            var output = Path.Combine(directory, "pinyin.epub");
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using (var mimetype = new StreamWriter(archive.CreateEntry("mimetype").Open()))
                    mimetype.Write("application/epub+zip");
                using var chapter = new StreamWriter(archive.CreateEntry("chapter.xhtml").Open());
                chapter.Write("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>银行。</p><p>你好。</p></body></html>");
            }
            var generation = new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output,
                new PinyinBookOptions { EnableAiReview = false }, scope.Progress);
            try
            {
                await engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await scope.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Render(scope.Window);
                Assert.False(generation.IsCompleted);
                var active = Assert.Single(scope.Rows());
                Assert.Equal("银行。", active.OriginalText);
                Assert.Equal(PinyinBookSegmentStatus.Processing, active.Status);
                Assert.Empty(active.AnnotatedText);
                Assert.InRange(scope.Bar.Value, 1, 99);
                Assert.Contains(scope.Window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "正在生成注音…");
                Capture(scope.Window, "active-paragraph");
            }
            finally { engine.Resume(); }

            var result = await generation.WaitAsync(TimeSpan.FromSeconds(10));
            await scope.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            scope.Window.GetType().GetMethod("MarkCompleted")!.Invoke(scope.Window, [result]);
            await Render(scope.Window);
            Assert.Equal(new[] { "银行。", "你好。" }, scope.Rows().Select(row => row.OriginalText));
            Assert.All(scope.Rows(), row => Assert.Equal(PinyinBookSegmentStatus.Completed, row.Status));
            Assert.Equal(100, scope.Bar.Value);
            Assert.True(File.Exists(output));
            Capture(scope.Window, "completed-paragraphs");
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task FastLocalProgressYieldsToInputAndRendersBeforeCompletion() => Run(async () =>
    {
        using var scope = new ProgressWindow();
        const int count = 256;
        var renderedPercentages = new List<double>();
        scope.Bar.PropertyChanged += (_, change) =>
        {
            if (change.Property == ProgressBar.ValueProperty)
                Dispatcher.UIThread.Post(() => renderedPercentages.Add(scope.Bar.Value), DispatcherPriority.Render);
        };
        for (var index = 0; index < count; index++)
        {
            scope.Report(Row(index, PinyinBookSegmentStatus.Processing), 30 + index * 60d / count, index, count);
            scope.Report(Row(index, PinyinBookSegmentStatus.Completed), 30 + (index + 1) * 60d / count, index + 1, count);
        }
        scope.Progress.Report(new PinyinBookProgress("完成", "chapter.xhtml", 1, 1, count,
            OverallPercentage: 100, ProcessedSegments: count, TotalSegments: count));

        var input = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => input.SetResult(scope.List.ItemCount), DispatcherPriority.Input);
        var flushed = scope.FlushAsync();
        Assert.False(flushed.IsCompleted);
        await Task.WhenAll(input.Task, flushed).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await input.Task < count, "Input must run before the paragraph queue drains.");
        Assert.True(renderedPercentages.Count(value => value > 0 && value < 100) > 1,
            "Render callbacks must see intermediate progress, not only the final 100%.");
        Assert.Equal(count, scope.List.ItemCount);
        Assert.Equal(100, scope.Bar.Value);
        Assert.Equal(Enumerable.Range(0, count), scope.Rows().Select(row => row.Index));
        Assert.All(scope.Rows(), row => Assert.Equal(PinyinBookSegmentStatus.Completed, row.Status));
    });

    [Fact]
    public Task LongBooksVirtualizeHistoryAndFollowCompletedLocalParagraphs() => Run(async () =>
    {
        using var scope = new ProgressWindow();
        const int count = 1500;
        for (var index = 0; index < count; index++)
            scope.Report(Row(index, PinyinBookSegmentStatus.Completed), 30 + (index + 1) * 60d / count, index + 1, count);
        await scope.FlushAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Render(scope.Window);

        Assert.Equal(count, scope.List.ItemCount);
        Assert.InRange(scope.List.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 24);
        var last = scope.List.ContainerFromIndex(count - 1);
        Assert.NotNull(last);
        Assert.Contains(last.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Row(count - 1).OriginalText);
        Capture(scope.Window, "long-book-latest");

        scope.List.ScrollIntoView(scope.List.Items[0]!);
        await Render(scope.Window);
        var first = scope.List.ContainerFromIndex(0);
        Assert.NotNull(first);
        Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Row(0).OriginalText);
        Assert.DoesNotContain(first.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Row(count - 1).OriginalText);
        Assert.InRange(scope.List.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 24);
    });

    [Fact]
    public Task ReviewResultsStayOrderedAndCancellationKeepsCompletedRows() => session.Session.Dispatch(() =>
    {
        using var scope = new ProgressWindow();
        for (var index = 0; index < 5; index++) scope.Update(Row(index));
        scope.Update(Row(2, PinyinBookSegmentStatus.Completed));
        Assert.Equal(new[] { 2, 0, 1, 3, 4 }, scope.Rows().Select(row => row.Index));
        scope.Update(Row(1, PinyinBookSegmentStatus.Failed));
        Assert.Equal(new[] { 2, 0, 3, 4, 1 }, scope.Rows().Select(row => row.Index));
        scope.Update(Row(3, PinyinBookSegmentStatus.Completed));
        scope.Update(Row(0, PinyinBookSegmentStatus.Completed));
        scope.Update(Row(4, PinyinBookSegmentStatus.Failed));
        Assert.Equal(new[] { 0, 2, 3, 1, 4 }, scope.Rows().Select(row => row.Index));
        scope.Update(Row(1));
        scope.Update(Row(4));
        Assert.Equal(new[] { 0, 2, 3, 1, 4 }, scope.Rows().Select(row => row.Index));

        scope.Window.GetType().GetMethod("MarkCanceled")!.Invoke(scope.Window, null);
        scope.Update(Row(1, PinyinBookSegmentStatus.Completed));
        Assert.Equal(3, scope.Rows().Count(row => row.Status == PinyinBookSegmentStatus.Completed));
        Assert.Equal(2, scope.Rows().Count(row => row.Status == PinyinBookSegmentStatus.Canceled));
    }, CancellationToken.None);

    [Fact]
    public Task ClosingWindowReleasesPendingProgressWithoutReplayingIt() => Run(async () =>
    {
        using var scope = new ProgressWindow();
        for (var index = 0; index < 500; index++) scope.Report(Row(index), 30, 0, 500);
        var flushed = scope.FlushAsync();
        Assert.False(flushed.IsCompleted);
        scope.Window.Close();
        await flushed.WaitAsync(TimeSpan.FromSeconds(5));
        scope.Report(Row(501), 90, 500, 501);
        Assert.Empty(scope.Rows());
    });

    // Dispatch(Func<T>) would otherwise return Task<Task> for an async lambda,
    // allowing xUnit to finish before its UI assertions run.
    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private static PinyinBookSegmentProgress Row(int index, PinyinBookSegmentStatus status = PinyinBookSegmentStatus.Processing) =>
        new(index, "chapter.xhtml", $"第 {index} 段：春风吹过山谷，读书的人在窗前认真地读着一本书。",
            status == PinyinBookSegmentStatus.Processing ? string.Empty : "春(chūn)风(fēng)吹(chuī)过(guò)山(shān)谷(gǔ)。",
            status, status == PinyinBookSegmentStatus.Processing ? "读取原文 → 本地注音" : "读取原文 → 本地注音 → 写入 EPUB");

    private static async Task Render(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }, DispatcherPriority.Background);
            if (pass == 0) await Task.Delay(150);
        }
    }

    private static void Capture(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_PINYIN_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var image = window.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private sealed class GatedPinyinEngine : IPinyinEngine
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string EngineId => "progress-test";
        public string[] ToPinyinList(string text, PinyinBookOutputStyle style)
        {
            Entered.TrySetResult();
            _resume.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return text.Select(character => LookupChar(character)[0]).ToArray();
        }
        public bool ContainsChar(char character) => character is '银' or '行' or '你' or '好';
        public IReadOnlyList<string> LookupChar(char character) => [character switch
        {
            '银' => "yín", '行' => "háng", '你' => "nǐ", '好' => "hǎo", _ => character.ToString()
        }];
        public void Resume() => _resume.TrySetResult();
        public void Dispose() => Resume();
    }

    private sealed class ProgressWindow : IDisposable
    {
        private readonly object _reporter;
        public Window Window { get; }
        public ListBox List { get; }
        public ProgressBar Bar { get; }
        public IProgress<PinyinBookProgress> Progress => (IProgress<PinyinBookProgress>)_reporter;

        public ProgressWindow()
        {
            var assembly = typeof(MainWindow).Assembly;
            Window = (Window)Activator.CreateInstance(assembly.GetType("Kkindle.PinyinBookProgressWindow")!, "注音瀑布流测试", false)!;
            Window.Show();
            List = Field<ListBox>("_segmentList");
            Bar = Field<ProgressBar>("_progressBar");
            _reporter = Activator.CreateInstance(assembly.GetType("Kkindle.PinyinBookProgressReporter")!, Window)!;
        }

        public void Report(PinyinBookSegmentProgress row, double percentage, int processed, int total) =>
            Progress.Report(new PinyinBookProgress("正在生成拼音", row.EntryName, 1, 1, processed,
                OverallPercentage: percentage, Segment: row, ProcessedSegments: processed, TotalSegments: total));

        public void Update(PinyinBookSegmentProgress row) => Window.GetType().GetMethod("Update")!.Invoke(Window,
            [new PinyinBookProgress("正在生成拼音", row.EntryName, 1, 1, 0, OverallPercentage: 50, Segment: row)]);

        public Task FlushAsync() => (Task)_reporter.GetType().GetMethod("FlushAsync")!.Invoke(_reporter, null)!;

        public PinyinBookSegmentProgress[] Rows() => List.Items.Cast<object>()
            .Select(item => (PinyinBookSegmentProgress)item.GetType().GetProperty("Segment")!.GetValue(item)!).ToArray();

        private T Field<T>(string name) => (T)Window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;

        public void Dispose()
        {
            ((IDisposable)_reporter).Dispose();
            Window.Close();
        }
    }
}
