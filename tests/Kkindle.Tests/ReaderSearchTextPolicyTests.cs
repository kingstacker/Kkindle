using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderSearchTextPolicyTests
{
    [Fact]
    public void FindsAllWhitespaceInsensitiveMatchesWithRawOffsets()
    {
        var matches = ReaderSearchTextPolicy.FindMatches(
            "前文\n\n既然  这样。后文既然如此。",
            "既然  这样");

        var firstStart = "前文\n\n".Length;
        Assert.Equal(
            new[] { new ReaderSearchMatch(firstStart, "既然  这样".Length) },
            matches);
    }

    [Fact]
    public void ContextChoosesLaterRepeatedOccurrenceInsteadOfFirstOne()
    {
        const string body = "既然第一次。\n中间内容。\n既然第二次。";
        const string context = "中间内容。 既然第二次。";

        var offset = ReaderSearchTextPolicy.FindBestMatchOffset(
            body,
            "既然",
            context,
            offsetHint: body.IndexOf("既然第二次", StringComparison.Ordinal));

        Assert.Equal(body.LastIndexOf("既然", StringComparison.Ordinal), offset);
    }

    [Fact]
    public void OffsetHintChoosesNearestMatchWhenContextIsUnavailable()
    {
        const string body = "既然第一次。中间内容。既然第二次。";

        var offset = ReaderSearchTextPolicy.FindBestMatchOffset(
            body,
            "既然",
            context: null,
            offsetHint: body.LastIndexOf("既然", StringComparison.Ordinal));

        Assert.Equal(body.LastIndexOf("既然", StringComparison.Ordinal), offset);
    }
}
