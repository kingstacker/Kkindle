using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class AiModule
{
    internal AiModule(ApplicationModuleOptions options, AppSettings settings, ReaderModule reader, IDeviceFeatureAccess features)
    {
        Settings = new AiSettingsStore(options.Paths, options.SecretProtector);
        ChatClient = new AiChatClient(features: features);
        Translation = new TranslationService(ChatClient);
        EpubTranslation = new EpubTranslationService(options.Paths, options.SecretProtector, ChatClient);
        EmbeddingModels = new EmbeddingModelDownloadService(options.Paths);
        PinyinModels = new G2PWModelDownloadService(options.Paths, proxyAddress: settings.Translation.GoogleProxyAddress);
        Embedding = new LocalEmbeddingService(options.Paths, settings.EmbeddingModelId);
        EmbeddingIndex = new ReaderEmbeddingIndexService(reader.Data, Embedding);
        Retriever = new HybridRetriever(new KeywordRetriever(reader.Data),
            new VectorRetriever(reader.Data, Embedding), log: options.RetrievalLog);
        Context = new ReaderAiContextBuilder(reader.Data);
        QueryRewrite = new QueryRewriteService(ChatClient, options.RetrievalLog);
        WebSearch = new ReaderWebSearchService();
    }

    public AiSettingsStore Settings { get; }
    public AiChatClient ChatClient { get; }
    public TranslationService Translation { get; }
    public IEpubTranslationService EpubTranslation { get; }
    public EmbeddingModelDownloadService EmbeddingModels { get; }
    public G2PWModelDownloadService PinyinModels { get; }
    public LocalEmbeddingService Embedding { get; }
    public ReaderEmbeddingIndexService EmbeddingIndex { get; }
    public IReaderRetriever Retriever { get; }
    public ReaderAiContextBuilder Context { get; }
    public QueryRewriteService QueryRewrite { get; }
    public ReaderWebSearchService WebSearch { get; }
}
