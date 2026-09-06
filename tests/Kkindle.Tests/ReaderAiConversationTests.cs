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
        Assert.Contains(block.Inlines!.OfType<Avalonia.Controls.Documents.Run>(), run => run.Text == "Completed answer");
        Assert.Contains("Completed answer", block.Markdown);
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
