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
}
