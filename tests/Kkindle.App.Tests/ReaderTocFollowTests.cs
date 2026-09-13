using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderTocFollowTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    public Task ReadingThroughSubchaptersUpdatesTheTocInBothDirections(int flow, bool vertical, bool spread) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await LoadBook(scope, new(FlowMode: flow, VerticalWriting: vertical, TwoPageMode: spread));
        var layout = NativeField<ChapterLayout>(host, "_layout");
        var secondPage = layout.GetPageIndexOfFragment("chapter-二");
        var thirdPage = layout.GetPageIndexOfFragment("three");
        Assert.True(secondPage > 1 && thirdPage > secondPage + 3);

        // Read normally, without clicking a TOC row or asking the shell to
        // refresh. Incidental paragraph ids must never replace a TOC anchor.
        while (VisiblePage(host) < secondPage) Advance(host, 1);
        AssertCurrent(scope, "二", "chapter-二");
        await ReaderTests.Render();
        Capture(scope.Window, $"toc-follow-{flow}-{vertical}-{spread}");
        var rows = scope.Field<ReaderTocRow[]>("_readerTocRows");
        Assert.True(rows.Single(row => row.Title == "上部").IsExpanded);
        var source = scope.Get<ListBox>("ReaderTocList").ItemsSource;
        Advance(host, 1);
        AssertCurrent(scope, "二", "chapter-二");
        Assert.Same(source, scope.Get<ListBox>("ReaderTocList").ItemsSource);
        while (VisiblePage(host) >= secondPage) Advance(host, -1);
        AssertCurrent(scope, "一", "one");
        host.SeekToBoundary(toEnd: false);
        AssertCurrent(scope, "封面", "cover");
        Detach(scope);
    });

    [Fact]
    public Task ReadingPositionRefreshAndLayoutChangesKeepTheCurrentSubchapter() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await LoadBook(scope, new());
        var layout = NativeField<ChapterLayout>(host, "_layout");
        var page = layout.GetPageIndexOfFragment("chapter-二") + 2;
        host.SeekToPixelScroll(page * host.Bounds.Width);
        AssertCurrent(scope, "二", "chapter-二");

        // A host swap/restore reads state directly, independently of a scroll
        // event. It must correct an old selection as well as the page counter.
        scope.Call("SetReaderTocSelection", scope.Field<IReadOnlyList<EpubReaderNavigationItem>>("_readerTocItems")[0]);
        scope.Set("_readerCurrentFragment", null);
        await scope.Call<Task>("UpdateReaderScrollStateAsync", host);
        AssertCurrent(scope, "二", "chapter-二");

        var offset = layout.Pages[host.CurrentPage].TextStartOffset;
        scope.Set("_readerLayout", new ReaderLayoutSettings(FontScale: 1.35));
        await scope.Call<Task>("ApplyReaderLayoutToHostsAsync", CancellationToken.None);
        await ReaderTests.Render();
        layout = NativeField<ChapterLayout>(host, "_layout");
        Assert.Equal(layout.GetPageIndexOfOffset(offset), host.CurrentPage);
        Assert.True(host.CurrentPage > layout.GetPageIndexOfFragment("chapter-二"));
        AssertCurrent(scope, "二", "chapter-二");
        Detach(scope);
    });

    [Fact]
    public Task ReturningAcrossAChapterBoundarySelectsItsFinalSubchapter() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var active = await LoadBook(scope, new());
        using var preload = new NativeReaderHost();
        preload.WebMessageReceived += (sender, args) => scope.Call("ReaderHost_WebMessageReceived", sender, args);
        scope.Set("_readerPreloadHost", preload);
        scope.Get<ContentControl>("ReaderPreloadHostSlot").Content = preload;
        scope.Call("SetReaderHostLayer", true);
        await ReaderTests.Render();
        await scope.Call<Task>("PreloadNextReaderChapterAsync", CancellationToken.None);
        AssertCurrent(scope, "封面", "cover");

        await scope.Call<Task>("MoveReaderChapterAsync", 1, false, false);
        AssertCurrent(scope, "下部", "other");
        await scope.Call<Task>("MoveReaderChapterAsync", -1, false, false);
        AssertCurrent(scope, "三", "three");
        Assert.False(scope.Field<bool>("_readerShowingPreload"));
        Detach(scope);
    });

    [Fact]
    public Task RestoringProgressTracksTheSavedPageInsteadOfTheInitialCoverReport() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await LoadBook(scope, new());
        var layout = NativeField<ChapterLayout>(host, "_layout");
        var page = layout.GetPageIndexOfFragment("chapter-二") + 2;
        var progress = new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "sections.xhtml",
            "chapter-二", 0, (int)(page * host.Bounds.Width), 0.5, 1, DateTimeOffset.UtcNow);
        scope.Set("_readerRestoredProgress", progress);
        scope.Set("_readerScrollPosition", (double)progress.ScrollPosition);
        scope.Set("_readerCurrentFragment", progress.Fragment);
        scope.Field<Dictionary<IReaderHost, Uri>>("_readerLoadedHostSources").Remove(host);
        Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostAndWaitAsync",
            host, host.Source!, CancellationToken.None, false));
        Assert.Equal(page, host.CurrentPage);
        AssertCurrent(scope, "二", "chapter-二");
        Assert.Null(scope.Field<ReaderProgressRow?>("_readerRestoredProgress"));
        Detach(scope);
    });

    private static async Task<NativeReaderHost> LoadBook(ReaderTestWindow scope, ReaderLayoutSettings settings)
    {
        var chapter = Path.Combine(scope.Paths.ReaderCache, "sections.xhtml");
        var next = Path.Combine(scope.Paths.ReaderCache, "next.xhtml");
        static string Section(string id, string title, int paragraphs) => $"<h1 id='{id}'>{title}</h1>"
            + string.Concat(Enumerable.Range(0, paragraphs).Select(index =>
                $"<p id='{id}-paragraph-{index}'>第{index + 1}段。风吹过树梢，阳光落在书页上。章节中的正文继续向前，目录应当跟随正在阅读的小节。The story continues across each page.</p>"));
        await File.WriteAllTextAsync(chapter, "<html><body>" + Section("cover", "封面", 1)
            + Section("volume", "上部", 1) + Section("one", "一", 45)
            + Section("chapter-二", "二", 45) + Section("three", "三", 45) + "</body></html>");
        await File.WriteAllTextAsync(next, "<html><body>" + Section("other", "下部", 8) + "</body></html>");
        var uri = new Uri(chapter).AbsoluteUri;
        EpubReaderNavigationItem[] items = [
            new("封面", uri + "#cover", 0), new("上部", uri + "#volume", 0),
            new("一", uri + "#one", 0, 1), new("二", uri + "#chapter-%E4%BA%8C", 0, 1),
            new("三", uri + "#three", 0, 1), new("下部", new Uri(next).AbsoluteUri + "#other", 1)];
        scope.Set("_readerLayout", settings);
        scope.Set("_readerPageAnimation", 0);
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, [chapter, next], items, []));
        scope.Set("_readerTocItems", items);
        scope.Call("BuildReaderTocRows");
        scope.Call("RefreshReaderTocRows");
        scope.Call("SetReaderCompactNavigationItems", (object)items);
        scope.Call("SetReaderTocSelection", items[0]);
        var host = new NativeReaderHost();
        host.WebMessageReceived += (sender, args) => scope.Call("ReaderHost_WebMessageReceived", sender, args);
        scope.Set("_readerActiveHost", host);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = host;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Get<TextBlock>("ReaderBookInfoText").Text = "目录跟随验证 · EPUB";
        scope.Call("ApplyReaderPanelLayout");
        await ReaderTests.Render();
        Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostAndWaitAsync",
            host, new Uri(chapter), CancellationToken.None, false));
        await ReaderTests.Render();
        return host;
    }

    private static void Advance(NativeReaderHost host, int direction)
    {
        if (host.IsPaginated) Assert.True(host.TurnPage(direction));
        else host.ScrollByPixel(direction * host.Bounds.Height);
    }

    private static int VisiblePage(NativeReaderHost host) => host.IsPaginated
        ? host.CurrentPage : (int)(host.GetScrollState().Position / host.Bounds.Height);

    private static void AssertCurrent(ReaderTestWindow scope, string title, string fragment)
    {
        var row = Assert.IsType<ReaderTocRow>(scope.Get<ListBox>("ReaderTocList").SelectedItem);
        Assert.Equal(title, row.Title);
        Assert.True(row.IsCurrent);
        Assert.Single(scope.Field<ReaderTocRow[]>("_readerTocRows"), item => item.IsCurrent);
        Assert.Equal(fragment, scope.Field<string?>("_readerCurrentFragment"));
        Assert.Equal(row.Item.Target, scope.Field<string?>("_readerCompactSelectedTarget"));
    }

    private static void Detach(ReaderTestWindow scope)
    {
        scope.Set("_readerActiveHost", null);
        scope.Set("_readerPreloadHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
        scope.Get<ContentControl>("ReaderPreloadHostSlot").Content = null;
    }

    private static T NativeField<T>(NativeReaderHost host, string name) =>
        (T)typeof(NativeReaderHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static void Capture(MainWindow window, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var image = window.CaptureRenderedFrame();
        image!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}
