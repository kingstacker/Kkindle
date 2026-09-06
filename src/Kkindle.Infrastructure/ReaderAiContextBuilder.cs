using System.Text;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Expands retrieval hits to nearby chunks, removes overlap introduced by the
/// EPUB chunker, and renders stable source IDs for the answer model.
/// </summary>
public sealed class ReaderAiContextBuilder
{
    public const int DefaultMaxTokenBudget = 6000;
    private readonly ReaderDataService _readerData;

    public ReaderAiContextBuilder(ReaderDataService readerData)
    {
        _readerData = readerData ?? throw new ArgumentNullException(nameof(readerData));
    }

    /// <summary>Spread a bounded overview across chapters and their beginning,
    /// middle and end, rather than spending the entire budget on early chapters.</summary>
    public static ReaderAiContext BuildOverview(IReadOnlyList<BookContentChunk> chunks, int budget = DefaultMaxTokenBudget)
    {
        var chapters = chunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk.Content))
            .GroupBy(chunk => (chunk.BookFileId, chunk.ChapterIndex))
            .OrderBy(group => group.Key.ChapterIndex)
            .Select(group => group.OrderBy(chunk => chunk.StartOffset).ToArray()).ToArray();
        if (chapters.Length == 0) return new ReaderAiContext(string.Empty, []);
        var sourceLimit = Math.Min(24, Math.Max(1, (Math.Clamp(budget, 512, 16000) - 128) / 128));
        var sampledChapters = SampleEvenly(chapters, sourceLimit);
        var perChapter = Math.Max(1, sourceLimit / sampledChapters.Count);
        var sampled = sampledChapters.SelectMany(chapter => SampleEvenly(chapter, perChapter)).ToArray();
        var allowance = Math.Max(1, (Math.Clamp(budget, 512, 16000) - 128) / sampled.Length - 48);
        var sources = sampled.Select((chunk, index) => new ReaderAiSource(
            $"S{index + 1}", chunk, TruncateToTokenBudget(NormalizeContent(chunk.Content), allowance))).ToArray();
        var text = new StringBuilder()
            .AppendLine($"概览范围：抽样覆盖 {sampledChapters.Count}/{chapters.Length} 个章节，共 {sources.Length}/{chunks.Count} 个片段。")
            .AppendLine("这是原文抽样，不是全文精读。请在回答中说明覆盖范围；未提供的内容不能推断为书中结论。");
        foreach (var source in sources)
            text.AppendLine($"[{source.Id}] chapter: {source.Chunk.ChapterTitle}")
                .AppendLine($"location: chapter={source.Chunk.ChapterIndex + 1}; offset={source.Chunk.StartOffset}")
                .AppendLine("content:").AppendLine(source.Content);
        return new ReaderAiContext(text.ToString(), sources);
    }

    public static IReadOnlyList<T> SampleEvenly<T>(IReadOnlyList<T> items, int limit)
    {
        if (limit <= 0 || items.Count == 0) return [];
        if (items.Count <= limit) return items.ToArray();
        if (limit == 1) return [items[items.Count / 2]];
        return Enumerable.Range(0, limit)
            .Select(index => items[(int)((long)index * (items.Count - 1) / (limit - 1))]).ToArray();
    }

    public async Task<ReaderAiContext> BuildAsync(
        Guid bookId,
        IReadOnlyList<ReaderRetrievalResult> retrievalResults,
        int maxTokenBudget = DefaultMaxTokenBudget,
        int neighborRadius = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retrievalResults);
        maxTokenBudget = Math.Clamp(maxTokenBudget, 512, 16000);
        neighborRadius = Math.Clamp(neighborRadius, 0, 3);
        if (retrievalResults.Count == 0)
            return new ReaderAiContext(string.Empty, []);

        var allChunks = await _readerData
            .GetBookChunksAsync(bookId, cancellationToken)
            .ConfigureAwait(false);
        var byId = allChunks.ToDictionary(chunk => chunk.Id);
        var groups = allChunks
            .GroupBy(chunk => (chunk.BookFileId, chunk.ChapterIndex))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(chunk => chunk.ChunkIndex).ThenBy(chunk => chunk.Id).ToArray());

        var candidates = new Dictionary<long, Candidate>();
        foreach (var result in retrievalResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.ContainsKey(result.Chunk.Id)) continue;

            AddCandidate(candidates, result.Chunk, result.Score, isHit: true);
            if (!groups.TryGetValue((result.Chunk.BookFileId, result.Chunk.ChapterIndex), out var group))
                continue;

            var position = Array.FindIndex(group, chunk => chunk.Id == result.Chunk.Id);
            if (position < 0) continue;
            for (var delta = -neighborRadius; delta <= neighborRadius; delta++)
            {
                if (delta == 0) continue;
                var neighbor = position + delta;
                if (neighbor < 0 || neighbor >= group.Length) continue;
                AddCandidate(
                    candidates,
                    group[neighbor],
                    result.Score * 0.5,
                    isHit: false);
            }
        }

        var selected = SelectWithinBudget(candidates.Values, maxTokenBudget);
        if (selected.Count == 0)
            return new ReaderAiContext(string.Empty, []);

        var ordered = selected
            .OrderBy(item => item.Chunk.BookFileId)
            .ThenBy(item => item.Chunk.ChapterIndex)
            .ThenBy(item => item.Chunk.StartOffset)
            .ThenBy(item => item.Chunk.Id)
            .ToArray();
        var sources = new List<ReaderAiSource>(ordered.Length);
        var rendered = new StringBuilder();
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var previousByGroup = new Dictionary<(Guid FileId, int ChapterIndex), BookContentChunk>();
        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = RemoveChunkOverlap(candidate.Chunk, previousByGroup);
            content = NormalizeContent(content);
            if (content.Length == 0) continue;

            var contentKey = CollapseWhitespace(content);
            if (!seenContent.Add(contentKey)) continue;

            var sourceId = $"S{sources.Count + 1}";
            var source = new ReaderAiSource(sourceId, candidate.Chunk, content);
            sources.Add(source);
            rendered.Append('[').Append(sourceId).AppendLine("]");
            rendered.Append("chapter: ").AppendLine(candidate.Chunk.ChapterTitle);
            rendered.Append("location: chapter=")
                .Append(candidate.Chunk.ChapterIndex + 1)
                .Append("; path=")
                .Append(candidate.Chunk.ChapterPath)
                .Append("; offset=")
                .Append(candidate.Chunk.StartOffset)
                .Append('-')
                .AppendLine(candidate.Chunk.EndOffset.ToString());
            rendered.AppendLine("content:");
            rendered.AppendLine(content);
            rendered.AppendLine();

            previousByGroup[(candidate.Chunk.BookFileId, candidate.Chunk.ChapterIndex)] = candidate.Chunk;
        }

        return new ReaderAiContext(rendered.ToString().Trim(), sources);
    }

    private static void AddCandidate(
        IDictionary<long, Candidate> candidates,
        BookContentChunk chunk,
        double score,
        bool isHit)
    {
        if (!candidates.TryGetValue(chunk.Id, out var existing)
            || (isHit && !existing.IsHit)
            || score > existing.Score)
        {
            candidates[chunk.Id] = new Candidate(chunk, score, isHit || existing?.IsHit == true);
        }
    }

    private static IReadOnlyList<Candidate> SelectWithinBudget(
        IEnumerable<Candidate> candidates,
        int maxTokenBudget)
    {
        var selected = new List<Candidate>();
        var used = 0;
        foreach (var candidate in candidates
                     .OrderByDescending(item => item.IsHit)
                     .ThenByDescending(item => item.Score)
                     .ThenBy(item => item.Chunk.ChapterIndex)
                     .ThenBy(item => item.Chunk.ChunkIndex))
        {
            var content = NormalizeContent(candidate.Chunk.Content);
            if (content.Length == 0) continue;
            var cost = EstimateTokens(content) + 32;
            if (used + cost <= maxTokenBudget)
            {
                selected.Add(candidate);
                used += cost;
                continue;
            }

            var remaining = maxTokenBudget - used - 32;
            if (remaining <= 32) continue;
            var truncated = TruncateToTokenBudget(content, remaining);
            if (truncated.Length == 0) continue;
            selected.Add(candidate with { Chunk = candidate.Chunk with { Content = truncated } });
            break;
        }
        return selected;
    }

    private static string RemoveChunkOverlap(
        BookContentChunk chunk,
        IReadOnlyDictionary<(Guid FileId, int ChapterIndex), BookContentChunk> previousByGroup)
    {
        var content = chunk.Content ?? string.Empty;
        if (!previousByGroup.TryGetValue((chunk.BookFileId, chunk.ChapterIndex), out var previous))
            return content;

        var overlap = previous.EndOffset - chunk.StartOffset;
        if (overlap <= 0 || overlap >= content.Length) return content;
        return content[overlap..];
    }

    private static string TruncateToTokenBudget(string content, int budget)
    {
        var maximumLength = Math.Min(content.Length, Math.Max(0, budget));
        if (maximumLength <= 0) return string.Empty;
        var result = content[..maximumLength];
        var boundary = result.LastIndexOfAny(['。', '！', '？', '；', '.', '!', '?', ';', '\n']);
        if (boundary >= Math.Max(80, maximumLength / 2))
            result = result[..(boundary + 1)];
        return result.TrimEnd() + (result.Length < content.Length ? "…" : string.Empty);
    }

    private static string NormalizeContent(string? value) =>
        (value ?? string.Empty).ReplaceLineEndings("\n").Trim();

    private static string CollapseWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static int EstimateTokens(string value)
    {
        var tokens = 0;
        var asciiRun = 0;
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9')
            {
                asciiRun++;
                continue;
            }

            if (asciiRun > 0)
            {
                tokens += Math.Max(1, (asciiRun + 3) / 4);
                asciiRun = 0;
            }
            if (!char.IsWhiteSpace(character)) tokens++;
        }
        if (asciiRun > 0) tokens += Math.Max(1, (asciiRun + 3) / 4);
        return tokens;
    }

    private sealed record Candidate(
        BookContentChunk Chunk,
        double Score,
        bool IsHit);
}
