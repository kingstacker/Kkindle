using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Tests;

public sealed class PdfNavigationTests
{
    [Fact]
    public void ReadsPageLabelsLinksAndRotatedDestinations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Kkindle-PdfNavigation-" + Guid.NewGuid());
        try
        {
            using var document = new PdfDocumentService(PdfFixture.WriteNavigation(directory));
            Assert.Equal(new[] { "i", "ii", "1", "2" }, document.ReadInfo().PageLabels);
            var links = document.ReadPage(1).Links;
            Assert.Equal(3, links.Count); // JavaScript is not an actionable reader link.
            var note = Assert.Single(links, l => l.PageNumber == 3);
            Assert.Equal(0.5, note.Top!.Value, 3);
            Assert.Equal(40.0 / 600, note.Left!.Value, 3);
            Assert.Contains(links, l => l.Uri == "https://example.org/");
            var rotated = Assert.Single(links, l => l.PageNumber == 4);
            Assert.InRange(rotated.Top!.Value, 0, 1);
            Assert.InRange(rotated.Left!.Value, 0, 1);
            Assert.Equal(1, Assert.Single(document.ReadPage(2).Links).PageNumber);
            Assert.Equal(1, Assert.Single(document.ReadPage(4).Links).PageNumber);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SelectsColumnInReadingOrderWithoutChangingStoredOffsets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Kkindle-PdfColumns-" + Guid.NewGuid());
        try
        {
            using var document = new PdfDocumentService(PdfFixture.WriteNavigation(directory));
            var page = document.ReadPage(1);
            Assert.Equal(document.ReadPageText(1), page.Text);
            var layout = new PdfTextSelectionLayout(page);
            var start = layout.IndexAtOffset(page.Text.IndexOf("Left first", StringComparison.Ordinal));
            var endOffset = page.Text.IndexOf("esting example.", StringComparison.Ordinal) + "esting example.".Length - 1;
            var end = layout.IndexAtOffset(endOffset) + 1;
            var ranges = layout.GetRanges(start, end);
            var text = layout.GetText(ranges);
            Assert.DoesNotContain("Right", text);
            Assert.Equal("Left first line. An interesting example.", PdfTextSelectionLayout.NormalizeCopy(text));
            var fragment = PdfTextRanges.Encode(ranges);
            Assert.Equal(ranges, PdfTextRanges.Read(fragment, ranges.Min(r => r.Start), ranges.Max(r => r.End)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0.006)]
    [InlineData(0.009)]
    public void StaggeredColumnsKeepWholeLinesAndCopiedText(double stagger)
    {
        // PDFium emits each column in reading order. Their baselines need not
        // align; punctuation/descenders must not bridge neighboring rows.
        var left = new[] { "Left short.", "Left has a much longer second line.", "Left third." };
        var right = new[] { "The device is high-performance,", "and generates any frequency.", "Output power stays constant." };
        var header = new[] { "Header one.", "Header two." };
        var text = string.Join("\r\n", header.Concat(header).Concat(left).Concat(right));
        var glyphs = new List<PdfTextGlyph>();
        var offset = 0;
        foreach (var (lines, x, top) in new[]
                 { (header, 0.08, 0.02), (header, 0.52, 0.02), (left, 0.08, 0.1), (right, 0.52, 0.1 + stagger) })
        {
            for (var line = 0; line < lines.Length; line++)
            {
                for (var character = 0; character < lines[line].Length; character++)
                {
                    var value = lines[line][character];
                    if (value != ' ')
                    {
                        var height = value is '.' or ',' or '-' ? 0.003 : char.IsUpper(value) ? 0.009 : 0.007;
                        glyphs.Add(new(offset + character, 1, new(x + character * 0.01,
                            top + line * 0.014 + 0.009 - height, 0.008, height)));
                    }
                }
                offset += lines[line].Length + 2;
            }
        }
        var page = new PdfPageContent(612, 792, text, glyphs);
        var layout = new PdfTextSelectionLayout(page);
        var start = layout.IndexAtOffset(text.IndexOf(right[0], StringComparison.Ordinal));
        var end = layout.IndexAtOffset(text.Length - 1) + 1;
        var ranges = layout.GetRanges(start, end);
        Assert.Equal(string.Join(' ', right), PdfTextSelectionLayout.NormalizeCopy(layout.GetText(ranges)));
        Assert.Equal(glyphs.Where(g => g.Offset >= text.IndexOf(right[0], StringComparison.Ordinal)).Select(g => g.Offset).Order(),
            glyphs.Where(g => ranges.Any(r => g.Offset >= r.Start && g.Offset < r.End)).Select(g => g.Offset).Order());
    }

    [Theory]
    [InlineData("one\ntwo\nthree", "one two three")]
    [InlineData("inter-\nesting\nexample", "interesting example")]
    [InlineData("第一行\n第二行\n第三行", "第一行第二行第三行")]
    [InlineData("First paragraph.\n\nSecond paragraph.", "First paragraph.\n\nSecond paragraph.")]
    [InlineData("well-known\nterm", "well-known term")]
    public void CopyRepairsLineWrapsAndPreservesParagraphs(string source, string expected) =>
        Assert.Equal(expected, PdfTextSelectionLayout.NormalizeCopy(source));
}
