using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Kkindle;

/// <summary>Named overlay elements the fallback transition player drives.</summary>
/// <param name="SnapshotSource">The live reader surface to photograph.</param>
/// <param name="Snapshot">Image showing the outgoing frame.</param>
/// <param name="Ghost">Reserved for future cross-fade layering.</param>
/// <param name="Trail">Wave trailing band; doubles as fade backdrop veil.</param>
/// <param name="Front">Wave leading band.</param>
/// <param name="Edge">Slide edge shadow.</param>
internal sealed record ReaderLinuxFallbackTransitionSurface(
    Visual SnapshotSource,
    Image Snapshot,
    Image Ghost,
    Rectangle Trail,
    Rectangle Front,
    Rectangle Edge,
    IBrush? Backdrop);

/// <summary>
/// Native playback of the three reader page-turn animations on the Linux
/// text-fallback surface: a RenderTargetBitmap of the outgoing frame is
/// layered over the freshly rendered incoming frame and animated away with
/// timings lifted from the WebView implementations — fade 300ms out / 360ms
/// in, slide 430ms cubic-bezier(.38,0,.2,1), wave sweep
/// ReaderWaveScripts.TotalDurationMs plus a GhostTailMs residue.
///
/// Snapshot capture failure degrades silently to an instant switch so a
/// cosmetic hiccup can never block navigation.
/// </summary>
internal static class ReaderLinuxFallbackTransitionPlayer
{
    // Mirrors the private ReaderAnimation* constants in MainWindow.
    internal const int AnimationNone = 0;
    internal const int AnimationFade = 1;
    internal const int AnimationSlide = 2;
    internal const int AnimationWave = 3;

    private const int FrameIntervalMs = 15;
    private const double FadeOutMs = 300;
    private const double FadeInMs = 360;
    private const double SlideDurationMs = 430;
    private const double SlideShadowWidthRatio = 0.05;
    private const double WaveSweepMs = ReaderWaveScripts.TotalDurationMs;
    private const double WaveBandWidthRatio = 0.125;
    private const double WaveGhostOpacity = 0.022;
    private const double WaveGhostHoldMs = 60;
    private const double WaveGhostFadeMs = 260;

    // Canonical sibling Z order restored by Reset(): content < ghost <
    // snapshot < trail/front bands + slide shadow on top.
    private const int ZGhost = 10;
    private const int ZSnapshot = 20;
    private const int ZTrail = 30;
    private const int ZTop = 40;

    public static async Task<T> RunAsync<T>(
        ReaderLinuxFallbackTransitionSurface surface,
        int animation,
        int visualDirection,
        Func<Task<T>> changeContentAsync,
        CancellationToken cancellationToken)
    {
        if (animation == AnimationNone)
            return await changeContentAsync();

        // Clear stale overlays before photographing so the snapshot shows
        // only live reader content.
        Reset(surface);
        var snapshot = Capture(surface.SnapshotSource);
        if (snapshot is null || cancellationToken.IsCancellationRequested)
        {
            snapshot?.Dispose();
            Reset(surface);
            return await changeContentAsync();
        }

        try
        {
            Begin(surface, snapshot, animation);
            // The incoming content renders underneath while the outgoing
            // frame plays out on top of it.
            var pendingChange = changeContentAsync();
            await PlayFramesAsync(
                surface,
                animation,
                visualDirection,
                cancellationToken);
            return await pendingChange;
        }
        finally
        {
            Reset(surface);
            snapshot.Dispose();
        }
    }

    private static async Task PlayFramesAsync(
        ReaderLinuxFallbackTransitionSurface surface,
        int animation,
        int visualDirection,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(FrameIntervalMs)
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested
                    && DrawFrame(surface, animation, visualDirection, clock.Elapsed.TotalMilliseconds))
                {
                    return;
                }

                timer.Stop();
                if (cancellationToken.IsCancellationRequested)
                    completion.TrySetCanceled(cancellationToken);
                else
                    completion.TrySetResult();
            }
            catch (Exception ex)
            {
                timer.Stop();
                completion.TrySetException(ex);
            }
        };
        timer.Start();
        await completion.Task;
    }

    private static bool DrawFrame(
        ReaderLinuxFallbackTransitionSurface surface,
        int animation,
        int visualDirection,
        double elapsed)
    {
        return animation switch
        {
            AnimationFade => DrawFadeFrame(surface, elapsed),
            AnimationSlide => DrawSlideFrame(surface, visualDirection, elapsed),
            AnimationWave => DrawWaveFrame(surface, visualDirection, elapsed),
            _ => false
        };
    }

    /// <summary>Fade-through-background: old page dissolves over the veil,
    /// then the veil dissolves over the new page — same two beats as the
    /// WebView opacity transitions.</summary>
    private static bool DrawFadeFrame(ReaderLinuxFallbackTransitionSurface surface, double elapsed)
    {
        if (elapsed < FadeOutMs)
        {
            // Trail rectangle sits below the snapshot as an opaque veil in
            // the reader background colour (arranged in Begin).
            surface.Snapshot.Opacity = 1d - EaseCubicBezier(elapsed / FadeOutMs, 0.4, 0, 0.6, 1);
            return true;
        }

        surface.Snapshot.IsVisible = false;
        if (elapsed < FadeOutMs + FadeInMs)
        {
            surface.Trail.Opacity = 1d - EaseCubicBezier((elapsed - FadeOutMs) / FadeInMs, 0.4, 0, 0.2, 1);
            return true;
        }

        return false;
    }

    private static bool DrawSlideFrame(
        ReaderLinuxFallbackTransitionSurface surface,
        int visualDirection,
        double elapsed)
    {
        if (elapsed >= SlideDurationMs)
            return false;

        var forward = visualDirection >= 0;
        var p = EaseCubicBezier(elapsed / SlideDurationMs, 0.38, 0, 0.2, 1);
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var offsetX = (forward ? -width : width) * p;
        surface.Snapshot.RenderTransform = new TranslateTransform(offsetX, 0);

        // Shadow hugs the page's trailing edge and falls onto the new page.
        var shadowWidth = Math.Max(18, width * SlideShadowWidthRatio);
        var edge = surface.Edge;
        edge.Fill = CreateHorizontalShadowGradient(forward);
        edge.Width = shadowWidth;
        edge.Height = height;
        edge.RenderTransform = new TranslateTransform(
            forward ? offsetX + width : offsetX - shadowWidth,
            0);
        edge.IsVisible = true;
        return true;
    }

    private static bool DrawWaveFrame(
        ReaderLinuxFallbackTransitionSurface surface,
        int visualDirection,
        double elapsed)
    {
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var forward = visualDirection >= 0;

        if (elapsed < WaveSweepMs)
        {
            var p = EaseCubicBezier(elapsed / WaveSweepMs, 0.3, 0.08, 0.35, 0.96);
            var boundary = forward ? width * (1d - p) : width * p;
            surface.Snapshot.Clip = new RectangleGeometry(forward
                ? new Rect(0, 0, Math.Max(0, boundary), height)
                : new Rect(boundary, 0, Math.Max(0, width - boundary), height));

            var bandWidth = Math.Max(24, width * WaveBandWidthRatio);
            ConfigureWaveBand(surface.Front, boundary, bandWidth, 0.16, forward, lead: true);
            ConfigureWaveBand(surface.Trail, boundary, bandWidth, 0.08, forward, lead: false);
            return true;
        }

        // Sweep finished: bands drop, a faint ink residue lingers briefly
        // then lifts — mirrors the WebView ghost canvas tail.
        surface.Front.IsVisible = false;
        surface.Trail.IsVisible = false;
        surface.Snapshot.Clip = null;
        var tail = elapsed - WaveSweepMs;
        if (tail < WaveGhostHoldMs)
        {
            surface.Snapshot.Opacity = WaveGhostOpacity;
            return true;
        }
        if (tail < WaveGhostHoldMs + WaveGhostFadeMs)
        {
            surface.Snapshot.Opacity = WaveGhostOpacity
                * (1d - (tail - WaveGhostHoldMs) / WaveGhostFadeMs);
            return true;
        }
        return false;
    }

    private static void ConfigureWaveBand(
        Rectangle band,
        double boundary,
        double bandWidth,
        double alpha,
        bool forward,
        bool lead)
    {
        // Bands travel on the newly revealed side of the sweep boundary; the
        // trailing band lags behind the leading one.
        var x = forward == lead
            ? boundary + (lead ? 0 : bandWidth * 0.45)
            : boundary - bandWidth * (lead ? 1d : 1.45d);
        band.Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0));
        band.Width = bandWidth;
        band.RenderTransform = new TranslateTransform(x, 0);
        band.IsVisible = true;
    }

    private static LinearGradientBrush CreateHorizontalShadowGradient(bool opaqueAtStart)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)(opaqueAtStart ? 70 : 0), 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)(opaqueAtStart ? 0 : 70), 0, 0, 0), 1));
        return brush;
    }

    private static void Begin(
        ReaderLinuxFallbackTransitionSurface surface,
        RenderTargetBitmap snapshot,
        int animation)
    {
        Reset(surface);

        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;

        Pin(surface.Snapshot, ZSnapshot);
        surface.Snapshot.Source = snapshot;
        surface.Snapshot.Width = width;
        surface.Snapshot.Height = height;
        surface.Snapshot.Clip = null;
        surface.Snapshot.RenderTransform = null;
        surface.Snapshot.Opacity = 1;
        surface.Snapshot.IsVisible = true;

        if (animation == AnimationFade)
        {
            // Fade repurposes the trail rectangle as a background-coloured
            // veil between the incoming page and the outgoing snapshot.
            Pin(surface.Trail, ZSnapshot - 5);
            surface.Trail.Fill = surface.Backdrop ?? Brushes.White;
            surface.Trail.Width = width;
            surface.Trail.Height = height;
            surface.Trail.Opacity = 1;
            surface.Trail.IsVisible = true;
        }
    }

    private static void Pin(Control control, int zIndex)
    {
        control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        control.ZIndex = zIndex;
    }

    private static void Reset(ReaderLinuxFallbackTransitionSurface surface)
    {
        ResetImage(surface.Snapshot, ZSnapshot);
        ResetImage(surface.Ghost, ZGhost);
        ResetRectangle(surface.Trail, ZTrail);
        ResetRectangle(surface.Front, ZTop);
        ResetRectangle(surface.Edge, ZTop);
    }

    private static void ResetImage(Image image, int zIndex)
    {
        image.Source = null;
        image.IsVisible = false;
        image.Opacity = 1;
        image.Clip = null;
        image.RenderTransform = null;
        image.Width = double.NaN;
        image.Height = double.NaN;
        Pin(image, zIndex);
    }

    private static void ResetRectangle(Rectangle rectangle, int zIndex)
    {
        rectangle.Fill = null;
        rectangle.IsVisible = false;
        rectangle.Opacity = 1;
        rectangle.Clip = null;
        rectangle.RenderTransform = null;
        rectangle.Width = double.NaN;
        rectangle.Height = double.NaN;
        Pin(rectangle, zIndex);
    }

    private static RenderTargetBitmap? Capture(Visual source)
    {
        try
        {
            var bounds = source.Bounds;
            var scaling = TopLevel.GetTopLevel(source)?.RenderScaling ?? 1;
            var pixelWidth = (int)Math.Ceiling(bounds.Width * scaling);
            var pixelHeight = (int)Math.Ceiling(bounds.Height * scaling);
            if (pixelWidth < 16 || pixelHeight < 16)
                return null;

            var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(source);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CSS cubic-bezier(x1,y1,x2,y2) progress evaluator via binary
    /// search on the curve parameter — keeps native timing curves identical
    /// to the WebView transition styles.</summary>
    private static double EaseCubicBezier(double t, double x1, double y1, double x2, double y2)
    {
        t = Math.Clamp(t, 0, 1);
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        double Sample(double s) =>
            3 * s * (1 - s) * (1 - s) * y1
            + 3 * s * s * (1 - s) * y2
            + s * s * s;

        const double epsilon = 1e-5;
        var lo = 0d;
        var hi = 1d;
        var s = t;
        while (hi - lo > epsilon)
        {
            var x = 3 * s * (1 - s) * (1 - s) * x1
                + 3 * s * s * (1 - s) * x2
                + s * s * s;
            if (x < t) lo = s;
            else hi = s;
            s = (lo + hi) / 2;
        }

        return Sample(s);
    }
}
