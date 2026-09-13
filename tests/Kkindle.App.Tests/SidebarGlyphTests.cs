using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class SidebarGlyphTests(SettingsUiSession session)
{
    [Fact]
    public Task NavigationAndToolbarStrokesMatchAcrossDpiAndFractionalLayoutChanges() => session.Session.Dispatch(async () =>
    {
        // Both levels share an immutable resource. Shrinking the child should
        // preserve the pen weight and must not change the parent's artwork.
        var source = PathGeometry.Parse("M4,4 H20 V20 H4 Z");
        var parent = CreateGlyph(source, 1);
        var child = CreateGlyph(source, 0.9);
        var toolbar = new GlyphPath
        {
            Classes = { "libraryGlyph" }, Data = source, Stroke = Brushes.Black,
            Fill = Brushes.Transparent, UseLayoutRounding = false
        };
        var panel = new Canvas { Children = { parent, child, toolbar }, UseLayoutRounding = false };
        var window = new Window
        {
            Width = 170, Height = 70, Background = Brushes.White,
            WindowDecorations = WindowDecorations.None, UseLayoutRounding = false, Content = panel
        };
        window.Show();
        try
        {
            foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0, 1.25 })
            {
                window.SetRenderScaling(dpi);
                foreach (var offset in new[] { 0.2, 0.65 })
                {
                    Canvas.SetLeft(parent, 12 + offset);
                    Canvas.SetLeft(child, 60 + offset);
                    Canvas.SetLeft(toolbar, 108 + offset);
                    Canvas.SetTop(parent, 10 + offset);
                    Canvas.SetTop(child, 10 + offset);
                    Canvas.SetTop(toolbar, 10 + offset);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await Task.Delay(100);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                    using var frame = window.CaptureRenderedFrame();
                    using var png = new MemoryStream();
                    frame!.Save(png, PngBitmapEncoderOptions.Default);
                    png.Position = 0;
                    using var pixels = SKBitmap.Decode(png);
                    var strokes = new List<double[]>();
                    foreach (var glyph in new GlyphPath[] { parent, child, toolbar })
                    {
                        Assert.Equal(toolbar.StrokeThickness, glyph.StrokeThickness);
                        var bounds = glyph.RenderedGeometry!.Bounds;
                        var origin = glyph.TranslatePoint(default, window)!.Value;
                        // Include antialiased edge coverage. Rounding the pen to
                        // whole pixels or scaling it with the artwork changes
                        // the painted width even if the boxes stay aligned.
                        strokes.Add([
                            StrokeCoverage(pixels, (origin.X + bounds.Left) * dpi,
                                (origin.Y + bounds.Center.Y) * dpi, horizontal: true),
                            StrokeCoverage(pixels, (origin.X + bounds.Right) * dpi,
                                (origin.Y + bounds.Center.Y) * dpi, horizontal: true),
                            StrokeCoverage(pixels, (origin.X + bounds.Center.X) * dpi,
                                (origin.Y + bounds.Top) * dpi, horizontal: false),
                            StrokeCoverage(pixels, (origin.X + bounds.Center.X) * dpi,
                                (origin.Y + bounds.Bottom) * dpi, horizontal: false)
                        ]);
                    }
                    for (var icon = 0; icon < 2; icon++)
                        for (var edge = 0; edge < 4; edge++)
                            Assert.True(Math.Abs(strokes[icon][edge] - strokes[2][edge]) <= 0.06,
                                $"DPI {dpi}, icon {icon}, edge {edge}: {strokes[icon][edge]} vs toolbar {strokes[2][edge]}.");
                    Assert.True(child.RenderedGeometry!.Bounds.Width < parent.RenderedGeometry!.Bounds.Width);
                    Assert.Equal(new Rect(4, 4, 16, 16), source.Bounds);
                }
            }
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    private static SidebarGlyph CreateGlyph(Geometry source, double scale) => new()
    {
        Classes = { "libraryGlyph", "sidebarGlyph" },
        Data = source, IconScale = scale, Width = 24, Height = 24, Stretch = Stretch.None,
        Stroke = Brushes.Black, Fill = Brushes.Transparent, UseLayoutRounding = false
    };

    private static double StrokeCoverage(SKBitmap bitmap, double x, double y, bool horizontal)
    {
        double coverage = 0;
        for (var offset = -4; offset <= 4; offset++)
        {
            var pixelX = (int)Math.Floor(x) + (horizontal ? offset : 0);
            var pixelY = (int)Math.Floor(y) + (horizontal ? 0 : offset);
            var pixel = bitmap.GetPixel(pixelX, pixelY);
            coverage += (255 - pixel.Red) / 255d;
        }
        return coverage;
    }
}
