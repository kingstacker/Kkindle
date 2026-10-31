using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class ReaderModule
{
    internal ReaderModule(ApplicationModuleOptions options, LibraryModule library)
    {
        Data = new ReaderDataService(options.Paths);
        ProgressRecovery = new ReaderProgressRecoveryService(options.Paths, Data);
        ReadingTimeRecovery = new ReaderReadingTimeRecoveryService(options.Paths, Data);
        Content = new EpubBookContentService(Data);
        Preparation = new EpubReaderPreparationService(options.Paths);
        FormatCache = new ReaderFormatCacheService(options.Paths, library.FormatConverter);
        Footnotes = new EpubFootnoteResolver();
        TtsSettings = new TtsSettingsStore(options.Paths);
        TtsCache = new TtsCacheManager(options.TtsCacheDirectory);
    }

    public ReaderDataService Data { get; }
    public ReaderProgressRecoveryService ProgressRecovery { get; }
    public ReaderReadingTimeRecoveryService ReadingTimeRecovery { get; }
    public EpubBookContentService Content { get; }
    public EpubReaderPreparationService Preparation { get; }
    public ReaderFormatCacheService FormatCache { get; }
    public EpubFootnoteResolver Footnotes { get; }
    public TtsSettingsStore TtsSettings { get; }
    public TtsCacheManager TtsCache { get; }
}
