using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class G2PWPinyinEngineTests
{
    [Theory]
    [InlineData("长乐还发", "長樂還發")]
    [InlineData("😀银行 ABC-12，长大！", "😀銀行 ABC-12，長大！")]
    [InlineData("長樂還發", "長樂還發")]
    public void UsesOfficialSimplifiedMappingWithoutChangingUtf16Positions(string source, string expected)
    {
        var converted = G2PWChineseText.ToTraditional(source);
        Assert.Equal(expected, converted);
        Assert.Equal(source.Length, converted.Length);
    }

    [Theory]
    [InlineData(PinyinBookOutputStyle.ToneMarked, "háng")]
    [InlineData(PinyinBookOutputStyle.ToneNumber, "hang2")]
    [InlineData(PinyinBookOutputStyle.NoTone, "hang")]
    public void FallbackAlignsPronunciationWithUtf16AfterSupplementaryCharacters(PinyinBookOutputStyle style, string expected)
    {
        using var engine = new DotNetG2PPinyinEngine();
        const string text = "😀银行🚀中文";
        var pinyins = engine.ToPinyinList(text, style);
        Assert.Equal(text.Length, pinyins.Length);
        Assert.Equal(expected, pinyins[text.IndexOf('行')]);
        Assert.Equal(string.Empty, pinyins[1]);
        Assert.Equal(string.Empty, pinyins[5]);
        Assert.False(string.IsNullOrWhiteSpace(pinyins[text.IndexOf('文')]));
    }

    [Fact]
    public void HonorsCancellationBeforeAttemptingToLoadTheModel()
    {
        using var engine = new G2PWPinyinEngine(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            engine.ToPinyinList("长大", PinyinBookOutputStyle.ToneMarked, cancellation.Token));
    }

    [Fact]
    public void TokenizerKeepsTargetAfterEmojiAtTheCorrectTokenPosition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var vocabulary = Path.Combine(root, "vocab.txt");
            File.WriteAllLines(vocabulary, ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "銀", "行"]);
            var tokens = new G2PWWordPieceTokenizer(vocabulary).Tokenize("😀銀行", 3);
            Assert.Equal(new long[] { 2, 1, 4, 5, 3 }, tokens.InputIds);
            Assert.Equal(3, tokens.QueryTokenIndex);
            Assert.All(tokens.AttentionMask, value => Assert.Equal(1, value));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
