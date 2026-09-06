using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed partial class EpubBookContentService
{
    private const int TargetChunkLength = 1000;
    private const int MinimumChunkLength = 620;
    private const int ChunkOverlap = 160;
    private const long MaxChapterSourceBytes = 64L * 1024 * 1024;
    private const int MaxChapterTextCharacters = 20_000_000;
    private readonly ReaderDataService _readerData;

    public EpubBookContentService(ReaderDataService readerData)
    {
        _readerData = readerData;
    }

    public async Task<int> EnsureIndexedAsync(
        Book book,
        BookFile file,
        EpubReaderDocument document,
        CancellationToken cancellationToken = default)
    {
        if (await _readerData.IsIndexCurrentAsync(file.Id, file.Sha256, cancellationToken))
            return 0;

        return await _readerData.ReplaceBookChunksStreamingAsync(
            book.Id,
            file.Id,
            file.Sha256,
            EnumerateChunkDraftsAsync(document, cancellationToken),
            cancellationToken);
    }

    private async IAsyncEnumerable<BookContentChunkDraft> EnumerateChunkDraftsAsync(
        EpubReaderDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var chapterIndex = 0; chapterIndex < document.Chapters.Count; chapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapterPath = Path.GetFullPath(document.Chapters[chapterIndex]);
            EnsureContainedPath(document.RootPath, chapterPath);
            var relativePath = Path.GetRelativePath(document.RootPath, chapterPath).Replace('\\', '/');
            var chapterTitle = document.Navigation.FirstOrDefault(item => item.ChapterIndex == chapterIndex)?.Title
                ?? $"第 {chapterIndex + 1} 章";
            var text = await ExtractPlainTextAsync(chapterPath, cancellationToken);
            foreach (var chunk in CreateChunks(text, chapterIndex, chapterTitle, relativePath))
                yield return chunk;
        }
    }

    internal static IReadOnlyList<BookContentChunkDraft> CreateChunks(
        string text,
        int chapterIndex,
        string chapterTitle,
        string chapterPath)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var chunks = new List<BookContentChunkDraft>();
        var start = 0;
        var chunkIndex = 0;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length) break;

            var idealEnd = Math.Min(text.Length, start + TargetChunkLength);
            var end = FindChunkBoundary(text, start, idealEnd);
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end <= start) end = Math.Min(text.Length, start + TargetChunkLength);

            var content = text[start..end].Trim();
            if (content.Length > 0)
            {
                chunks.Add(new BookContentChunkDraft(
                    chapterIndex,
                    chunkIndex++,
                    chapterTitle,
                    chapterPath,
                    start,
                    end,
                    content));
            }

            if (end >= text.Length) break;
            var nextStart = Math.Max(start + 1, end - ChunkOverlap);
            start = nextStart;
        }
        return chunks;
    }

    public static async Task<string> ExtractPlainTextAsync(
        string chapterPath,
        CancellationToken cancellationToken = default)
    {
        var chapterInfo = new FileInfo(chapterPath);
        if (!chapterInfo.Exists)
            throw new FileNotFoundException("EPUB 章节文件不存在。", chapterPath);
        if (chapterInfo.Length > MaxChapterSourceBytes)
            throw new InvalidDataException("EPUB 单个章节过大，已停止建立全文索引。");

        try
        {
            await using var stream = new FileStream(chapterPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var document = await XDocument.LoadAsync(stream, LoadOptions.PreserveWhitespace, cancellationToken);
            var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body")
                ?? document.Root;
            if (body is null) return string.Empty;

            var builder = new StringBuilder();
            AppendElementText(body, builder);
            return NormalizeExtractedText(builder.ToString());
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidDataException)
        {
            var html = await File.ReadAllTextAsync(chapterPath, cancellationToken);
            html = HiddenContentRegex().Replace(html, " ");
            html = BreakRegex().Replace(html, "\n");
            html = TagRegex().Replace(html, " ");
            return NormalizeExtractedText(WebUtility.HtmlDecode(html));
        }
    }

    private static void AppendElementText(XElement element, StringBuilder builder)
    {
        if (builder.Length >= MaxChapterTextCharacters) return;
        var name = element.Name.LocalName.ToLowerInvariant();
        if (name is "script" or "style" or "noscript" or "svg" or "math") return;
        var isBlock = BlockElements.Contains(name);
        if (isBlock && builder.Length > 0) AppendBounded(builder, "\n");

        if (name == "br") AppendBounded(builder, "\n");
        else if (name == "img")
        {
            var alt = element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("alt", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(alt))
            {
                AppendBounded(builder, " ");
                AppendBounded(builder, alt);
                AppendBounded(builder, " ");
            }
        }
        else
        {
            foreach (var node in element.Nodes())
            {
                if (node is XText text) AppendBounded(builder, text.Value);
                else if (node is XElement child) AppendElementText(child, builder);
            }
        }

        if (isBlock) AppendBounded(builder, "\n");
    }

    private static void AppendBounded(StringBuilder builder, string value)
    {
        var remaining = MaxChapterTextCharacters - builder.Length;
        if (remaining <= 0 || value.Length == 0) return;
        builder.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
    }

    private static string NormalizeExtractedText(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Replace('\u00A0', ' ');
        var lines = decoded
            .Split(['\r', '\n'])
            .Select(line => InlineWhitespaceRegex().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n\n", lines);
    }

    private static int FindChunkBoundary(string text, int start, int idealEnd)
    {
        if (idealEnd >= text.Length) return text.Length;
        var minimum = Math.Min(idealEnd, start + MinimumChunkLength);
        for (var index = idealEnd; index >= minimum; index--)
            if (IsBoundary(text[index - 1])) return index;

        var maximum = Math.Min(text.Length, idealEnd + 220);
        for (var index = idealEnd; index < maximum; index++)
            if (IsBoundary(text[index])) return index + 1;
        return idealEnd;
    }

    private static bool IsBoundary(char value) => value is '\n' or '。' or '！' or '？' or '；' or '!' or '?' or ';' or '.';

    private static void EnsureContainedPath(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("章节路径越出了 EPUB 缓存目录。");
    }

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt", "figcaption",
        "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "li", "main", "nav",
        "ol", "p", "pre", "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul"
    };

    [GeneratedRegex(@"<(script|style|noscript|svg|math)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenContentRegex();

    [GeneratedRegex(@"<(br\s*/?|/p|/div|/li|/h[1-6]|/section|/tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[\t\f\v ]+")]
    private static partial Regex InlineWhitespaceRegex();
}
