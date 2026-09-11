using System.Text.Json.Serialization;

namespace Kkindle.Core;

public enum BookTranslationProvider
{
    Ai = 0,
    BingFree = 1,
    GoogleFree = 2
}

[Flags]
public enum BookTranslationOutputMode
{
    None = 0,
    Original = 1,
    Translated = 2,
    Bilingual = 4
}

public sealed record BookTranslationSettings
{
    public const int MaxAiRequestsPerMinute = 65_536;

    public BookTranslationProvider Provider { get; init; } = BookTranslationProvider.Ai;
    public string SourceLanguage { get; init; } = "auto";
    public string TargetLanguage { get; init; } = "zh-CN";
    public BookTranslationOutputMode OutputMode { get; init; } = BookTranslationOutputMode.Translated;
    public int AiRequestsPerMinute { get; init; }
    public string GoogleProxyAddress { get; init; } = string.Empty;
    public bool ContextMenuEnabled { get; init; } = true;

    public static BookTranslationSettings Normalize(BookTranslationSettings? settings)
    {
        settings ??= new BookTranslationSettings();
        var provider = Enum.IsDefined(settings.Provider)
            ? settings.Provider
            : BookTranslationProvider.Ai;
        var source = TranslationLanguageCatalog.NormalizeSource(settings.SourceLanguage);
        var target = TranslationLanguageCatalog.NormalizeTarget(settings.TargetLanguage);
        var output = settings.OutputMode & (
            BookTranslationOutputMode.Original
            | BookTranslationOutputMode.Translated
            | BookTranslationOutputMode.Bilingual);
        if (output == BookTranslationOutputMode.None)
            output = BookTranslationOutputMode.Translated;

        return settings with
        {
            Provider = provider,
            SourceLanguage = source,
            TargetLanguage = target,
            OutputMode = output,
            AiRequestsPerMinute = Math.Clamp(
                settings.AiRequestsPerMinute,
                0,
                MaxAiRequestsPerMinute),
            GoogleProxyAddress = (settings.GoogleProxyAddress ?? string.Empty).Trim()
        };
    }
}

public sealed record TranslationLanguageOption(
    string Code,
    string DisplayName,
    string BingCode);

public static class TranslationLanguageCatalog
{
    public static IReadOnlyList<TranslationLanguageOption> All { get; } =
    [
        new("auto", "自动检测", "auto-detect"),
        new("zh-CN", "中文（简体）", "zh-Hans"),
        new("zh-TW", "中文（繁体）", "zh-Hant"),
        new("en", "English", "en"),
        new("ja", "日本語", "ja"),
        new("ko", "한국어", "ko"),
        new("fr", "Français", "fr"),
        new("de", "Deutsch", "de"),
        new("es", "Español", "es"),
        new("it", "Italiano", "it"),
        new("pt", "Português", "pt"),
        new("ru", "Русский", "ru"),
        new("ar", "العربية", "ar"),
        new("th", "ไทย", "th"),
        new("vi", "Tiếng Việt", "vi"),
        new("id", "Bahasa Indonesia", "id"),
        new("tr", "Türkçe", "tr"),
        new("pl", "Polski", "pl"),
        new("nl", "Nederlands", "nl")
    ];

    public static TranslationLanguageOption Find(string? code)
    {
        var normalized = code?.Trim();
        return All.FirstOrDefault(option =>
                   option.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? All[0];
    }

    public static string NormalizeSource(string? code) =>
        Find(code).Code;

    public static string NormalizeTarget(string? code)
    {
        var normalized = Find(code).Code;
        return normalized == "auto" ? "zh-CN" : normalized;
    }
}

public enum BookTranslationSegmentStatus
{
    None = 0,
    Processing,
    Completed,
    Failed,
    Canceled
}

public sealed record BookTranslationSegmentProgress(
    int Index,
    string EntryName,
    string OriginalText,
    string TranslatedText,
    BookTranslationSegmentStatus Status,
    string ProcessFlow,
    string? ErrorMessage = null);

public sealed record BookTranslationProgress(
    string Stage,
    string CurrentItem,
    int ProcessedSegments,
    int TotalSegments,
    long ProcessedCharacters = 0,
    long TotalCharacters = 0,
    BookTranslationSegmentProgress? Segment = null)
{
    [JsonIgnore]
    public double Percentage => TotalSegments <= 0
        ? Stage.Contains("完成", StringComparison.Ordinal) ? 100 : 0
        : Math.Clamp(ProcessedSegments * 100d / TotalSegments, 0, 100);
}

public enum BookTranslationResumeMode
{
    Restart = 0,
    Resume
}

public sealed record BookTranslationResumeInfo(
    BookTranslationSettings Settings,
    int CompletedSegments,
    int FailedSegments,
    int TotalSegments,
    DateTimeOffset UpdatedAt)
{
    public int IncompleteSegments => Math.Max(TotalSegments - CompletedSegments, 0);
}

public sealed record BookTranslationResult(
    string SourcePath,
    string OutputDirectory,
    IReadOnlyList<string> OutputPaths,
    int SegmentCount,
    long SourceCharacters);
