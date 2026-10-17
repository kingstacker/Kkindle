using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class SmartShelfTests
{
    [Fact]
    public void NormalizeKeepsNamedShelvesAndAssignsIds()
    {
        var settings = new AppSettings
        {
            SmartShelves =
            [
                new SmartShelf(Guid.Empty, "  最近阅读  ", Tag: " 小说 "),
                new SmartShelf(Guid.NewGuid(), "   ")
            ]
        };

        var normalized = AppSettings.Normalize(settings);

        var shelf = Assert.Single(normalized.SmartShelves);
        Assert.NotEqual(Guid.Empty, shelf.Id);
        Assert.Equal("最近阅读", shelf.Name);
        Assert.Equal("小说", shelf.Tag);
    }
}
