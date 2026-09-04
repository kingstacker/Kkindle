using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderAiRetrievalTests
{
    [Fact]
    public async Task MissingOnnxModelReportsUnavailableWithoutBreakingTheProcess()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            using var embeddings = new OnnxEmbeddingService(new OnnxEmbeddingOptions
            {
                ModelDirectory = Path.Combine(root, "missing-model")
            });

            var availability = await embeddings.CheckAvailabilityAsync();

            Assert.False(availability.IsAvailable);
            Assert.Contains("未找到", availability.Message, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BuildsIncrementalVectorIndexAndExpandsNeighborsWithinBudget()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('a', 64);
            var readerData = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await readerData.InitializeAsync();
            await readerData.ReplaceBookChunksAsync(bookId, fileId, hash,
            [
                new BookContentChunkDraft(0, 0, "第一章", "text/one.xhtml", 0, 8, "前置背景内容。"),
                new BookContentChunkDraft(0, 1, "第一章", "text/one.xhtml", 8, 16, "目标事实内容。"),
                new BookContentChunkDraft(0, 2, "第一章", "text/one.xhtml", 16, 24, "后续解释内容。")
            ]);

            var embeddings = new DeterministicEmbeddingService();
            var indexer = new ReaderEmbeddingIndexService(readerData, embeddings);
            var first = await indexer.EnsureIndexedAsync(bookId, fileId, hash);
            var second = await indexer.EnsureIndexedAsync(bookId, fileId, hash);

            Assert.True(first.IsAvailable);
            Assert.Equal(3, first.GeneratedCount);
            Assert.Equal(0, second.GeneratedCount);
            Assert.Equal(1, embeddings.BatchCalls);

            var vectorRetriever = new VectorRetriever(readerData, embeddings);
            var hits = await vectorRetriever.RetrieveAsync(bookId, "目标事实", 1);
            var hit = Assert.Single(hits);
            Assert.Equal(1, hit.Chunk.ChunkIndex);
            Assert.Equal(1, hit.VectorRank);

            var context = await new ReaderAiContextBuilder(readerData).BuildAsync(
                bookId,
                hits,
                maxTokenBudget: 6000,
                neighborRadius: 1);

            Assert.Equal(3, context.Sources.Count);
            Assert.Contains("[S1]", context.Text, StringComparison.Ordinal);
            Assert.Contains("目标事实内容", context.Text, StringComparison.Ordinal);
            Assert.Contains("前置背景内容", context.Text, StringComparison.Ordinal);
            Assert.Contains("后续解释内容", context.Text, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReplacingSourceChunksRemovesStaleEmbeddings()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var readerData = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await readerData.InitializeAsync();
            await readerData.ReplaceBookChunksAsync(bookId, fileId, new string('a', 64),
            [new BookContentChunkDraft(0, 0, "第一章", "one.xhtml", 0, 6, "旧内容")]);

            var embeddings = new DeterministicEmbeddingService();
            await new ReaderEmbeddingIndexService(readerData, embeddings)
                .EnsureIndexedAsync(bookId, fileId, new string('a', 64));
            Assert.Single(await readerData.GetBookChunkEmbeddingsAsync(
                bookId,
                embeddings.ModelId,
                embeddings.Dimension));

            await readerData.ReplaceBookChunksAsync(bookId, fileId, new string('b', 64),
            [new BookContentChunkDraft(0, 0, "第一章", "one.xhtml", 0, 6, "新内容")]);

            Assert.Empty(await readerData.GetBookChunkEmbeddingsAsync(
                bookId,
                embeddings.ModelId,
                embeddings.Dimension));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void RrfFusionCombinesRanksWithoutComparingScoreScales()
    {
        var chunks = Enumerable.Range(0, 3)
            .Select(index => new BookContentChunk(
                index + 1,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new string('a', 64),
                0,
                index,
                "第一章",
                "one.xhtml",
                index * 10,
                index * 10 + 10,
                $"内容 {index}"))
            .ToArray();
        var fusion = new RrfFusionService();

        var results = fusion.Fuse(
            [
                new ReaderRetrievalResult(chunks[0], 100, KeywordRank: 1),
                new ReaderRetrievalResult(chunks[1], 1, KeywordRank: 2)
            ],
            [
                new ReaderRetrievalResult(chunks[1], 0.2, VectorRank: 1),
                new ReaderRetrievalResult(chunks[2], 0.1, VectorRank: 2)
            ],
            topK: 3);

        Assert.Equal(chunks[1].Id, results[0].Chunk.Id);
        Assert.Equal(2, results[0].KeywordRank);
        Assert.Equal(1, results[0].VectorRank);
    }

    [Fact]
    public async Task HybridRetrieverFallsBackToKeywordWhenVectorSearchFails()
    {
        var expected = new ReaderRetrievalResult(
            new BookContentChunk(
                1,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new string('a', 64),
                0,
                0,
                "第一章",
                "one.xhtml",
                0,
                4,
                "关键词命中"),
            1,
            KeywordRank: 1);
        var hybrid = new HybridRetriever(
            new StaticKeywordRetriever(expected),
            new ThrowingVectorRetriever());

        var results = await hybrid.RetrieveAsync(Guid.NewGuid(), "问题", 4);

        var actual = Assert.Single(results);
        Assert.Equal(expected.Chunk.Id, actual.Chunk.Id);
        Assert.Equal(1, actual.KeywordRank);
    }

    private sealed class DeterministicEmbeddingService : IEmbeddingService
    {
        public int Dimension => 3;
        public string ModelId => "test-embedding";
        public bool IsAvailable => true;
        public int BatchCalls { get; private set; }

        public Task<EmbeddingAvailability> CheckAvailabilityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingAvailability(
                true,
                "test",
                Dimension: Dimension));

        public Task<float[]> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Map(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(Map).ToArray());
        }

        public void Dispose()
        {
        }

        private static float[] Map(string text) =>
            text.Contains("目标", StringComparison.Ordinal)
                ? [1, 0, 0]
                : text.Contains("前置", StringComparison.Ordinal)
                    ? [0.8f, 0.2f, 0]
                    : [0, 1, 0];
    }

    private sealed class StaticKeywordRetriever(ReaderRetrievalResult result) : IKeywordRetriever
    {
        public Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
            Guid bookId,
            string query,
            int topK,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReaderRetrievalResult>>([result]);
    }

    private sealed class ThrowingVectorRetriever : IVectorRetriever
    {
        public Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(
            Guid bookId,
            string query,
            int topK,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<ReaderRetrievalResult>>(
                new InvalidOperationException("model unavailable"));
    }
}
