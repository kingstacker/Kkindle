using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle;

/// <summary>
/// Host half of the book translation and pinyin plugin features. The plugin
/// page chooses options and renders progress; the existing engine does the
/// work and adds the generated files to the library.
/// </summary>
internal sealed class PluginBookTaskService : IPluginBookTaskHost
{
    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly IEpubTranslationService _epubTranslation;
    private readonly IBookFormatConverter _formatConverter;
    private readonly AiChatClient _aiChatClient;
    private readonly AiSettingsStore _aiSettings;
    private readonly G2PWModelDownloadService _pinyinModels;
    private readonly Func<AppSettings?> _appSettings;
    private readonly Func<BookFile, CancellationToken, Task<string?>> _resolveFile;
    private readonly SemaphoreSlim _taskGate = new(1, 1);

    public PluginBookTaskService(
        AppPaths paths,
        IBookLibraryService library,
        IEpubTranslationService epubTranslation,
        IBookFormatConverter formatConverter,
        AiChatClient aiChatClient,
        AiSettingsStore aiSettings,
        G2PWModelDownloadService pinyinModels,
        Func<AppSettings?> appSettings,
        Func<BookFile, CancellationToken, Task<string?>> resolveFile)
    {
        _paths = paths;
        _library = library;
        _epubTranslation = epubTranslation;
        _formatConverter = formatConverter;
        _aiChatClient = aiChatClient;
        _aiSettings = aiSettings;
        _pinyinModels = pinyinModels;
        _appSettings = appSettings;
        _resolveFile = resolveFile;
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        await RunExclusiveAsync(async () => { await action(); return true; });
    }

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        if (!_taskGate.Wait(0))
            throw new InvalidOperationException(
                UiText.Get("已有一项书籍处理任务正在进行，请等待完成或先取消。"));
        try
        {
            return await action();
        }
        finally
        {
            _taskGate.Release();
        }
    }

    // ---- translation ---------------------------------------------------

    public Task<JsonObject> TranslateBookAsync(
        Guid bookId,
        PluginTranslationRequest request,
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => TranslateBookCoreAsync(bookId, request, progress, cancellationToken));

    private async Task<JsonObject> TranslateBookCoreAsync(
        Guid bookId,
        PluginTranslationRequest request,
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = _appSettings();
        if (settings is not null && !settings.NetworkEnabled)
            throw new InvalidOperationException(UiText.Get("网络访问已关闭，无法翻译书籍。"));
        if (settings is not null
            && request.Provider == "ai"
            && !settings.AiEnabled)
            throw new InvalidOperationException(UiText.Get("AI 功能已关闭，无法翻译书籍。"));
        if (request.Provider == "ai")
        {
            // Fail at the start rather than after the first EPUB page has been
            // parsed: a missing key would otherwise surface mid-translation.
            var aiSettings = await _aiSettings.LoadAsync(cancellationToken);
            if (!aiSettings.IsConfigured)
                throw new InvalidOperationException(
                    UiText.Get("请先在“阅读 → AI 设置”中配置 AI 服务、模型和 API Key。"));
        }

        var book = await _library.GetBookAsync(bookId, cancellationToken)
            ?? throw new InvalidOperationException(UiText.Get("书籍不存在或已被删除。"));
        var (_, epubPath) = await ResolveTranslationSourceAsync(book, cancellationToken);
        var title = string.IsNullOrWhiteSpace(book.Title)
            ? Path.GetFileNameWithoutExtension(epubPath)
            : book.Title.Trim();
        var outputMode = BookTranslationOutputMode.None;
        if (request.Translated) outputMode |= BookTranslationOutputMode.Translated;
        if (request.Bilingual) outputMode |= BookTranslationOutputMode.Bilingual;
        if (outputMode == BookTranslationOutputMode.None)
            throw new InvalidOperationException(UiText.Get("请至少选择一种翻译结果。"));

        var translationSettings = BookTranslationSettings.Normalize(
            (settings?.Translation ?? new BookTranslationSettings()) with
            {
                Provider = request.Provider switch
                {
                    "bing" => BookTranslationProvider.BingFree,
                    "google" => BookTranslationProvider.GoogleFree,
                    _ => BookTranslationProvider.Ai
                },
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                OutputMode = outputMode,
                AiRequestsPerMinute = request.AiRequestsPerMinute,
                GoogleProxyAddress = string.IsNullOrWhiteSpace(request.GoogleProxyAddress)
                    ? settings?.Translation.GoogleProxyAddress ?? string.Empty
                    : request.GoogleProxyAddress,
                ContextMenuEnabled = false
            });

        var outputDirectory = GetTranslationOutputDirectory(Path.GetFileNameWithoutExtension(epubPath));
        var resumeMode = BookTranslationResumeMode.Restart;
        if (request.Resume)
        {
            BookTranslationResumeInfo? resumeInfo = null;
            try
            {
                resumeInfo = await _epubTranslation.FindResumeAsync(
                    epubPath,
                    outputDirectory,
                    translationSettings,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to inspect translation cache: {exception.Message}");
            }
            if (resumeInfo is not null && resumeInfo.IncompleteSegments > 0)
                resumeMode = BookTranslationResumeMode.Resume;
        }

        var existingFiles = await FindExistingTranslationFilesAsync(
            book,
            title,
            outputMode,
            cancellationToken);

        progress?.Report(new PluginTaskProgress(
            "prepare",
            UiText.Get("正在准备翻译…"),
            0,
            0,
            0,
            true));
        var translationProgress = new Progress<BookTranslationProgress>(value =>
        {
            var stage = PluginTaskText.Stage(value.Stage);
            progress?.Report(new PluginTaskProgress(
                stage,
                string.IsNullOrWhiteSpace(value.CurrentItem)
                    ? stage
                    : PluginTaskText.Detail(value.CurrentItem),
                value.Percentage,
                value.ProcessedSegments,
                value.TotalSegments));
        });

        var result = await _epubTranslation.TranslateAsync(
            epubPath,
            outputDirectory,
            translationSettings,
            translationProgress,
            cancellationToken,
            resumeMode);

        var outputPaths = result.OutputPaths
            .Where(IsGeneratedTranslationOutputPath)
            .Where(File.Exists)
            .ToArray();
        var added = 0;
        var skipped = 0;
        var failed = 0;
        if (outputPaths.Length > 0)
        {
            progress?.Report(new PluginTaskProgress(
                "import",
                UiText.Get("正在加入书库…"),
                100,
                outputPaths.Length,
                outputPaths.Length,
                true));
            var replacements = outputPaths.ToDictionary(
                path => path,
                path => (IReadOnlyList<BookFile>)existingFiles
                    .Where(existing => existing.Kind == GetGeneratedTranslationKind(path))
                    .Select(existing => existing.File)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
            var importResult = await BookTranslationLibraryImport.ImportAsync(
                _library,
                replacements,
                cancellationToken);
            added = importResult.AddedCount;
            skipped = importResult.SkippedCount;
            failed = importResult.FailureCount;
        }

        progress?.Report(new PluginTaskProgress(
            "done",
            UiText.Get("翻译完成"),
            100,
            result.SegmentCount,
            result.SegmentCount));

        return new JsonObject
        {
            ["bookId"] = bookId.ToString("D"),
            ["title"] = title,
            ["outputCount"] = outputPaths.Length,
            ["added"] = added,
            ["skipped"] = skipped,
            ["failed"] = failed,
            ["characters"] = result.SourceCharacters,
            ["segments"] = result.SegmentCount,
            ["outputs"] = new JsonArray(outputPaths
                .Select(path => (JsonNode?)JsonValue.Create(Path.GetFileName(path)))
                .ToArray())
        };
    }

    private async Task<(BookFile File, string Path)> ResolveTranslationSourceAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var candidates = book.Files
            .Where(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => GetGeneratedTranslationKind(file.RelativePath) is null ? 0 : 1)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException(UiText.Get("未找到可翻译的 EPUB 文件。"));

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = await ResolveAsync(candidate, cancellationToken);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return (candidate, path);
        }
        throw new InvalidOperationException(
            UiText.Get("找不到可翻译的 EPUB 文件，请检查书籍文件是否可用。"));
    }

    private async Task<string> ResolveAsync(BookFile file, CancellationToken cancellationToken)
    {
        var path = _library.GetAbsoluteFilePath(file);
        if (File.Exists(path)) return path;
        return await _resolveFile(file, cancellationToken) ?? path;
    }

    private async Task<IReadOnlyList<ExistingTranslationFile>> FindExistingTranslationFilesAsync(
        Book sourceBook,
        string title,
        BookTranslationOutputMode outputMode,
        CancellationToken cancellationToken)
    {
        var booksById = new Dictionary<Guid, Book> { [sourceBook.Id] = sourceBook };
        try
        {
            foreach (var book in await _library.SearchAsync(title, cancellationToken))
            {
                if (!SameMetadata(book.Title, sourceBook.Title)
                    || !SameMetadata(book.Authors, sourceBook.Authors))
                    continue;
                booksById[book.Id] = book;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to search existing translations: {exception.Message}");
        }

        return booksById.Values
            .SelectMany(book => book.Files.Select(file => (Book: book, File: file)))
            .Select(item => (item.Book, item.File, Kind: GetGeneratedTranslationKind(item.File.RelativePath)))
            .Where(item => item.Kind is not null && IsRequestedKind(item.Kind!, outputMode))
            .Select(item => new ExistingTranslationFile(item.Book, item.File, item.Kind!))
            .GroupBy(item => (item.Book.Id, item.File.Id))
            .Select(group => group.First())
            .ToArray();
    }

    private sealed record ExistingTranslationFile(Book Book, BookFile File, string Kind);

    private static bool SameMetadata(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsRequestedKind(string kind, BookTranslationOutputMode outputMode) => kind switch
    {
        "译文" => outputMode.HasFlag(BookTranslationOutputMode.Translated),
        "双语" => outputMode.HasFlag(BookTranslationOutputMode.Bilingual),
        _ => false
    };

    private string GetTranslationOutputDirectory(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(name.Select(character =>
            invalid.Contains(character) ? '_' : character)).Trim();
        if (safeName.Length == 0) safeName = "书籍";
        if (safeName.Length > 100) safeName = safeName[..100].TrimEnd();
        return Path.Combine(_paths.Data, "translations", safeName);
    }

    private static string? GetGeneratedTranslationKind(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith("_双语版", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-双语", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_双语", StringComparison.OrdinalIgnoreCase))
            return "双语";
        if (stem.EndsWith("_单译版", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-译文", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_译文", StringComparison.OrdinalIgnoreCase))
            return "译文";
        return null;
    }

    private static bool IsGeneratedTranslationOutputPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("_单译版", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_双语版", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-译文", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_译文", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-双语", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_双语", StringComparison.OrdinalIgnoreCase);
    }

    // ---- pinyin --------------------------------------------------------

    public PluginPinyinStatus GetPinyinStatus()
    {
        try
        {
            return new PluginPinyinStatus(_pinyinModels.IsInstalled());
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to inspect g2pW model: {exception.Message}");
            return new PluginPinyinStatus(false);
        }
    }

    public Task DownloadPinyinModelAsync(
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () => await DownloadPinyinModelCoreAsync(progress, cancellationToken));

    private Task DownloadPinyinModelCoreAsync(
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = _appSettings();
        if (settings is not null && !settings.NetworkEnabled)
            throw new InvalidOperationException(
                UiText.Get("请先在应用设置中开启网络访问，再下载 g2pW 模型。"));

        var downloadProgress = new Progress<G2PWModelDownloadProgress>(value =>
        {
            var percent = value.OverallPercentage ?? value.FilePercentage;
            var stage = PluginTaskText.Stage(value.Stage);
            var message = string.IsNullOrWhiteSpace(value.FileName)
                ? stage
                : $"{stage} · {value.FileName}";
            progress?.Report(new PluginTaskProgress(
                "download",
                message,
                percent ?? 0,
                Indeterminate: percent is null));
        });
        return _pinyinModels.DownloadAsync(true, downloadProgress, cancellationToken);
    }

    public Task<JsonObject> AnnotatePinyinAsync(
        Guid bookId,
        PluginPinyinRequest request,
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => AnnotatePinyinCoreAsync(bookId, request, progress, cancellationToken));

    private async Task<JsonObject> AnnotatePinyinCoreAsync(
        Guid bookId,
        PluginPinyinRequest request,
        IProgress<PluginTaskProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = _appSettings();
        var book = await _library.GetBookAsync(bookId, cancellationToken)
            ?? throw new InvalidOperationException(UiText.Get("书籍不存在或已被删除。"));
        // Regenerating is allowed; the older generated edition is replaced
        // only after the new one has been produced successfully.
        var existingPinyinFiles = book.Files
            .Where(PinyinBookPolicy.IsGeneratedPinyinVersion)
            .ToArray();

        var sourceFile = SelectPinyinSourceFile(book.Files)
            ?? throw new InvalidOperationException(
                UiText.Get("需要 EPUB、AZW3、MOBI 或 PDF 作为书籍注音来源。"));
        var sourcePath = await ResolveAsync(sourceFile, cancellationToken);
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException(
                UiText.Get("找不到书籍注音来源：{0}", sourceFile.RelativePath));

        var engineKind = PinyinBookEngineCatalog.Parse(request.Engine);
        if (engineKind == PinyinBookEngineKind.G2PW && !GetPinyinStatus().G2pwInstalled)
            throw new InvalidOperationException(
                UiText.Get("请先下载 g2pW 模型，下载完成后即可使用。"));

        var generatedTitle = PinyinBookPolicy.CreateGeneratedTitle(book.Title);
        var outputDirectory = GetPinyinOutputDirectory(book.Title);
        var outputPath = Path.Combine(
            outputDirectory,
            KindleTransferPolicy.CreateSafeFileName(generatedTitle, ".epub"));

        AiConnectionSettings? aiSettings = null;
        if (request.EnableAiReview
            && (settings is null || (settings.NetworkEnabled && settings.AiEnabled)))
        {
            try
            {
                var loaded = await _aiSettings.LoadAsync(cancellationToken);
                if (loaded.IsConfigured) aiSettings = loaded;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Local annotation remains available without AI review.
            }
        }

        var options = new PinyinBookOptions
        {
            Engine = engineKind,
            OutputStyle = request.OutputStyle switch
            {
                "tone-number" => PinyinBookOutputStyle.ToneNumber,
                "none" => PinyinBookOutputStyle.NoTone,
                _ => PinyinBookOutputStyle.ToneMarked
            },
            EnableAiReview = aiSettings is not null
        };
        using var engine = PinyinEngineFactory.Create(_paths, engineKind);
        var service = new PinyinBookService(_formatConverter, _aiChatClient, aiSettings, engine);
        var resumeMode = PinyinBookResumeMode.Restart;
        if (request.Resume)
        {
            try
            {
                var resumeInfo = await service.FindResumeAsync(
                    sourcePath,
                    outputPath,
                    options,
                    cancellationToken);
                if (resumeInfo is not null && resumeInfo.IncompleteSegments > 0)
                    resumeMode = PinyinBookResumeMode.Resume;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to inspect pinyin cache: {exception.Message}");
            }
        }

        var imported = false;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var pinyinProgress = new Progress<PinyinBookProgress>(value =>
            {
                var stage = PluginTaskText.Stage(value.Stage);
                progress?.Report(new PluginTaskProgress(
                    stage,
                    string.IsNullOrWhiteSpace(value.CurrentItem)
                        ? stage
                        : PluginTaskText.Detail(value.CurrentItem),
                    value.Percentage,
                    value.ProcessedSegments,
                    value.TotalSegments,
                    value.OverallPercentage is null && value.TotalSegments <= 0));
            });
            var result = await service.GenerateAsync(
                sourcePath,
                outputPath,
                options,
                pinyinProgress,
                cancellationToken,
                resumeMode);

            if (result.AnnotatedCharacterCount == 0)
                throw new InvalidOperationException(UiText.Get("没有发现可注音的中文正文。"));
            if (result.FailedSegmentCount > 0 || result.AiReviewError is not null)
            {
                var reason = result.AiReviewError
                    ?? UiText.Get("仍有 {0} 段未完成 AI 复核", result.FailedSegmentCount);
                throw new InvalidOperationException(
                    UiText.Get("书籍注音未完全完成：{0}。已保存的进度可下次继续。", reason));
            }

            progress?.Report(new PluginTaskProgress(
                "import",
                UiText.Get("正在加入书库…"),
                100,
                0,
                0,
                true));
            // Remove the previous generated edition first so the new file can
            // take the canonical "_pinyin" name instead of a "(2)" suffix.
            foreach (var existing in existingPinyinFiles)
                await _library.DeleteFileAsync(bookId, existing.Id, cancellationToken);
            await _library.AddFileToBookAsync(bookId, outputPath, cancellationToken);
            imported = true;

            return new JsonObject
            {
                ["bookId"] = bookId.ToString("D"),
                ["title"] = generatedTitle,
                ["replaced"] = existingPinyinFiles.Length,
                ["scannedPages"] = result.ScannedPageCount,
                ["annotatedPages"] = result.AnnotatedPageCount,
                ["characters"] = result.AnnotatedCharacterCount,
                ["reviewCandidates"] = result.ReviewCandidateCount,
                ["aiReviewed"] = result.AiReviewedCandidateCount,
                ["aiReview"] = aiSettings is not null
            };
        }
        finally
        {
            if (imported)
                TryDeletePinyinArtifacts(outputPath, outputDirectory, removeDirectory: true);
        }
    }

    private static BookFile? SelectPinyinSourceFile(IEnumerable<BookFile>? files)
    {
        if (files is null) return null;
        var supported = files
            .Where(file => BookFormatConversionPolicy.IsCalibreInputFormat(file.Format))
            .Where(file => !PinyinBookPolicy.IsGeneratedPinyinVersion(file))
            .ToArray();
        return supported.FirstOrDefault(file =>
                   BookFormatConversionPolicy.Normalize(file.Format) == "epub")
            ?? supported.FirstOrDefault(file =>
                BookFormatConversionPolicy.Normalize(file.Format) is "azw3" or "mobi" or "pdf")
            ?? supported.FirstOrDefault();
    }

    private string GetPinyinOutputDirectory(string? bookTitle)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.IsNullOrWhiteSpace(bookTitle) ? "书籍" : bookTitle.Trim();
        var safeName = string.Concat(name.Select(character =>
            invalid.Contains(character) ? '_' : character)).Trim();
        if (safeName.Length == 0) safeName = "书籍";
        if (safeName.Length > 100) safeName = safeName[..100].TrimEnd();
        return Path.Combine(_paths.Data, "pinyin", safeName);
    }

    private static void TryDeletePinyinArtifacts(string outputPath, string directory, bool removeDirectory)
    {
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (removeDirectory
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Turns the Chinese progress text produced by the translation and pinyin
/// engines into the application's current UI language. Unknown strings (file
/// names, calibre output) pass through unchanged.
/// </summary>
internal static class PluginTaskText
{
    private static readonly Regex SegmentsFound =
        new(@"^共发现 ([\d,]+) 个文本段$", RegexOptions.CultureInvariant);
    private static readonly Regex OutputsGenerated =
        new(@"^已生成 ([\d,]+) 个文件$", RegexOptions.CultureInvariant);
    private static readonly Regex PagesGenerated =
        new(@"^已生成 ([\d,]+) 个正文页面(?:，([\d,]+) 段待复核)?$", RegexOptions.CultureInvariant);
    private static readonly Regex AiBatchRequested =
        new(@"^正在请求 AI：([\d,]+)-([\d,]+) / ([\d,]+) 项$", RegexOptions.CultureInvariant);
    private static readonly Regex AiBatchProcessed =
        new(@"^已处理 ([\d,]+) / ([\d,]+) 项$", RegexOptions.CultureInvariant);

    public static string Stage(string? stage) =>
        string.IsNullOrWhiteSpace(stage) ? string.Empty : KnownStage(stage) ?? stage;

    public static string Detail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        if (KnownStage(detail) is { } exactStage) return exactStage;

        if (SegmentsFound.Match(detail) is { Success: true } found
            && TryNumber(found.Groups[1].Value, out var segments))
            return UiText.Get("共发现 {0} 个文本段", segments);

        if (OutputsGenerated.Match(detail) is { Success: true } generated
            && TryNumber(generated.Groups[1].Value, out var files))
            return UiText.Get("已生成 {0} 个文件", files);

        if (PagesGenerated.Match(detail) is { Success: true } pages
            && TryNumber(pages.Groups[1].Value, out var pageCount))
        {
            if (pages.Groups[2].Success && TryNumber(pages.Groups[2].Value, out var pending))
                return UiText.Get("已生成 {0} 个正文页面，{1} 段待复核", pageCount, pending);
            return UiText.Get("已生成 {0} 个正文页面", pageCount);
        }

        if (AiBatchRequested.Match(detail) is { Success: true } requested
            && TryNumber(requested.Groups[1].Value, out var first)
            && TryNumber(requested.Groups[2].Value, out var last)
            && TryNumber(requested.Groups[3].Value, out var total))
            return UiText.Get("正在请求 AI：{0}-{1} / {2} 项", first, last, total);

        if (AiBatchProcessed.Match(detail) is { Success: true } processed
            && TryNumber(processed.Groups[1].Value, out var done)
            && TryNumber(processed.Groups[2].Value, out var count))
            return UiText.Get("已处理 {0} / {1} 项", done, count);

        // Only exact engine phrases are translated; anything else is book
        // content (file names, source sentences) and must pass through.
        return detail switch
        {
            "正在准备翻译…" => UiText.Get("正在准备翻译…"),
            "正在加入书库…" => UiText.Get("正在加入书库…"),
            "翻译完成" => UiText.Get("翻译完成"),
            "原文件已复制" => UiText.Get("原文件已复制"),
            "双语对照" => UiText.Get("双语对照"),
            "单翻译" => UiText.Get("单翻译"),
            "转换器已启动…" => UiText.Get("转换器已启动…"),
            "正在安装 Calibre KFX Input 插件…" => UiText.Get("正在安装 Calibre KFX Input 插件…"),
            "KFX Input 插件已就绪，正在启动转换…" => UiText.Get("KFX Input 插件已就绪，正在启动转换…"),
            "转换完成。" => UiText.Get("转换完成。"),
            _ => detail
        };
    }

    private static string? KnownStage(string stage) => stage switch
    {
        "正在准备" => UiText.Get("正在准备"),
        "正在翻译" => UiText.Get("正在翻译"),
        "正在扫描 EPUB" => UiText.Get("正在扫描 EPUB"),
        "正在生成 EPUB" => UiText.Get("正在生成 EPUB"),
        "完成" => UiText.Get("完成"),
        "部分完成" => UiText.Get("部分完成"),
        "正在生成拼音" => UiText.Get("正在生成拼音"),
        "正在复核疑问拼音" => UiText.Get("正在复核疑问拼音"),
        "下载 g2pW 模型" => UiText.Get("下载 g2pW 模型"),
        "解压 g2pW 模型" => UiText.Get("解压 g2pW 模型"),
        "下载 BERT 词表" => UiText.Get("下载 BERT 词表"),
        "下载拼音映射" => UiText.Get("下载拼音映射"),
        _ => null
    };

    private static bool TryNumber(string value, out long number) =>
        long.TryParse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out number);
}
