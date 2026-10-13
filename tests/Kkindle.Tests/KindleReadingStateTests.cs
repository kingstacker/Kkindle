using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class KindleReadingStateTests
{
    [Fact]
    public void DeleteRemovesAssociatedAnnotationsAndLeavesOtherBooks()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "book.sdr");
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "annotations"), "keep my notes");
            Directory.CreateDirectory(Path.Combine(root, "other.sdr"));
            KindleReadingState.DeleteForRemovedBook(Path.Combine(root, "book.azw3"));
            Assert.False(Directory.Exists(state));
            Assert.True(Directory.Exists(Path.Combine(root, "other.sdr")));
            Assert.Empty(Directory.GetDirectories(root, "kkindle-reading-backup-*"));
            KindleReadingState.DeleteForRemovedBook(Path.Combine(root, "book.azw3"));
            Assert.Empty(Directory.GetDirectories(root, "kkindle-reading-backup-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
