using Kkindle.Layout;

namespace Kkindle.Tests;

public sealed class ReaderSpeechTextBuilderTests
{
    [Fact]
    public void SpeechOmitsRubyAndPreservesSourceOffsets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kkindle-speech-{Guid.NewGuid():N}.xhtml");
        try
        {
            File.WriteAllText(path, "<html><body><p><ruby>银<rt>yín</rt></ruby>行测试</p></body></html>");
            var content = new XhtmlChapterLoader(false).Load(path);
            var speech = ReaderSpeechTextBuilder.Load(path, false, CancellationToken.None);
            Assert.NotNull(speech);
            Assert.Contains("银", speech.Text);
            Assert.Contains("行测试", speech.Text);
            Assert.DoesNotContain("yín", speech.Text);
            var target = speech.Text.IndexOf("测试", StringComparison.Ordinal);
            var mapped = speech.MapToSource(target, 2);
            Assert.Equal("测试", content.BodyText.Substring(mapped.Start, mapped.Length));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadHonorsCancellationBeforeOpeningTheFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ReaderSpeechTextBuilder.Load("missing.xhtml", false, cancellation.Token));
    }
}
