using System.Text;
using System.Text.RegularExpressions;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>Bounded, local keyword retrieval for the file selected by the mobile reader.</summary>
internal sealed class MobileAiBookRetrieval(ApplicationModules modules, Book book)
{
    private const int MaxPdfPages = 10_000;
    private const long MaxPdfCharacters = 20_000_000;
    private const int MaxPdfHits = 8;
    private const int PdfExcerptLength = 700;

    public async Task<(string Text, string Status)> RetrieveAsync(string query, CancellationToken token)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return (string.Empty, "数据恢复期间无法检索书籍。");
        // Keep this order in sync with ReaderPage.ChooseReadableFile.
        var file = new[] { "epub", "pdf", "azw3", "mobi" }
            .Select(format => book.Files.FirstOrDefault(candidate =>
                candidate.Format.Trim().TrimStart('.').Equals(format, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(candidate => candidate is not null);
        if (file is null) return (string.Empty, "没有可检索的书籍文件，仅使用当前页。");

        var path = modules.Library.Books.GetAbsoluteFilePath(file);
        if (!File.Exists(path)) return (string.Empty, "书籍文件不在本机，仅使用当前页。");
        var format = file.Format.Trim().TrimStart('.').ToLowerInvariant();
        if (format == "pdf")
            return await Task.Run(() => SearchPdf(path, query, token), token).ConfigureAwait(false);

        if (format is "azw3" or "mobi")
        {
            var sourceHash = string.IsNullOrWhiteSpace(file.Sha256)
                ? await Hashing.Sha256Async(path, token).ConfigureAwait(false) : file.Sha256;
            var converted = await modules.Reader.FormatCache.PrepareEpubAsync(path, sourceHash, format, token)
                .ConfigureAwait(false);
            path = converted.EpubPath;
        }
        var hash = string.IsNullOrWhiteSpace(file.Sha256)
            ? await Hashing.Sha256Async(path, token).ConfigureAwait(false) : file.Sha256;
        var document = await modules.Reader.Preparation.PrepareAsync(path, hash, token).ConfigureAwait(false);
        await modules.Reader.Content.EnsureIndexedAsync(book, file, document, token).ConfigureAwait(false);
        // SQLite's async operations may run synchronously, including LIKE fallback.
        return await Task.Run(async () =>
        {
            var hits = await modules.Reader.Data.SearchBookAsync(book.Id, query, 8, token, bookFileId: file.Id)
                .ConfigureAwait(false);
            if (hits.Count == 0) return (string.Empty, "全书关键词检索无匹配，仅使用当前页。");
            var results = hits.Select((chunk, index) => new ReaderRetrievalResult(chunk, 1d - index * 0.01d)).ToArray();
            var context = await modules.Ai.Context.BuildAsync(book.Id, results, 4500, 1, token).ConfigureAwait(false);
            return context.Sources.Count == 0
                ? (string.Empty, "全书关键词检索无可用正文，仅使用当前页。")
                : (context.Text, $"全书关键词检索 · {context.Sources.Count} 个原文片段");
        }, token).ConfigureAwait(false);
    }

    private static (string Text, string Status) SearchPdf(string path, string query, CancellationToken token)
    {
        var terms = GetPdfTerms(query);
        if (terms.Count == 0) return (string.Empty, "没有可用的检索词，仅使用当前页。");
        using var document = new PdfiumDocumentFactory().Open(path);
        var hits = new List<(int Page, int Score, string Text)>();
        var characters = 0L;
        var scanned = 0;
        for (var page = 1; page <= document.PageCount && page <= MaxPdfPages && characters < MaxPdfCharacters; page++)
        {
            token.ThrowIfCancellationRequested();
            var text = document.ReadPageText(page, token);
            scanned++;
            characters += text.Length;
            var bestIndex = -1;
            var score = 0;
            foreach (var term in terms)
            {
                var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                score += Math.Min(term.Length, 8);
                if (bestIndex < 0) bestIndex = index;
            }
            if (bestIndex < 0) continue;
            var start = Math.Max(0, bestIndex - PdfExcerptLength / 3);
            var length = Math.Min(PdfExcerptLength, text.Length - start);
            hits.Add((page, score, text.Substring(start, length).ReplaceLineEndings(" ").Trim()));
            if (hits.Count > MaxPdfHits)
                hits.Remove(hits.OrderBy(hit => hit.Score).ThenByDescending(hit => hit.Page).First());
        }
        var coverage = scanned < document.PageCount ? $"（已检索 {scanned}/{document.PageCount} 页）" : string.Empty;
        if (hits.Count == 0) return (string.Empty, $"PDF 关键词检索无匹配{coverage}，仅使用当前页。");
        var output = new StringBuilder();
        foreach (var hit in hits.OrderBy(hit => hit.Page))
            output.AppendLine($"[PDF 第 {hit.Page} 页]").AppendLine(hit.Text).AppendLine();
        return (output.ToString(), $"PDF 关键词检索 · {hits.Count} 页{coverage}");
    }

    private static IReadOnlyList<string> GetPdfTerms(string query)
    {
        if (query.Length > 300) query = query[..300];
        foreach (var phrase in new[] { "请根据", "请帮我", "这本书", "本书", "这一章", "本章", "当前章节", "当前", "如何", "怎么", "什么是", "为什么", "哪些", "是否", "请", "帮我", "一下", "总结", "概括", "解释", "分析", "介绍", "关于", "根据" })
            query = query.Replace(phrase, string.Empty, StringComparison.OrdinalIgnoreCase);
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(query, @"[A-Za-z0-9_\-]{3,}|[㐀-鿿]{2,}"))
        {
            var value = match.Value;
            if (value[0] < '㐀') terms.Add(value);
            else
            {
                if (value.Length >= 3) terms.Add(value.Length <= 18 ? value : value[..18]);
                for (var size = Math.Min(6, value.Length); size >= 3; size--)
                    for (var start = 0; start + size <= value.Length; start += Math.Max(1, size - 2))
                        terms.Add(value.Substring(start, size));
            }
        }
        if (terms.Count == 0 && !string.IsNullOrWhiteSpace(query)) terms.Add(query.Trim());
        return terms.OrderByDescending(term => term.Length).Take(24).ToArray();
    }
}
