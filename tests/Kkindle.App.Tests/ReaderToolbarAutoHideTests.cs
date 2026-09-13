using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Layout;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderToolbarAutoHideTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(1, false, true, false)]
    [InlineData(0, false, false, false)]
    [InlineData(1, false, false, true)]
    [InlineData(1, true, false, true)]
    [InlineData(1, false, true, true)]
    [InlineData(0, false, false, true)]
    public Task HiddenToolbarsReclaimSpaceAndPreserveReadingPositionAcrossReflow(int flow, bool spread, bool vertical, bool minimal) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope,
            new ReaderLayoutSettings(FlowMode: flow, TwoPageMode: spread, VerticalWriting: vertical));
        if (minimal)
            scope.Call("SetReaderTocMinimal", true);
        else
            CloseToc(scope);
        MovePointer(scope, 0.5, 0.005);
        await WaitForToolbars(scope, visible: true);
        host.SeekToRatio(0.4);
        await ReaderTests.Render();
        var bounds = host.Bounds;
        var state = host.GetScrollState();
        var quote = host.GetCurrentPageQuote();
        var anchor = FirstVisibleTextOffset(host, flow == 0);
        Assert.Equal(minimal, scope.Get<Border>("ReaderTocCompactPanel").IsVisible);
        AssertToolbarsDoNotCoverText(scope, host);

        foreach (var edge in new[] { 0.005, 0.995, 0.005 })
        {
            MovePointer(scope, 0.5, 0.5);
            await WaitForToolbars(scope, visible: false);
            var reclaimedSpace = scope.Get<Border>("ReaderHeaderBar").Bounds.Height
                + scope.Get<Border>("ReaderFooterBar").Bounds.Height;
            Assert.Equal(bounds.Height + reclaimedSpace, host.Bounds.Height);
            Assert.Contains(VisibleRuns(host, flow == 0, spread), run =>
                run.TextStart <= anchor && run.TextStart + run.TextLength > anchor);
            Capture(scope, $"hidden-{flow}-{spread}-{vertical}-{minimal}");
            MovePointer(scope, 0.5, edge);
            await WaitForToolbars(scope, visible: true);
            await ReaderTests.Render();
            Assert.Equal(bounds, host.Bounds);
            Assert.Equal(state.Position, host.GetScrollState().Position, 6);
            Assert.Equal(state.Ratio, host.GetScrollState().Ratio, 6);
            Assert.Equal(quote, host.GetCurrentPageQuote());
            AssertToolbarsDoNotCoverText(scope, host);
            Capture(scope, $"revealed-{flow}-{spread}-{vertical}-{minimal}-{edge}");
        }
        Detach(scope);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public Task NavigationWhileHiddenBecomesTheNewReadingAnchor(int flow) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new(FlowMode: flow));
        scope.Call("SetReaderTocMinimal", true);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        host.SeekToRatio(0.25);
        MovePointer(scope, 0.5, 0.005);
        await WaitForToolbars(scope, visible: true);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        host.SeekToRatio(0.75);
        await ReaderTests.Render();
        var state = host.GetScrollState();
        var quote = host.GetCurrentPageQuote();
        MovePointer(scope, 0.5, 0.995);
        await WaitForToolbars(scope, visible: true);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        Assert.Equal(state.Position, host.GetScrollState().Position, 6);
        Assert.Equal(state.Ratio, host.GetScrollState().Ratio, 6);
        Assert.Equal(quote, host.GetCurrentPageQuote());
        Detach(scope);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OpeningFullTocRestoresPermanentToolbars(bool minimal) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        if (minimal)
            scope.Call("SetReaderTocMinimal", true);
        else
            CloseToc(scope);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        scope.Call("ReaderTocButton_Click", null, new RoutedEventArgs());
        await WaitForToolbars(scope, visible: true);
        Assert.False(scope.Field<bool>("_readerToolbarAutoHideEnabled"));
        AssertToolbarsDoNotCoverText(scope, host);
        MovePointer(scope, 0.5, 0.5);
        await Task.Delay(1750);
        Assert.Equal(1, scope.Get<Border>("ReaderHeaderBar").Opacity);
        Assert.Equal(1, scope.Get<Border>("ReaderFooterBar").Opacity);
        Detach(scope);
    });

    [Fact]
    public Task MenuAndSettingsStayVisibleUntilTheirInteractionEnds() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        scope.Call("SetReaderTocMinimal", true);
        MovePointer(scope, 0.5, 0.005);
        var menu = scope.Get<Button>("ReaderMoreButton").Flyout!;
        menu.ShowAt(scope.Get<Button>("ReaderMoreButton"));
        MovePointer(scope, 0.5, 0.5);
        await Task.Delay(1750);
        Assert.True(menu.IsOpen);
        Assert.Equal(1, scope.Get<Border>("ReaderHeaderBar").Opacity);
        menu.Hide();
        host.Focus(NavigationMethod.Pointer);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);

        scope.Call("ReaderLayoutSettingsButton_Click", null, new RoutedEventArgs());
        await WaitForToolbars(scope, visible: true);
        MovePointer(scope, 0.5, 0.5);
        await Task.Delay(1750);
        Assert.True(scope.Get<Popup>("ReaderLayoutSettingsPopup").IsOpen);
        Assert.Equal(1, scope.Get<Border>("ReaderHeaderBar").Opacity);
        scope.Call("ReaderLayoutSettingsCloseButton_Click", null, new RoutedEventArgs());
        host.Focus(NavigationMethod.Pointer);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        Detach(scope);
    });

    [Fact]
    public Task KeyboardFocusRevealsTheControlsAndKeepsThemAvailable() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        CloseToc(scope);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        Assert.True(scope.Get<Button>("ReaderMoreButton").Focus(NavigationMethod.Tab));
        await WaitForToolbars(scope, visible: true);
        await Task.Delay(1750);
        Assert.Equal(1, scope.Get<Border>("ReaderHeaderBar").Opacity);
        host.Focus(NavigationMethod.Pointer);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        Detach(scope);
    });

    [Fact]
    public Task ProgressThumbDragKeepsTheFooterVisibleOutsideItsBounds() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        CloseToc(scope);
        MovePointer(scope, 0.5, 0.995);
        await WaitForToolbars(scope, visible: true);
        await ReaderTests.Render();
        var thumb = scope.Get<Slider>("ReaderProgressSlider").GetVisualDescendants().OfType<Thumb>().First();
        var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        try
        {
            MovePointer(scope, 0.5, 0.5);
            await Task.Delay(1750);
            Assert.Equal(1, scope.Get<Border>("ReaderFooterBar").Opacity);
            Assert.True(scope.Get<Border>("ReaderFooterBar").IsHitTestVisible);
        }
        finally
        {
            scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        }
        host.Focus(NavigationMethod.Pointer);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        Detach(scope);
    });

    [Fact]
    public Task SelectingTextDoesNotResizeTheReadingAreaDuringTheDrag() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        CloseToc(scope);
        MovePointer(scope, 0.5, 0.005);
        await WaitForToolbars(scope, visible: true);
        var bounds = host.Bounds;
        var start = host.TranslatePoint(new Point(80, 90), scope.Window)!.Value;
        var end = start + new Vector(120, 0);
        scope.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        try
        {
            scope.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            await Task.Delay(1800);
            Assert.Equal(1, scope.Get<Border>("ReaderHeaderBar").Opacity);
            Assert.Equal(bounds, host.Bounds);
        }
        finally
        {
            scope.Window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        }
        Detach(scope);
    });

    [Fact]
    public Task ZenAndReaderCloseStopAutoHideAndRestoreTheCorrectMode() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        CloseToc(scope);
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        scope.Call("ToggleReaderZenMode");
        Assert.True(scope.Field<bool>("_readerZenMode"));
        Assert.False(scope.Field<bool>("_readerToolbarAutoHideEnabled"));
        Assert.False(scope.Get<Border>("ReaderHeaderBar").IsVisible);
        Assert.False(scope.Get<Border>("ReaderFooterBar").IsVisible);
        scope.Call("ExitReaderZenMode");
        Assert.True(scope.Field<bool>("_readerToolbarAutoHideEnabled"));
        MovePointer(scope, 0.5, 0.5);
        await WaitForToolbars(scope, visible: false);
        var bounds = host.Bounds;
        var gate = scope.Field<SemaphoreSlim>("_readerPageTurnGate");
        await gate.WaitAsync();
        var closing = scope.Call<Task>("CloseReaderAsync");
        try
        {
            Assert.Equal(bounds, host.Bounds);
            Assert.False(scope.Field<DispatcherTimer>("_readerToolbarHideTimer").IsEnabled);
        }
        finally { gate.Release(); }
        await closing;
        Assert.True(scope.Window.IsVisible);
        Assert.True(scope.Get<Control>("LibraryRoot").IsVisible);
        Assert.False(scope.Field<bool>("_readerToolbarAutoHideEnabled"));
        Assert.False(scope.Field<DispatcherTimer>("_readerToolbarHideTimer").IsEnabled);
        Detach(scope);
    });

    private static void CloseToc(ReaderTestWindow scope) =>
        scope.Call("ReaderTocButton_Click", null, new RoutedEventArgs());

    private static void AssertToolbarsDoNotCoverText(ReaderTestWindow scope, NativeReaderHost host)
    {
        var panel = scope.Get<Grid>("ReaderContentPanel");
        var header = scope.Get<Border>("ReaderHeaderBar");
        var footer = scope.Get<Border>("ReaderFooterBar");
        var viewport = new Rect(host.TranslatePoint(default, panel)!.Value, host.Bounds.Size);
        Assert.True(viewport.Width > 0 && viewport.Height > 0);
        Assert.True(viewport.Top >= header.Bounds.Bottom,
            $"Header {header.Bounds} overlaps the reader viewport {viewport}.");
        Assert.True(viewport.Bottom <= footer.Bounds.Top,
            $"Footer {footer.Bounds} overlaps the reader viewport {viewport}.");
    }

    private static void MovePointer(ReaderTestWindow scope, double x, double y)
    {
        var panel = scope.Get<Grid>("ReaderContentPanel");
        scope.Window.MouseMove(panel.TranslatePoint(
            new Point(panel.Bounds.Width * x, panel.Bounds.Height * y), scope.Window)!.Value);
    }

    private static async Task WaitForToolbars(ReaderTestWindow scope, bool visible)
    {
        var header = scope.Get<Border>("ReaderHeaderBar");
        var footer = scope.Get<Border>("ReaderFooterBar");
        bool Reached()
        {
            var area = scope.Get<Grid>("ReaderReadingArea");
            var panel = scope.Get<Grid>("ReaderContentPanel");
            var expectedTop = visible ? header.Bounds.Bottom : 0;
            var expectedHeight = panel.Bounds.Height - (visible ? header.Bounds.Height + footer.Bounds.Height : 0);
            if (header.Opacity != (visible ? 1 : 0) || footer.Opacity != (visible ? 1 : 0)
                || header.IsHitTestVisible != visible || footer.IsHitTestVisible != visible
                || Math.Abs(area.Bounds.Top - expectedTop) > 0.5
                || Math.Abs(area.Bounds.Height - expectedHeight) > 0.5) return false;
            return scope.Field<IReaderHost>("_readerActiveHost") is not NativeReaderHost host
                || Math.Abs(NativeLayout(host).Pages[0].Height - host.Bounds.Height) < 0.5;
        }
        var deadline = Task.Delay(TimeSpan.FromSeconds(5));
        while (!Reached() && !deadline.IsCompleted)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Assert.True(Reached(), $"Toolbars did not become {(visible ? "visible" : "hidden")}; header opacity is {header.Opacity}.");
    }

    private static ChapterLayout NativeLayout(NativeReaderHost host) =>
        (ChapterLayout)typeof(NativeReaderHost).GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static int FirstVisibleTextOffset(NativeReaderHost host, bool scroll) =>
        VisibleRuns(host, scroll, spread: false).First().TextStart;

    private static IEnumerable<PlacedRun> VisibleRuns(NativeReaderHost host, bool scroll, bool spread)
    {
        var layout = NativeLayout(host);
        var height = host.Bounds.Height;
        var position = host.GetScrollState().Position;
        var first = scroll ? (int)(position / height) : host.CurrentPage;
        var last = Math.Min(layout.Pages.Count - 1, first + (scroll || spread ? 1 : 0));
        for (var index = first; index <= last; index++)
        {
            foreach (var run in layout.Pages[index].Runs)
            {
                var y = run.OriginY + (scroll ? index * height - position : 0);
                if (run.TextStart >= 0 && run.TextLength > 0 && y >= 0 && y < height) yield return run;
            }
        }
    }

    private static void Detach(ReaderTestWindow scope)
    {
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    }

    private static void Capture(ReaderTestWindow scope, string name)
    {
        var output = Environment.GetEnvironmentVariable("KKINDLE_READER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = scope.Window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(output, "toolbars-" + name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}
