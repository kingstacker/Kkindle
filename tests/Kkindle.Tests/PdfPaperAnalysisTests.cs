using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class PdfPaperAnalysisTests
{
    [Fact]
    public void RecognizesSectionsFiguresTablesAndReferencesWithStableOffsets()
    {
        var pages = new[]
        {
            new PdfPageText(1, "A Paper Title\r\nAbstract\r\nWe study a problem.\r\n1 Introduction\r\nContext."),
            new PdfPageText(2, "2 Methods\r\nFigure 1: Model overview\r\nTable 1: Results"),
            new PdfPageText(3, "References\r\n[1] A useful paper\r\n[2] Another paper")
        };

        var analysis = PdfPaperAnalysisService.Analyze(pages);

        Assert.True(analysis.IsLikelyPaper);
        Assert.Equal(new[] { "Abstract", "Introduction", "Methods", "References" },
            analysis.Sections.Select(item => item.Title));
        Assert.Equal(new[] { PdfPaperNavigationKind.Figure, PdfPaperNavigationKind.Table,
            PdfPaperNavigationKind.Reference, PdfPaperNavigationKind.Reference },
            analysis.Elements.Select(item => item.Kind));
        Assert.Equal(new[] { 2, 2, 3, 3 }, analysis.Elements.Select(item => item.PageNumber));
        Assert.Equal("[1]", analysis.Elements[2].Label);
        Assert.True(analysis.Sections[0].StartOffset > 0);
        Assert.True(analysis.Sections[1].StartOffset > analysis.Sections[0].StartOffset);
    }

    [Fact]
    public void DetectsTwoColumnsOnlyWhenThereIsABalancedCentralGutter()
    {
        var glyphs = Enumerable.Range(0, 30)
            .Select(index => new PdfTextGlyph(index, 1, new PdfTextBounds(0.08 + index * 0.009, 0.1, 0.006, 0.01)))
            .Concat(Enumerable.Range(0, 30)
                .Select(index => new PdfTextGlyph(30 + index, 1, new PdfTextBounds(0.60 + index * 0.009, 0.1, 0.006, 0.01))))
            .ToArray();

        var columns = PdfPaperAnalysisService.DetectColumns(
            new PdfPageContent(1, 1, new string('x', glyphs.Length), glyphs));

        Assert.True(columns.IsTwoColumn);
        Assert.InRange(columns.Left.Width, 0.35, 0.49);
        Assert.InRange(columns.Right.X, 0.51, 0.70);
        Assert.False(PdfPaperAnalysisService.DetectColumns(
            new PdfPageContent(1, 1, new string('x', glyphs.Length),
                Enumerable.Range(0, glyphs.Length)
                    .Select(index => new PdfTextGlyph(
                        index,
                        1,
                        new PdfTextBounds(0.08 + index * 0.013, 0.1, 0.006, 0.01)))
                    .ToArray())).IsTwoColumn);
    }

    [Theory]
    [InlineData(0.25, 0.75)]
    [InlineData(0, 1)]
    public void PointAnnotationAnchorsRoundTrip(double x, double y)
    {
        var encoded = ReaderPdfPointAnchor.Encode(x, y);

        Assert.True(ReaderPdfPointAnchor.TryParse(encoded, out var actualX, out var actualY));
        Assert.Equal(x, actualX, 12);
        Assert.Equal(y, actualY, 12);
    }
}
