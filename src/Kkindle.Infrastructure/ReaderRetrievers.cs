using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class KeywordRetriever : IKeywordRetriever
{
    private readonly ReaderDataService _readerData;

    public KeywordRetriever(ReaderDataService readerData)
    {
        _readerData = readerData ?? throw new ArgumentNullException(nameof(readerData));
    }

    public async Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
        Guid bookId,
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var limit = NormalizeLimit(topK);
        var chunks = await _readerData.SearchBookAsync(
                bookId,
                query,
                limit,
                cancellationToken)
            .ConfigureAwait(false);
        return chunks
            .Select((chunk, index) => new ReaderRetrievalResult(
                chunk,
                chunk.Rank,
                KeywordRank: index + 1))
            .ToArray();
    }

    private static int NormalizeLimit(int limit) => Math.Clamp(limit, 1, 100);
}

public sealed class VectorRetriever : IVectorRetriever
{
    private readonly ReaderDataService _readerData;
    private readonly IEmbeddingService _embeddingService;

    public VectorRetriever(
        ReaderDataService readerData,
        IEmbeddingService embeddingService)
    {
        _readerData = readerData ?? throw new ArgumentNullException(nameof(readerData));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    }

    public async Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
        Guid bookId,
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var queryVector = await _embeddingService
            .EmbedQueryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        if (queryVector.Length == 0) return [];

        var candidates = await _readerData.GetBookChunkEmbeddingsAsync(
                bookId,
                _embeddingService.ModelId,
                _embeddingService.Dimension,
                cancellationToken)
            .ConfigureAwait(false);
        var ranked = candidates
            .Select(candidate =>
            {
                var score = CosineSimilarity(queryVector, candidate.Vector);
                return (candidate.Chunk, Score: score);
            })
            .Where(item => double.IsFinite(item.Score))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.ChapterIndex)
            .ThenBy(item => item.Chunk.ChunkIndex)
            .Take(Math.Clamp(topK, 1, 100))
            .ToArray();

        return ranked
            .Select((item, index) => new ReaderRetrievalResult(
                item.Chunk,
                item.Score,
                VectorRank: index + 1))
            .ToArray();
    }

    public static double CosineSimilarity(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return 0;
        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];
            if (!float.IsFinite(leftValue) || !float.IsFinite(rightValue)) return 0;
            dot += leftValue * rightValue;
            leftNorm += leftValue * leftValue;
            rightNorm += rightValue * rightValue;
        }

        if (leftNorm <= double.Epsilon || rightNorm <= double.Epsilon) return 0;
        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}

public sealed class RrfFusionService
{
    public const int DefaultK = 60;

    public IReadOnlyList<ReaderRetrievalResult> Fuse(
        IReadOnlyList<ReaderRetrievalResult> keywordResults,
        IReadOnlyList<ReaderRetrievalResult> vectorResults,
        int topK,
        int k = DefaultK)
    {
        ArgumentNullException.ThrowIfNull(keywordResults);
        ArgumentNullException.ThrowIfNull(vectorResults);
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));

        var fused = new Dictionary<long, FusedResult>();
        AddRanks(fused, keywordResults, keyword: true, k);
        AddRanks(fused, vectorResults, keyword: false, k);
        return fused.Values
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.ChapterIndex)
            .ThenBy(item => item.Chunk.ChunkIndex)
            .ThenBy(item => item.Chunk.Id)
            .Take(Math.Clamp(topK, 1, 100))
            .Select(item => new ReaderRetrievalResult(
                item.Chunk,
                item.Score,
                item.KeywordRank,
                item.VectorRank))
            .ToArray();
    }

    private static void AddRanks(
        IDictionary<long, FusedResult> fused,
        IReadOnlyList<ReaderRetrievalResult> results,
        bool keyword,
        int k)
    {
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var rank = keyword
                ? result.KeywordRank ?? index + 1
                : result.VectorRank ?? index + 1;
            if (!fused.TryGetValue(result.Chunk.Id, out var current))
            {
                current = new FusedResult(result.Chunk);
                fused[result.Chunk.Id] = current;
            }

            current.Score += 1d / (k + rank);
            if (keyword) current.KeywordRank ??= rank;
            else current.VectorRank ??= rank;
        }
    }

    private sealed class FusedResult(BookContentChunk chunk)
    {
        public BookContentChunk Chunk { get; } = chunk;
        public double Score { get; set; }
        public int? KeywordRank { get; set; }
        public int? VectorRank { get; set; }
    }
}

public sealed class HybridRetriever : IReaderRetriever
{
    private readonly IKeywordRetriever _keywordRetriever;
    private readonly IVectorRetriever _vectorRetriever;
    private readonly RrfFusionService _fusion;
    private readonly Action<string>? _log;

    public HybridRetriever(
        IKeywordRetriever keywordRetriever,
        IVectorRetriever vectorRetriever,
        RrfFusionService? fusion = null,
        Action<string>? log = null)
    {
        _keywordRetriever = keywordRetriever ?? throw new ArgumentNullException(nameof(keywordRetriever));
        _vectorRetriever = vectorRetriever ?? throw new ArgumentNullException(nameof(vectorRetriever));
        _fusion = fusion ?? new RrfFusionService();
        _log = log;
    }

    public async Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
        Guid bookId,
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var candidateLimit = Math.Clamp(Math.Max(topK, 16), 1, 100);
        var keywordTask = _keywordRetriever.RetrieveAsync(
            bookId,
            query,
            candidateLimit,
            cancellationToken);
        var vectorTask = _vectorRetriever.RetrieveAsync(
            bookId,
            query,
            candidateLimit,
            cancellationToken);

        IReadOnlyList<ReaderRetrievalResult> keyword = [];
        try
        {
            keyword = await keywordTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Keyword retrieval unavailable: {Limit(exception.Message, 180)}");
        }

        IReadOnlyList<ReaderRetrievalResult> vector = [];
        try
        {
            vector = await vectorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Vector search unavailable. Fallback to keyword search. {Limit(exception.Message, 180)}");
        }

        if (vector.Count == 0)
        {
            _log?.Invoke("Vector search unavailable. Fallback to keyword search.");
            return keyword.Take(Math.Clamp(topK, 1, 100)).ToArray();
        }
        if (keyword.Count == 0)
            return vector.Take(Math.Clamp(topK, 1, 100)).ToArray();

        var fused = _fusion.Fuse(keyword, vector, Math.Clamp(topK, 1, 100));
        _log?.Invoke($"Hybrid retrieval: keyword={keyword.Count}, vector={vector.Count}, fused={fused.Count}");
        return fused;
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
