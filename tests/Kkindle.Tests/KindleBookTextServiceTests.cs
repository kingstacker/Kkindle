using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class KindleBookTextServiceTests
{
    [Theory]
    [InlineData("native-compressed.azw3")]
    [InlineData("native-uncompressed.azw3")]
    [InlineData("native-hybrid.azw3")]
    [InlineData("native-legacy.mobi")]
    public async Task ReadsRawTextFindsStableByteRangeAndDerivesBookIdentity(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", fixture);

        var document = await KindleBookTextService.ReadAsync(path);

        Assert.False(string.IsNullOrWhiteSpace(document.Title));
        Assert.False(string.IsNullOrWhiteSpace(document.PalmName));
        Assert.NotNull(document.CreateBookId());
        Assert.True(document.TryFindRange("你好，世界。😀", null, null, out var start, out var end));
        Assert.True(start >= 0);
        Assert.True(end > start);
        Assert.Equal("你好，世界。😀", document.ExtractRange(start, end));
    }
}
