using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderPaginationScriptTests
{
    [Theory]
    [InlineData(false, "column-count: 1 !important")]
    [InlineData(true, "column-count: 2 !important")]
    public void FlowCssPinsTheVisibleColumnCount(bool twoPage, string expected)
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: false,
            twoPage: twoPage);

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("column-count: auto !important", css, StringComparison.Ordinal);
            Assert.Contains("-webkit-column-width:", css, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expected, css, StringComparison.Ordinal);
            Assert.Contains("column-width: auto !important", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FlowCssReducesConfiguredMarginsForNarrowReaderViewports()
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: false,
            horizontalPadding: 68);

        Assert.Contains("min(68px, max(24px, 5vw))", css, StringComparison.Ordinal);
        Assert.Contains(
            "calc(min(68px, max(24px, 5vw)) + min(68px, max(24px, 5vw)))",
            css,
            StringComparison.Ordinal);
        Assert.DoesNotContain(")px", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlowCssUsesTheLiveCssViewportForColumnGeometry(bool twoPage)
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: false,
            twoPage: twoPage);

        Assert.Contains("100vw", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--kkindle-reader-page-viewport-width", css, StringComparison.Ordinal);
    }

    [Fact]
    public void PageStepPrioritizesTheLiveScrollingViewport()
    {
        Assert.StartsWith(
            "document.scrollingElement?.clientWidth",
            ReaderPaginationScripts.PageStepExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--kkindle-reader-page-viewport-width",
            ReaderPaginationScripts.PageStepExpression,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TurnScriptDefaultsToInstantScrolling()
    {
        var script = ReaderPaginationScripts.CreateTurnScript(direction: 1);

        Assert.Contains("behavior: 'instant'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("behavior: 'smooth'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationScriptsKeepTheSnappedViewportBoundary()
    {
        Assert.DoesNotContain("AlignPaginatedPage", ReaderPaginationScripts.Snap(vertical: false), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AlignPaginatedPage",
            ReaderPaginationScripts.CreateTurnScript(direction: 1),
            StringComparison.Ordinal);

        var fragmentScript = ReaderNavigationScripts.CreateFragmentScroll(
            needle: "section",
            flowMode: 1,
            vertical: false);
        Assert.Contains("const pageLeft = pageIndex * step", fragmentScript, StringComparison.Ordinal);
        Assert.DoesNotContain("horizontalError", fragmentScript, StringComparison.Ordinal);
        Assert.DoesNotContain("scroller.scrollLeft +", fragmentScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationScriptsDoNotClampLogicalBoundaryToIntegerRawMaximum()
    {
        var scripts = new[]
        {
            ReaderPaginationScripts.Snap(vertical: false),
            ReaderPaginationScripts.Snap(vertical: true),
            ReaderPaginationScripts.CreateTurnScript(direction: 1),
            ReaderPaginationScripts.CreateTurnScript(direction: 1, vertical: true),
            ReaderPaginationScripts.CreateCanTurnScript(direction: 1),
            ReaderPaginationScripts.CreateCanTurnScript(direction: 1, vertical: true),
            ReaderPaginationScripts.CreateRestorePositionScript(982, 0, pagination: true),
            ReaderPaginationScripts.CreateRestorePositionScript(-982, 0, pagination: true, vertical: true),
            ReaderNavigationScripts.CreateFragmentScroll("section", flowMode: 1, vertical: false)
        };

        foreach (var script in scripts)
        {
            Assert.Contains("Math.round(Math.max(0, rawMax -", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Math.min(rawMax", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestorePositionScriptSnapsOnlyPaginatedLayouts(bool pagination)
    {
        var script = ReaderPaginationScripts.CreateRestorePositionScript(
            left: 1234,
            top: 56,
            pagination);

        if (pagination)
        {
            Assert.Contains("const requestedRaw = 1234", script, StringComparison.Ordinal);
            Assert.Contains("const pageIndex = Math.round(requested / step)", script, StringComparison.Ordinal);
            Assert.Contains("left: vertical ? -target : target, top: 0", script, StringComparison.Ordinal);
            Assert.DoesNotContain("left: 1234", script, StringComparison.Ordinal);
        }
        else
        {
            // Continuous vertical restores negate the saved distance so the
            // negative-range scroller lands on the same offset.
            Assert.Contains("const vertical = false", script, StringComparison.Ordinal);
            Assert.Contains(": 1234, top: 56", script, StringComparison.Ordinal);
            Assert.DoesNotContain("pageIndex", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PaginationRestoreDoesNotInstallReactiveScrollGuards()
    {
        var script = ReaderPaginationScripts.CreateRestorePositionScript(
            left: 957,
            top: 0,
            pagination: true);

        Assert.DoesNotContain("addEventListener", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", script, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollend", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__kkindlePaginationDiagnostics", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationScriptsKeepTheSnappedViewportBoundary_Vertical()
    {
        Assert.DoesNotContain(
            "AlignPaginatedPage",
            ReaderPaginationScripts.Snap(vertical: true),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AlignPaginatedPage",
            ReaderPaginationScripts.CreateTurnScript(direction: 1, vertical: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalScriptsMeasureDistanceFromTheRightOrigin()
    {
        var turn = ReaderPaginationScripts.CreateTurnScript(direction: 1, vertical: true);
        var canTurn = ReaderPaginationScripts.CreateCanTurnScript(direction: -1, vertical: true);
        var snap = ReaderPaginationScripts.Snap(vertical: true);
        var restore = ReaderPaginationScripts.CreateRestorePositionScript(
            left: -2468,
            top: 0,
            pagination: true,
            vertical: true);

        foreach (var script in new[] { turn, canTurn, snap })
        {
            // Chromium anchors vertical-rl overflow at the right edge and
            // reports scrollLeft in a negative range.
            Assert.Contains(
                "vertical ? Math.abs(el.scrollLeft || 0)",
                script,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "left: vertical ? -target : target",
            turn,
            StringComparison.Ordinal);
        Assert.Contains(
            "left: vertical ? -target : target",
            snap,
            StringComparison.Ordinal);
        Assert.Contains("Math.abs(requestedRaw)", restore, StringComparison.Ordinal);

        // Natural vertical flow can finish on a partial page, so its real
        // horizontal overflow is authoritative rather than a rounded column.
        Assert.Contains("const max = vertical", turn, StringComparison.Ordinal);
        Assert.Contains("? rawMax", turn, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, false, "left: moveToEnd ? el.scrollWidth || 0 : 0")]
    [InlineData(false, true, false, "left: moveToEnd ? el.scrollWidth || 0 : 0")]
    [InlineData(true, false, false, "top: moveToEnd ? el.scrollHeight || 0 : 0")]
    [InlineData(false, false, false, "left: 0, top: moveToEnd ? el.scrollHeight || 0 : 0")]
    [InlineData(true, false, true, "left: moveToEnd ? -(el.scrollWidth || 0) : 0")]
    [InlineData(false, false, true, "left: moveToEnd ? -(el.scrollWidth || 0) : 0")]
    public void ChapterBoundaryScriptTargetsTheRequestedEdge(
        bool moveToEnd,
        bool horizontal,
        bool vertical,
        string expected)
    {
        var script = ReaderPaginationScripts.CreateChapterBoundaryScript(moveToEnd, horizontal, vertical);

        Assert.Contains($"const moveToEnd = {moveToEnd.ToString().ToLowerInvariant()}", script, StringComparison.Ordinal);
        Assert.Contains($"const horizontal = {horizontal.ToString().ToLowerInvariant()}", script, StringComparison.Ordinal);
        Assert.Contains($"const vertical = {vertical.ToString().ToLowerInvariant()}", script, StringComparison.Ordinal);
        Assert.Contains(expected, script, StringComparison.Ordinal);
        Assert.Contains("behavior: 'instant'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalFlowCssPinsTheRootWritingMode()
    {
        var paginated = ReaderPaginationScripts.CreateFlowCss(pagination: true, vertical: true);

        Assert.Contains("width: 100%; height: 100%; overflow: hidden !important", paginated, StringComparison.Ordinal);
        Assert.Contains("writing-mode: vertical-rl !important", paginated, StringComparison.Ordinal);
        Assert.Contains("width: max-content !important", paginated, StringComparison.Ordinal);
        Assert.Contains("min-width: 100% !important", paginated, StringComparison.Ordinal);
        Assert.Contains("column-width: auto !important", paginated, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-page-side", paginated, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-page-step", paginated, StringComparison.Ordinal);
        Assert.Contains("html::before", paginated, StringComparison.Ordinal);
        Assert.Contains("html::after", paginated, StringComparison.Ordinal);
        Assert.Contains("body::before", paginated, StringComparison.Ordinal);
        Assert.Contains("body::after", paginated, StringComparison.Ordinal);
        Assert.Contains("background: #FFFFFF !important", paginated, StringComparison.Ordinal);

        var staleScrollRequest = ReaderPaginationScripts.CreateFlowCss(
            pagination: false,
            vertical: true,
            twoPage: true);

        Assert.Contains("writing-mode: vertical-rl !important", staleScrollRequest, StringComparison.Ordinal);
        Assert.Contains("column-width", staleScrollRequest, StringComparison.Ordinal);
        Assert.Contains("column-count: auto !important", staleScrollRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("column-count: 2 !important", staleScrollRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalScriptsUseTheLiveViewportMinusPageMargins()
    {
        var scripts = new[]
        {
            ReaderPaginationScripts.Snap(vertical: true),
            ReaderPaginationScripts.CreateTurnScript(direction: 1, vertical: true),
            ReaderPaginationScripts.CreateCanTurnScript(direction: -1, vertical: true),
            ReaderPaginationScripts.CreateRestorePositionScript(-982, 0, pagination: true, vertical: true)
        };

        foreach (var script in scripts)
        {
            Assert.Contains(
                "viewport - sides",
                script,
                StringComparison.Ordinal);
            Assert.Contains("Math.floor(available / line) * line", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-page-step", script, StringComparison.Ordinal);
            Assert.DoesNotContain(".columnWidth", script, StringComparison.Ordinal);
            Assert.DoesNotContain(".columnGap", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VerticalFragmentScrollPositionsByLiveViewportDelta()
    {
        var fragmentScript = ReaderNavigationScripts.CreateFragmentScroll(
            needle: "section",
            flowMode: 1,
            vertical: true);

        Assert.Contains("Math.abs(scroller.scrollLeft || 0)", fragmentScript, StringComparison.Ordinal);
        Assert.Contains("contentRight - rect.right", fragmentScript, StringComparison.Ordinal);
        Assert.Contains("viewport - sides", fragmentScript, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalTypographyKeepsPublisherBlocksOnTheGlyphGrid()
    {
        var css = ReaderPaginationScripts.VerticalTypographyGridCss;

        Assert.Contains("margin-block: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains("padding-block: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains("block-size: auto !important", css, StringComparison.Ordinal);
        Assert.Contains("margin-block: 1lh !important", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 1rem !important", css, StringComparison.Ordinal);
    }
}
