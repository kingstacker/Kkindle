using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotNetG2P.Chinese;
using HtmlAgilityPack;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Creates a new EPUB whose Chinese text is annotated with HTML ruby markup.
/// The source archive is copied entry-for-entry, so images, stylesheets,
/// navigation and metadata remain part of the generated book.
/// </summary>
public sealed class PinyinBookService : IPinyinBookService
{
    private const long MaxArchiveEntryBytes = 128L * 1024 * 1024;
    private const int MaxAiReviewContextCharacters = 120;
    private const int PinyinCacheVersion = 1;
    private const string PinyinCacheFileSuffix = ".kkindle-pinyin-cache.jsonl";

    private static readonly JsonSerializerOptions PinyinCacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<char, (char Base, int Tone)> ToneMarks =
        new Dictionary<char, (char Base, int Tone)>
        {
            ['ā'] = ('a', 1), ['á'] = ('a', 2), ['ǎ'] = ('a', 3), ['à'] = ('a', 4),
            ['ē'] = ('e', 1), ['é'] = ('e', 2), ['ě'] = ('e', 3), ['è'] = ('e', 4),
            ['ī'] = ('i', 1), ['í'] = ('i', 2), ['ǐ'] = ('i', 3), ['ì'] = ('i', 4),
            ['ō'] = ('o', 1), ['ó'] = ('o', 2), ['ǒ'] = ('o', 3), ['ò'] = ('o', 4),
            ['ū'] = ('u', 1), ['ú'] = ('u', 2), ['ǔ'] = ('u', 3), ['ù'] = ('u', 4),
            ['ǖ'] = ('ü', 1), ['ǘ'] = ('ü', 2), ['ǚ'] = ('ü', 3), ['ǜ'] = ('ü', 4)
        };

    private static readonly IReadOnlyDictionary<(char Base, int Tone), char> ToneMarkByBaseAndTone =
        ToneMarks.ToDictionary(pair => pair.Value, pair => pair.Key);

    private static readonly HashSet<string> SkippedElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "noscript", "svg", "math", "title",
        "pre", "code", "ruby", "rt", "rp"
    };

    private readonly IBookFormatConverter? _formatConverter;
    private readonly PinyinPhraseDictionary _phraseDictionary;
    private readonly ChineseG2PEngine _pinyinEngine;
    private readonly AiChatClient? _aiChatClient;
    private readonly AiConnectionSettings? _aiSettings;

    public PinyinBookService(
        IBookFormatConverter? formatConverter = null,
        AiChatClient? aiChatClient = null,
        AiConnectionSettings? aiSettings = null)
    {
        _formatConverter = formatConverter;
        _phraseDictionary = PinyinPhraseDictionary.LoadEmbedded();
        _pinyinEngine = new ChineseG2PEngine();
        _aiChatClient = aiChatClient;
        _aiSettings = aiSettings?.Clone();
    }

    public async Task<PinyinBookResumeInfo?> FindResumeAsync(
        string sourcePath,
        string destinationPath,
        PinyinBookOptions options,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source)
            || !Path.GetExtension(destination).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            return null;

        var cachePath = GetPinyinCachePath(destination);
        if (!File.Exists(cachePath)) return null;

        try
        {
            var sourceHash = await Hashing.Sha256Async(source, cancellationToken);
            var snapshot = await TryLoadPinyinCacheAsync(
                cachePath,
                source,
                sourceHash,
                PinyinBookOptions.Normalize(options),
                cancellationToken);
            if (snapshot is null) return null;

            var totalSegments = GetCachedTotalSegments(snapshot);
            if (totalSegments <= 0) return null;
            var completedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == PinyinBookSegmentStatus.Completed
                && !string.IsNullOrWhiteSpace(item.AnnotatedMarkup));
            var failedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == PinyinBookSegmentStatus.Failed);
            return new PinyinBookResumeInfo(
                snapshot.Header.Options,
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
            System.Diagnostics.Debug.WriteLine(
                $"Unable to inspect pinyin cache: {exception.Message}");
            return null;
        }
    }

    public async Task<PinyinBookResult> GenerateAsync(
        string sourcePath,
        string destinationPath,
        PinyinBookOptions? options = null,
        IProgress<PinyinBookProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PinyinBookResumeMode resumeMode = PinyinBookResumeMode.Restart)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("待处理的书籍文件不存在。", source);
        if (!string.Equals(Path.GetExtension(destination), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("拼音版目前输出为 EPUB 格式。 ");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拼音版必须输出为新文件，不能覆盖原书。 ");

        var normalizedOptions = PinyinBookOptions.Normalize(options);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        var cachePath = GetPinyinCachePath(destination);
        if (File.Exists(destination))
        {
            if (!File.Exists(cachePath))
                throw new IOException("拼音版输出文件已经存在，请换一个文件名。 ");

            // The output is managed together with the resume cache. A previous
            // run may have written the archive before AI review failed; rebuild
            // that managed file from the source when the user retries.
            File.Delete(destination);
        }

        var sourceHash = await Hashing.Sha256Async(source, cancellationToken);

        string? temporaryDirectory = null;
        var epubSource = source;
        try
        {
            if (!string.Equals(Path.GetExtension(source), ".epub", StringComparison.OrdinalIgnoreCase))
            {
                if (_formatConverter is null)
                    throw new NotSupportedException("处理非 EPUB 书籍需要先配置 Calibre。 ");

                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "Kkindle",
                    "pinyin",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                epubSource = Path.Combine(temporaryDirectory, "source.epub");
                Report(progress, new PinyinBookProgress(
                    "正在准备",
                    Path.GetFileName(source),
                    0,
                    0,
                    0));

                var conversionProgress = progress is null
                    ? null
                    : new Progress<FormatConversionProgress>(value => Report(
                        progress,
                        new PinyinBookProgress(
                            "正在准备",
                            value.Message,
                            0,
                            0,
                            0)));
                await _formatConverter.ConvertAsync(
                    source,
                    epubSource,
                    conversionProgress,
                    cancellationToken);
            }

            return await GenerateFromEpubAsync(
                epubSource,
                destination,
                normalizedOptions,
                progress,
                cancellationToken,
                source,
                sourceHash,
                resumeMode);
        }
        finally
        {
            if (temporaryDirectory is not null)
                TryDeleteDirectory(temporaryDirectory);
        }
    }

    private async Task<PinyinBookResult> GenerateFromEpubAsync(
        string epubSourcePath,
        string destinationPath,
        PinyinBookOptions options,
        IProgress<PinyinBookProgress>? progress,
        CancellationToken cancellationToken,
        string cacheSourcePath,
        string cacheSourceHash,
        PinyinBookResumeMode resumeMode)
    {
        var pages = new List<PinyinPage>();
        var xhtmlEntryNames = new List<string>();
        var hasMimetype = false;

        using (var source = ZipFile.OpenRead(epubSourcePath))
        {
            foreach (var entry in source.Entries)
            {
                EnsureSafeArchiveEntryName(entry.FullName);
                if (entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase))
                    hasMimetype = true;
                if (IsXhtmlEntry(entry.FullName))
                    xhtmlEntryNames.Add(entry.FullName);
            }

            if (!hasMimetype)
                throw new InvalidDataException("EPUB 缺少 mimetype 文件。 ");

            var totalPages = xhtmlEntryNames.Count;
            Report(progress, new PinyinBookProgress(
                "正在扫描 EPUB",
                Path.GetFileName(epubSourcePath),
                0,
                totalPages,
                0,
                OverallPercentage: 0));

            for (var index = 0; index < xhtmlEntryNames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = source.GetEntry(xhtmlEntryNames[index]);
                if (entry is null) continue;
                if (entry.Length > MaxArchiveEntryBytes)
                {
                    Report(progress, new PinyinBookProgress(
                        "正在扫描 EPUB",
                        entry.FullName,
                        index + 1,
                        totalPages,
                        0,
                        OverallPercentage: totalPages <= 0 ? 0 : (index + 1) * 15d / totalPages));
                    continue;
                }

                var markup = await ReadArchiveTextAsync(entry, cancellationToken);
                if (TryParseDocument(markup, out var document))
                {
                    var body = document.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
                    if (body is not null)
                    {
                        var pageSegments = GetPinyinTextNodes(body)
                            .Select(node => new PinyinSegment(
                                -1,
                                entry.FullName,
                                node,
                                node.Value))
                            .ToArray();
                        pages.Add(new PinyinPage(entry.FullName, document, pageSegments));
                    }
                }

                Report(progress, new PinyinBookProgress(
                    "正在扫描 EPUB",
                    entry.FullName,
                    index + 1,
                    totalPages,
                    0,
                    OverallPercentage: totalPages <= 0 ? 0 : 15 + (index + 1) * 15d / totalPages));
            }
        }

        var nextSegmentIndex = 0;
        pages = pages
            .Select(page =>
            {
                var segments = page.Segments
                    .Select(segment => segment with { Index = nextSegmentIndex++ })
                    .ToArray();
                return page with { Segments = segments };
            })
            .ToList();
        var segments = pages.SelectMany(page => page.Segments).ToArray();
        if (segments.Length == 0)
            throw new InvalidDataException("EPUB 中没有找到可注音的中文正文。 ");

        var totalSegments = segments.Length;
        var totalCharacters = segments.Sum(segment => (long)segment.OriginalText.Length);
        var cachePath = GetPinyinCachePath(destinationPath);
        var resumeSnapshot = resumeMode == PinyinBookResumeMode.Resume
            ? await TryLoadPinyinCacheAsync(
                cachePath,
                cacheSourcePath,
                cacheSourceHash,
                options,
                cancellationToken)
            : null;
        PinyinCacheWriter? cacheWriter = null;
        var generationCompleted = false;
        try
        {
            cacheWriter = new PinyinCacheWriter(
                cachePath,
                new PinyinCacheHeader(
                    "header",
                    PinyinCacheVersion,
                    cacheSourcePath,
                    cacheSourceHash,
                    options,
                    totalSegments,
                    DateTimeOffset.UtcNow),
                append: resumeSnapshot is not null);

            var renderedDocuments = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            var reviewTargets = new List<PinyinReviewTarget>();
            var workStates = new List<PinyinWorkState>();
            var processedSegments = 0;
            var annotatedCharacters = 0;
            var reviewCandidateCount = 0;
            var reviewedCandidates = 0;

            Report(progress, new PinyinBookProgress(
                "正在生成拼音",
                $"共发现 {totalSegments:N0} 个正文段",
                0,
                xhtmlEntryNames.Count,
                0,
                OverallPercentage: 30,
                ProcessedSegments: 0,
                TotalSegments: totalSegments));

            void ReportSegment(
                PinyinSegment segment,
                int pageIndex,
                PinyinBookSegmentStatus status,
                string annotatedText,
                string processFlow,
                string? errorMessage,
                int currentProcessedSegments,
                int currentAnnotatedCharacters,
                int currentReviewCandidates,
                int currentReviewedCandidates,
                string? annotatedMarkup,
                int segmentAnnotatedCharacters,
                int segmentReviewCandidates,
                int segmentReviewedCandidates,
                bool persist)
            {
                var update = new PinyinBookSegmentProgress(
                    segment.Index,
                    segment.EntryName,
                    segment.OriginalText,
                    annotatedText,
                    status,
                    processFlow,
                    errorMessage);
                if (persist)
                {
                    cacheWriter.Append(
                        update,
                        annotatedMarkup ?? string.Empty,
                        segmentAnnotatedCharacters,
                        segmentReviewCandidates,
                        segmentReviewedCandidates);
                }

                var localPercentage = totalSegments <= 0
                    ? 0
                    : Math.Min(70, currentProcessedSegments * 70d / totalSegments);
                Report(progress, new PinyinBookProgress(
                    status == PinyinBookSegmentStatus.Failed ? "正在生成拼音" : "正在生成拼音",
                    segment.EntryName,
                    Math.Min(pageIndex + 1, xhtmlEntryNames.Count),
                    xhtmlEntryNames.Count,
                    currentAnnotatedCharacters,
                    currentReviewCandidates,
                    currentReviewedCandidates,
                    localPercentage,
                    update,
                    currentProcessedSegments,
                    totalSegments));
            }

            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var page = pages[pageIndex];
                foreach (var segment in page.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PinyinCacheSegmentLine? cached = null;
                    var canRestore = resumeSnapshot is not null
                        && resumeSnapshot.Segments.TryGetValue(segment.Index, out cached)
                        && cached is not null
                        && cached.Status == PinyinBookSegmentStatus.Completed
                        && cached.AnnotatedMarkup.Length > 0
                        && string.Equals(cached.OriginalText, segment.OriginalText, StringComparison.Ordinal);
                    if (canRestore && cached is not null && !TryApplyAnnotatedMarkup(segment, cached.AnnotatedMarkup))
                        canRestore = false;
                    if (canRestore && cached is not null)
                    {
                        renderedDocuments[page.EntryName] = page.Document;
                        reviewCandidateCount += cached.ReviewCandidateCount;
                        reviewedCandidates += cached.ReviewedCandidateCount;
                        ReportSegment(
                            segment,
                            pageIndex,
                            PinyinBookSegmentStatus.Processing,
                            cached.AnnotatedText,
                            "读取原文 → 恢复本地缓存",
                            null,
                            processedSegments,
                            annotatedCharacters,
                            reviewCandidateCount,
                            reviewedCandidates,
                            cached.AnnotatedMarkup,
                            cached.AnnotatedCharacterCount,
                            cached.ReviewCandidateCount,
                            cached.ReviewedCandidateCount,
                            persist: false);
                        processedSegments++;
                        annotatedCharacters += cached.AnnotatedCharacterCount;
                        ReportSegment(
                            segment,
                            pageIndex,
                            PinyinBookSegmentStatus.Completed,
                            cached.AnnotatedText,
                            "读取原文 → 恢复本地缓存 → 写入 EPUB",
                            null,
                            processedSegments,
                            annotatedCharacters,
                            reviewCandidateCount,
                            reviewedCandidates,
                            cached.AnnotatedMarkup,
                            cached.AnnotatedCharacterCount,
                            cached.ReviewCandidateCount,
                            cached.ReviewedCandidateCount,
                            persist: true);
                        continue;
                    }

                    var annotation = AnnotateTextNode(segment, options);
                    renderedDocuments[page.EntryName] = page.Document;
                    reviewTargets.AddRange(annotation.Targets);
                    reviewCandidateCount += annotation.Targets.Count;
                    var localMarkup = SerializeNodes(annotation.Nodes);
                    ReportSegment(
                        segment,
                        pageIndex,
                        PinyinBookSegmentStatus.Processing,
                        annotation.AnnotatedText,
                        annotation.Targets.Count > 0
                            ? "读取原文 → 本地注音 → 检测疑问多音字"
                            : "读取原文 → 本地注音 → 写入 EPUB",
                        null,
                        processedSegments,
                        annotatedCharacters,
                        reviewCandidateCount,
                        reviewedCandidates,
                        localMarkup,
                        annotation.AnnotatedCharacters,
                        annotation.Targets.Count,
                        0,
                        persist: true);
                    workStates.Add(new PinyinWorkState(segment, pageIndex, annotation));
                    annotatedCharacters += annotation.AnnotatedCharacters;
                }
            }

            var reviewResult = await ReviewPinyinAsync(
                reviewTargets,
                options,
                processedSegments,
                totalSegments,
                xhtmlEntryNames.Count,
                annotatedCharacters,
                reviewCandidateCount,
                progress,
                cancellationToken);
            reviewedCandidates += reviewResult.ReviewedCount;

            var failedSegments = 0;
            foreach (var work in workStates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasTargets = work.Annotation.Targets.Count > 0;
                var allTargetsChecked = !hasTargets
                    || work.Annotation.Targets.All(target =>
                        reviewResult.CheckedSegmentIndexes.Contains(target.SegmentIndex));
                var failed = reviewResult.Error is not null && hasTargets && !allTargetsChecked;
                var finalMarkup = SerializeNodes(work.Annotation.Nodes);
                var finalText = CreateAnnotatedDisplayText(work.Annotation.Nodes);
                if (failed) failedSegments++;
                processedSegments++;
                ReportSegment(
                    work.Segment,
                    work.PageIndex,
                    failed ? PinyinBookSegmentStatus.Failed : PinyinBookSegmentStatus.Completed,
                    finalText,
                    failed
                        ? "读取原文 → 本地注音 → AI 复核失败，保留本地拼音"
                        : hasTargets
                            ? "读取原文 → 本地注音 → AI 复核 → 写入 EPUB"
                            : "读取原文 → 本地注音 → 写入 EPUB",
                    failed ? reviewResult.Error : null,
                    processedSegments,
                    annotatedCharacters,
                    reviewCandidateCount,
                    reviewedCandidates,
                    finalMarkup,
                    work.Annotation.AnnotatedCharacters,
                    work.Annotation.Targets.Count,
                    work.Annotation.Targets.Count(target =>
                        reviewResult.CheckedSegmentIndexes.Contains(target.SegmentIndex)),
                    persist: true);
            }

            var renderedPages = renderedDocuments.ToDictionary(
                pair => pair.Key,
                pair => Encoding.UTF8.GetBytes(SerializeDocument(pair.Value)),
                StringComparer.OrdinalIgnoreCase);
            Report(progress, new PinyinBookProgress(
                "正在生成 EPUB",
                Path.GetFileName(destinationPath),
                xhtmlEntryNames.Count,
                xhtmlEntryNames.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                95,
                ProcessedSegments: processedSegments,
                TotalSegments: totalSegments));
            await WriteArchiveAsync(epubSourcePath, destinationPath, renderedPages, cancellationToken);

            generationCompleted = failedSegments == 0 && reviewResult.Error is null;
            var result = new PinyinBookResult(
                cacheSourcePath,
                destinationPath,
                xhtmlEntryNames.Count,
                renderedPages.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                reviewResult.Error,
                failedSegments);
            Report(progress, new PinyinBookProgress(
                failedSegments == 0 ? "完成" : "部分完成",
                failedSegments == 0
                    ? $"已生成 {renderedPages.Count} 个正文页面"
                    : $"已生成 {renderedPages.Count} 个正文页面，{failedSegments:N0} 段待复核",
                xhtmlEntryNames.Count,
                xhtmlEntryNames.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                100,
                ProcessedSegments: processedSegments,
                TotalSegments: totalSegments));
            return result;
        }
        finally
        {
            cacheWriter?.Dispose();
            if (generationCompleted)
                TryDeletePinyinCache(cachePath);
        }
    }

    private PinyinAnnotationResult AnnotateTextNode(
        PinyinSegment segment,
        PinyinBookOptions options)
    {
        var textNode = segment.TextNode;
        var value = segment.OriginalText;
        if (value.Length == 0 || !value.Any(IsHanCharacter))
            return new PinyinAnnotationResult([], 0, [], string.Empty);

        var parent = textNode.Parent;
        if (parent is null)
            return new PinyinAnnotationResult([], 0, [], string.Empty);

        var nodes = new List<XNode>();
        var reviewTargets = new List<PinyinReviewTarget>();
        var annotatedCharacters = 0;
        var cursor = 0;
        while (cursor < value.Length)
        {
            if (!IsHanCharacter(value[cursor]))
            {
                var plainStart = cursor;
                while (cursor < value.Length && !IsHanCharacter(value[cursor])) cursor++;
                nodes.Add(new XText(value[plainStart..cursor]));
                continue;
            }

            var hanStart = cursor;
            while (cursor < value.Length && IsHanCharacter(value[cursor])) cursor++;
            var hanText = value[hanStart..cursor];
            var pinyins = _pinyinEngine.ToPinyinList(hanText, GetPinyinStyle(options.OutputStyle));
            var decisions = AnalyzeHanRun(hanText, pinyins);
            for (var index = 0; index < hanText.Length; index++)
            {
                var character = hanText[index];
                var pinyin = index < pinyins.Length ? pinyins[index] : string.Empty;
                if (string.IsNullOrWhiteSpace(pinyin)
                    || !IsHanCharacter(character)
                    || !_pinyinEngine.ContainsChar(character))
                {
                    nodes.Add(new XText(character.ToString()));
                    continue;
                }

                var ns = parent.Name.Namespace;
                var ruby = new XElement(
                    ns + "ruby",
                    new XText(character.ToString()),
                    new XElement(ns + "rt", pinyin));
                nodes.Add(ruby);
                var decision = index < decisions.Count ? decisions[index] : null;
                if (decision is { Candidates.Count: > 1 })
                {
                    reviewTargets.Add(new PinyinReviewTarget(
                        segment.Index,
                        ruby,
                        character,
                        pinyin,
                        decision.Candidates,
                        ExtractReviewContext(value, hanStart + index)));
                }
                annotatedCharacters++;
            }
        }

        textNode.ReplaceWith(nodes);
        return new PinyinAnnotationResult(
            nodes,
            annotatedCharacters,
            reviewTargets,
            CreateAnnotatedDisplayText(nodes));
    }

    private static IReadOnlyList<XText> GetPinyinTextNodes(XElement body)
    {
        var nodes = new List<XText>();
        foreach (var node in body.DescendantNodes().OfType<XText>())
        {
            if (!node.Value.Any(IsHanCharacter)) continue;

            var skipped = false;
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (ShouldSkipElement(parent))
                {
                    skipped = true;
                    break;
                }
                if (ReferenceEquals(parent, body)) break;
            }

            if (!skipped) nodes.Add(node);
        }
        return nodes;
    }

    private IReadOnlyList<LocalPinyinDecision> AnalyzeHanRun(
        string hanText,
        IReadOnlyList<string> pinyins)
    {
        var decisions = new List<LocalPinyinDecision>(hanText.Length);
        var cursor = 0;
        while (cursor < hanText.Length)
        {
            var phraseLength = _phraseDictionary.FindLongestMatch(
                hanText,
                cursor,
                out _);
            if (phraseLength > 1)
            {
                for (var offset = 0; offset < phraseLength && cursor + offset < hanText.Length; offset++)
                {
                    var pinyin = cursor + offset < pinyins.Count
                        ? pinyins[cursor + offset]
                        : string.Empty;
                    decisions.Add(new LocalPinyinDecision(
                        pinyin,
                        Array.Empty<string>(),
                        FromPhraseDictionary: true));
                }

                cursor += phraseLength;
                continue;
            }

            var character = hanText[cursor];
            var candidates = _pinyinEngine.LookupChar(character)
                .Select(NormalizeToneMarkedPinyin)
                .Where(candidate => candidate.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentPinyin = cursor < pinyins.Count ? pinyins[cursor] : string.Empty;
            decisions.Add(new LocalPinyinDecision(
                currentPinyin,
                candidates,
                FromPhraseDictionary: false));
            cursor++;
        }

        while (decisions.Count < hanText.Length)
        {
            var index = decisions.Count;
            decisions.Add(new LocalPinyinDecision(
                index < pinyins.Count ? pinyins[index] : string.Empty,
                Array.Empty<string>(),
                FromPhraseDictionary: false));
        }

        return decisions;
    }

    private static string ExtractReviewContext(string value, int characterIndex)
    {
        if (value.Length <= MaxAiReviewContextCharacters) return value;

        var half = MaxAiReviewContextCharacters / 2;
        var start = Math.Clamp(characterIndex - half, 0, Math.Max(0, value.Length - MaxAiReviewContextCharacters));
        var length = Math.Min(MaxAiReviewContextCharacters, value.Length - start);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < value.Length ? "…" : string.Empty;
        return prefix + value.Substring(start, length) + suffix;
    }

    private async Task<AiReviewResult> ReviewPinyinAsync(
        IReadOnlyList<PinyinReviewTarget> targets,
        PinyinBookOptions options,
        int processedSegments,
        int totalSegments,
        int totalPages,
        int annotatedCharacters,
        int reviewCandidateCount,
        IProgress<PinyinBookProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return new AiReviewResult(0, null, new HashSet<int>());

        if (!options.EnableAiReview)
        {
            ReportReviewSkipped(
                "已跳过 AI 复核",
                "本次未启用 AI，保留本地拼音。",
                processedSegments,
                totalSegments,
                totalPages,
                annotatedCharacters,
                reviewCandidateCount,
                progress);
            return new AiReviewResult(
                0,
                null,
                targets.Select(target => target.SegmentIndex).ToHashSet());
        }

        if (_aiChatClient is null || _aiSettings is null || !_aiSettings.IsConfigured)
        {
            ReportReviewSkipped(
                "已跳过 AI 复核",
                "AI 未配置，待下次复核。",
                processedSegments,
                totalSegments,
                totalPages,
                annotatedCharacters,
                reviewCandidateCount,
                progress);
            return new AiReviewResult(0, "AI 未配置", new HashSet<int>());
        }

        var requests = targets
            .GroupBy(CreateReviewKey, StringComparer.Ordinal)
            .Select((group, index) =>
            {
                var first = group.First();
                return new PinyinReviewRequest(
                    index + 1,
                    first.Character,
                    NormalizeToneMarkedPinyin(first.CurrentPinyin),
                    first.Candidates,
                    first.Context,
                    group.ToArray());
            })
            .ToArray();

        var reviewedCount = 0;
        string? error = null;
        var checkedSegmentIndexes = new HashSet<int>();
        for (var offset = 0; offset < requests.Length; offset += options.AiReviewBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = requests
                .Skip(offset)
                .Take(options.AiReviewBatchSize)
                .ToArray();
            var batchEnd = offset + batch.Length;
            Report(progress, new PinyinBookProgress(
                "正在复核疑问拼音",
                $"正在请求 AI：{offset + 1}-{batchEnd} / {requests.Length} 项",
                totalPages,
                totalPages,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCount,
                70 + offset * 20d / Math.Max(1, requests.Length),
                ProcessedSegments: processedSegments,
                TotalSegments: totalSegments));

            string answer;
            try
            {
                answer = await _aiChatClient.CompleteAsync(
                    _aiSettings,
                    AiReviewInstructions,
                    BuildAiReviewQuestion(batch),
                    Array.Empty<AiConversationTurn>(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                error = $"AI 请求失败：{exception.Message}";
                ReportReviewSkipped(
                    "AI 复核失败",
                    "请求失败，已保留本地拼音。",
                    processedSegments,
                    totalSegments,
                    totalPages,
                    annotatedCharacters,
                    reviewCandidateCount,
                    progress,
                    reviewedCount,
                    90);
                break;
            }

            if (!TryReadAiReview(answer, batch, out var decisions))
            {
                error = "AI 返回格式无法识别";
                ReportReviewSkipped(
                    "AI 复核失败",
                    "返回格式无法识别，已保留本地拼音。",
                    processedSegments,
                    totalSegments,
                    totalPages,
                    annotatedCharacters,
                    reviewCandidateCount,
                    progress,
                    reviewedCount,
                    90);
                break;
            }

            foreach (var request in batch)
            {
                foreach (var target in request.Targets)
                    checkedSegmentIndexes.Add(target.SegmentIndex);
                if (!decisions.TryGetValue(request.Id, out var selectedPinyin)) continue;
                var renderedPinyin = ApplyPinyinStyle(selectedPinyin, options.OutputStyle);
                foreach (var target in request.Targets)
                {
                    var rt = target.Ruby.Elements().FirstOrDefault(element =>
                        element.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase));
                    if (rt is null) continue;
                    rt.Value = renderedPinyin;
                    reviewedCount++;
                }
            }

            Report(progress, new PinyinBookProgress(
                "正在复核疑问拼音",
                $"已复核 {reviewedCount:N0} 项",
                totalPages,
                totalPages,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCount,
                70 + batchEnd * 20d / Math.Max(1, requests.Length),
                ProcessedSegments: processedSegments,
                TotalSegments: totalSegments));
        }

        return new AiReviewResult(reviewedCount, error, checkedSegmentIndexes);
    }

    private static string BuildAiReviewQuestion(IReadOnlyList<PinyinReviewRequest> requests)
    {
        var payload = requests.Select(request => new
        {
            id = request.Id,
            character = request.Character.ToString(),
            context = request.Context,
            current = request.CurrentPinyin,
            candidates = request.Candidates
        });
        return "请判断下面这些汉字在各自上下文中的普通话拼音。"
            + "每项只能从 candidates 中选择一个；无法确定时 pinyin 返回 null。"
            + "不要改写句子，不要添加候选之外的读音。只返回 JSON 数组："
            + Environment.NewLine
            + JsonSerializer.Serialize(payload);
    }

    private bool TryReadAiReview(
        string answer,
        IReadOnlyList<PinyinReviewRequest> requests,
        out Dictionary<int, string> decisions)
    {
        decisions = new Dictionary<int, string>();
        var json = ExtractJsonPayload(answer);
        if (json.Length == 0) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : FindReviewItems(root);
            if (items.ValueKind != JsonValueKind.Array) return false;

            var requestById = requests.ToDictionary(request => request.Id);
            foreach (var item in items.EnumerateArray())
            {
                if (!TryReadReviewId(item, out var id)
                    || !requestById.TryGetValue(id, out var request))
                    continue;
                if (!item.TryGetProperty("pinyin", out var pinyinValue)
                    || pinyinValue.ValueKind != JsonValueKind.String)
                    continue;

                var selected = NormalizeToneMarkedPinyin(pinyinValue.GetString() ?? string.Empty);
                if (selected.Length == 0 || selected.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    continue;
                var allowed = request.Candidates.FirstOrDefault(candidate =>
                    NormalizeToneMarkedPinyin(candidate).Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (allowed is not null)
                    decisions[id] = NormalizeToneMarkedPinyin(allowed);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement FindReviewItems(JsonElement root)
    {
        foreach (var propertyName in new[] { "items", "results", "decisions" })
        {
            if (root.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        return default;
    }

    private static bool TryReadReviewId(JsonElement item, out int id)
    {
        id = 0;
        if (!item.TryGetProperty("id", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out id);
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out id);
    }

    private static string ExtractJsonPayload(string value)
    {
        var answer = value.Trim();
        var fencedStart = answer.IndexOf("```", StringComparison.Ordinal);
        if (fencedStart >= 0)
        {
            var contentStart = answer.IndexOf('\n', fencedStart);
            var contentEnd = answer.IndexOf("```", contentStart >= 0 ? contentStart + 1 : fencedStart + 3, StringComparison.Ordinal);
            if (contentStart >= 0 && contentEnd > contentStart)
                answer = answer[(contentStart + 1)..contentEnd].Trim();
        }

        var arrayStart = answer.IndexOf('[');
        var arrayEnd = answer.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
            return answer[arrayStart..(arrayEnd + 1)];

        var objectStart = answer.IndexOf('{');
        var objectEnd = answer.LastIndexOf('}');
        return objectStart >= 0 && objectEnd > objectStart
            ? answer[objectStart..(objectEnd + 1)]
            : string.Empty;
    }

    private static string CreateReviewKey(PinyinReviewTarget target) =>
        $"{target.Character}\u001F{NormalizeToneMarkedPinyin(target.CurrentPinyin)}\u001F"
        + $"{target.Context}\u001F{string.Join('\u001E', target.Candidates)}";

    private static string NormalizeToneMarkedPinyin(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Replace("u:", "ü", StringComparison.Ordinal)
            .Replace("v", "ü", StringComparison.Ordinal)
            .Where(character => !char.IsWhiteSpace(character))
            .ToArray());
        if (normalized.Any(char.IsDigit))
            normalized = ConvertToneNumberToMarked(normalized);
        return normalized.Trim().Trim('`', '"', '\'', '。', '，', ',', '.');
    }

    private static string ApplyPinyinStyle(string toneMarkedPinyin, PinyinBookOutputStyle style) => style switch
    {
        PinyinBookOutputStyle.ToneNumber => ConvertToneMarkedToNumber(toneMarkedPinyin),
        PinyinBookOutputStyle.NoTone => RemoveToneMarks(toneMarkedPinyin),
        _ => toneMarkedPinyin
    };

    private static string ConvertToneNumberToMarked(string value)
    {
        var tone = 5;
        var baseText = value;
        if (value.Length > 0 && value[^1] is >= '1' and <= '5')
        {
            tone = value[^1] - '0';
            baseText = value[..^1];
        }

        baseText = RemoveToneMarks(baseText);
        if (tone is < 1 or > 4) return baseText;

        var placement = FindToneVowelIndex(baseText);
        if (placement < 0) return baseText;
        var vowel = baseText[placement];
        return baseText[..placement]
            + (ToneMarkByBaseAndTone.TryGetValue((vowel, tone), out var marked)
                ? marked.ToString()
                : vowel.ToString())
            + baseText[(placement + 1)..];
    }

    private static string ConvertToneMarkedToNumber(string value)
    {
        var tone = 5;
        var builder = new StringBuilder(value.Length + 1);
        foreach (var character in value)
        {
            if (ToneMarks.TryGetValue(character, out var toneMark))
            {
                tone = toneMark.Tone;
                builder.Append(toneMark.Base);
            }
            else builder.Append(character);
        }

        if (tone is >= 1 and <= 4) builder.Append(tone);
        return builder.ToString();
    }

    private static string RemoveToneMarks(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (ToneMarks.TryGetValue(character, out var toneMark))
            {
                builder.Append(toneMark.Base);
                continue;
            }

            if (character is >= '1' and <= '5') continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static int FindToneVowelIndex(string value)
    {
        var a = value.IndexOf('a');
        if (a >= 0) return a;
        var e = value.IndexOf('e');
        if (e >= 0) return e;
        var ou = value.IndexOf("ou", StringComparison.Ordinal);
        if (ou >= 0) return ou;

        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (value[index] is 'a' or 'e' or 'i' or 'o' or 'u' or 'ü')
                return index;
        }
        return -1;
    }

    private static void ReportReviewSkipped(
        string stage,
        string currentItem,
        int processedSegments,
        int totalSegments,
        int totalPages,
        int annotatedCharacters,
        int reviewCandidateCount,
        IProgress<PinyinBookProgress>? progress,
        int reviewedCandidates = 0,
        double overallPercentage = 70) => Report(progress, new PinyinBookProgress(
        stage,
        currentItem,
        totalPages,
        totalPages,
        annotatedCharacters,
        reviewCandidateCount,
        reviewedCandidates,
        overallPercentage,
        ProcessedSegments: processedSegments,
        TotalSegments: totalSegments));

    private const string AiReviewInstructions = "你是中文普通话拼音校对器。只处理指定的多音字，严格遵守用户给出的候选读音。";

    private static bool ShouldSkipElement(XElement element) =>
        SkippedElementNames.Contains(element.Name.LocalName);

    private static PinyinStyle GetPinyinStyle(PinyinBookOutputStyle style) => style switch
    {
        PinyinBookOutputStyle.ToneNumber => PinyinStyle.ToneNumber,
        PinyinBookOutputStyle.NoTone => PinyinStyle.Normal,
        _ => PinyinStyle.ToneMarked
    };

    private static bool IsHanCharacter(char character) =>
        character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';

    private static async Task WriteArchiveAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, byte[]> renderedPages,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            var entries = source.Entries
                .OrderBy(entry => entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();
            if (!entries.Any(entry => entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("EPUB 缺少 mimetype 文件。 ");

            using (var destination = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var sourceEntry in entries)
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
                    if (renderedPages.TryGetValue(sourceEntry.FullName, out var rendered))
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

            File.Move(temporaryPath, destinationPath, overwrite: false);
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

    private static string SerializeNodes(IReadOnlyList<XNode> nodes)
    {
        var builder = new StringBuilder();
        using var stringWriter = new StringWriter(
            builder,
            System.Globalization.CultureInfo.InvariantCulture);
        using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            Indent = false
        }))
        {
            foreach (var node in nodes)
                node.WriteTo(xmlWriter);
        }
        return builder.ToString();
    }

    private static string CreateAnnotatedDisplayText(IReadOnlyList<XNode> nodes)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
                continue;
            }

            if (node is not XElement element) continue;
            if (element.Name.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase))
            {
                var baseText = string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value));
                var pronunciation = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase));
                builder.Append(baseText);
                if (pronunciation is not null)
                    builder.Append('(').Append(pronunciation.Value).Append(')');
                continue;
            }

            builder.Append(element.Value);
        }
        return builder.ToString().Trim();
    }

    private static bool TryApplyAnnotatedMarkup(PinyinSegment segment, string markup)
    {
        if (segment.TextNode.Parent is null || string.IsNullOrWhiteSpace(markup)) return false;
        try
        {
            var wrapper = XElement.Parse(
                $"<kkindle-fragment>{markup}</kkindle-fragment>",
                LoadOptions.PreserveWhitespace);
            segment.TextNode.ReplaceWith(wrapper.Nodes().ToList());
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
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
        if (segments.Any(segment => segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");
    }

    private static void Report(
        IProgress<PinyinBookProgress>? progress,
        PinyinBookProgress value) => progress?.Report(value);

    private static string GetPinyinCachePath(string destinationPath) =>
        Path.GetFullPath(destinationPath) + PinyinCacheFileSuffix;

    private static async Task<PinyinCacheSnapshot?> TryLoadPinyinCacheAsync(
        string cachePath,
        string sourcePath,
        string sourceHash,
        PinyinBookOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath)) return null;

        var lines = await File.ReadAllLinesAsync(cachePath, Encoding.UTF8, cancellationToken);
        PinyinCacheHeader? header = null;
        var segments = new Dictionary<int, PinyinCacheSegmentLine>();
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
                    header = JsonSerializer.Deserialize<PinyinCacheHeader>(
                        line,
                        PinyinCacheJsonOptions);
                    if (header is not null && header.UpdatedAt > updatedAt)
                        updatedAt = header.UpdatedAt;
                }
                else if (string.Equals(kind, "segment", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = JsonSerializer.Deserialize<PinyinCacheSegmentLine>(
                        line,
                        PinyinCacheJsonOptions);
                    if (segment is null || segment.Index < 0) continue;
                    segments[segment.Index] = segment;
                    if (segment.UpdatedAt > updatedAt) updatedAt = segment.UpdatedAt;
                }
            }
            catch (JsonException)
            {
                // A process interruption can leave one incomplete final line;
                // every earlier flushed state remains recoverable.
            }
        }

        if (header is null
            || header.Version != PinyinCacheVersion
            || string.IsNullOrWhiteSpace(header.SourcePath)
            || string.IsNullOrWhiteSpace(header.SourceHash)
            || !Path.GetFullPath(header.SourcePath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
            || !header.SourceHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            return null;

        var cachedOptions = PinyinBookOptions.Normalize(header.Options);
        if (cachedOptions.OutputStyle != options.OutputStyle
            || cachedOptions.EnableAiReview != options.EnableAiReview)
            return null;

        return new PinyinCacheSnapshot(
            header,
            segments,
            updatedAt == DateTimeOffset.MinValue ? header.UpdatedAt : updatedAt);
    }

    private static int GetCachedTotalSegments(PinyinCacheSnapshot snapshot)
    {
        var inferredTotal = snapshot.Segments.Keys.DefaultIfEmpty(-1).Max() + 1;
        return Math.Max(snapshot.Header.TotalSegments, inferredTotal);
    }

    private static void TryDeletePinyinCache(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to delete completed pinyin cache: {exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LocalPinyinDecision(
        string Pinyin,
        IReadOnlyList<string> Candidates,
        bool FromPhraseDictionary);

    private sealed record PinyinReviewTarget(
        int SegmentIndex,
        XElement Ruby,
        char Character,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        string Context);

    private sealed record PinyinReviewRequest(
        int Id,
        char Character,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        string Context,
        IReadOnlyList<PinyinReviewTarget> Targets);

    private sealed record PinyinAnnotationResult(
        IReadOnlyList<XNode> Nodes,
        int AnnotatedCharacters,
        IReadOnlyList<PinyinReviewTarget> Targets,
        string AnnotatedText);

    private sealed record PinyinPage(
        string EntryName,
        XDocument Document,
        IReadOnlyList<PinyinSegment> Segments);

    private sealed record PinyinSegment(
        int Index,
        string EntryName,
        XText TextNode,
        string OriginalText);

    private sealed record PinyinWorkState(
        PinyinSegment Segment,
        int PageIndex,
        PinyinAnnotationResult Annotation);

    private sealed record AiReviewResult(
        int ReviewedCount,
        string? Error,
        IReadOnlySet<int> CheckedSegmentIndexes);

    private sealed record PinyinCacheHeader(
        string Kind,
        int Version,
        string SourcePath,
        string SourceHash,
        PinyinBookOptions Options,
        int TotalSegments,
        DateTimeOffset UpdatedAt);

    private sealed record PinyinCacheSegmentLine(
        string Kind,
        int Index,
        string EntryName,
        string OriginalText,
        string AnnotatedText,
        string AnnotatedMarkup,
        int AnnotatedCharacterCount,
        int ReviewCandidateCount,
        int ReviewedCandidateCount,
        PinyinBookSegmentStatus Status,
        string ProcessFlow,
        string? ErrorMessage,
        DateTimeOffset UpdatedAt);

    private sealed record PinyinCacheSnapshot(
        PinyinCacheHeader Header,
        IReadOnlyDictionary<int, PinyinCacheSegmentLine> Segments,
        DateTimeOffset UpdatedAt);

    private sealed class PinyinCacheWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private bool _disposed;

        public PinyinCacheWriter(
            string path,
            PinyinCacheHeader header,
            bool append)
        {
            var stream = new FileStream(
                path,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (append && stream.Length > 0)
                _writer.WriteLine();
            WriteHeader(header);
        }

        public void Append(
            PinyinBookSegmentProgress segment,
            string annotatedMarkup,
            int annotatedCharacterCount,
            int reviewCandidateCount,
            int reviewedCandidateCount)
        {
            if (_disposed) return;
            var line = new PinyinCacheSegmentLine(
                "segment",
                segment.Index,
                segment.EntryName,
                segment.OriginalText,
                segment.AnnotatedText,
                annotatedMarkup,
                annotatedCharacterCount,
                reviewCandidateCount,
                reviewedCandidateCount,
                segment.Status,
                segment.ProcessFlow,
                segment.ErrorMessage,
                DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, PinyinCacheJsonOptions));
            _writer.Flush();
        }

        private void WriteHeader(PinyinCacheHeader header)
        {
            _writer.WriteLine(JsonSerializer.Serialize(header, PinyinCacheJsonOptions));
            _writer.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
