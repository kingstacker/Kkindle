using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderAppearanceScriptTests
{
    [Fact]
    public void StandardLineBreakingPreservesChinesePunctuationProhibitions()
    {
        var css = ReaderAppearanceScripts.StandardLineBreakingCss;

        Assert.Contains("line-break: strict !important", css, StringComparison.Ordinal);
        Assert.Contains("-webkit-line-break: strict !important", css, StringComparison.Ordinal);
        Assert.Contains("-epub-line-break: strict !important", css, StringComparison.Ordinal);
        Assert.Contains("word-break: normal !important", css, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: normal !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("line-break: anywhere", css, StringComparison.Ordinal);
        Assert.DoesNotContain("word-break: break-all", css, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalTypographyUsesPublicationAuthoredInlineLayout()
    {
        var css = ReaderAppearanceScripts.VerticalPublicationTypographyCss;

        Assert.Contains("text-orientation: mixed !important", css, StringComparison.Ordinal);
        Assert.Contains("-webkit-text-orientation: mixed !important", css, StringComparison.Ordinal);
        Assert.Contains("-epub-text-orientation: mixed !important", css, StringComparison.Ordinal);
        Assert.Contains("display: contents !important", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-tcy", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-vertical-latin", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-vertical-number", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-native-vertical-digits", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-native-vertical-digit", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-native-vertical-footnote", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-number", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-single", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-single-punctuation", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-cjk", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-centered-mark", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-tcy", css, StringComparison.Ordinal);
        Assert.Contains("display: inline-grid !important", css, StringComparison.Ordinal);
        Assert.Contains("align-items: center !important", css, StringComparison.Ordinal);
        Assert.Contains("justify-items: center !important", css, StringComparison.Ordinal);
        Assert.Contains("text-indent: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains("vertical-align: baseline !important", css, StringComparison.Ordinal);
        Assert.Contains("width: 1em !important", css, StringComparison.Ordinal);
        Assert.Contains("height: 1em !important", css, StringComparison.Ordinal);
        Assert.Contains("line-height: 1em !important", css, StringComparison.Ordinal);
        Assert.Contains(
            "body .kkindle-linux-vertical-cjk,\nbody .kkindle-linux-vertical-single",
            css,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--kkindle-linux-vertical-inline-advance", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden !important", css, StringComparison.Ordinal);
        Assert.Contains("margin: 0 !important", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-footnote", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-linux-footnote-scale", css, StringComparison.Ordinal);
        Assert.Contains("transform: none !important", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-pair-open", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-pair-close", css, StringComparison.Ordinal);
        Assert.Contains("place-items: center !important", css, StringComparison.Ordinal);
        Assert.Contains("--kkindle-vertical-ink-shift", css, StringComparison.Ordinal);
        Assert.Contains(".kkindle-linux-vertical-cjk-ink", css, StringComparison.Ordinal);
        Assert.DoesNotContain("padding-left: 0.22em !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("padding-right: 0.22em !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("justify-items: start !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("justify-items: end !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("margin-top: 0.12em !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("margin-inline-start: 0.12em !important", css, StringComparison.Ordinal);
        Assert.Contains("Unicode vertical presentation", css, StringComparison.Ordinal);
        Assert.Contains("letter-spacing: normal !important", css, StringComparison.Ordinal);
        Assert.Contains("word-spacing: normal !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("body *", css, StringComparison.Ordinal);
        Assert.DoesNotContain("font-feature-settings:", css, StringComparison.Ordinal);
        Assert.DoesNotContain("font-variant-numeric:", css, StringComparison.Ordinal);
        Assert.DoesNotContain("position: absolute", css, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body .kkindle-linux-vertical-cjk {\n          display: inline !important;\n          position: relative",
            css,
            StringComparison.Ordinal);
        Assert.DoesNotContain("font-family:", css, StringComparison.Ordinal);
        Assert.Contains("line-break: strict !important", css, StringComparison.Ordinal);
        // In vertical-rl `direction` selects the inline axis, not the column
        // axis. rtl therefore bottom-aligns every partially filled line — a
        // paragraph's last column, short paragraphs and the 2em first-line
        // indent all hug the page bottom — while leaving the scrollLeft range
        // untouched. It must never come back.
        Assert.DoesNotContain("direction: rtl", css, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalDebugOutlinesExposeCompatibilityOuterAndInnerCells()
    {
        var css = ReaderAppearanceScripts.VerticalDebugOutlineCss;

        Assert.Contains("data-kkindle-vertical-debug-boxes=\"1\"", css, StringComparison.Ordinal);
        Assert.Contains("kkindle-linux-vertical-pair-punctuation", css, StringComparison.Ordinal);
        Assert.Contains("kkindle-linux-vertical-cell-inner", css, StringComparison.Ordinal);
        Assert.Contains("outline: 1px solid", css, StringComparison.Ordinal);
        Assert.Contains("outline: 1px dashed", css, StringComparison.Ordinal);
        // Han now owns a real one-em layout cell. Its green outer frame and
        // blue inner frame must be the boxes being tested, not a detached
        // fixed-position approximation.
        Assert.Contains("kkindle-linux-vertical-cjk", css, StringComparison.Ordinal);
        Assert.Contains("kkindle-linux-vertical-cjk-ink", css, StringComparison.Ordinal);
        Assert.Contains("rgba(22, 163, 74, 0.92)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalFlowCssKeepsInlineDirectionLeftToRight()
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: true,
            twoPage: false,
            horizontalPadding: 24,
            maxContentWidth: 1200);

        Assert.Contains("writing-mode: vertical-rl !important", css, StringComparison.Ordinal);
        Assert.Contains("direction: ltr !important", css, StringComparison.Ordinal);
        Assert.DoesNotContain("direction: rtl", css, StringComparison.Ordinal);
    }
}
