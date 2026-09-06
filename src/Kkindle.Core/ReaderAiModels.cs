namespace Kkindle.Core;

/// <summary>
/// A persisted vector produced for one indexed reader chunk.
/// </summary>
public sealed record ReaderChunkEmbedding(
    long ChunkId,
    Guid BookId,
    Guid BookFileId,
    string SourceHash,
    string EmbeddingModel,
    int EmbeddingDimension,
    IReadOnlyList<float> Vector);

/// <summary>
/// One candidate returned by a reader retriever. KeywordRank and VectorRank
/// are deliberately kept separate so fusion never compares incompatible
/// BM25 and cosine-score scales directly.
/// </summary>
public sealed record ReaderRetrievalResult(
    BookContentChunk Chunk,
    double Score,
    int? KeywordRank = null,
    int? VectorRank = null);

public sealed record ReaderAiSource(
    string Id,
    BookContentChunk Chunk,
    string Content);

public sealed record ReaderAiContext(
    string Text,
    IReadOnlyList<ReaderAiSource> Sources);

public sealed record EmbeddingAvailability(
    bool IsAvailable,
    string Message,
    string? ModelPath = null,
    int? Dimension = null);

public sealed record EmbeddingIndexProgress(
    Guid BookId,
    Guid BookFileId,
    int Processed,
    int Total,
    string ModelId)
{
    public double Percentage => Total <= 0
        ? 100
        : Math.Clamp(Processed * 100d / Total, 0, 100);
}

public sealed record EmbeddingIndexResult(
    bool IsAvailable,
    int GeneratedCount,
    int RemainingCount,
    string Message);

public interface IEmbeddingService
{
    int Dimension { get; }

    string ModelId { get; }

    Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds a user query. Providers that distinguish queries from passages
    /// (for example E5) can override this to add the model's query prefix;
    /// existing providers keep the original behavior by default.
    /// </summary>
    Task<float[]> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        EmbedAsync(text, cancellationToken);

    /// <summary>
    /// Embeds book passages for indexing. Providers that distinguish queries
    /// from passages can override this to add the model's passage prefix.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default) =>
        EmbedBatchAsync(texts, cancellationToken);
}

/// <summary>
/// Optional diagnostics exposed by local or remote embedding providers. The
/// core embedding contract stays small so a provider only needs to implement
/// inference; callers can still use this capability when available.
/// </summary>
public interface IEmbeddingAvailability
{
    bool IsAvailable { get; }

    Task<EmbeddingAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default);
}

public interface IKeywordRetriever
{
    Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
        Guid bookId,
        string query,
        int topK,
        CancellationToken cancellationToken = default);
}

public interface IVectorRetriever
{
    Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
        Guid bookId,
        string query,
        int topK,
        CancellationToken cancellationToken = default);
}

public interface IReaderRetriever
{
    Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
        Guid bookId,
        string query,
        int topK,
        CancellationToken cancellationToken = default);
}
