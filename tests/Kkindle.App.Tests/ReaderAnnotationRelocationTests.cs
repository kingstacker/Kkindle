using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderAnnotationRelocationTests(SettingsUiSession session)
{
    [Fact]
    public Task NativeEpubAnnotationsUseQuoteAndContextWhenOffsetsMove() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        var body = Assert.IsType<string>(host.BodyText);

        const string uniqueQuote = "纸上的时光";
        var uniqueStart = body.IndexOf(uniqueQuote, StringComparison.Ordinal);
        Assert.True(uniqueStart >= 0);

        var staleUnique = new ReaderAnnotation
        {
            StartOffset = Math.Max(0, body.Length - 4),
            EndOffset = body.Length,
            SelectedText = $"  {uniqueQuote}  ",
        };
        Assert.True(host.TryGetAnnotationRange(staleUnique, out var relocatedUniqueStart, out var relocatedUniqueEnd));
        Assert.Equal(uniqueStart, relocatedUniqueStart);
        Assert.Equal(uniqueStart + uniqueQuote.Length, relocatedUniqueEnd);

        const string repeatedQuote = "窗外的树影慢慢移过书页，文字安静地留在纸上。";
        const string prefix = "第42段。";
        const string suffix = "阅读时，颜色与纸张的细微纹理相互衬托。";
        var context = prefix + repeatedQuote + suffix;
        var contextStart = body.IndexOf(context, StringComparison.Ordinal);
        Assert.True(contextStart >= 0);

        var repeated = new ReaderAnnotation
        {
            StartOffset = 0,
            EndOffset = 1,
            SelectedText = repeatedQuote,
            Prefix = prefix,
            Suffix = suffix,
        };
        Assert.True(host.TryGetAnnotationRange(repeated, out var relocatedRepeatedStart, out var relocatedRepeatedEnd));
        Assert.Equal(contextStart + prefix.Length, relocatedRepeatedStart);
        Assert.Equal(contextStart + prefix.Length + repeatedQuote.Length, relocatedRepeatedEnd);

        var fallback = new ReaderAnnotation
        {
            StartOffset = 5,
            EndOffset = 12,
            SelectedText = "这段文字已经不存在。",
        };
        Assert.True(host.TryGetAnnotationRange(fallback, out var fallbackStart, out var fallbackEnd));
        Assert.Equal(5, fallbackStart);
        Assert.Equal(12, fallbackEnd);

        host.SetAnnotations([staleUnique]);
        Assert.NotEmpty(host.GetAnnotationBounds(staleUnique));
    });

    private Task Run(Func<Task> action) =>
        session.Session.Dispatch(
            async () =>
            {
                await action();
                return true;
            },
            CancellationToken.None);
}
