using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderTests(SettingsUiSession session)
{
    [Fact]
    public Task ExpandingAndSelectingSubchaptersFillsTheTocViewport() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var items = new List<EpubReaderNavigationItem>();
        foreach (var title in new[] { "封面", "目录", "封面", "代序 余秀华：让我疼痛的诗歌" })
            items.Add(new(title, "file:///book.xhtml", 0));
        items.Add(new("辑一", "file:///book.xhtml#part1", 0));
        for (var index = 0; index < 45; index++)
            items.Add(new(index == 0 ? "我爱你" : $"子章节 {index + 1}", $"file:///book.xhtml#p{index}", 0, 1));
        items[12] = items[12] with { Title = string.Concat(Enumerable.Repeat("需要完整换行显示的子章节标题", 5)) };
        items.Add(new("辑二", "file:///book.xhtml#part2", 0));
        items.Add(new("第二辑子章节", "file:///book.xhtml#part2-child", 0, 1));
        items.Add(new("后记", "file:///book.xhtml#afterword", 0));
        scope.Set("_readerTocItems", items);
        scope.Call("BuildReaderTocRows");
        scope.Call("RefreshReaderTocRows");
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await Render();
        var list = scope.Get<ListBox>("ReaderTocList");
        Assert.Equal(7, list.ItemCount);
        Assert.InRange(list.ContainerFromIndex(0)!.TranslatePoint(default, list)!.Value.Y, 0, 4);

        var rows = scope.Field<ReaderTocRow[]>("_readerTocRows");
        for (var iteration = 0; iteration < 3; iteration++)
        {
            scope.Call("ReaderTocToggle_Click", new Button { DataContext = rows[4] }, new RoutedEventArgs());
            scope.Call("SetReaderTocSelection", items[5]);
            await Render();
            Assert.Equal(52, list.ItemCount);
            Capture(scope.Window, "toc-expanded");
            AssertViewportFilled(list);

            var scrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height);
            await Render();
            Assert.NotNull(list.ContainerFromItem(rows[^1]));
            scope.Call("ReaderTocToggle_Click", new Button { DataContext = rows[4] }, new RoutedEventArgs());
            await Render();
            Assert.Equal(7, list.ItemCount);
            Assert.InRange(list.ContainerFromIndex(0)!.TranslatePoint(default, list)!.Value.Y, 0, 4);
        }
    });

    [Fact]
    public Task LargeNestedTocKeepsAllChildrenAndReusesTheVisibleListOnSelection() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var items = new List<EpubReaderNavigationItem> { new("第一卷", "file:///book.xhtml#part1", 0) };
        items.Add(new("第一组", "file:///book.xhtml#group1", 0, 1));
        for (var index = 0; index < 5000; index++)
            items.Add(new($"第 {index + 1} 章", $"file:///book.xhtml#chapter{index}", 0, 2));
        items.Add(new("第二组", "file:///book.xhtml#group2", 0, 1));
        items.Add(new("第二组子章节", "file:///book.xhtml#group2-child", 0, 2));
        items.Add(new("第二卷", "file:///book.xhtml#part2", 0));
        scope.Set("_readerTocItems", items);
        scope.Call("BuildReaderTocRows");
        scope.Call("RefreshReaderTocRows");
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await Render();
        var list = scope.Get<ListBox>("ReaderTocList");
        Assert.Equal(2, list.ItemCount);
        scope.Call("SetReaderTocSelection", items[2]);
        await Render();
        Assert.Equal(5004, list.ItemCount);
        Assert.InRange(list.GetRealizedContainers().Count(), 10, 50);
        AssertViewportFilled(list);
        var source = list.ItemsSource;
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var offset = scroll.Offset;
        scope.Call("SetReaderTocSelection", items[3]);
        await Render();
        Assert.Same(source, list.ItemsSource);
        Assert.Equal(offset, scroll.Offset);
        Assert.Equal(1, scope.Field<ReaderTocRow[]>("_readerTocRows").Count(row => row.IsCurrent));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TocNavigationReplacesAnUnfinishedOrCancelledPreload(bool sameChapter) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var chapters = Enumerable.Range(0, 3)
            .Select(index => Path.Combine(scope.Paths.ReaderCache, $"chapter{index}.xhtml"))
            .ToArray();
        var target = new Uri(chapters[sameChapter ? 1 : 2]);
        var current = new ControlledReaderHost(new Uri(chapters[0]));
        var hidden = new ControlledReaderHost();
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, chapters, [], []));
        scope.Set("_readerActiveHost", current);
        scope.Set("_readerPreloadHost", hidden);
        scope.Set("_readerSessionCancellation", cancellation);
        var preload = scope.Call<Task>("PreloadNextReaderChapterAsync", cancellation.Token);
        Assert.Equal(new Uri(chapters[1]), Assert.Single(hidden.Requests));
        if (sameChapter)
        {
            scope.Call("CancelReaderChapterPreload", (object?)null);
            await preload;
        }
        var navigation = scope.Call<Task<bool>>("NavigateToReaderItemAsync",
            new EpubReaderNavigationItem("Target chapter", target.AbsoluteUri, sameChapter ? 1 : 2),
            cancellation.Token, ReaderNavigationIntent.Toc, null);
        try
        {
            // An unfinished preload cannot complete on its own. The user's
            // request must cancel it and reach the host without that result.
            var deadline = Task.Delay(TimeSpan.FromSeconds(2));
            while (hidden.Requests.Count < 2 && !deadline.IsCompleted && !navigation.IsCompleted)
                await Task.Delay(10);
            Assert.Equal(2, hidden.Requests.Count);
            Assert.Equal(target, hidden.Requests[1]);
            Assert.Equal(1, hidden.StopCount);
            hidden.CompleteNavigation();
            Assert.True(await navigation.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            cancellation.Cancel();
            await preload;
            await navigation;
            scope.Set("_readerSessionCancellation", null);
        }
    });

    [Fact]
    public Task ClickingTheChapterBeingPreloadedUsesThatLoad() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var chapters = Enumerable.Range(0, 2)
            .Select(index => Path.Combine(scope.Paths.ReaderCache, $"chapter{index}.xhtml"))
            .ToArray();
        var hidden = new ControlledReaderHost();
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, chapters, [], []));
        scope.Set("_readerActiveHost", new ControlledReaderHost(new Uri(chapters[0])));
        scope.Set("_readerPreloadHost", hidden);
        scope.Set("_readerSessionCancellation", cancellation);
        var preload = scope.Call<Task>("PreloadNextReaderChapterAsync", cancellation.Token);
        var navigation = scope.Call<Task<bool>>("NavigateToReaderItemAsync",
            new EpubReaderNavigationItem("Next chapter", new Uri(chapters[1]).AbsoluteUri, 1),
            cancellation.Token, ReaderNavigationIntent.Toc, null);
        try
        {
            hidden.CompleteNavigation();
            Assert.True(await navigation.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Single(hidden.Requests);
            Assert.Equal(0, hidden.StopCount);
        }
        finally
        {
            cancellation.Cancel();
            await preload;
            await navigation;
            scope.Set("_readerSessionCancellation", null);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task FirstChapterUsesTheBookSettingsWithoutASecondLayout(bool vertical, bool spread) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = new NativeReaderHost();
        var path = Path.Combine(scope.Paths.ReaderCache, "chapter.xhtml");
        await File.WriteAllTextAsync(path, "<html><body><h1 id='chapter'>章节标题</h1>"
            + string.Concat(Enumerable.Repeat("<p>用于验证章节首次排版的正文，包含中文和 English words。</p>", 60))
            + "</body></html>");
        var settings = new ReaderLayoutSettings(FontScale: 1.5, LineHeight: 2.2,
            VerticalWriting: vertical, TwoPageMode: spread) { ParagraphIndent = false };
        scope.Set("_readerLayout", settings);
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, [path], [], []));
        scope.Set("_readerActiveHost", host);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = host;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await Render();
        ChapterLayout? firstLayout = null;
        host.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess) firstLayout = NativeField<ChapterLayout>(host, "_layout");
        };
        Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostAndWaitAsync",
            host, new Uri(path), CancellationToken.None, false));
        Assert.NotNull(firstLayout);
        Assert.Equal(vertical ? TypesetWritingMode.VerticalRl : TypesetWritingMode.HorizontalTb, firstLayout.Options.WritingMode);
        Assert.Equal(24f, firstLayout.Options.BaseFontSize);
        Assert.Equal(2.2f, firstLayout.Options.LineHeight);
        Assert.False(firstLayout.Options.ParagraphIndent);
        Assert.Same(firstLayout, NativeField<ChapterLayout>(host, "_layout"));
        Assert.False(NativeField<bool>(host, "_loadedParagraphIndent"));
        Assert.Equal(1L, NativeField<long>(host, "_navigationVersion"));
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
        scope.Set("_readerActiveHost", null);
    });

    [Fact]
    public Task CancelledNativeNavigationCannotCompleteANewerRequestToTheSameChapter() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = new NativeReaderHost();
        var path = Path.Combine(scope.Paths.ReaderCache, "chapter.xhtml");
        await File.WriteAllTextAsync(path, "<html><body><p>取消旧请求后仍应显示当前章节。</p></body></html>");
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = host;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await Render();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new List<bool>();
        host.NavigationCompleted += (_, args) =>
        {
            completions.Add(args.IsSuccess);
            if (args.IsSuccess) completed.TrySetResult();
        };
        host.Navigate(new Uri(path));
        host.Stop();
        host.Navigate(new Uri(path));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Render();
        Assert.Equal([true], completions);
        Assert.Contains("取消旧请求", NativeField<ChapterContent>(host, "_content").BodyText);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    private static T NativeField<T>(NativeReaderHost host, string name) =>
        (T)typeof(NativeReaderHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;

    private static void AssertViewportFilled(ListBox list)
    {
        var viewport = list.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var visibleRows = list.GetRealizedContainers()
            .Select(row => (Row: row, Top: row.TranslatePoint(default, viewport)!.Value.Y))
            .Where(entry => entry.Top >= 0 && entry.Top < viewport.Bounds.Height)
            .OrderBy(entry => entry.Top)
            .ToArray();
        Assert.True(visibleRows.Length >= 10, $"Only {visibleRows.Length} rows fill a {viewport.Bounds.Height}px viewport.");
        for (var index = 1; index < visibleRows.Length; index++)
            Assert.InRange(visibleRows[index].Top - visibleRows[index - 1].Top - visibleRows[index - 1].Row.Bounds.Height, -0.5, 3);
        Assert.True(visibleRows[^1].Top + visibleRows[^1].Row.Bounds.Height >= viewport.Bounds.Height - 45,
            $"The last row ends at {visibleRows[^1].Top + visibleRows[^1].Row.Bounds.Height}px in a {viewport.Bounds.Height}px viewport.");
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);

    internal static async Task Render()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Task.Delay(100);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void Capture(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var image = window.CaptureRenderedFrame();
        image!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

internal sealed class ControlledReaderHost(Uri? source = null) : IReaderHost
{
    public object View { get; } = new Border();
    public Uri? Source { get; private set; } = source;
    public Task ReadyTask => Task.CompletedTask;
    public List<Uri> Requests { get; } = [];
    public int StopCount { get; private set; }
    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived { add { } remove { } }
    public void Navigate(Uri uri)
    {
        NavigationStarting?.Invoke(this, new ReaderNavigationStartingEventArgs(uri));
        Source = uri;
        Requests.Add(uri);
    }
    public void CompleteNavigation() => NavigationCompleted?.Invoke(this, new(Source, true));
    public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>(null);
    public void Stop() => StopCount++;
    public void Dispose() { }
}

internal sealed class ReaderTestWindow(MainWindow window, AppPaths paths, string directory) : IAsyncDisposable
{
    public MainWindow Window => window;
    public AppPaths Paths => paths;
    public T Get<T>(string name) where T : Control => window.FindControl<T>(name)!;
    public T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    public void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    public object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    public T Call<T>(string name, params object?[] args) => (T)Call(name, args)!;

    public static async Task<ReaderTestWindow> Create()
    {
        ((Kkindle.App)Application.Current!).ApplyLanguage("zh-CN");
        var directory = Path.Combine(Path.GetTempPath(), "kkindle-reader-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(directory);
        paths.EnsureDirectories();
        var settings = new AppSettings
        {
            UiLanguage = "zh-CN", OnboardingCompleted = true, NetworkEnabled = false,
            AutoUpdateCheckEnabled = false, AutoConnectDevice = false
        };
        await new AppSettingsStore(paths).SaveAsync(settings);
        var library = new SqliteBookLibraryService(paths, new BookMetadataService());
        await library.InitializeAsync();
        var window = new MainWindow(paths, library, startupSettings: settings) { Width = 1294, Height = 818 };
        var scope = new ReaderTestWindow(window, paths, directory);
        await scope.Field<ReaderDataService>("_readerData").InitializeAsync();
        window.Show();
        await ReaderTests.Render();
        return scope;
    }

    public async ValueTask DisposeAsync()
    {
        if (window.IsVisible)
        {
            Set("_allowWindowCloseForS3Sync", true);
            window.Close();
        }
        await Task.Delay(30);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
