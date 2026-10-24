using HtmlAgilityPack;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class DictionaryHtmlRendererTests
{
    [Fact]
    public void PreservesDictionaryHierarchyAndCssWhileRemovingSourceScripts()
    {
        var html = DictionaryHtmlRenderer.Render([new("apple", "苹果", "朗文", """
            <link href="lm6.css" rel="stylesheet"><script>untrusted()</script>
            <div class="entry"><span class="pos">noun</span><div class="sense"><b>1</b>
            <span class="def">苹果</span><span class="example">an apple</span></div>
            <span onclick="dictionary.toggle(this)">Examples</span><div class="content">more</div>
            <a href="entry://fruit">fruit</a><img src="file:///secret.png" onerror="steal()"></div>
            """, ".sense{display:block;color:blue}")]);
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//div[@class='entry']/div[@class='sense']/span[@class='example']"));
        Assert.Contains(".sense{display:block;color:blue}", html);
        Assert.DoesNotContain("untrusted()", html);
        Assert.Null(doc.DocumentNode.SelectSingleNode("//*[@onclick or @onerror]"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//img"));
        Assert.Equal("toggle", doc.DocumentNode.SelectSingleNode("//*[@data-dictionary-toggle]").GetAttributeValue("data-dictionary-toggle", ""));
        Assert.Equal("fruit", doc.DocumentNode.SelectSingleNode("//*[@data-dictionary-term]").GetAttributeValue("data-dictionary-term", ""));
        Assert.Contains("Content-Security-Policy", html);
    }

    [Fact]
    public void PlainDefinitionsAndDictionaryNamesAreEscaped()
    {
        var html = DictionaryHtmlRenderer.Render([new("x", "<script>example</script>", "<b>name</b>")]);
        Assert.Contains("&lt;script&gt;example&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;name&lt;/b&gt;", html);
    }
}
