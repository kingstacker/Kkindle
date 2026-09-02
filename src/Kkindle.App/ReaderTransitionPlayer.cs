using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Kkindle;

/// <summary>Named overlay elements the reader transition player drives.</summary>
/// <param name="SnapshotSource">The live reader surface to photograph.</param>
/// <param name="Snapshot">Image showing the outgoing frame.</param>
/// <param name="Trail">Wave trail or fade-through backdrop veil.</param>
/// <param name="Front">Wave leading band.</param>
/// <param name="Edge">Slide edge shadow.</param>
internal sealed record ReaderTransitionSurface(
    Visual SnapshotSource,
    Image Snapshot,
    Rectangle Trail,
    Rectangle Front,
    Rectangle Edge,
    IBrush? Backdrop);

/// <summary>
/// Native playback of the three reader page-turn animations: a
/// RenderTargetBitmap of the outgoing frame is layered over the freshly
/// rendered incoming frame and animated away — a quiet 495ms fade-through, a
/// 495ms soft-edged slide, or a 368ms e-ink wave. All three are eased on one
/// shared gentle curve and used by both the self-drawn reader surface and the
/// Linux text-fallback surface.
///
/// Snapshot capture failure degrades silently to an instant switch so a
/// cosmetic hiccup can never block navigation.
/// </summary>
internal static class ReaderTransitionPlayer
{
    // Mirrors the private ReaderAnimation* constants in MainWindow.
    internal const int AnimationNone = 0;
    internal const int AnimationFade = 1;
    internal const int AnimationSlide = 2;
    internal const int AnimationWave = 3;

    private const int FrameIntervalMs = 15;

    // Softening follows three rules across all three animations: ease with a
    // curve that never sits still at the start, keep overlays wide but faint
    // (a broad pale shadow reads gentler than a narrow dark one), and give the
    // motion enough time that it never snaps. cubic-bezier(.25,.1,.25,1) is the
    // shared gentle curve — it leaves immediately and lands on a long tail.
    private const double SoftX1 = 0.25;
    private const double SoftY1 = 0.1;
    private const double SoftX2 = 0.25;
    private const double SoftY2 = 1d;

    private const double FadeOutMs = 232.5;
    private const double FadeInMs = 262.5;
    // A restrained paper veil hides the doubled glyphs at the hand-off without
    // turning the middle of the transition into a white flash.
    private const double FadeVeilMaxOpacity = 0.48;
    private const double SlideDurationMs = 495;
    private const double SlideShadowWidthRatio = 0.02;
    private const double SlideShadowMinWidth = 4;
    private const double SlideShadowMaxWidth = 14;
    private const byte SlideShadowAlpha = 24;
    private const double WaveSweepMs = 367.5;
    private const double WaveBandWidthRatio = 0.11;
    private const double WaveSoftEdgeWidthRatio = 0.035;
    private const double WaveLeadBandAlpha = 0.055;

    // Canonical sibling Z order restored by Reset(): content < snapshot <
    // trail/front bands + slide shadow on top.
    private const int ZSnapshot = 20;
    private const int ZTrail = 30;
    private const int ZTop = 40;

    public static async Task<T> RunAsync<T>(
        ReaderTransitionSurface surface,
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
            // Keep the outgoing snapshot visible while the new content is
            // being composed/configured. Starting the clock before this task
            // completes could expose a blank or partially laid-out page on a
            // slow chapter switch.
            var changedContent = await changeContentAsync();
            await PlayFramesAsync(
                surface,
                animation,
                visualDirection,
                cancellationToken);
            return changedContent;
        }
        finally
        {
            Reset(surface);
            snapshot.Dispose();
        }
    }

    private static async Task PlayFramesAsync(
        ReaderTransitionSurface surface,
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
        ReaderTransitionSurface surface,
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

    /// <summary>Fade-through-background: the outgoing page dissolves into a
    /// restrained paper veil, then the new page is revealed underneath. This
    /// avoids stacking two pages of glyphs through the whole transition.</summary>
    private static bool DrawFadeFrame(ReaderTransitionSurface surface, double elapsed)
    {
        if (elapsed < FadeOutMs)
        {
            var progress = EaseSoft(elapsed / FadeOutMs);
            surface.Snapshot.Opacity = 1d - progress;
            surface.Trail.Opacity = FadeVeilMaxOpacity * progress;
            return true;
        }

        surface.Snapshot.IsVisible = false;
        var revealElapsed = elapsed - FadeOutMs;
        if (revealElapsed < FadeInMs)
        {
            surface.Trail.Opacity = FadeVeilMaxOpacity
                * (1d - EaseSoft(revealElapsed / FadeInMs));
            return true;
        }

        surface.Trail.Opacity = 0;
        return false;
    }

    private static bool DrawSlideFrame(
        ReaderTransitionSurface surface,
        int visualDirection,
        double elapsed)
    {
        if (elapsed >= SlideDurationMs)
            return false;

        var forward = visualDirection >= 0;
        var p = EaseSoft(elapsed / SlideDurationMs);
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var offsetX = (forward ? -width : width) * p;
        surface.Snapshot.RenderTransform = new TranslateTransform(offsetX, 0);

        // Shadow hugs the page's trailing edge and falls onto the new page.
        var shadowWidth = Math.Clamp(
            width * SlideShadowWidthRatio,
            SlideShadowMinWidth,
            SlideShadowMaxWidth);
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
        ReaderTransitionSurface surface,
        int visualDirection,
        double elapsed)
    {
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var forward = visualDirection >= 0;

        if (elapsed < WaveSweepMs)
        {
            var p = EaseSoft(elapsed / WaveSweepMs);
            var boundary = forward ? width * (1d - p) : width * p;
            // A hard RectangleGeometry edge reads like a wipe. An opacity
            // mask gives the refresh front a small antialiased ramp while
            // leaving the page geometry fixed underneath.
            var bandWidth = Math.Max(24, width * WaveBandWidthRatio);
            var softEdgeWidth = Math.Max(16, width * WaveSoftEdgeWidthRatio);
            surface.Snapshot.Clip = null;
            surface.Snapshot.OpacityMask = CreateWaveOpacityMask(
                boundary,
                width,
                softEdgeWidth,
                forward);

            ConfigureWaveBand(surface.Front, boundary, bandWidth, height, WaveLeadBandAlpha, forward);
            surface.Trail.IsVisible = false;
            return true;
        }

        // Sweep finished: remove every effect in the same frame so the new page
        // lands cleanly without an afterimage.
        surface.Front.IsVisible = false;
        surface.Trail.IsVisible = false;
        surface.Snapshot.Clip = null;
        surface.Snapshot.OpacityMask = null;
        surface.Snapshot.IsVisible = false;
        return false;
    }

    private static void ConfigureWaveBand(
        Rectangle band,
        double boundary,
        double bandWidth,
        double height,
        double alpha,
        bool forward)
    {
        // The single band travels on the newly revealed side of the sweep
        // boundary and fades at both edges.
        var x = forward ? boundary : boundary - bandWidth;
        band.Fill = CreateWaveBandBrush(alpha);
        band.Width = bandWidth;
        // Pin() aligns the band to the top, so its height must be explicit.
        band.Height = height;
        band.RenderTransform = new TranslateTransform(x, 0);
        band.IsVisible = true;
    }

    private static LinearGradientBrush CreateWaveBandBrush(double peakAlpha)
    {
        var peak = (byte)Math.Round(Math.Clamp(peakAlpha, 0, 1) * 255);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        // Transparent edges keep the two overlapping bands from becoming a
        // pair of hard grey bars. The small shoulder gives the wave a visible
        // centre without making it heavier than the page shadow.
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(peak * 0.35), 0, 0, 0), 0.22));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, 0, 0, 0), 0.5));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(peak * 0.35), 0, 0, 0), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        return brush;
    }

    private static LinearGradientBrush CreateWaveOpacityMask(
        double boundary,
        double width,
        double softEdgeWidth,
        bool forward)
    {
        var safeWidth = Math.Max(1, width);
        var halfEdge = Math.Clamp(softEdgeWidth / 2, 1, safeWidth / 2);
        var left = Math.Clamp((boundary - halfEdge) / safeWidth, 0, 1);
        var right = Math.Clamp((boundary + halfEdge) / safeWidth, 0, 1);
        var stops = new List<(double Offset, double Opacity)>();

        void AddStop(double offset, double opacity)
        {
            var safeOffset = Math.Clamp(offset, 0, 1);
            var safeOpacity = Math.Clamp(opacity, 0, 1);
            if (stops.Count > 0 && Math.Abs(stops[^1].Offset - safeOffset) < 0.0001)
            {
                stops[^1] = (safeOffset, safeOpacity);
                return;
            }

            stops.Add((safeOffset, safeOpacity));
        }

        if (forward)
        {
            // Next page reveals from right to left; the outgoing page remains
            // opaque on the left and fades out across the moving front.
            AddStop(0, 1);
            AddStop(left, 1);
            AddStop(right, 0);
            AddStop(1, 0);
        }
        else
        {
            // Previous page is the mirror image: the old page remains on the
            // right while the new page arrives from the left.
            AddStop(0, 0);
            AddStop(left, 0);
            AddStop(right, 1);
            AddStop(1, 1);
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        foreach (var (offset, opacity) in stops)
        {
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255),
                offset));
        }

        return brush;
    }

    private static LinearGradientBrush CreateHorizontalShadowGradient(bool opaqueAtStart)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        var transparent = Color.FromArgb(0, 0, 0, 0);
        var shadow = Color.FromArgb(SlideShadowAlpha, 0, 0, 0);
        brush.GradientStops.Add(new GradientStop(opaqueAtStart ? shadow : transparent, 0));
        brush.GradientStops.Add(new GradientStop(
            opaqueAtStart
                ? Color.FromArgb((byte)Math.Round(SlideShadowAlpha * 0.55), 0, 0, 0)
                : Color.FromArgb((byte)Math.Round(SlideShadowAlpha * 0.12), 0, 0, 0),
            0.38));
        brush.GradientStops.Add(new GradientStop(
            opaqueAtStart
                ? Color.FromArgb((byte)Math.Round(SlideShadowAlpha * 0.12), 0, 0, 0)
                : Color.FromArgb((byte)Math.Round(SlideShadowAlpha * 0.55), 0, 0, 0),
            0.72));
        brush.GradientStops.Add(new GradientStop(opaqueAtStart ? transparent : shadow, 1));
        return brush;
    }

    private static void Begin(
        ReaderTransitionSurface surface,
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
        // Capture() already uses the reader's logical-DIP size, so the
        // snapshot is a 1:1 page image. Keeping Stretch.None here prevents
        // Avalonia from moving the page's text toward the overlay's top-left
        // corner while it calculates a second fit.
        surface.Snapshot.Stretch = Stretch.None;
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
            surface.Trail.Opacity = 0;
            surface.Trail.IsVisible = true;
        }
    }

    private static void Pin(Control control, int zIndex)
    {
        control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        control.ZIndex = zIndex;
    }

    private static void Reset(ReaderTransitionSurface surface)
    {
        ResetImage(surface.Snapshot, ZSnapshot);
        ResetRectangle(surface.Trail, ZTrail);
        ResetRectangle(surface.Front, ZTop);
        ResetRectangle(surface.Edge, ZTop);
    }

    private static void ResetImage(Image image, int zIndex)
    {
        image.Source = null;
        image.IsVisible = false;
        image.Opacity = 1;
        image.OpacityMask = null;
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
            // The reader host has its own high-DPI backing bitmap. Capturing
            // at the host's logical size lets its normal Render() path do the
            // one required downsample, keeping the transition image in the
            // same coordinate system as the live page.
            var pixelWidth = (int)Math.Ceiling(bounds.Width);
            var pixelHeight = (int)Math.Ceiling(bounds.Height);
            if (pixelWidth < 16 || pixelHeight < 16)
                return null;

            // RenderTargetBitmap's pixel size is intentionally kept at the
            // monitor resolution, but its logical coordinate system must stay
            // at 96 DPI. NativeReaderHost already accounts for RenderScaling
            // when it paints its backing bitmap; tagging this target as
            // high-DPI makes that physical bitmap look 1.25x/1.5x when it is
            // later shown in the transition Image.
            var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96, 96));
            bitmap.Render(source);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The one gentle curve all three animations ease on.</summary>
    private static double EaseSoft(double t) =>
        EaseCubicBezier(t, SoftX1, SoftY1, SoftX2, SoftY2);

    /// <summary>CSS cubic-bezier(x1,y1,x2,y2) progress evaluator via binary
    /// search on the curve parameter.</summary>
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
