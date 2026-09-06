using Kkindle.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Kkindle.Infrastructure;

public sealed class PdfTextService
{
    private const int MaxIndexedPages = 10_000;
    private const int MaxIndexedCharacters = 20_000_000;

    public Task<IReadOnlyList<PdfPageText>> ExtractAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PdfPageText>>(() =>
        {
            var pages = new List<PdfPageText>();
            var characterCount = 0L;
            using var document = PdfDocument.Open(path);
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pages.Count >= MaxIndexedPages || characterCount >= MaxIndexedCharacters) break;
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                characterCount += text.Length;
                pages.Add(new PdfPageText(page.Number, text));
            }
            return pages;
        }, cancellationToken);

    public static IReadOnlyList<PdfSearchResult> Search(IReadOnlyList<PdfPageText> pages, string query, int limit = 100)
    {
        var term = query.Trim();
        if (term.Length == 0) return [];
        var requestedLimit = limit == int.MaxValue ? int.MaxValue : Math.Clamp(limit, 1, 500);
        var result = new List<PdfSearchResult>();
        foreach (var page in pages)
        {
            var offset = 0;
            while (offset < page.Text.Length)
            {
                var index = page.Text.IndexOf(term, offset, StringComparison.CurrentCultureIgnoreCase);
                if (index < 0) break;
                var start = Math.Max(0, index - 45);
                var length = Math.Min(page.Text.Length - start, term.Length + 90);
                result.Add(new PdfSearchResult(page.PageNumber, page.Text.Substring(start, length).ReplaceLineEndings(" "), index));
                if (result.Count >= requestedLimit) return result;
                offset = index + Math.Max(1, term.Length);
            }
        }
        return result;
    }
}
