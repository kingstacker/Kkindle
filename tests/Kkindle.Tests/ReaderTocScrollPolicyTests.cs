using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderTocScrollPolicyTests
{
    private static object[] Rows(int count) =>
        Enumerable.Range(0, count).Select(_ => new object()).ToArray();

    [Fact]
    public void UnchangedVisibleRowsSkipTheItemsSourceReset()
    {
        var rows = Rows(5);
        // A TOC click on a row whose branch is already open recomputes the same
        // rows. Rebuilding then would reset the virtualizing panel and cost it
        // the measured heights it estimates scroll offsets from.
        Assert.False(ReaderTocScrollPolicy.RequiresRowRebuild(rows, [.. rows]));
    }

    [Fact]
    public void FoldChangesStillRebuild()
    {
        var rows = Rows(5);
        Assert.True(ReaderTocScrollPolicy.RequiresRowRebuild(rows, [.. rows.Take(3)]));
        Assert.True(ReaderTocScrollPolicy.RequiresRowRebuild(rows, [.. rows, new object()]));
        Assert.True(ReaderTocScrollPolicy.RequiresRowRebuild(rows, [.. rows.Reverse()]));
        Assert.True(ReaderTocScrollPolicy.RequiresRowRebuild(null, rows));
    }

    [Fact]
    public void SameTitlesInDifferentRowsAreNotTreatedAsUnchanged()
    {
        // Rows are compared by identity: two rows can carry the same title, and
        // a rebuilt list must still replace the stale instances.
        Assert.True(ReaderTocScrollPolicy.RequiresRowRebuild(Rows(3), Rows(3)));
    }

    [Theory]
    [InlineData(0d, 30d)]      // flush with the top edge
    [InlineData(120d, 30d)]    // mid rail
    [InlineData(370d, 30d)]    // flush with the bottom edge
    [InlineData(-0.4d, 30d)]   // half a device pixel out is still in view
    public void VisibleRowsAreNotScrolled(double rowTop, double rowHeight)
    {
        Assert.False(ReaderTocScrollPolicy.RequiresScrollIntoView(rowTop, rowHeight, 400d));
    }

    [Theory]
    [InlineData(-31d, 30d)]    // fully above
    [InlineData(-5d, 30d)]     // clipped at the top
    [InlineData(390d, 30d)]    // clipped at the bottom
    [InlineData(410d, 30d)]    // fully below
    public void OffScreenRowsAreScrolled(double rowTop, double rowHeight)
    {
        Assert.True(ReaderTocScrollPolicy.RequiresScrollIntoView(rowTop, rowHeight, 400d));
    }

    [Fact]
    public void RowTallerThanTheRailAnchorsOnItsTopEdge()
    {
        // Chasing the bottom edge of an over-long wrapped title would scroll the
        // title itself out of sight and never settle.
        Assert.False(ReaderTocScrollPolicy.RequiresScrollIntoView(0d, 120d, 100d));
        Assert.True(ReaderTocScrollPolicy.RequiresScrollIntoView(-20d, 120d, 100d));
    }

    [Fact]
    public void AnUnmeasuredRailNeverScrolls()
    {
        Assert.False(ReaderTocScrollPolicy.RequiresScrollIntoView(0d, 30d, 0d));
    }
}
