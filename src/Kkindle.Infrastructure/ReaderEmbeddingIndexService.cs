using System.Diagnostics;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Builds the local vector index incrementally for one indexed book file.
/// The service never deletes a valid previous model's rows until the source
/// chunks themselves are rebuilt, so a model load failure can safely fall
/// back to keyword search.
/// </summary>
public sealed class ReaderEmbeddingIndexService
{
    private const int EmbeddingBatchSize = 16;
    private readonly ReaderDataService _readerData;
    private readonly IEmbeddingService _embeddingService;

    public ReaderEmbeddingIndexService(
        ReaderDataService readerData,
        IEmbeddingService embeddingService)
    {
        _readerData = readerData ?? throw new ArgumentNullException(nameof(readerData));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    }

    public async Task<EmbeddingIndexResult> EnsureIndexedAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        IProgress<EmbeddingIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_embeddingService is IEmbeddingAvailability availabilityService)
        {
            var availability = await availabilityService
                .CheckAvailabilityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                return new EmbeddingIndexResult(
                    false,
                    0,
                    0,
                    availability.Message);
            }
        }

        var pending = await _readerData.GetChunksNeedingEmbeddingsAsync(
                bookFileId,
                sourceHash,
                _embeddingService.ModelId,
                _embeddingService.Dimension,
                cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return new EmbeddingIndexResult(
                true,
                0,
                0,
                "Embedding 索引已是最新。" );
        }

        Debug.WriteLine(
            $"Generating embeddings: book={bookId:N}; file={bookFileId:N}; chunks={pending.Count}; model={_embeddingService.ModelId}");
        var stopwatch = Stopwatch.StartNew();
        var generated = 0;
        foreach (var batch in pending.Chunk(EmbeddingBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var texts = batch.Select(chunk => chunk.Content).ToArray();
            var vectors = await _embeddingService
                .EmbedPassagesAsync(texts, cancellationToken)
                .ConfigureAwait(false);
            if (vectors.Count != batch.Length)
                throw new InvalidDataException("Embedding 服务返回的批量数量不一致。" );

            var dimension = vectors.FirstOrDefault()?.Length ?? 0;
            if (dimension <= 0 || vectors.Any(vector => vector.Length != dimension))
                throw new InvalidDataException("Embedding 服务返回的向量维度不一致。" );

            var rows = batch
                .Select((chunk, index) => new ReaderChunkEmbedding(
                    chunk.Id,
                    chunk.BookId,
                    chunk.BookFileId,
                    chunk.SourceHash,
                    _embeddingService.ModelId,
                    dimension,
                    vectors[index]))
                .ToArray();
            await _readerData.UpsertChunkEmbeddingsAsync(
                    bookId,
                    bookFileId,
                    sourceHash,
                    _embeddingService.ModelId,
                    dimension,
                    rows,
                    cancellationToken)
                .ConfigureAwait(false);

            generated += batch.Length;
            progress?.Report(new EmbeddingIndexProgress(
                bookId,
                bookFileId,
                generated,
                pending.Count,
                _embeddingService.ModelId));
        }

        stopwatch.Stop();
        Debug.WriteLine(
            $"Embedding index completed: book={bookId:N}; generated={generated}; elapsed={stopwatch.ElapsedMilliseconds}ms");
        return new EmbeddingIndexResult(
            true,
            generated,
            Math.Max(0, pending.Count - generated),
            "Embedding 索引建立完成。" );
    }
}
