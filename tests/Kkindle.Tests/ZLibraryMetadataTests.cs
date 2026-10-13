using Kkindle.Core;
using Xunit;

namespace Kkindle.Tests;

public sealed class ZLibraryMetadataTests
{
    [Theory]
    [InlineData("Different title", "Different author", "978-7-020-09622-0", true)]
    [InlineData(" 书 名 ", "乙；甲", null, true)]
    [InlineData("书名", "其他作者", null, false)]
    [InlineData("别的书名", "甲、乙", null, false)]
    [InlineData("书名", "未知作者", null, true)]
    public void DuplicateDetectionUsesIsbnOrTitleAndAuthors(string title, string author, string? isbn, bool expected)
    {
        var local = new LibraryBookMatch(Guid.NewGuid(), title, author, []) { Isbn = isbn };
        var online = new ZLibraryBook { Title = "书名", Author = "甲、乙", Identifier = "9787020096220 / 7020096220", Extension = "pdf" };
        Assert.Equal(expected, ZLibraryMetadata.Matches(local, online));
    }
}
