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
    [InlineData(true, "inset(0 100% 0 0)")]
    [InlineData(false, "inset(0 0 0 100%)")]
    public void EinkViewTransitionSweepsTheOldPageOutInTheTurnDirection(
        bool forward,
        string expectedFinalClip)
    {
        var script = ReaderWaveScripts.CreateWaveViewTransitionStartScript(
            forward,
            durationMs: 760);

        Assert.Contains("::view-transition-old(root)", script, StringComparison.Ordinal);
        Assert.Contains("animation: none", script, StringComparison.Ordinal);
        Assert.Contains("::view-transition-new(root)", script, StringComparison.Ordinal);
        Assert.Contains("@keyframes kk-eink-vt-old", script, StringComparison.Ordinal);
        Assert.Contains(expectedFinalClip, script, StringComparison.Ordinal);
        Assert.DoesNotContain("polygon(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.style.transform", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EinkRefreshPropagatesWithinTheModernKindleTimingWindow()
    {
        var script = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward: true);

        // 波前传播时长落在普通文本翻页的 200~250ms 窗口内。
        Assert.Contains("230ms", script, StringComparison.Ordinal);

        var clamped = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward: true,
            totalDurationMs: 5000);
        Assert.Contains("2000ms", clamped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "linear-gradient(270deg")]
    [InlineData(false, "linear-gradient(90deg")]
    public void EinkRefreshWavePropagatesAlongTheTurnDirection(
        bool forward,
        string expectedMaskAngle)
    {
        var script = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward);

        // 软边缘蒙版沿刷新方向传播：前进翻页新页从右缘向左缘显现；旧页与新页几何位置保持不动。
        Assert.Contains("-webkit-mask-image: " + expectedMaskAngle, script, StringComparison.Ordinal);
        Assert.Contains("mask-image: " + expectedMaskAngle, script, StringComparison.Ordinal);
        Assert.Contains("mask-position", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.style.transform", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.body.style", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EinkRefreshCarriesABandTrailJitterAndResidualGhost(bool forward)
    {
        var script = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward);

        // 柔和灰暗前沿、墨水迁移拖尾、灰阶抖动与短暂残影。
        Assert.Contains("#kk-wave-front", script, StringComparison.Ordinal);
        Assert.Contains("kk-eink-band-move", script, StringComparison.Ordinal);
        Assert.Contains("#kk-wave-trail", script, StringComparison.Ordinal);
        Assert.Contains("kk-eink-trail-move", script, StringComparison.Ordinal);
        Assert.Contains("kk-eink-jitter", script, StringComparison.Ordinal);
        Assert.Contains("steps(2, jump-none)", script, StringComparison.Ordinal);
        Assert.Contains("#kk-wave-ghost", script, StringComparison.Ordinal);
        Assert.Contains("kk-eink-ghost-hold", script, StringComparison.Ordinal);
        Assert.Contains("opacity: 0.022", script, StringComparison.Ordinal);
        Assert.DoesNotContain("grayscale(.2)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("polygon(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.createElement('img')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EinkRefreshStabilizesColorfulContentWithASoftColorSettle()
    {
        var script = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward: true);

        Assert.Contains("#kk-wave-color", script, StringComparison.Ordinal);
        Assert.Contains("kk-eink-color-settle", script, StringComparison.Ordinal);
        Assert.Contains("backdrop-filter: saturate(.62)", script, StringComparison.Ordinal);
        Assert.Contains("360ms", script, StringComparison.Ordinal);
        // 彩色检测在快照降采样上进行，纯文本页面不会触发色彩稳定。
        Assert.Contains("getImageData", script, StringComparison.Ordinal);
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
        Assert.Contains("animation-play-state: paused", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kk-refresh-band", script, StringComparison.Ordinal);
        Assert.DoesNotContain("polygon(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.createElement('img')", script, StringComparison.Ordinal);
    }
}
