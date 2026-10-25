using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderAiConversationTests
{
    [Fact]
    public void MarkdownSurvivesCitationBindingAndStreamingUpdates()
    {
        var block = new KreaderMarkdownTextBlock { Markdown = "## Heading\n\n**First answer**" };
        Assert.NotEmpty(block.Inlines!);
        block.CitationAction = _ => { };
        Assert.NotEmpty(block.Inlines!);
        block.Markdown = "## Heading\n\n**Completed answer**";
        Assert.Contains(InlineTree(block.Inlines!).OfType<Avalonia.Controls.Documents.Run>(), run => run.Text == "Completed answer");
        Assert.Contains("Completed answer", block.Markdown);
    }

    [Fact]
    public void MarkdownPreviewRendersTaskListsAndStrikethrough()
    {
        var block = new KreaderMarkdownTextBlock
        {
            Markdown = "- [ ] Todo\n- [x] Done\n~~Old idea~~"
        };

        var tree = InlineTree(block.Inlines!).ToArray();
        var text = string.Concat(tree.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text));
        Assert.Contains("☐ Todo", text);
        Assert.Contains("☑ Done", text);
        Assert.Contains(tree.OfType<Avalonia.Controls.Documents.Span>(), span => span.TextDecorations is not null);
    }

    [Fact]
    public void MathSurvivesMarkdownFormattingButCodeAndPricesStayLiteral()
    {
        var block = new KreaderMarkdownTextBlock
        {
            Markdown = "## **频率** \\(f_{PD}\\)\n\n" +
                "\\[f_{PD}=\\frac{f_{OSC}\\times OSC\\_2X\\times MULT}{PLL\\_R\\_PRE\\times PLL\\_R}\\]\n\n" +
                "行内 $x$，块公式 $$\\sqrt{x^2+1}$$\n\n`$literal$`，价格 $5 和 $10。 [S1]"
        };
        var tree = InlineTree(block.Inlines!).ToArray();
        var controls = tree.OfType<Avalonia.Controls.Documents.InlineUIContainer>().Select(inline => inline.Child).ToArray();
        var formulas = controls.OfType<Avalonia.Controls.Viewbox>()
            .Select(view => Assert.IsType<CSharpMath.Avalonia.MathView>(view.Child)).ToArray();
        Assert.Equal(4, formulas.Length);
        Assert.All(formulas, formula => Assert.Null(formula.ErrorMessage));
        Assert.Contains(controls.OfType<Avalonia.Controls.Button>(), button => Equals("[S1]", button.Content));
        var text = string.Concat(tree.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text));
        Assert.Contains("$literal$", text);
        Assert.Contains("$5 和 $10", text);
        Assert.DoesNotContain("\\frac", text);
    }

    [Fact]
    public void StreamingIncompleteMathRecoversAndNestedListsKeepNumbers()
    {
        var block = new KreaderMarkdownTextBlock { Markdown = @"Result \\(\\frac{1}{" };
        block.Markdown = "3. **First**\n   - Nested *detail*\n4. Second\n\n\\(\\frac{1}{2}\\)";
        var tree = InlineTree(block.Inlines!).ToArray();
        var text = string.Concat(tree.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text));
        Assert.Contains("3. First", text);
        Assert.Contains("   • Nested detail", text);
        Assert.Contains("4. Second", text);
        Assert.Single(tree.OfType<Avalonia.Controls.Documents.InlineUIContainer>(),
            inline => inline.Child is Avalonia.Controls.Viewbox);
    }

    private static IEnumerable<Avalonia.Controls.Documents.Inline> InlineTree(
        IEnumerable<Avalonia.Controls.Documents.Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Avalonia.Controls.Documents.Span span)
                foreach (var child in InlineTree(span.Inlines)) yield return child;
        }
    }

    [Fact]
    public void ReusedCitationNumbersResolveWithinEachAnswer()
    {
        ReaderAiSourceViewModel? navigated = null;
        using var first = new ReaderAiMessageViewModel("assistant", citationAction: source => navigated = source);
        using var second = new ReaderAiMessageViewModel("assistant", citationAction: source => navigated = source);
        first.SetSources([new ReaderAiSourceViewModel(new PdfPageText(3, "First evidence"), "S1")]);
        second.SetSources([new ReaderAiSourceViewModel(new PdfPageText(28, "Second evidence"), "S1")]);

        first.CitationAction!("S1");
        Assert.Equal(3, navigated!.Page!.PageNumber);
        second.CitationAction!("s1");
        Assert.Equal(28, navigated.Page!.PageNumber);
        navigated = null;
        first.CitationAction!("S99");
        Assert.Null(navigated);
        Assert.Equal("First evidence", first.CitationSourceResolver("s1")!.Content);
        Assert.Equal("Second evidence", second.CitationSourceResolver("S1")!.Content);
        Assert.Null(first.CitationSourceResolver("S99"));
        second.SetSources([new ReaderAiSourceViewModel(new PdfPageText(9, "Updated evidence"))]);
        Assert.Equal(9, second.CitationSourceResolver("S1")!.Page!.PageNumber);
    }

    [Fact]
    public void InterruptedAnswerRemainsCopyableAndDisposalReleasesSourcesAndRetry()
    {
        var message = new ReaderAiMessageViewModel("assistant");
        message.SetSources([new ReaderAiSourceViewModel(new PdfPageText(1, "Evidence"))]);
        message.Update("Partial answer", "Reasoning", isStreaming: true);
        Assert.False(message.CanCopy);
        message.Update(message.Content, message.Reasoning, isStreaming: false);
        Assert.True(message.CanCopy);
        Assert.Equal("Partial answer", message.Content);
        message.RetryAction = () => { };
        message.Dispose();
        Assert.Empty(message.Sources);
        Assert.Null(message.RetryAction);
    }

    [Fact]
    public void OverviewIncludesBookEndAndDisclosesSampling()
    {
        var book = Guid.NewGuid();
        var file = Guid.NewGuid();
        var chunks = Enumerable.Range(0, 60).Select(index => new BookContentChunk(
            index + 1, book, file, "hash", index, 0, $"Chapter {index}", $"{index}.xhtml",
            0, 4000, new string((char)('一' + index), 4000))).ToArray();
        var context = ReaderAiContextBuilder.BuildOverview(chunks);
        Assert.Contains(context.Sources, source => source.Chunk.ChapterIndex == 0);
        Assert.Contains(context.Sources, source => source.Chunk.ChapterIndex == 59);
        Assert.Equal(24, context.Sources.Count);
        Assert.Contains("24/60", context.Text);
        Assert.True(context.Text.Length < 8000);
        Assert.All(context.Sources, source => Assert.True(source.Content.Length < source.Chunk.Content.Length));
    }

    [Fact]
    public void ChapterOverviewIncludesMiddleAndEndInsteadOfOnlyOpening()
    {
        var book = Guid.NewGuid();
        var file = Guid.NewGuid();
        var chunks = Enumerable.Range(0, 100).Select(index => new BookContentChunk(
            index + 1, book, file, "hash", 0, index, "Long chapter", "one.xhtml",
            index * 2000, (index + 1) * 2000, new string('文', 2000))).ToArray();
        var context = ReaderAiContextBuilder.BuildOverview(chunks);
        Assert.Contains(context.Sources, source => source.Chunk.ChunkIndex == 99);
        Assert.Contains(context.Sources, source => source.Chunk.ChunkIndex is >= 40 and <= 60);
    }

    [Fact]
    public void EmptyOverviewProducesNoEvidence()
    {
        Assert.Empty(ReaderAiContextBuilder.BuildOverview([]).Sources);
        Assert.Empty(ReaderAiContextBuilder.SampleEvenly(new[] { 1, 2 }, 0));
    }
}
