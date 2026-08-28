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
            Assert.Contains("const rawPageIndex = Math.round(requested / step)", script, StringComparison.Ordinal);
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
        Assert.Contains("if (vertical) requestAnimationFrame", turn, StringComparison.Ordinal);
        Assert.Contains(
            "left: vertical ? -target : target",
            snap,
            StringComparison.Ordinal);
        Assert.Contains("if (vertical) requestAnimationFrame", snap, StringComparison.Ordinal);
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
        Assert.Contains("text-orientation: mixed !important", paginated, StringComparison.Ordinal);
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
        Assert.Contains("overflow-x: auto !important", staleScrollRequest, StringComparison.Ordinal);
        Assert.Contains("column-count: auto !important", staleScrollRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("--kkindle-vertical-page-step", staleScrollRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("kkindle-vertical-edge-mask", staleScrollRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("html::before", staleScrollRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("column-count: 2 !important", staleScrollRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousVerticalBoundaryScriptSnapsSidePaddingToWholeColumns()
    {
        var script = ReaderPaginationScripts.ContinuousVerticalBoundaryScript;

        Assert.Contains("body.style.removeProperty('padding-left')", script, StringComparison.Ordinal);
        Assert.Contains("body.style.removeProperty('padding-right')", script, StringComparison.Ordinal);
        Assert.Contains("Math.floor(available / line)", script, StringComparison.Ordinal);
        Assert.Contains("body.style.setProperty('padding-left'", script, StringComparison.Ordinal);
        Assert.Contains("body.style.setProperty('padding-right'", script, StringComparison.Ordinal);
        Assert.Contains("body.style.setProperty('padding-top'", script, StringComparison.Ordinal);
        Assert.Contains("body.style.setProperty('padding-bottom'", script, StringComparison.Ordinal);
        Assert.Contains("Math.floor(inlineAvailable / inlineAdvance)", script, StringComparison.Ordinal);
        Assert.Contains("__kkindleScrollContinuousVerticalBy", script, StringComparison.Ordinal);
        Assert.Contains("Math.round(distance / line) * line", script, StringComparison.Ordinal);
        Assert.Contains("kkindle-vertical-flow-guard-left", script, StringComparison.Ordinal);
        Assert.Contains("kkindle-vertical-flow-guard-right", script, StringComparison.Ordinal);
        Assert.Contains("kkindle-vertical-flow-guard-top", script, StringComparison.Ordinal);
        Assert.Contains("kkindle-vertical-flow-guard-bottom", script, StringComparison.Ordinal);
        Assert.Contains("__kkindleUpdateContinuousVerticalGuards", script, StringComparison.Ordinal);
        Assert.Contains("kkindleVerticalFlowBoundary", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationRestoreUsesChapterRatioWhenViewportStepChanged()
    {
        var script = ReaderPaginationScripts.CreateRestorePositionScript(
            left: 3110.4,
            top: 0,
            pagination: true,
            vertical: true,
            chapterRatio: 0.5);

        Assert.Contains("const ratio = 0.5", script, StringComparison.Ordinal);
        Assert.Contains("const rawIsAligned", script, StringComparison.Ordinal);
        Assert.Contains("Math.round((max * ratio) / step)", script, StringComparison.Ordinal);
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
            Assert.Contains("Math.ceil(available / line) * line", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-page-step", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-viewport-width", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-origin-shift", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-content-shift", script, StringComparison.Ordinal);
            Assert.Contains(
                "const originShift = (viewport - baseLeft - baseRight) / 2",
                script,
                StringComparison.Ordinal);
            Assert.Contains("const safeLeft = baseLeft + originShift", script, StringComparison.Ordinal);
            Assert.Contains("const safeRight = baseRight + originShift", script, StringComparison.Ordinal);
            Assert.Contains("rect.left + candidate", script, StringComparison.Ordinal);
            Assert.Contains("window.__kkindleVerticalContentShift", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-safe-left", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-safe-right", script, StringComparison.Ordinal);
            Assert.Contains("const maskRects = visibleRects.map", script, StringComparison.Ordinal);
            Assert.Contains("rect.right + clearance", script, StringComparison.Ordinal);
            Assert.DoesNotContain("window.__kkindleVerticalGapShift", script, StringComparison.Ordinal);
            Assert.Contains("--kkindle-vertical-trailing-extent", script, StringComparison.Ordinal);
            Assert.Contains("alignedMax - naturalMax", script, StringComparison.Ordinal);
            Assert.Contains("window.__kkindleVerticalTrailingKey", script, StringComparison.Ordinal);
            Assert.Contains("window.__kkindleVerticalTrailingExtent", script, StringComparison.Ordinal);
            Assert.Contains(
                "const pageIndex = Math.round(Math.abs(root.scrollLeft || 0) / resolvedStep)",
                script,
                StringComparison.Ordinal);
            Assert.Contains("document.createRange()", script, StringComparison.Ordinal);
            Assert.Contains("nodeRange.selectNodeContents(node)", script, StringComparison.Ordinal);
            Assert.Contains("if (!nodeIsVisible) continue", script, StringComparison.Ordinal);
            Assert.DoesNotContain(".columnWidth", script, StringComparison.Ordinal);
            Assert.DoesNotContain(".columnGap", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VerticalMasksUseTheSameClientViewportAsThePageStep()
    {
        var css = ReaderPaginationScripts.CreateFlowCss(pagination: true, vertical: true);

        Assert.Contains("--kkindle-vertical-viewport-width: 100%", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-origin-shift: 0px", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-content-shift: 0px", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-trailing-extent: 0px", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-safe-left: 0px", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-safe-right: 100000px", css, StringComparison.Ordinal);
        Assert.Contains(
            "calc(var(--kkindle-vertical-page-side) - var(--kkindle-vertical-content-shift))",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "calc(var(--kkindle-vertical-page-side) + var(--kkindle-vertical-content-shift) + var(--kkindle-vertical-trailing-extent))",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-step)",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "left: min(calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-side) + var(--kkindle-vertical-origin-shift)), var(--kkindle-vertical-safe-right))",
            css,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "width: calc(100vw - var(--kkindle-vertical-page-step)",
            css,
            StringComparison.Ordinal);
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
    public void ContinuousVerticalFragmentScrollUsesNegativeWebKitRange()
    {
        var fragmentScript = ReaderNavigationScripts.CreateFragmentScroll(
            needle: "section",
            flowMode: 0,
            vertical: true);

        Assert.Contains("const rawMax = Math.max(0, scroller.scrollWidth - scroller.clientWidth)", fragmentScript, StringComparison.Ordinal);
        Assert.Contains("const distance = Math.abs(scroller.scrollLeft || 0)", fragmentScript, StringComparison.Ordinal);
        Assert.Contains("const target = Math.max(0, distance > rawMax ? rawMax : distance)", fragmentScript, StringComparison.Ordinal);
        Assert.Contains("left: -target", fragmentScript, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalTypographyKeepsPublisherBlocksOnTheGlyphGrid()
    {
        var css = ReaderPaginationScripts.VerticalTypographyGridCss;

        // Publisher wrappers stay real boxes whose block-size is reset onto
        // the paragraph grid. Flattening them with display:contents tripped a
        // WebKitGTK line-box bug: once orthogonal inline cells appeared in a
        // flattened wrapper, every line box drifted downward by one cell and
        // the rest of the chapter was clipped below the viewport.
        Assert.DoesNotContain("display: contents !important", css, StringComparison.Ordinal);
        Assert.Contains("block-size: auto !important", css, StringComparison.Ordinal);
        Assert.Contains(
            ":not(#kkindle-selection-bar, #kkindle-selection-bar *)",
            css,
            StringComparison.Ordinal);
        Assert.Contains("margin-block: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains("padding-block: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains("margin-block: 1lh !important", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 1rem !important", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-chapter-heading", css, StringComparison.Ordinal);
        Assert.Contains("text-align: center !important", css, StringComparison.Ordinal);
        Assert.Contains("text-indent: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains("body :where(sup, sub)", css, StringComparison.Ordinal);
        Assert.Contains("line-height: 0 !important; vertical-align: baseline !important", css, StringComparison.Ordinal);
    }
}
