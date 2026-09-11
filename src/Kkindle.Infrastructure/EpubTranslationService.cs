using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Translates EPUB XHTML while keeping the archive's images, stylesheets and
/// navigation files intact. The web translators intentionally live here, next
/// to the existing AI client, so the UI only needs one EPUB-level contract.
/// </summary>
public sealed class EpubTranslationService : IEpubTranslationService
{
    private const string BingTranslatorUrl = "https://www.bing.com/translator";
    private const string GoogleTranslatorUrl = "https://translate.googleapis.com/translate_a/single";
    private const string GoogleLegacyTranslatorUrl = "https://clients5.google.com/translate_a/t";
    private const int GoogleRequestCharacterLimit = 1_400;
    private const int BingRequestCharacterLimit = 950;
    private const int AiRequestCharacterLimit = 5_500;
    private const int MaxArchiveEntryBytes = 64 * 1024 * 1024;
    private const int MaxRetryCount = 2;
    private const int TranslationCacheVersion = 2;
    private const string TranslationCacheFileName = ".kkindle-translation-cache.jsonl";
    private static readonly TimeSpan BingSessionLifetime = TimeSpan.FromMinutes(25);
    private static readonly JsonSerializerOptions TranslationCacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex BingIgPattern = new(
        @"window\._G\s*=\s*\{.*?\bIG\s*:\s*""(?<ig>[^""\r\n]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BingFallbackIgPattern = new(
        @"[""']IG[""']\s*:\s*[""'](?<ig>[^""'\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BingIidPattern = new(
        @"data-iid\s*=\s*[""'](?<iid>translator\.[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BingAbusePattern = new(
        @"params_AbusePreventionHelper\s*=\s*\[\s*(?<key>\d+)\s*,\s*""(?<token>[^""\r\n]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SegmentMarkerPattern = new(
        @"__KKINDLE_SEG_(?<id>\d{1,6})__",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> TranslatableBlockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt",
        "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "li", "main", "nav", "ol", "p", "pre", "section", "table", "tbody", "td", "tfoot",
        "th", "thead", "tr", "ul", "body"
    };

    private readonly AppPaths _paths;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatClient _aiChatClient;
    private readonly bool _ownsAiChatClient;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _bingSessionGate = new(1, 1);
    private readonly AiRequestsPerMinuteLimiter _aiRequestsPerMinuteLimiter = new();
    private readonly object _googleProxyClientsGate = new();
    private readonly Dictionary<string, HttpClient> _googleProxyClients = new(StringComparer.OrdinalIgnoreCase);
    private BingSession? _bingSession;
    private bool _disposed;

    public EpubTranslationService(
        AppPaths paths,
        ISecretProtector secretProtector,
        AiChatClient? aiChatClient = null,
        HttpMessageHandler? httpHandler = null)
    {
        _paths = paths;
        _aiSettingsStore = new AiSettingsStore(paths, secretProtector);
        _aiChatClient = aiChatClient ?? new AiChatClient();
        _ownsAiChatClient = aiChatClient is null;
        if (httpHandler is null)
        {
            var defaultHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            _httpClient = new HttpClient(defaultHandler, disposeHandler: true);
        }
        else
        {
            _httpClient = new HttpClient(httpHandler, disposeHandler: true);
        }
        ConfigureHttpClient(_httpClient);
    }

    public Task<BookTranslationResumeInfo?> FindResumeAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        CancellationToken cancellationToken = default) => Task.Run(
            () => FindResumeCoreAsync(epubPath, outputDirectory, settings, cancellationToken),
            cancellationToken);

    private async Task<BookTranslationResumeInfo?> FindResumeCoreAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sourcePath = Path.GetFullPath(epubPath);
        if (!File.Exists(sourcePath)) return null;
        var normalizedSettings = BookTranslationSettings.Normalize(settings);
        if (!normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            && !normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual))
            return null;

        var cachePath = GetTranslationCachePath(outputDirectory);
        if (!File.Exists(cachePath)) return null;

        try
        {
            var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
            var snapshot = await TryLoadTranslationCacheAsync(
                cachePath,
                sourcePath,
                sourceHash,
                normalizedSettings,
                cancellationToken);
            if (snapshot is null) return null;

            var totalSegments = GetCachedTotalSegments(snapshot);
            var completedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == BookTranslationSegmentStatus.Completed
                && !string.IsNullOrWhiteSpace(item.TranslatedText));
            var failedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == BookTranslationSegmentStatus.Failed);
            return new BookTranslationResumeInfo(
                snapshot.Header.Settings,
                completedSegments,
                failedSegments,
                totalSegments,
                snapshot.UpdatedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A truncated or old cache should never prevent a fresh
            // translation. Keep it on disk so the next fresh run can replace
            // it atomically when it starts.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to inspect translation cache: {exception.Message}");
            return null;
        }
    }

    public Task<BookTranslationResult> TranslateAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        BookTranslationResumeMode resumeMode = BookTranslationResumeMode.Restart) => Task.Run(
            () => TranslateCoreAsync(epubPath, outputDirectory, settings, progress, cancellationToken, resumeMode),
            cancellationToken);

    private async Task<BookTranslationResult> TranslateCoreAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress,
        CancellationToken cancellationToken,
        BookTranslationResumeMode resumeMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sourcePath = Path.GetFullPath(epubPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("待翻译的 EPUB 文件不存在。", sourcePath);
        if (!string.Equals(Path.GetExtension(sourcePath), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("书籍翻译目前只支持 EPUB 文件。 ");

        var normalizedSettings = BookTranslationSettings.Normalize(settings);
        var targetDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(targetDirectory);
        var outputPaths = new List<string>();

        Report(progress, new BookTranslationProgress(
            "正在准备",
            Path.GetFileName(sourcePath),
            0,
            0));

        if (normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Original))
        {
            var originalPath = CreateOutputPath(targetDirectory, sourcePath, "原文");
            File.Copy(sourcePath, originalPath, overwrite: true);
            outputPaths.Add(originalPath);
        }

        var needsTranslatedArchive = normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            || normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual);
        if (!needsTranslatedArchive)
        {
            Report(progress, new BookTranslationProgress(
                "完成",
                "原文件已复制",
                0,
                0));
            return new BookTranslationResult(sourcePath, targetDirectory, outputPaths, 0, 0);
        }

        var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
        var cachePath = GetTranslationCachePath(targetDirectory);
        var resumeSnapshot = resumeMode == BookTranslationResumeMode.Resume
            ? await TryLoadTranslationCacheAsync(
                cachePath,
                sourcePath,
                sourceHash,
                normalizedSettings,
                cancellationToken)
            : null;
        TranslationCacheWriter? cacheWriter = null;
        var translationCompleted = false;
        try
        {
            var pages = await ReadTranslationPagesAsync(sourcePath, progress, cancellationToken);
            var segments = pages
                .SelectMany(page => page.Segments)
                .Select((segment, index) => segment with { Index = index })
                .ToArray();
            var totalCharacters = segments.Sum(segment => (long)segment.Text.Length);
            cacheWriter = new TranslationCacheWriter(
                cachePath,
                new TranslationCacheHeader(
                    "header",
                    TranslationCacheVersion,
                    sourcePath,
                    sourceHash,
                    normalizedSettings,
                    segments.Length,
                    DateTimeOffset.UtcNow),
                append: resumeSnapshot is not null);
            Report(progress, new BookTranslationProgress(
                "正在翻译",
                $"共发现 {segments.Length} 个文本段",
                0,
                segments.Length,
                0,
                totalCharacters));

            var translations = await TranslateSegmentsAsync(
                segments,
                normalizedSettings,
                progress,
                totalCharacters,
                cancellationToken,
                resumeSnapshot?.Segments,
                cacheWriter,
                new TranslationRequestContext(normalizedSettings, resumeSnapshot?.Parts, resumeSnapshot?.SplitPlans, cacheWriter));

            var renderedPages = new Dictionary<BookTranslationOutputMode, IReadOnlyDictionary<string, byte[]>>();
            if (normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Translated))
            {
                renderedPages[BookTranslationOutputMode.Translated] = RenderPages(
                    pages,
                    translations,
                    BookTranslationOutputMode.Translated,
                    normalizedSettings.TargetLanguage,
                    cancellationToken);
            }

            if (normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual))
            {
                renderedPages[BookTranslationOutputMode.Bilingual] = RenderPages(
                    pages,
                    translations,
                    BookTranslationOutputMode.Bilingual,
                    normalizedSettings.TargetLanguage,
                    cancellationToken);
            }

            var modesToWrite = new[]
            {
                BookTranslationOutputMode.Translated,
                BookTranslationOutputMode.Bilingual
            };
            foreach (var mode in modesToWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!renderedPages.TryGetValue(mode, out var pageBytes)) continue;

                Report(progress, new BookTranslationProgress(
                    "正在生成 EPUB",
                    GetOutputModeLabel(mode),
                    segments.Length,
                    segments.Length,
                    totalCharacters,
                    totalCharacters));

                var suffix = mode == BookTranslationOutputMode.Bilingual ? "双语版" : "单译版";
                var destination = CreateOutputPath(targetDirectory, sourcePath, suffix, '_');
                var packageEntries = await RenderPackageEntriesAsync(
                    sourcePath,
                    mode,
                    normalizedSettings.TargetLanguage,
                    cancellationToken);
                await WriteArchiveAsync(
                    sourcePath,
                    destination,
                    pageBytes,
                    packageEntries,
                    cancellationToken);
                outputPaths.Add(destination);
            }

            Report(progress, new BookTranslationProgress(
                "完成",
                $"已生成 {outputPaths.Count} 个文件",
                segments.Length,
                segments.Length,
                totalCharacters,
                totalCharacters));
            translationCompleted = true;
            return new BookTranslationResult(
                sourcePath,
                targetDirectory,
                outputPaths,
                segments.Length,
                totalCharacters);
        }
        finally
        {
            cacheWriter?.Dispose();
            if (translationCompleted)
                TryDeleteTranslationCache(cachePath);
        }
    }

    private async Task<IReadOnlyList<TranslationPage>> ReadTranslationPagesAsync(
        string sourcePath,
        IProgress<BookTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<TranslationPage>();
        using var archive = ZipFile.OpenRead(sourcePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isNcx = Path.GetExtension(entry.FullName).Equals(".ncx", StringComparison.OrdinalIgnoreCase);
            if (entry.Length > MaxArchiveEntryBytes || (!IsXhtmlEntry(entry.FullName) && !isNcx)) continue;
            EnsureSafeArchiveEntryName(entry.FullName);

            var markup = await ReadArchiveTextAsync(entry, cancellationToken);
            if (!TryParseDocument(markup, out var document)) continue;
            var segments = ExtractSegments(document, entry.FullName);
            pages.Add(new TranslationPage(
                entry.FullName,
                markup,
                segments,
                IsTocNavigationDocument(document),
                isNcx));
            Report(progress, new BookTranslationProgress(
                "正在扫描 EPUB",
                entry.FullName,
                0,
                0));
        }

        if (pages.Count == 0 || pages.All(page => page.Segments.Count == 0))
            throw new InvalidDataException("EPUB 中没有找到可翻译的正文。 ");
        return pages;
    }

    private async Task<Dictionary<string, string>> TranslateSegmentsAsync(
        IReadOnlyList<TranslationSegment> segments,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress,
        long totalCharacters,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<int, TranslationCacheSegmentLine>? resumedSegments,
        TranslationCacheWriter cacheWriter,
        TranslationRequestContext requestContext)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var processed = 0;
        long processedCharacters = 0;
        var index = 0;
        var batchingEnabled = true;

        bool TryGetResumed(TranslationSegment segment, out string value)
        {
            value = string.Empty;
            if (resumedSegments?.TryGetValue(segment.Index, out var resumed) != true
                || resumed is null
                || resumed.Status != BookTranslationSegmentStatus.Completed
                || string.IsNullOrWhiteSpace(resumed.TranslatedText)
                || resumed.OriginalText != segment.Text
                || resumed.RequestText != segment.Markup.RequestText
                || !segment.Markup.IsValidTranslation(resumed.TranslatedText)) return false;
            value = resumed.TranslatedText;
            return true;
        }

        void ReportSegmentState(
            IProgress<BookTranslationProgress>? currentProgress,
            TranslationSegment segment,
            int currentProcessed,
            int total,
            long currentProcessedCharacters,
            long currentTotalCharacters,
            BookTranslationSegmentStatus status,
            string translatedText,
            string processFlow,
            string? errorMessage = null) => ReportSegmentStateCore(
                currentProgress,
                segment,
                currentProcessed,
                total,
                currentProcessedCharacters,
                currentTotalCharacters,
                status,
                translatedText,
                processFlow,
                errorMessage,
                cacheWriter);

        while (index < segments.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = segments[index];
            var cacheKey = CreateTranslationCacheKey(settings, current.Markup.RequestText);
            if (TryGetResumed(current, out var resumedText))
            {
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Processing,
                    string.Empty,
                    "读取原文 → 恢复本地缓存");
                cache[cacheKey] = resumedText;
                translations[current.Key] = resumedText;
                index++;
                processed++;
                processedCharacters += current.Text.Length;
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Completed,
                    resumedText,
                    "读取原文 → 恢复本地缓存 → 写入译文");
                continue;
            }

            if (cache.TryGetValue(cacheKey, out var cached))
            {
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Processing,
                    string.Empty,
                    "读取原文 → 命中本地缓存");
                translations[current.Key] = cached;
                index++;
                processed++;
                processedCharacters += current.Text.Length;
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Completed,
                    cached,
                    "读取原文 → 命中本地缓存 → 写入译文");
                continue;
            }

            var batchLimit = GetBatchCharacterLimit(settings.Provider);
            var batch = new List<TranslationSegment>();
            var batchLength = 0;
            while (batchingEnabled && index + batch.Count < segments.Count)
            {
                var candidate = segments[index + batch.Count];
                var candidateCacheKey = CreateTranslationCacheKey(settings, candidate.Markup.RequestText);
                if (cache.ContainsKey(candidateCacheKey)) break;
                if (TryGetResumed(candidate, out _)) break;
                if (candidate.Markup.RequestText.Contains("__KKINDLE_SEG_", StringComparison.Ordinal)) break;
                var candidateLength = candidate.Markup.RequestText.Length + 2 * CreateMarker(batch.Count).Length;
                var separatorLength = batch.Count == 0 ? 0 : Environment.NewLine.Length;
                if (batchLength + separatorLength + candidateLength > batchLimit)
                    break;
                batch.Add(candidate);
                batchLength += separatorLength + candidateLength;
            }

            if (batch.Count > 1)
            {
                foreach (var segment in batch)
                {
                    ReportSegmentState(
                        progress,
                        segment,
                        processed,
                        segments.Count,
                        processedCharacters,
                        totalCharacters,
                        BookTranslationSegmentStatus.Processing,
                        string.Empty,
                        "读取原文 → 批量请求翻译");
                }

                var markerPayload = string.Join(
                    Environment.NewLine,
                    batch.Select((segment, offset) =>
                        $"{CreateMarker(offset)}{segment.Markup.RequestText}{CreateMarker(offset)}"));
                string batchResult;
                try
                {
                    batchResult = await TranslateTextWithRetryAsync(
                        markerPayload,
                        requestContext,
                        cancellationToken,
                        cacheParts: false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var segment in batch)
                    {
                        ReportSegmentState(
                            progress,
                            segment,
                            processed,
                            segments.Count,
                            processedCharacters,
                            totalCharacters,
                            BookTranslationSegmentStatus.Failed,
                            string.Empty,
                            "读取原文 → 批量请求翻译 → 失败",
                            exception.Message);
                    }

                    throw;
                }

                if (TryReadMarkedTranslations(batchResult, batch.Count, out var markedTranslations)
                    && batch.Select((segment, offset) => segment.Markup.IsValidTranslation(markedTranslations[offset])).All(valid => valid))
                {
                    for (var offset = 0; offset < batch.Count; offset++)
                    {
                        var segment = batch[offset];
                        var value = markedTranslations[offset];
                        var key = CreateTranslationCacheKey(settings, segment.Markup.RequestText);
                        cache[key] = value;
                        translations[segment.Key] = value;
                        processed++;
                        processedCharacters += segment.Text.Length;
                        ReportSegmentState(
                            progress,
                            segment,
                            processed,
                            segments.Count,
                            processedCharacters,
                            totalCharacters,
                            BookTranslationSegmentStatus.Completed,
                            value,
                            "读取原文 → 批量请求翻译 → 接收译文 → 写入译文");
                    }

                    index += batch.Count;
                    continue;
                }

                batchingEnabled = false;
                foreach (var segment in batch)
                {
                    ReportSegmentState(
                        progress,
                        segment,
                        processed,
                        segments.Count,
                        processedCharacters,
                        totalCharacters,
                        BookTranslationSegmentStatus.Processing,
                        string.Empty,
                        "批量结果校验失败 → 改为逐段翻译");
                }
            }

            // Some web translators normalize or remove the markers. Falling
            // back to one segment at a time keeps the output correct even when
            // a provider changes its response format.
            ReportSegmentState(
                progress,
                current,
                processed,
                segments.Count,
                processedCharacters,
                totalCharacters,
                BookTranslationSegmentStatus.Processing,
                string.Empty,
                "读取原文 → 请求翻译服务");

            string translated;
            try
            {
                translated = await TranslateSegmentAsync(
                    current,
                    requestContext,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Failed,
                    string.Empty,
                    "读取原文 → 请求翻译服务 → 失败",
                    exception.Message);
                throw;
            }

            cache[cacheKey] = translated;
            translations[current.Key] = translated;
            processed++;
            processedCharacters += current.Text.Length;
            ReportSegmentState(
                progress,
                current,
                processed,
                segments.Count,
                processedCharacters,
                totalCharacters,
                BookTranslationSegmentStatus.Completed,
                translated,
                "读取原文 → 请求翻译服务 → 接收译文 → 写入译文");
            index++;
        }

        return translations;
    }

    private async Task<string> TranslateSegmentAsync(
        TranslationSegment segment,
        TranslationRequestContext context,
        CancellationToken cancellationToken)
    {
        var markup = segment.Markup;
        if (!markup.HasMarkers || markup.RequestText.Length <= GetSingleRequestCharacterLimit(context.Settings.Provider))
        {
            var translated = await TranslateTextWithRetryAsync(
                markup.RequestText, context, cancellationToken, cacheParts: !markup.HasMarkers);
            if (markup.IsValidTranslation(translated)) return translated;
            if (!markup.HasMarkers)
                throw new InvalidDataException("翻译结果包含无法写入 EPUB 的字符，请重试。");
        }

        // Providers that strip markup markers cannot safely align a translated
        // sentence with links/emphasis. Translate its text runs independently
        // and retain the original local element boundaries instead.
        var builder = new StringBuilder();
        foreach (var part in markup.Parts())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.IsMarker || !ContainsTranslatableCharacters(part.Text))
            {
                builder.Append(part.Text);
                continue;
            }
            var text = part.Text.Trim();
            var leading = part.Text.Length - part.Text.TrimStart().Length;
            var trailing = part.Text.Length - part.Text.TrimEnd().Length;
            builder.Append(part.Text.AsSpan(0, leading));
            builder.Append(await TranslateTextWithRetryAsync(text, context, cancellationToken));
            if (trailing > 0) builder.Append(part.Text.AsSpan(part.Text.Length - trailing));
        }
        var result = builder.ToString();
        if (!markup.IsValidTranslation(result))
            throw new InvalidDataException("翻译结果的行内结构校验失败，请重试。");
        return result;
    }

    private async Task<string> TranslateTextWithRetryAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken,
        bool cacheParts = true)
    {
        var settings = context.Settings;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (IsSameLanguage(settings.SourceLanguage, settings.TargetLanguage)) return text;
        if (cacheParts && context.Parts.TryGetValue(text, out var completedPart)) return completedPart;

        async Task<string> TranslatePartAsync(string part)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cacheParts && context.Parts.TryGetValue(part, out var cached)) return cached;
            var value = await TranslateSingleWithRetryAsync(part, context, cancellationToken);
            if (cacheParts)
            {
                context.Parts[part] = value;
                context.CacheWriter.AppendPart(part, value);
            }
            return value;
        }

        var maxLength = GetSingleRequestCharacterLimit(settings.Provider);
        var hasSavedPlan = cacheParts && context.SplitPlans.TryGetValue(text, out _);
        if (hasSavedPlan || text.Length > maxLength)
        {
            var parts = hasSavedPlan ? context.SplitPlans[text] : SplitText(text, maxLength);
            if (cacheParts && !hasSavedPlan && parts.Count > 1)
            {
                context.SplitPlans[text] = parts;
                context.CacheWriter.AppendSplitPlan(text, parts);
            }
            var translatedParts = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                // Keep the previous provider's successful chunk boundaries when
                // switching engines. Only unfinished oversized chunks need splitting.
                translatedParts.Add(parts.Count > 1 && part.Length > maxLength
                    ? await TranslateTextWithRetryAsync(part, context, cancellationToken, cacheParts)
                    : await TranslatePartAsync(part));
            }
            return string.Join(" ", translatedParts.Where(part => part.Length > 0));
        }

        return await TranslatePartAsync(text);
    }

    private async Task<string> TranslateSingleWithRetryAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var translated = await TranslateSingleAsync(text, context, cancellationToken);
                if (string.IsNullOrWhiteSpace(translated))
                    throw new InvalidDataException("翻译服务返回了空文本。 ");
                XmlConvert.VerifyXmlChars(translated);
                return translated.Trim();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (attempt >= MaxRetryCount) break;
                await Task.Delay(TimeSpan.FromMilliseconds(350 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{GetProviderDisplayName(settings.Provider)} 翻译失败：{lastFailure?.Message ?? "未知错误"}",
            lastFailure);
    }

    private async Task<string> TranslateSingleAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken) => context.Settings.Provider switch
        {
            BookTranslationProvider.Ai => await TranslateWithAiAsync(text, context, cancellationToken),
            BookTranslationProvider.BingFree => await TranslateWithBingAsync(text, context.Settings, cancellationToken),
            BookTranslationProvider.GoogleFree => await TranslateWithGoogleAsync(text, context.Settings, cancellationToken),
            _ => throw new NotSupportedException("未知的翻译服务。 ")
        };

    private async Task<string> TranslateWithAiAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        context.AiSettings ??= await _aiSettingsStore.LoadAsync(cancellationToken);
        if (!context.AiSettings.IsConfigured)
            throw new InvalidOperationException("请先在“阅读 → AI 设置”中配置 AI 服务、模型和 API Key。 ");

        var source = TranslationLanguageCatalog.Find(settings.SourceLanguage).DisplayName;
        var target = TranslationLanguageCatalog.Find(settings.TargetLanguage).DisplayName;
        var instructions =
            $"你是专业的书籍翻译引擎。请将文本从{source}翻译成{target}。"
            + "只输出译文，不要解释、不要加引号、不要添加标题。"
            + "必须原样保留形如 __KKINDLE_SEG_0__、__KKINDLE_INLINE_0__ 的标记及其顺序；标记之间的正文需要翻译。";
        await _aiRequestsPerMinuteLimiter.WaitAsync(
            settings.AiRequestsPerMinute,
            cancellationToken);
        return await _aiChatClient.CompleteAsync(
            context.AiSettings,
            instructions,
            text,
            Array.Empty<AiConversationTurn>(),
            cancellationToken);
    }

    private async Task<string> TranslateWithGoogleAsync(
        string text,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        var source = Uri.EscapeDataString(ToGoogleLanguageCode(settings.SourceLanguage));
        var target = Uri.EscapeDataString(ToGoogleLanguageCode(settings.TargetLanguage));
        var query = Uri.EscapeDataString(text);
        var endpoint = $"{GoogleTranslatorUrl}?client=gtx&sl={source}&tl={target}&dt=t&q={query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var httpClient = GetGoogleHttpClient(settings.GoogleProxyAddress);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return await TranslateWithGoogleLegacyAsync(
                text,
                settings,
                httpClient,
                cancellationToken);
        }
        EnsureSuccess(response, body, "Google 免费翻译");
        return ParseGoogleTranslation(body);
    }

    private async Task<string> TranslateWithGoogleLegacyAsync(
        string text,
        BookTranslationSettings settings,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var source = Uri.EscapeDataString(ToGoogleLanguageCode(settings.SourceLanguage));
        var target = Uri.EscapeDataString(ToGoogleLanguageCode(settings.TargetLanguage));
        var query = Uri.EscapeDataString(text);
        var endpoint = $"{GoogleLegacyTranslatorUrl}?client=dict-chrome-ex&sl={source}&tl={target}&q={query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = new Uri("https://translate.google.com/");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "Google 免费翻译备用接口");
        return ParseGoogleTranslation(body);
    }

    private async Task<string> TranslateWithBingAsync(
        string text,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        var session = await GetBingSessionAsync(forceRefresh: false, cancellationToken);
        var translated = await TryTranslateWithBingSessionAsync(session, text, settings, cancellationToken);
        if (translated is not null) return translated;

        session = await GetBingSessionAsync(forceRefresh: true, cancellationToken);
        return await TryTranslateWithBingSessionAsync(session, text, settings, cancellationToken)
            ?? throw new InvalidDataException("Bing 免费翻译返回了无法识别的结果。 ");
    }

    private async Task<string?> TryTranslateWithBingSessionAsync(
        BingSession session,
        string text,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        var endpoint =
            $"https://www.bing.com/ttranslatev3?isVertical=1&IG={Uri.EscapeDataString(session.Ig)}"
            + $"&IID={Uri.EscapeDataString(session.Iid)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fromLang"] = TranslationLanguageCatalog.Find(settings.SourceLanguage).BingCode,
                ["to"] = TranslationLanguageCatalog.Find(settings.TargetLanguage).BingCode,
                ["text"] = text,
                ["token"] = session.Token,
                ["key"] = session.Key,
                ["tryFetchingGenderDebiasedTranslations"] = "true"
            })
        };
        request.Headers.Referrer = new Uri(BingTranslatorUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return ParseBingTranslation(body);
    }

    private async Task<BingSession> GetBingSessionAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await _bingSessionGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && _bingSession is { } cached
                && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Get, BingTranslatorUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Referrer = new Uri("https://www.bing.com/");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, html, "Bing 免费翻译");

            var ig = BingIgPattern.Match(html).Groups["ig"].Value;
            if (ig.Length == 0)
                ig = BingFallbackIgPattern.Match(html).Groups["ig"].Value;
            var iidMatches = BingIidPattern.Matches(html);
            var iid = iidMatches
                .Cast<Match>()
                .Select(match => match.Groups["iid"].Value)
                .FirstOrDefault(value => value.Equals("translator.5025", StringComparison.OrdinalIgnoreCase))
                ?? iidMatches.Cast<Match>().Select(match => match.Groups["iid"].Value).LastOrDefault();
            var abuse = BingAbusePattern.Match(html);
            if (ig.Length == 0 || string.IsNullOrWhiteSpace(iid) || !abuse.Success)
                throw new InvalidDataException("Bing 翻译页面缺少临时访问参数。 ");

            _bingSession = new BingSession(
                ig,
                iid!,
                abuse.Groups["key"].Value,
                abuse.Groups["token"].Value,
                DateTimeOffset.UtcNow.Add(BingSessionLifetime));
            return _bingSession;
        }
        finally
        {
            _bingSessionGate.Release();
        }
    }

    private HttpClient GetGoogleHttpClient(string? proxyAddress)
    {
        var normalizedAddress = TranslationProxy.NormalizeAddress(proxyAddress);
        if (normalizedAddress.Length == 0) return _httpClient;

        lock (_googleProxyClientsGate)
        {
            if (_googleProxyClients.TryGetValue(normalizedAddress, out var existing))
                return existing;

            var client = new HttpClient(
                TranslationProxy.CreateHandler(normalizedAddress),
                disposeHandler: true);
            ConfigureHttpClient(client);
            _googleProxyClients[normalizedAddress] = client;
            return client;
        }
    }

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; Kkindle EPUB Translator/1.0)");
    }

    private static IReadOnlyDictionary<string, byte[]> RenderPages(
        IReadOnlyList<TranslationPage> pages,
        IReadOnlyDictionary<string, string> translations,
        BookTranslationOutputMode mode,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var rendered = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseDocument(page.OriginalMarkup, out var document))
                throw new InvalidDataException($"无法回填 EPUB 页面：{page.EntryName}");
            var segments = ExtractSegments(document, page.EntryName);
            var changed = false;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!translations.TryGetValue(segment.Key, out var translated)
                    || string.IsNullOrWhiteSpace(translated))
                    throw new InvalidDataException($"EPUB 文本段缺少完整译文：{page.EntryName}");

                if (mode == BookTranslationOutputMode.Bilingual)
                {
                    if (page.IsNcx)
                        segment.Element.Value = $"{segment.Text} / {segment.Markup.DisplayText(translated)}";
                    else if (page.IsNavigation && segment.Element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
                        AppendBilingualNavigationText(segment, translated, targetLanguage);
                    else
                        AppendBilingualText(segment, translated, targetLanguage);
                }
                else
                {
                    ReplaceInlineText(segment, translated);
                }
                changed = true;
            }

            if (mode == BookTranslationOutputMode.Bilingual && changed && !page.IsNcx)
                AddBilingualStylesheet(document);
            if (mode == BookTranslationOutputMode.Translated && document.Root is { } root)
            {
                UpdateLanguageAttributes(root, targetLanguage);
                root.SetAttributeValue(XNamespace.Xml + "lang", targetLanguage);
                if (!page.IsNcx) root.SetAttributeValue("lang", targetLanguage);
                changed = true;
            }
            if (changed)
                rendered[page.EntryName] = Encoding.UTF8.GetBytes(SerializeDocument(document));
        }
        return rendered;
    }

    private static IReadOnlyList<TranslationSegment> ExtractSegments(
        XDocument document,
        string entryName)
    {
        var segments = new List<TranslationSegment>();
        var ordinal = 0;
        if (document.Root?.Name.LocalName.Equals("ncx", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var label in document.Descendants().Where(element =>
                         element.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase)
                         && element.Parent?.Name.LocalName.Equals("navLabel", StringComparison.OrdinalIgnoreCase) == true))
                AddSegment(label, label.Nodes().ToArray());
            return segments;
        }

        var body = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null) return [];
        var isNavigation = IsTocNavigationDocument(document);
        Visit(body);
        return segments;

        void AddSegment(XElement container, IReadOnlyList<XNode> nodes)
        {
            if (nodes.Count == 0) return;
            var markup = new EpubTranslationMarkup(nodes);
            var key = CreateSegmentKey(entryName, ordinal++);
            if (!markup.HasTranslatableText) return;
            segments.Add(new TranslationSegment(key, markup.Text, container, markup));
        }

        bool IsBoundary(XElement element) => TranslatableBlockNames.Contains(element.Name.LocalName)
            || (isNavigation && element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase));

        void Visit(XElement container)
        {
            if (EpubTranslationMarkup.IsHidden(container)) return;
            var run = new List<XNode>();
            foreach (var node in container.Nodes())
            {
                if (node is XElement element && !EpubTranslationMarkup.IsHidden(element)
                    && (IsBoundary(element) || element.Descendants().Any(IsBoundary)))
                {
                    AddSegment(container, run.ToArray());
                    run.Clear();
                    Visit(element);
                }
                else run.Add(node);
            }
            AddSegment(container, run.ToArray());
        }
    }

    private static IReadOnlyList<XNode> RenderInlineNodes(TranslationSegment segment, string translated)
    {
        if (!segment.Markup.TryRender(translated, out var nodes))
            throw new InvalidDataException("译文中的行内结构标记不完整，请重新翻译该段。");
        return nodes;
    }

    private static void ReplaceInlineText(TranslationSegment segment, string translated)
    {
        var nodes = RenderInlineNodes(segment, translated);
        segment.Markup.Nodes[0].AddBeforeSelf(nodes);
        foreach (var node in segment.Markup.Nodes) node.Remove();
    }

    private static void AppendBilingualText(TranslationSegment segment, string translated, string targetLanguage)
    {
        var ns = segment.Element.Name.Namespace;
        var span = new XElement(ns + "span",
            new XAttribute("class", "kkindle-translation"),
            new XAttribute("lang", targetLanguage),
            new XAttribute(XNamespace.Xml + "lang", targetLanguage),
            RenderInlineNodes(segment, translated));
        PrepareBilingualCopy(span, targetLanguage);
        segment.Markup.Nodes[^1].AddAfterSelf(
            new XElement(ns + "br"),
            span);
    }

    // Kindle and a number of EPUB converters build the table of contents from
    // the text inside the navigation link itself. Keep the bilingual label in
    // the <a> text rather than relying on Kkindle's reader-only sibling span.
    private static void AppendBilingualNavigationText(
        TranslationSegment segment,
        string translated,
        string targetLanguage)
    {
        var nodes = RenderInlineNodes(segment, translated);
        foreach (var element in nodes.OfType<XElement>()) PrepareBilingualCopy(element, targetLanguage);
        segment.Element.Add(new XText(" / "), nodes);
    }

    private static void PrepareBilingualCopy(XElement root, string targetLanguage)
    {
        foreach (var id in root.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.LocalName == "id").ToArray())
            id.Remove();
        UpdateLanguageAttributes(root, targetLanguage);
    }

    private static void UpdateLanguageAttributes(XElement root, string targetLanguage)
    {
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(attribute =>
                     attribute.Name == XNamespace.Xml + "lang" || attribute.Name == "lang"))
            attribute.Value = targetLanguage;
    }

    private static void AddBilingualStylesheet(XDocument document)
    {
        var root = document.Root;
        if (root is null) return;
        var ns = root.Name.Namespace;
        var head = root.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase));
        if (head is null)
        {
            head = new XElement(ns + "head");
            root.AddFirst(head);
        }

        if (head.Descendants().Any(element =>
                element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)
                && element.Value.Contains("kkindle-translation", StringComparison.Ordinal)))
            return;

        head.Add(new XElement(
            ns + "style",
            new XAttribute("type", "text/css"),
            ".kkindle-translation{display:block;margin-top:.55em;opacity:.82;}"));
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> RenderPackageEntriesAsync(
        string sourcePath,
        BookTranslationOutputMode mode,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var rendered = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        using var archive = ZipFile.OpenRead(sourcePath);
        foreach (var entry in archive.Entries.Where(entry =>
                     Path.GetExtension(entry.FullName).Equals(".opf", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > MaxArchiveEntryBytes)
                throw new InvalidDataException("EPUB 的语言元数据文件过大。");
            var markup = await ReadArchiveTextAsync(entry, cancellationToken);
            if (!TryParseDocument(markup, out var document) || document.Root is null)
                throw new InvalidDataException("无法更新 EPUB 的语言元数据。");
            var metadata = document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
            if (metadata is null)
            {
                metadata = new XElement(document.Root.Name.Namespace + "metadata");
                document.Root.AddFirst(metadata);
            }
            var languages = metadata.Elements(dc + "language").ToArray();
            if (mode == BookTranslationOutputMode.Translated)
            {
                if (languages.Length == 0) metadata.Add(new XElement(dc + "language", targetLanguage));
                else
                {
                    languages[0].Value = targetLanguage;
                    foreach (var language in languages.Skip(1)) language.Remove();
                }
            }
            else if (!languages.Any(language => language.Value.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase)))
                metadata.Add(new XElement(dc + "language", targetLanguage));
            rendered[entry.FullName] = Encoding.UTF8.GetBytes(SerializeDocument(document));
        }
        return rendered;
    }

    private static bool IsTocNavigationDocument(XDocument document) =>
        document.Descendants().Any(element =>
            element.Name.LocalName.Equals("nav", StringComparison.OrdinalIgnoreCase)
            && (HasToken(GetAttributeValue(element, "type"), "toc")
                || HasToken(GetAttributeValue(element, "role"), "doc-toc")));


    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetAttributeValue(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static async Task WriteArchiveAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, byte[]> renderedPages,
        IReadOnlyDictionary<string, byte[]> renderedNavigationEntries,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            using (var destination = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var sourceEntry in source.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureSafeArchiveEntryName(sourceEntry.FullName);
                    var compression = sourceEntry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase)
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Optimal;
                    var destinationEntry = destination.CreateEntry(sourceEntry.FullName, compression);
                    try { destinationEntry.LastWriteTime = sourceEntry.LastWriteTime; }
                    catch (ArgumentOutOfRangeException) { }

                    await using var output = destinationEntry.Open();
                    if (renderedPages.TryGetValue(sourceEntry.FullName, out var rendered)
                        || renderedNavigationEntries.TryGetValue(sourceEntry.FullName, out rendered))
                    {
                        await output.WriteAsync(rendered, cancellationToken);
                    }
                    else
                    {
                        await using var input = sourceEntry.Open();
                        await input.CopyToAsync(output, 81920, cancellationToken);
                    }
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryParseDocument(string markup, out XDocument document)
    {
        try
        {
            using var stringReader = new StringReader(markup);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            });
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
            return document.Root is not null;
        }
        catch (XmlException)
        {
            try
            {
                var html = new HtmlDocument
                {
                    OptionOutputAsXml = true,
                    OptionFixNestedTags = true,
                    OptionAutoCloseOnEnd = true,
                    OptionWriteEmptyNodes = true
                };
                html.LoadHtml(markup);
                var root = html.DocumentNode.SelectSingleNode("//html")
                    ?? html.DocumentNode.ChildNodes.FirstOrDefault(node => node.NodeType == HtmlNodeType.Element);
                if (root is null)
                {
                    document = new XDocument();
                    return false;
                }

                var builder = new StringBuilder();
                using (var writer = new StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture))
                    root.WriteTo(writer);
                using var stringReader = new StringReader(builder.ToString());
                using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null
                });
                document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
                return document.Root is not null;
            }
            catch (Exception exception) when (exception is XmlException or InvalidOperationException)
            {
                document = new XDocument();
                return false;
            }
        }
    }

    private static string SerializeDocument(XDocument document)
    {
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
            document.Save(writer, SaveOptions.DisableFormatting);
        return builder.ToString();
    }

    private static async Task<string> ReadArchiveTextAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static bool TryReadMarkedTranslations(
        string response,
        int expectedCount,
        out IReadOnlyList<string> translations)
    {
        var matches = SegmentMarkerPattern.Matches(response);
        if (matches.Count != expectedCount * 2)
        {
            translations = [];
            return false;
        }

        var values = new string[expectedCount];
        for (var index = 0; index < expectedCount; index++)
        {
            var start = matches[index * 2];
            var end = matches[index * 2 + 1];
            if (!start.Groups["id"].Value.Equals(end.Groups["id"].Value, StringComparison.Ordinal)
                || !start.Groups["id"].Value.Equals(index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                translations = [];
                return false;
            }
            values[index] = response[(start.Index + start.Length)..end.Index].Trim();
            if (values[index].Length == 0)
            {
                translations = [];
                return false;
            }
        }

        translations = values;
        return true;
    }

    private static string ParseGoogleTranslation(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidDataException("Google 翻译返回的数据格式无法识别。 ");
        if (root[0].ValueKind == JsonValueKind.String)
        {
            var text = root[0].GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
            throw new InvalidDataException("Google 翻译返回了空结果。 ");
        }
        if (root[0].ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Google 翻译返回的数据格式无法识别。 ");
        var builder = new StringBuilder();
        foreach (var item in root[0].EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array
                && item.GetArrayLength() > 0
                && item[0].ValueKind == JsonValueKind.String)
                builder.Append(item[0].GetString());
        }
        return builder.ToString();
    }

    private static string? ParseBingTranslation(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (!item.TryGetProperty("translations", out var translations)
                        || translations.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var translation in translations.EnumerateArray())
                    {
                        if (translation.TryGetProperty("text", out var text)
                            && text.ValueKind == JsonValueKind.String)
                            return text.GetString();
                    }
                }

                return null;
            }

            return root.TryGetProperty("translations", out var objectTranslations)
                && objectTranslations.ValueKind == JsonValueKind.Array
                ? objectTranslations.EnumerateArray()
                    .Select(item => item.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String
                        ? text.GetString()
                        : null)
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string provider)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = body.Length > 400 ? body[..400] : body;
        throw new HttpRequestException(
            $"{provider} 请求失败（HTTP {(int)response.StatusCode}）：{detail}");
    }

    private static int GetBatchCharacterLimit(BookTranslationProvider provider) => provider switch
    {
        BookTranslationProvider.GoogleFree => GoogleRequestCharacterLimit,
        BookTranslationProvider.BingFree => BingRequestCharacterLimit,
        _ => AiRequestCharacterLimit
    };

    private static int GetSingleRequestCharacterLimit(BookTranslationProvider provider) => provider switch
    {
        BookTranslationProvider.GoogleFree => GoogleRequestCharacterLimit,
        BookTranslationProvider.BingFree => BingRequestCharacterLimit,
        _ => AiRequestCharacterLimit
    };

    private static IReadOnlyList<string> SplitText(string text, int maxLength)
    {
        var parts = new List<string>();
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maxLength);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOfAny(['\n', '。', '！', '？', '!', '?', '.', '；', ';', ' '], end - 1, end - start);
                if (boundary > start + maxLength / 3) end = boundary + 1;
                var offset = Array.BinarySearch(boundaries, end);
                if (offset < 0)
                {
                    offset = ~offset;
                    end = offset > 0 && boundaries[offset - 1] > start
                        ? boundaries[offset - 1]
                        : offset < boundaries.Length ? boundaries[offset] : text.Length;
                }
            }
            parts.Add(text[start..end].Trim());
            start = end;
        }
        return parts.Where(part => part.Length > 0).ToArray();
    }

    private static bool ContainsTranslatableCharacters(string text) =>
        text.EnumerateRunes().Any(Rune.IsLetter);

    private static bool IsSameLanguage(string source, string target) =>
        TranslationLanguageCatalog.Find(source).Code.Equals(
            TranslationLanguageCatalog.Find(target).Code,
            StringComparison.OrdinalIgnoreCase)
        || (source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && target.Equals("auto", StringComparison.OrdinalIgnoreCase));

    private static string CreateTranslationCacheKey(BookTranslationSettings settings, string text) =>
        $"{settings.Provider}\u001F{settings.SourceLanguage}\u001F{settings.TargetLanguage}\u001F{text}";

    private static string CreateMarker(int index) =>
        $"__KKINDLE_SEG_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}__";

    private static string CreateSegmentKey(string entryName, int ordinal) =>
        $"{entryName}\u001F{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string ToGoogleLanguageCode(string code) =>
        TranslationLanguageCatalog.Find(code).Code switch
        {
            "zh-CN" => "zh-CN",
            "zh-TW" => "zh-TW",
            "auto" => "auto",
            var normalized => normalized
        };

    private static string GetProviderDisplayName(BookTranslationProvider provider) => provider switch
    {
        BookTranslationProvider.Ai => "AI",
        BookTranslationProvider.BingFree => "Bing 免费翻译",
        BookTranslationProvider.GoogleFree => "Google 免费翻译",
        _ => "翻译服务"
    };

    private static string GetOutputModeLabel(BookTranslationOutputMode mode) =>
        mode == BookTranslationOutputMode.Bilingual ? "双语对照" : "单翻译";

    private static string CreateOutputPath(
        string directory,
        string sourcePath,
        string suffix,
        char separator = '-')
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character)).Trim();
        if (safe.Length == 0) safe = "书籍";
        if (safe.Length > 120) safe = safe[..120].TrimEnd();
        return Path.Combine(directory, $"{safe}{separator}{suffix}.epub");
    }

    private static bool IsXhtmlEntry(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeArchiveEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith('/')
            || name.StartsWith('\\')
            || Path.IsPathFullyQualified(name))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");
        var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");
    }

    private static void Report(
        IProgress<BookTranslationProgress>? progress,
        BookTranslationProgress value) => progress?.Report(value);

    private static void ReportSegmentStateCore(
        IProgress<BookTranslationProgress>? progress,
        TranslationSegment segment,
        int processed,
        int total,
        long processedCharacters,
        long totalCharacters,
        BookTranslationSegmentStatus status,
        string translatedText,
        string processFlow,
        string? errorMessage,
        TranslationCacheWriter cacheWriter)
    {
        var update = new BookTranslationSegmentProgress(
            segment.Index,
            segment.Key.Split('\u001F')[0],
            segment.Text,
            segment.Markup.DisplayText(translatedText),
            status,
            processFlow,
            errorMessage);
        cacheWriter.Append(update, segment.Markup.RequestText, translatedText);
        Report(progress, new BookTranslationProgress(
            "正在翻译",
            segment.Key.Split('\u001F')[0],
            processed,
            total,
            processedCharacters,
            totalCharacters,
            update));
    }

    private static string GetTranslationCachePath(string outputDirectory) =>
        Path.Combine(Path.GetFullPath(outputDirectory), TranslationCacheFileName);

    private static async Task<TranslationCacheSnapshot?> TryLoadTranslationCacheAsync(
        string cachePath,
        string sourcePath,
        string sourceHash,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath)) return null;

        var lines = await File.ReadAllLinesAsync(cachePath, Encoding.UTF8, cancellationToken);
        TranslationCacheHeader? header = null;
        var segments = new Dictionary<int, TranslationCacheSegmentLine>();
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        var splitPlans = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var updatedAt = DateTimeOffset.MinValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("Kind", out var kindProperty)
                    || kindProperty.ValueKind != JsonValueKind.String)
                    continue;
                var kind = kindProperty.GetString();
                if (string.Equals(kind, "header", StringComparison.OrdinalIgnoreCase))
                {
                    header = JsonSerializer.Deserialize<TranslationCacheHeader>(
                        line,
                        TranslationCacheJsonOptions);
                    if (header is not null)
                        updatedAt = header.UpdatedAt > updatedAt ? header.UpdatedAt : updatedAt;
                }
                else if (string.Equals(kind, "segment", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = JsonSerializer.Deserialize<TranslationCacheSegmentLine>(
                        line,
                        TranslationCacheJsonOptions);
                    if (segment is null || segment.Index < 0) continue;
                    segments[segment.Index] = segment;
                    if (segment.UpdatedAt > updatedAt) updatedAt = segment.UpdatedAt;
                }
                else if (string.Equals(kind, "part", StringComparison.OrdinalIgnoreCase))
                {
                    var part = JsonSerializer.Deserialize<TranslationCachePartLine>(line, TranslationCacheJsonOptions);
                    if (part is null || string.IsNullOrWhiteSpace(part.SourceText) || string.IsNullOrWhiteSpace(part.TranslatedText)) continue;
                    XmlConvert.VerifyXmlChars(part.TranslatedText);
                    parts[part.SourceText] = part.TranslatedText;
                    if (part.UpdatedAt > updatedAt) updatedAt = part.UpdatedAt;
                }
                else if (string.Equals(kind, "split", StringComparison.OrdinalIgnoreCase))
                {
                    var split = JsonSerializer.Deserialize<TranslationCacheSplitLine>(line, TranslationCacheJsonOptions);
                    if (split is null || !IsValidSplitPlan(split.SourceText, split.Parts)) continue;
                    splitPlans[split.SourceText] = split.Parts;
                    if (split.UpdatedAt > updatedAt) updatedAt = split.UpdatedAt;
                }
            }
            catch (Exception exception) when (exception is JsonException or XmlException)
            {
                // An interrupted write can leave one incomplete final line;
                // earlier flushed entries are still valid and recoverable.
            }
        }

        if (header is null
            || header.Version != TranslationCacheVersion
            || string.IsNullOrWhiteSpace(header.SourcePath)
            || string.IsNullOrWhiteSpace(header.SourceHash)
            || !Path.GetFullPath(header.SourcePath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
            || !header.SourceHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            return null;

        var cachedSettings = BookTranslationSettings.Normalize(header.Settings);
        if (!cachedSettings.SourceLanguage.Equals(settings.SourceLanguage, StringComparison.OrdinalIgnoreCase)
            || !cachedSettings.TargetLanguage.Equals(settings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            return null;

        return new TranslationCacheSnapshot(
            header,
            segments,
            parts,
            splitPlans,
            updatedAt == DateTimeOffset.MinValue ? header.UpdatedAt : updatedAt);
    }

    private static int GetCachedTotalSegments(TranslationCacheSnapshot snapshot)
    {
        var inferredTotal = snapshot.Segments.Keys.DefaultIfEmpty(-1).Max() + 1;
        return Math.Max(snapshot.Header.TotalSegments, inferredTotal);
    }

    private static bool IsValidSplitPlan(string source, IReadOnlyList<string> parts)
    {
        if (string.IsNullOrEmpty(source) || parts is null || parts.Count < 2) return false;
        var offset = 0;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part) || part.Length >= source.Length) return false;
            while (offset < source.Length && char.IsWhiteSpace(source[offset])) offset++;
            if (!source.AsSpan(offset).StartsWith(part, StringComparison.Ordinal)) return false;
            offset += part.Length;
        }
        while (offset < source.Length && char.IsWhiteSpace(source[offset])) offset++;
        return offset == source.Length;
    }

    private static void TryDeleteTranslationCache(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to delete completed translation cache: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aiRequestsPerMinuteLimiter.Dispose();
        _bingSessionGate.Dispose();
        HttpClient[] proxyClients;
        lock (_googleProxyClientsGate)
        {
            proxyClients = _googleProxyClients.Values.ToArray();
            _googleProxyClients.Clear();
        }
        foreach (var proxyClient in proxyClients)
            proxyClient.Dispose();
        _httpClient.Dispose();
        if (_ownsAiChatClient) _aiChatClient.Dispose();
    }

    private sealed record TranslationCacheHeader(
        string Kind,
        int Version,
        string SourcePath,
        string SourceHash,
        BookTranslationSettings Settings,
        int TotalSegments,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCacheSegmentLine(
        string Kind,
        int Index,
        string EntryName,
        string OriginalText,
        string RequestText,
        string TranslatedText,
        BookTranslationSegmentStatus Status,
        string ProcessFlow,
        string? ErrorMessage,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCachePartLine(
        string Kind,
        string SourceText,
        string TranslatedText,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCacheSnapshot(
        TranslationCacheHeader Header,
        IReadOnlyDictionary<int, TranslationCacheSegmentLine> Segments,
        IReadOnlyDictionary<string, string> Parts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> SplitPlans,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCacheSplitLine(
        string Kind,
        string SourceText,
        IReadOnlyList<string> Parts,
        DateTimeOffset UpdatedAt);

    private sealed class TranslationCacheWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private bool _disposed;

        public TranslationCacheWriter(
            string path,
            TranslationCacheHeader header,
            bool append)
        {
            var stream = new FileStream(
                path,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (append && stream.Length > 0)
                _writer.WriteLine();
            WriteHeader(header);
        }

        public void Append(BookTranslationSegmentProgress segment, string requestText, string translatedText)
        {
            if (_disposed) return;
            var line = new TranslationCacheSegmentLine(
                "segment",
                segment.Index,
                segment.EntryName,
                segment.OriginalText,
                requestText,
                translatedText,
                segment.Status,
                segment.ProcessFlow,
                segment.ErrorMessage,
                DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        public void AppendPart(string sourceText, string translatedText)
        {
            if (_disposed) return;
            var line = new TranslationCachePartLine("part", sourceText, translatedText, DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        public void AppendSplitPlan(string sourceText, IReadOnlyList<string> parts)
        {
            if (_disposed) return;
            var line = new TranslationCacheSplitLine("split", sourceText, parts, DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        private void WriteHeader(TranslationCacheHeader header)
        {
            _writer.WriteLine(JsonSerializer.Serialize(header, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed record TranslationPage(
        string EntryName,
        string OriginalMarkup,
        IReadOnlyList<TranslationSegment> Segments,
        bool IsNavigation,
        bool IsNcx);

    private sealed record TranslationSegment(
        string Key,
        string Text,
        XElement Element,
        EpubTranslationMarkup Markup,
        int Index = -1);

    private sealed class TranslationRequestContext(
        BookTranslationSettings settings,
        IReadOnlyDictionary<string, string>? parts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? splitPlans,
        TranslationCacheWriter cacheWriter)
    {
        public BookTranslationSettings Settings { get; } = settings;
        public AiConnectionSettings? AiSettings { get; set; }
        public Dictionary<string, string> Parts { get; } = parts is null
            ? new(StringComparer.Ordinal)
            : new(parts, StringComparer.Ordinal);
        public TranslationCacheWriter CacheWriter { get; } = cacheWriter;
        public Dictionary<string, IReadOnlyList<string>> SplitPlans { get; } = splitPlans is null
            ? new(StringComparer.Ordinal)
            : new(splitPlans, StringComparer.Ordinal);
    }

    private sealed record BingSession(
        string Ig,
        string Iid,
        string Key,
        string Token,
        DateTimeOffset ExpiresAt);

    private sealed class Utf8StringWriter(
        StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
