using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kkindle.Mobile.Controls;
using Kkindle.Mobile.Views.Sheets;
using Xunit;

namespace Kkindle.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class ReaderSettingsScrollTests(MobileUiSession session)
{
    [Fact]
    public Task TypographySettingsCanScrollWithoutShowingScrollbar() => session.Session.Dispatch(async () =>
    {
        var window = new Window { Width = 390, Height = 300, Content = new ReaderSettingsSheet() };
        try
        {
            window.Show();
            await Task.Delay(150);
            var scroll = window.GetVisualDescendants().OfType<ElasticScrollViewer>().Single();
            Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            Assert.True(scroll.Offset.Y > 0);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Fact]
    public Task ScrollingUpdatesEveryHostedControlPosition() => session.Session.Dispatch(async () =>
    {
        var previousSegment = NativeSegmentGroup.CreateNativeControl;
        var previousSlider = NativeSliderGroup.CreateNativeControl;
        var previousSwitch = NativeSwitchGroup.CreateNativeControl;
        NativeSegmentGroup.CreateNativeControl = _ => new TestSegment();
        NativeSliderGroup.CreateNativeControl = (_, _, _) => new TestSlider();
        NativeSwitchGroup.CreateNativeControl = (_, _) => new TestSwitch();
        var segment = new NativeSegmentGroup
        {
            Content = new StackPanel
            {
                Children = { new Button { Content = "一" }, new Button { Content = "二" } }
            }
        };
        var slider = new NativeSliderGroup { Content = new Slider() };
        var toggle = new NativeSwitchGroup { Content = new ToggleSwitch() };
        var scroll = new ElasticScrollViewer
        {
            Content = new StackPanel
            {
                Children = { segment, slider, toggle, new Border { Height = 900 } }
            }
        };
        var window = new Window { Width = 390, Height = 300, Content = scroll };
        try
        {
            window.Show();
            await WaitUntil(() => window.GetVisualDescendants().OfType<TestSegment>().Any()
                && window.GetVisualDescendants().OfType<TestSlider>().Any()
                && window.GetVisualDescendants().OfType<TestSwitch>().Any());
            var nativeSegment = window.GetVisualDescendants().OfType<TestSegment>().Single();
            var nativeSlider = window.GetVisualDescendants().OfType<TestSlider>().Single();
            var nativeSwitch = window.GetVisualDescendants().OfType<TestSwitch>().Single();
            var before = nativeSegment.TranslatePoint(default, window)!.Value.Y;
            scroll.Offset = new Vector(0, 50);
            await WaitUntil(() => nativeSegment.Updates > 0 && nativeSlider.Updates > 0 && nativeSwitch.Updates > 0);
            Assert.True(nativeSegment.TranslatePoint(default, window)!.Value.Y < before);
        }
        finally
        {
            NativeSegmentGroup.CreateNativeControl = previousSegment;
            NativeSliderGroup.CreateNativeControl = previousSlider;
            NativeSwitchGroup.CreateNativeControl = previousSwitch;
            window.Close();
        }
    }, CancellationToken.None);

    [Fact]
    public Task CoveredSliderKeepsItsNativeAppearance() => session.Session.Dispatch(async () =>
    {
        var previousFactory = NativeSliderGroup.CreateNativeControl;
        NativeSliderGroup.CreateNativeControl = (_, _, _) => new TestSlider();
        var source = new Slider();
        var group = new NativeSliderGroup { Content = source };
        var window = new Window { Width = 390, Height = 300, Content = group };
        try
        {
            window.Show();
            await WaitUntil(() => window.GetVisualDescendants().OfType<TestSlider>().Any());
            var native = window.GetVisualDescendants().OfType<TestSlider>().Single();
            group.SetSheetObscured(true, covered: true);
            group.SetSheetClipRect(new Rect(0, 0, group.Bounds.Width, 0));
            Assert.True(native.IsVisible);
            Assert.Equal(0, source.Opacity);
            Assert.Equal(0, native.SheetClip!.Value.Height);
            group.SetSheetClipRect(null);
            group.SetSheetObscured(false, covered: false);
            Assert.True(native.IsVisible);
            Assert.Null(native.SheetClip);
            Assert.Equal(0, source.Opacity);
        }
        finally
        {
            NativeSliderGroup.CreateNativeControl = previousFactory;
            window.Close();
        }
    }, CancellationToken.None);

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestSegment : Control, INativeSegmentInteraction, INativeScrollPosition
    {
        public int Updates { get; private set; }
        public void UpdateScrollPosition() => Updates++;
        public void SetSheetInteractionBlocked(bool blocked) { }
        public void SetSheetScrim(Color? color, Color backdrop) { }
    }

    private sealed class TestSlider : Control, INativeSliderInteraction, INativeScrollPosition
    {
        public int Updates { get; private set; }
        public bool SupportsSheetClipping => true;
        public Rect? SheetClip { get; private set; }
        public void UpdateScrollPosition() => Updates++;
        public void SetSheetInteractionBlocked(bool blocked) { }
        public void SetSheetScrim(Color? color, Color backdrop) { }
        public void SetSheetClipRect(Rect? visible) => SheetClip = visible;
    }

    private sealed class TestSwitch : Control, INativeSwitchInteraction, INativeScrollPosition
    {
        public int Updates { get; private set; }
        public void UpdateScrollPosition() => Updates++;
        public void SetInteractionBlocked(bool blocked) { }
        public void SetEnabled(bool enabled) { }
        public void SetSheetScrim(Color? color, Color backdrop) { }
    }
}
