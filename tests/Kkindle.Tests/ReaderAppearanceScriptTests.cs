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
    public void VerticalTypographyUsesPublicationMixedOrientationAndTcy()
    {
        var css = ReaderAppearanceScripts.VerticalPublicationTypographyCss;

        Assert.Contains("text-orientation: mixed !important", css, StringComparison.Ordinal);
        Assert.Contains("text-combine-upright: digits 4 !important", css, StringComparison.Ordinal);
        Assert.Contains("text-combine-upright: all !important", css, StringComparison.Ordinal);
        Assert.Contains("-webkit-text-combine: horizontal !important", css, StringComparison.Ordinal);
        Assert.Contains("font-feature-settings: \"kern\" 1, \"vert\" 1, \"vrt2\" 1", css, StringComparison.Ordinal);
        Assert.Contains("ruby-position: over !important", css, StringComparison.Ordinal);
        Assert.Contains("line-break: strict !important", css, StringComparison.Ordinal);
    }
}
