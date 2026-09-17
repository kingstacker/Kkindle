using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderTransitionTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(3, false, false, false)]
    [InlineData(3, true, false, false)]
    [InlineData(2, false, true, false)]
    [InlineData(2, true, true, false)]
    [InlineData(2, true, false, true)]
    public Task ChapterBoundariesUseThePageAnimationInBothDirections(
        int animation, bool preloaded, bool vertical, bool spread) => Run(async () =>
    {
        await using var reader = await ReaderFixture.Create(preloaded, vertical, spread);
        var scope = reader.Scope;
        scope.Set("_readerPageAnimation", animation);

        // Exercise the ordinary page turn first, then the same entry point at
        // both chapter boundaries. The outgoing pixels must survive even when
        // the destination is loaded into that same native host.
        Assert.True(reader.Current.CanTurn(1));
        await ObserveTurn(reader, animation, 1, "page");
        reader.Current.SeekToBoundary(toEnd: true);
        await ReaderTests.Render();
        await ObserveTurn(reader, animation, 1, "next-chapter");
        Assert.Equal(1, scope.Field<int>("_readerChapterIndex"));
        Assert.Equal(new Uri(reader.Chapters[1]), reader.Current.Source);
        Assert.Equal(0, reader.Current.CurrentPage);
        Assert.Equal(preloaded, scope.Field<bool>("_readerShowingPreload"));

        await ObserveTurn(reader, animation, -1, "previous-chapter");
        Assert.Equal(0, scope.Field<int>("_readerChapterIndex"));
        Assert.Equal(new Uri(reader.Chapters[0]), reader.Current.Source);
        Assert.False(reader.Current.CanTurn(1));
        Assert.False(scope.Field<bool>("_readerShowingPreload"));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public Task SlowChapterLoadHoldsTheOldPageUntilItIsReady(int animation) => Run(async () =>
    {
        await using var reader = await ReaderFixture.Create();
        var scope = reader.Scope;
        var incoming = new ControlledReaderHost();
        scope.Set("_readerPreloadHost", incoming);
        scope.Get<ContentControl>("ReaderPreloadHostSlot").Content = incoming.View;
        scope.Set("_readerPageAnimation", animation);
        var expected = CaptureSignature(reader.Current);
        var snapshot = scope.Get<Image>("ReaderNativeTransitionSnapshot");
        var animated = false;
        snapshot.PropertyChanged += (_, _) => animated |= HasMotion(snapshot);

        var navigation = Navigate(scope, reader.Chapters[1], 1);
        await Until(() => incoming.Requests.Count == 1);
        Assert.True(snapshot.IsVisible);
        Assert.Equal(expected, Signature(Assert.IsType<RenderTargetBitmap>(snapshot.Source)));
        // Longer than a complete page-turn effect: loading must not consume
        // the animation clock or expose the incoming blank document.
        await Task.Delay(600);
        Assert.True(snapshot.IsVisible);
        Assert.Equal(1, snapshot.Opacity);
        Assert.False(animated);

        incoming.CompleteNavigation();
        if (animation != 0)
            await Until(() => IsPlaying(snapshot, animation, 1));
        Assert.True(await navigation.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(animation != 0, animated);
        Assert.Equal(1, scope.Field<int>("_readerChapterIndex"));
        AssertClean(scope);
    });

    [Fact]
    public Task SupersededTocLoadCannotClearTheNewChapterAnimation() => Run(async () =>
    {
        await using var reader = await ReaderFixture.Create(chapterCount: 3);
        var scope = reader.Scope;
        var incoming = new ControlledReaderHost();
        scope.Set("_readerPreloadHost", incoming);
        scope.Get<ContentControl>("ReaderPreloadHostSlot").Content = incoming.View;
        scope.Set("_readerPageAnimation", 2);
        var first = Navigate(scope, reader.Chapters[1], 1);
        await Until(() => incoming.Requests.Count == 1);
        var latest = Navigate(scope, reader.Chapters[2], 2);
        await Until(() => incoming.Requests.Count == 2);
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, incoming.StopCount);
        Assert.Equal(new Uri(reader.Chapters[2]), incoming.Requests[1]);
        var snapshot = scope.Get<Image>("ReaderNativeTransitionSnapshot");
        Assert.True(snapshot.IsVisible);
        await Task.Delay(100);
        Assert.True(snapshot.IsVisible);
        Assert.Equal(1, snapshot.Opacity);

        incoming.CompleteNavigation();
        await Until(() => IsPlaying(snapshot, 2, 1));
        Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(2, scope.Field<int>("_readerChapterIndex"));
        AssertClean(scope);
    });

    [Fact]
    public Task FailedChapterLoadReleasesTheSnapshotAndAllowsAnotherJump() => Run(async () =>
    {
        await using var reader = await ReaderFixture.Create(chapterCount: 3);
        var scope = reader.Scope;
        scope.Set("_readerPageAnimation", 2);
        var missing = Path.Combine(scope.Paths.ReaderCache, "missing.xhtml");
        Assert.False(await Navigate(scope, missing, 1).WaitAsync(TimeSpan.FromSeconds(5)));
        AssertClean(scope);

        var navigation = Navigate(scope, reader.Chapters[2], 2);
        await Until(() => IsPlaying(scope.Get<Image>("ReaderNativeTransitionSnapshot"), 2, 1));
        Assert.True(await navigation.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(2, scope.Field<int>("_readerChapterIndex"));
        AssertClean(scope);
    });

    private static async Task ObserveTurn(ReaderFixture reader, int animation, int direction, string label)
    {
        var scope = reader.Scope;
        var expected = CaptureSignature(reader.Current);
        var visualDirection = reader.Current.Vertical ? -direction : direction;
        var snapshot = scope.Get<Image>("ReaderNativeTransitionSnapshot");
        var turn = scope.Call<Task>("TurnReaderPageCoreAsync", direction, false);
        await Until(() => IsPlaying(snapshot, animation, visualDirection));
        Assert.False(turn.IsCompleted);
        Assert.Equal(expected, Signature(Assert.IsType<RenderTargetBitmap>(snapshot.Source)));
        CaptureFrame(scope, $"animation-{animation}-{reader.Description}-{label}");
        await turn.WaitAsync(TimeSpan.FromSeconds(5));
        AssertClean(scope);
        await ReaderTests.Render();
    }

    private static Task<bool> Navigate(ReaderTestWindow scope, string path, int chapter) =>
        scope.Call<Task<bool>>("NavigateToReaderItemAsync",
            new EpubReaderNavigationItem($"第{chapter + 1}章", new Uri(path).AbsoluteUri, chapter),
            CancellationToken.None, ReaderNavigationIntent.Toc, null);

    private static bool IsPlaying(Image snapshot, int animation, int direction) => snapshot.IsVisible && animation switch
    {
        1 => snapshot.Opacity is > 0 and < 1,
        2 => snapshot.RenderTransform is TranslateTransform slide && slide.X * direction < -1,
        3 => snapshot.OpacityMask is LinearGradientBrush mask
            && (direction > 0
                ? mask.GradientStops[0].Color.A > mask.GradientStops[^1].Color.A
                : mask.GradientStops[0].Color.A < mask.GradientStops[^1].Color.A),
        _ => false
    };

    private static bool HasMotion(Image snapshot) => snapshot.RenderTransform is not null
        || snapshot.OpacityMask is not null || snapshot.Opacity != 1;

    private static void AssertClean(ReaderTestWindow scope)
    {
        var snapshot = scope.Get<Image>("ReaderNativeTransitionSnapshot");
        Assert.False(snapshot.IsVisible);
        Assert.Null(snapshot.Source);
        Assert.False(HasMotion(snapshot));
        foreach (var name in new[] { "Trail", "Front", "Edge" })
            Assert.False(scope.Get<Control>("ReaderNativeTransition" + name).IsVisible);
    }

    private static byte[] CaptureSignature(NativeReaderHost host)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(host.Bounds.Width), (int)Math.Ceiling(host.Bounds.Height)),
            new Vector(96, 96));
        bitmap.Render(host);
        return Signature(bitmap);
    }

    private static byte[] Signature(RenderTargetBitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return SHA256.HashData(stream.ToArray());
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(5));
        while (!condition() && !deadline.IsCompleted)
            await Task.Delay(10);
        Assert.True(condition(), "The expected reader transition state was not reached.");
    }

    private static void CaptureFrame(ReaderTestWindow scope, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var image = scope.Window.CaptureRenderedFrame();
        image!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private sealed class ReaderFixture(
        ReaderTestWindow scope, NativeReaderHost active, NativeReaderHost? preload,
        string[] chapters, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        public ReaderTestWindow Scope => scope;
        public string[] Chapters => chapters;
        public NativeReaderHost Current => scope.Field<bool>("_readerShowingPreload") ? preload! : active;
        public string Description => $"{(preload is null ? "same-host" : "preloaded")}-{(active.Vertical ? "vertical" : "horizontal")}";

        public static async Task<ReaderFixture> Create(
            bool preloaded = false, bool vertical = false, bool spread = false, int chapterCount = 2)
        {
            var scope = await ReaderTestWindow.Create();
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var chapters = Enumerable.Range(0, chapterCount)
                .Select(index => Path.Combine(scope.Paths.ReaderCache, $"chapter{index}.xhtml")).ToArray();
            for (var index = 0; index < chapters.Length; index++)
            {
                await File.WriteAllTextAsync(chapters[index], $"<html><body><h1>第{index + 1}章</h1>"
                    + string.Concat(Enumerable.Range(1, 45).Select(paragraph =>
                        $"<p>第{index + 1}章，第{paragraph}段。风吹过树梢，阳光落在书页上。跨过章节时，文字依然连贯地向前。The next page continues the story.</p>"))
                    + "</body></html>");
            }
            var active = new NativeReaderHost();
            var preload = preloaded ? new NativeReaderHost() : null;
            var items = chapters.Select((path, index) =>
                new EpubReaderNavigationItem($"第{index + 1}章", new Uri(path).AbsoluteUri, index)).ToArray();
            scope.Set("_readerSessionCancellation", cancellation);
            scope.Set("_readerLayout", new ReaderLayoutSettings(VerticalWriting: vertical, TwoPageMode: spread));
            scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, chapters, [], []));
            scope.Set("_readerTocItems", items);
            scope.Call("BuildReaderTocRows");
            scope.Call("RefreshReaderTocRows");
            scope.Set("_readerActiveHost", active);
            scope.Set("_readerPreloadHost", preload);
            scope.Get<ContentControl>("ReaderActiveHostSlot").Content = active;
            scope.Get<ContentControl>("ReaderPreloadHostSlot").Content = preload;
            scope.Get<Control>("ReaderPreloadHostSlot").Opacity = 0;
            scope.Get<Control>("LibraryRoot").IsVisible = false;
            scope.Get<Control>("ReaderRoot").IsVisible = true;
            scope.Call("ApplyReaderPanelLayout");
            await ReaderTests.Render();
            Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostAndWaitAsync",
                active, new Uri(chapters[0]), cancellation.Token, false));
            scope.Call("SetReaderHostLayer", true);
            await ReaderTests.Render();
            if (preloaded)
                await scope.Call<Task>("PreloadNextReaderChapterAsync", cancellation.Token);
            return new(scope, active, preload, chapters, cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            await Task.Delay(30);
            scope.Set("_readerActiveHost", null);
            scope.Set("_readerPreloadHost", null);
            scope.Set("_readerSessionCancellation", null);
            scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
            scope.Get<ContentControl>("ReaderPreloadHostSlot").Content = null;
            active.Dispose();
            preload?.Dispose();
            cancellation.Dispose();
            await scope.DisposeAsync();
        }
    }
}
