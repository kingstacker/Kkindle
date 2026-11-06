namespace Kkindle.Infrastructure.Plugins;

/// <summary>Whole-book translation options chosen by the plugin page.</summary>
public sealed record PluginTranslationRequest(
    string Provider,
    string SourceLanguage,
    string TargetLanguage,
    bool Translated,
    bool Bilingual,
    int AiRequestsPerMinute,
    string GoogleProxyAddress,
    bool Resume);

/// <summary>Pinyin annotation options chosen by the plugin page.</summary>
public sealed record PluginPinyinRequest(
    string Engine,
    string OutputStyle,
    bool EnableAiReview,
    bool Resume);

/// <summary>One progress step reported to the plugin page.</summary>
public sealed record PluginTaskProgress(
    string Stage,
    string Message,
    double Percent,
    int Processed = 0,
    int Total = 0,
    bool Indeterminate = false);

public sealed record PluginPinyinStatus(bool G2pwInstalled);

/// <summary>
/// Long-running book jobs the desktop shell lends to plugins. The plugin only
/// chooses options and shows progress; EPUB rewriting, AI access and the
/// pinyin engine stay in the application.
/// </summary>
public interface IPluginBookTaskHost
{
    Task<System.Text.Json.Nodes.JsonObject> TranslateBookAsync(
        Guid bookId,
        PluginTranslationRequest request,
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken);

    Task<System.Text.Json.Nodes.JsonObject> AnnotatePinyinAsync(
        Guid bookId,
        PluginPinyinRequest request,
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken);

    PluginPinyinStatus GetPinyinStatus();

    Task DownloadPinyinModelAsync(
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken);
}
