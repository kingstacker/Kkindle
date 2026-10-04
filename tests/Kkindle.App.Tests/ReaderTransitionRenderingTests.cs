using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderTransitionRenderingTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, 1, ReaderTheme.Classic)]
    [InlineData(1, -1, ReaderTheme.Night)]
    [InlineData(2, 1, ReaderTheme.Ivory)]
    [InlineData(2, -1, ReaderTheme.Night)]
    [InlineData(3, 1, ReaderTheme.Ivory)]
    [InlineData(3, -1, ReaderTheme.Night)]
    public Task PageFramesKeepOutgoingAndIncomingInkSeparate(
        int animation, int direction, ReaderTheme theme) => session.Session.Dispatch(async () =>
    {
        var paper = ReaderPaperTexture.CreateBrush(ReaderPalette.For(theme).Page,
            new ReaderAppearanceSettings
            {
                Theme = theme, PaperEnabled = true, FibersEnabled = true, PaperStrength = 0.8
            });
        var root = new Grid { Background = paper };
        var content = new InkPage();
        var snapshot = new Image();
        var trail = new Rectangle();
        var front = new Rectangle();
        var edge = new Rectangle();
        var overlays = new Panel { ClipToBounds = true, Children = { snapshot, trail, front, edge } };
        var viewport = new Grid
        {
            Margin = new Thickness(17, 13, 23, 47),
            Children = { content, overlays }
        };
        root.Children.Add(viewport);
        var window = new Window { Width = 360, Height = 220, Content = root };
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource();
        Task<bool>? turn = null;
        try
        {
            window.Show();
            await ReaderTests.Render();
            using var outgoing = Capture(root);
            var surface = new ReaderTransitionSurface(content, snapshot, trail, front, edge, root);
            turn = ReaderTransitionPlayer.RunAsync(surface, animation, direction, async () =>
            {
                content.Incoming = true;
                content.InvalidateVisual();
                await release.Task;
                return true;
            }, cancellation.Token);
            await ReaderTests.Render();

            // Holding a transparent page must reproduce the original frame,
            // including the texture phase at this non-zero viewport offset.
            using (var held = Capture(root))
                Assert.Equal(outgoing.Bytes, held.Bytes);
            using (var captured = Decode((RenderTargetBitmap)snapshot.Source!))
                Assert.All(captured.Pixels, pixel => Assert.Equal(255, pixel.Alpha));

            var sawOld = false;
            var sawNew = false;
            foreach (var elapsed in new[] { 40d, 100d, 170d, 232.5, 290d, 400d })
            {
                ReaderTransitionPlayer.DrawFrame(surface, animation, direction, elapsed);
                await ReaderTests.Render();
                using var frame = Capture(root);
                var overlappingColumns = 0;
                for (var x = 20; x < 330; x++)
                {
                    // Red and blue rows represent different baselines on two
                    // pages. Both visible at one x is the doubled-text bug.
                    var oldInk = frame.GetPixel(x, 48);
                    var newInk = frame.GetPixel(x, 88);
                    var hasOld = oldInk.Red > oldInk.Green + 30 && oldInk.Red > oldInk.Blue + 30;
                    var hasNew = newInk.Blue > newInk.Green + 30 && newInk.Blue > newInk.Red + 30;
                    if (hasOld && hasNew) overlappingColumns++;
                    sawOld |= hasOld;
                    sawNew |= hasNew;
                    if (animation != ReaderTransitionPlayer.AnimationSlide)
                    {
                        var expected = outgoing.GetPixel(x, 133);
                        var actual = frame.GetPixel(x, 133);
                        // The subtle refresh band may shade the paper, but
                        // fade/wave must not replace its theme or shift it.
                        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 15);
                        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 15);
                        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 15);
                    }
                }
                // A fractional moving edge can antialias into one physical
                // pixel. It must never blend a band or a whole page of text.
                Assert.InRange(overlappingColumns, 0, animation == ReaderTransitionPlayer.AnimationFade ? 0 : 1);
            }
            Assert.True(sawOld);
            Assert.True(sawNew);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            if (turn is not null)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
            Assert.Null(snapshot.Source);
            Assert.All(new Control[] { snapshot, trail, front, edge }, control => Assert.False(control.IsVisible));
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    private static SKBitmap Capture(Control control)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(
            (int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)));
        bitmap.Render(control);
        return Decode(bitmap);
    }

    private static SKBitmap Decode(RenderTargetBitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return SKBitmap.Decode(stream.ToArray());
    }

    private sealed class InkPage : Control
    {
        public bool Incoming { get; set; }
        public override void Render(DrawingContext context) => context.FillRectangle(
            Incoming ? Brushes.Blue : Brushes.Red,
            new Rect(0, Incoming ? 70 : 30, Bounds.Width, 12));
    }
}
