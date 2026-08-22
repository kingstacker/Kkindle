using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderTransitionScriptTests
{
    [Theory]
    [InlineData(true, "-22px")]
    [InlineData(false, "22px")]
    public void SlideMovesTheCapturedOldPageOffTheReader(
        bool forward,
        string expectedShadow)
    {
        var script = ReaderWaveScripts.CreateSlideViewTransitionStartScript(
            forward,
            durationMs: 430);

        Assert.Contains("::view-transition-old(root)", script, StringComparison.Ordinal);
        Assert.Contains("animation: none", script, StringComparison.Ordinal);
        Assert.Contains("::view-transition-new(root)", script, StringComparison.Ordinal);
        Assert.Contains("@keyframes kk-slide-old", script, StringComparison.Ordinal);
        Assert.Contains(expectedShadow, script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.style.transform", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EinkRefreshUsesAStraightLowContrastSweep(bool forward)
    {
        var script = ReaderWaveScripts.CreateWaveViewTransitionStartScript(
            forward,
            durationMs: 760);

        Assert.Contains("kk-kindle-refresh-old", script, StringComparison.Ordinal);
        Assert.Contains("clip-path: inset(", script, StringComparison.Ordinal);
        Assert.Contains("grayscale(.2)", script, StringComparison.Ordinal);
        Assert.Contains("contrast(.975)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("polygon(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.style.transform", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "translate3d(-100%,0,0)")]
    [InlineData(false, "translate3d(100%,0,0)")]
    public void ChapterSlideFallbackMovesTheOldSnapshotInTheTurnDirection(
        bool forward,
        string expectedTarget)
    {
        var script = ReaderWaveScripts.CreateSlideOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward,
            durationMs: 430,
            startPaused: true);

        Assert.Contains(expectedTarget, script, StringComparison.Ordinal);
        Assert.Contains("#kk-slide-edge", script, StringComparison.Ordinal);
        Assert.Contains("document.createElement('canvas')", script, StringComparison.Ordinal);
        Assert.Contains("createImageBitmap(new Blob", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.createElement('img')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kk-slide-away", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturedOverlaysExposeAReadyGateBeforeTheRefreshStarts()
    {
        Assert.Contains("kk-wave-image", ReaderWaveScripts.WaveOverlayReadyScript, StringComparison.Ordinal);
        Assert.Contains("kk-slide-image", ReaderWaveScripts.SlideOverlayReadyScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ChapterWaveFallbackDecodesTheSnapshotWithoutCspBlockedImageSources()
    {
        var script = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward: true,
            startPaused: true);

        Assert.Contains("document.createElement('canvas')", script, StringComparison.Ordinal);
        Assert.Contains("createImageBitmap(new Blob", script, StringComparison.Ordinal);
        Assert.Contains("__kkindleStartWaveOverlay", script, StringComparison.Ordinal);
        Assert.Contains("#kk-wave-front", script, StringComparison.Ordinal);
        Assert.Contains("kk-kindle-refresh-front", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kk-wave-ghost", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kk-refresh-band", script, StringComparison.Ordinal);
        Assert.DoesNotContain("polygon(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.createElement('img')", script, StringComparison.Ordinal);
    }
}
