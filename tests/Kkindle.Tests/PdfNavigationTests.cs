using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Tests;

public sealed class PdfNavigationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TableSelectionKeepsCellsRowsAndSuperscripts(bool filledRules, bool form)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Kkindle-PdfTable-" + Guid.NewGuid());
        try
        {
            using var document = new PdfDocumentService(PdfFixture.WriteTable(directory, filledRules, form));
            var page = document.ReadPage(1);
            Assert.Equal(8, page.Rulings.Count);
            var layout = new PdfTextSelectionLayout(page);
            int Start(string value) => layout.IndexAtOffset(page.Text.IndexOf(value, StringComparison.Ordinal));
            int End(string value) => layout.IndexAtOffset(page.Text.IndexOf(value, StringComparison.Ordinal) + value.Length - 1) + 1;
            var descriptionStart = Start("Positive ramp");
            var descriptionEnd = End("Recalibrate each cycle");
            var expected = "Positive ramp Two’s complement = 2³⁰\nNegative ramp Recalibrate each cycle";
            foreach (var reverse in new[] { false, true })
            {
                var ranges = reverse ? layout.GetDragRanges(descriptionEnd, descriptionStart) : layout.GetDragRanges(descriptionStart, descriptionEnd);
                Assert.Equal(expected, layout.GetCopyText(ranges));
                Assert.DoesNotContain("RAMP", layout.GetText(ranges));
                var fragment = PdfTextRanges.Encode(ranges);
                Assert.NotNull(fragment);
                var restored = PdfTextRanges.Read(fragment, ranges.Min(r => r.Start), ranges.Max(r => r.End));
                Assert.Equal(expected, new PdfTextSelectionLayout(document.ReadPage(1)).GetCopyText(restored));
            }
            Assert.Equal("RAMP0_INC\t1677722\tPositive ramp Two’s complement = 2³⁰\nRAMP1_INC\t1072064102\tNegative ramp Recalibrate each cycle",
                layout.GetCopyText(layout.GetDragRanges(Start("RAMP0_INC"), descriptionEnd)));
            Assert.Equal("1677722\n1072064102", layout.GetCopyText(layout.GetDragRanges(Start("1677722"), End("1072064102"))));
            Assert.Equal("RAMP0_INC\t1677722", layout.GetCopyText(layout.GetDragRanges(Start("RAMP0_INC"), End("1677722"))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void DraggingOneDiagramLabelExcludesNeighboringLabels(int column, bool reverse)
    {
        var page = DiagramLabels();
        var layout = new PdfTextSelectionLayout(page);
        var start = layout.IndexAtOffset(column * 5);
        var end = layout.IndexAtOffset(16 + column * 4 + 2) + 1;
        var ranges = reverse ? layout.GetDragRanges(end, start) : layout.GetDragRanges(start, end);
        Assert.Equal(column == 1 ? "Cold air" : "Warm air",
            PdfTextSelectionLayout.NormalizeCopy(layout.GetText(ranges)));
        Assert.Equal(7, ranges.Sum(range => range.End - range.Start));
        Assert.Equal(ranges, PdfTextRanges.Read(PdfTextRanges.Encode(ranges),
            ranges.Min(range => range.Start), ranges.Max(range => range.End)));
        var firstWord = layout.GetDragRanges(start, layout.IndexAtOffset(column * 5 + 3) + 1);
        Assert.Equal(column == 1 ? "Cold" : "Warm", layout.GetText(firstWord));
    }

    [Fact]
    public void DraggingAcrossDiagramColumnsCanStillSelectAllLabels()
    {
        var page = DiagramLabels();
        var layout = new PdfTextSelectionLayout(page);
        var ranges = layout.GetDragRanges(0, layout.Glyphs.Count);
        Assert.Equal("Warm Cold Warm air air air", PdfTextSelectionLayout.NormalizeCopy(layout.GetText(ranges)));
    }

    private static PdfPageContent DiagramLabels()
    {
        const string text = "Warm Cold Warm\r\nair air air";
        var glyphs = new List<PdfTextGlyph>();
        // Match the screenshot's proportions: neighboring labels have less
        // whitespace than a normal page gutter, and the second row is centered.
        for (var column = 0; column < 3; column++)
        {
            for (var i = 0; i < 4; i++)
                glyphs.Add(new(column * 5 + i, 1, new(0.54 + column * 0.12 + i * 0.018, 0.15, 0.017, 0.014)));
            for (var i = 0; i < 3; i++)
                glyphs.Add(new(16 + column * 4 + i, 1, new(0.558 + column * 0.12 + i * 0.011, 0.175, 0.01, 0.014)));
        }
        return new(306, 505, text, glyphs.OrderBy(glyph => glyph.Offset).ToArray());
    }

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
            Assert.Equal(ranges, layout.GetDragRanges(start, end));
            Assert.Equal(ranges, layout.GetDragRanges(end, start));
            var text = layout.GetText(ranges);
            Assert.DoesNotContain("Right", text);
            Assert.Equal("Left first line. An interesting example.", PdfTextSelectionLayout.NormalizeCopy(text));
            Assert.Equal("Left first line. An interesting example.", layout.GetTranslationText(ranges));
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
        Assert.Equal(ranges, layout.GetDragRanges(start, end));
        Assert.Equal(ranges, layout.GetDragRanges(end, start));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DottedBordersDoNotEnterMultilineSelection(bool vertical)
    {
        var left = new[] { "Left first line.", "Left second line.", "Left third line." };
        var right = new[] { "First line keeps...", "next line intact.", "Final sentence." };
        var text = string.Join("\r\n", left.Concat(right)) + "\r\n" + new string('.', 32);
        var ruleStart = text.Length - 32;
        var glyphs = new List<PdfTextGlyph>();
        var offset = 0;
        foreach (var (lines, x) in new[] { (left, 0.08), (right, 0.55) })
        {
            for (var row = 0; row < lines.Length; row++)
            {
                for (var column = 0; column < lines[row].Length; column++)
                    if (lines[row][column] != ' ')
                        glyphs.Add(new(offset + column, 1, new(x + column * 0.009, 0.1 + row * 0.025, 0.007, 0.012)));
                offset += lines[row].Length + 2;
            }
        }
        for (var i = 0; i < 32; i++)
            glyphs.Add(new(ruleStart + i, 1, new(vertical ? 0.49 : 0.08 + i * 0.025,
                vertical ? 0.08 + i * 0.003 : 0.2, 0.002, 0.002)));
        var layout = new PdfTextSelectionLayout(new(612, 792, text, glyphs));
        var startOffset = text.IndexOf(right[0], StringComparison.Ordinal);
        var endOffset = ruleStart - 2;
        var ranges = layout.GetRanges(layout.IndexAtOffset(startOffset), layout.IndexAtOffset(endOffset - 1) + 1);
        Assert.Equal(string.Join(' ', right), PdfTextSelectionLayout.NormalizeCopy(layout.GetText(ranges)));
        Assert.Equal(startOffset, ranges.Min(range => range.Start));
        Assert.Equal(endOffset, ranges.Max(range => range.End));
        Assert.DoesNotContain(layout.Glyphs, glyph => glyph.Offset >= ruleStart);
        Assert.DoesNotContain(ranges, range => range.End > ruleStart);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void TranslationJoinsWrappedLinesAndKeepsGeometricParagraphs(string newline)
    {
        var lines = new[]
        {
            "I wrote the original edition when I was a teacher at the",
            "Swan School of English, Oxford. I would like to repeat my",
            "thanks to my former colleagues and students.",
            "For the fifth edition, I would like to thank Rebecca",
            "Winthrop and Chris Capper."
        };
        var text = string.Join(newline, lines);
        var glyphs = new List<PdfTextGlyph>();
        var offset = 0;
        for (var row = 0; row < lines.Length; row++)
        {
            for (var column = 0; column < lines[row].Length; column++)
                if (lines[row][column] != ' ')
                    glyphs.Add(new(offset + column, 1,
                        new(0.08 + column * 0.009, 0.1 + row * 0.02 + (row >= 3 ? 0.025 : 0), 0.007, 0.01)));
            offset += lines[row].Length + newline.Length;
        }
        var layout = new PdfTextSelectionLayout(new(612, 792, text, glyphs));
        PdfTextRange[] whole = [new(0, text.Length)];
        Assert.Equal(text, layout.GetText(whole)); // Stored offsets and copied source stay intact.
        Assert.Equal(string.Join(' ', lines.Take(3)) + "\n\n" + string.Join(' ', lines.Skip(3)),
            layout.GetTranslationText(whole));
        var start = text.IndexOf("Swan", StringComparison.Ordinal);
        var end = text.IndexOf("For the fifth", StringComparison.Ordinal);
        Assert.Equal(string.Join(' ', lines.Skip(1).Take(2)), layout.GetTranslationText([new(start, end)]));
    }

    [Fact]
    public void TranslationWithoutGlyphsRepairsWrapsAndKeepsExplicitParagraphs()
    {
        const string text = "An inter-\r\nesting example.\r\n\r\nAnother paragraph.";
        var layout = new PdfTextSelectionLayout(new(612, 792, text, []));
        Assert.Equal("An interesting example.\n\nAnother paragraph.", layout.GetTranslationText([new(0, text.Length)]));
    }

    [Fact]
    public void TranslationKeepsExplicitParagraphsEvenWithRegularLineSpacing()
    {
        const string text = "First paragraph.\r\n\r\nSecond paragraph.";
        var glyphs = text.Select((value, offset) => (value, offset))
            .Where(item => !char.IsWhiteSpace(item.value))
            .Select(item => new PdfTextGlyph(item.offset, 1,
                new(0.08 + (item.offset < 16 ? item.offset : item.offset - 20) * 0.009,
                    item.offset < 16 ? 0.1 : 0.12, 0.007, 0.01))).ToArray();
        var layout = new PdfTextSelectionLayout(new(612, 792, text, glyphs));
        Assert.Equal("First paragraph.\n\nSecond paragraph.", layout.GetTranslationText([new(0, text.Length)]));
    }
}
