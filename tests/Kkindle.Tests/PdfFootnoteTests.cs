using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Tests;

public sealed class PdfFootnoteTests
{
    [Fact]
    public void MatchesRaisedReferencesDespiteNativeTextOrderAndJoinsWrappedNotes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Kkindle-Footnotes-" + Guid.NewGuid());
        try
        {
            using var document = new PdfDocumentService(PdfFixture.WriteFootnotes(directory));
            var page = document.ReadPage(1);
            var references = PdfFootnoteLayout.Read(page);
            Assert.Equal(2, references.Count);
            Assert.All(references, note =>
            {
                Assert.Equal("(2)", note.Marker);
                Assert.Equal("Harmonics depend on board layout and components. This continuation belongs to the same footnote.", note.Text);
                Assert.True(note.Offset > page.Text.IndexOf("(3)", StringComparison.Ordinal));
            });
            Assert.Empty(PdfFootnoteLayout.Read(page with { Glyphs = [] }));
        }
        finally { Directory.Delete(directory, true); }
    }
}
