using Kkindle.Core;
using Xunit;

namespace Kkindle.Tests;

public sealed class BookDetailEditingTests
{
    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("0", true, 0d)]
    [InlineData("8.7", true, 8.7d)]
    [InlineData("10", true, 10d)]
    [InlineData("-1", false, null)]
    [InlineData("10.1", false, null)]
    [InlineData("NaN", false, null)]
    [InlineData("8,7", false, null)]
    public void RatingMustBeEmptyOrBetweenZeroAndTen(string? input, bool valid, double? expected)
    {
        Assert.Equal(valid, BookDetailEditing.TryParseRating(input, out var rating));
        Assert.Equal(expected, rating);
    }
}
