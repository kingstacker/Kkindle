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
    private const int PinyinCacheVersion = 2;
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

    private static readonly HashSet<string> BlockElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "li", "main", "nav", "ol", "p", "section", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "ul"
    };

    private readonly IBookFormatConverter? _formatConverter;
    private readonly PinyinPhraseDictionary _phraseDictionary;
    private readonly IPinyinEngine _pinyinEngine;
    private readonly AiChatClient? _aiChatClient;
    private readonly AiConnectionSettings? _aiSettings;

    public PinyinBookService(
        IBookFormatConverter? formatConverter = null,
        AiChatClient? aiChatClient = null,
        AiConnectionSettings? aiSettings = null,
        IPinyinEngine? pinyinEngine = null)
    {
        _formatConverter = formatConverter;
        _phraseDictionary = PinyinPhraseDictionary.LoadEmbedded();
        _pinyinEngine = pinyinEngine ?? new DotNetG2PPinyinEngine();
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
                && !string.IsNullOrWhiteSpace(item.AnnotatedMarkup)
                && item.ReviewTargets is not null
                && (!options.EnableAiReview || item.ReviewTargets.All(target => target.Reviewed)));
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
        cancellationToken.ThrowIfCancellationRequested();
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

            return await Task.Run(() => GenerateFromEpubAsync(
                epubSource,
                destination,
                normalizedOptions,
                progress,
                cancellationToken,
                source,
                sourceHash,
                resumeMode), cancellationToken);
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
                        OverallPercentage: totalPages <= 0 ? 0 : (index + 1) * 30d / totalPages));
                    continue;
                }

                var markup = await ReadArchiveTextAsync(entry, cancellationToken);
                if (TryParseDocument(markup, out var document))
                {
                    var body = document.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
                    if (body is not null)
                    {
                        var pageSegments = GetPinyinSegments(body, entry.FullName);
                        pages.Add(new PinyinPage(entry.FullName, document, pageSegments));
                    }
                }

                Report(progress, new PinyinBookProgress(
                    "正在扫描 EPUB",
                    entry.FullName,
                    index + 1,
                    totalPages,
                    0,
                    OverallPercentage: totalPages <= 0 ? 0 : (index + 1) * 30d / totalPages));
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
            var workStates = new Dictionary<int, PinyinWorkState>();
            var processedSegments = 0;
            var locallyProcessedSegments = 0;
            var annotatedCharacters = 0;
            var reviewCandidateCount = 0;
            var reviewedCandidates = 0;
            var localProgressRange = options.EnableAiReview ? 40d : 60d;
            var reviewing = false;

            void PersistWork(
                PinyinWorkState work,
                PinyinBookSegmentStatus status,
                string processFlow,
                double percentage,
                string? error = null)
            {
                if (status == PinyinBookSegmentStatus.Completed
                    && work.Status != PinyinBookSegmentStatus.Completed)
                    processedSegments++;
                work.Status = status;
                var annotation = work.Annotation;
                var update = new PinyinBookSegmentProgress(
                    work.Segment.Index, work.Segment.EntryName, work.Segment.OriginalText,
                    CreateAnnotatedDisplayText(annotation.Nodes), status, processFlow, error);
                cacheWriter.Append(
                    update, SerializeNodes(annotation.Nodes), annotation.AnnotatedCharacters,
                    annotation.Targets.Count, annotation.Targets.Count(target => target.Reviewed),
                    annotation.Targets);
                Report(progress, new PinyinBookProgress(
                    reviewing ? "正在复核疑问拼音" : "正在生成拼音",
                    work.Segment.EntryName, Math.Min(work.PageIndex + 1, xhtmlEntryNames.Count),
                    xhtmlEntryNames.Count, annotatedCharacters, reviewCandidateCount, reviewedCandidates,
                    percentage, update, processedSegments, totalSegments));
            }

            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var page = pages[pageIndex];
                foreach (var segment in page.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Show the source paragraph before model loading/inference.
                    // This is not durable work yet, so do not write it over a
                    // completed resume-cache entry or count it as processed.
                    Report(progress, new PinyinBookProgress(
                        "正在生成拼音", segment.EntryName, pageIndex + 1, xhtmlEntryNames.Count,
                        annotatedCharacters, reviewCandidateCount, reviewedCandidates,
                        30 + locallyProcessedSegments * localProgressRange / totalSegments,
                        new PinyinBookSegmentProgress(
                            segment.Index, segment.EntryName, segment.OriginalText, string.Empty,
                            PinyinBookSegmentStatus.Processing, "读取原文 → 正在本地注音"),
                        processedSegments, totalSegments));
                    cancellationToken.ThrowIfCancellationRequested();
                    PinyinAnnotationResult annotation;
                    var restored = false;
                    if (resumeSnapshot is not null
                        && resumeSnapshot.Segments.TryGetValue(segment.Index, out var cached)
                        && TryRestoreAnnotation(segment, cached, out var cachedAnnotation))
                    {
                        annotation = cachedAnnotation;
                        restored = true;
                    }
                    else
                    {
                        annotation = AnnotateTextNode(segment, options, cancellationToken);
                    }

                    var work = new PinyinWorkState(segment, pageIndex, annotation);
                    workStates.Add(segment.Index, work);
                    renderedDocuments[page.EntryName] = page.Document;
                    annotatedCharacters += annotation.AnnotatedCharacters;
                    reviewCandidateCount += annotation.Targets.Count;
                    reviewedCandidates += annotation.Targets.Count(target => target.Reviewed);
                    locallyProcessedSegments++;
                    var percentage = 30 + locallyProcessedSegments * localProgressRange / totalSegments;
                    var processFlow = restored ? "读取原文 → 恢复本地缓存" : "读取原文 → 本地注音";
                    PersistWork(work, PinyinBookSegmentStatus.Processing, processFlow, percentage);

                    // Local work is durable even if cancellation arrives in the
                    // progress report. Only unresolved review targets remain.
                    if (!options.EnableAiReview || annotation.Targets.All(target => target.Reviewed))
                        PersistWork(work, PinyinBookSegmentStatus.Completed,
                            processFlow + " → 写入 EPUB", percentage);
                }
            }

            var reviewTargets = workStates.Values.SelectMany(work => work.Annotation.Targets).ToArray();
            reviewing = options.EnableAiReview;
            var reviewError = await ReviewPinyinAsync(
                reviewTargets,
                options,
                (stage, item, percentage) => Report(progress, new PinyinBookProgress(
                    stage, item, xhtmlEntryNames.Count, xhtmlEntryNames.Count,
                    annotatedCharacters, reviewCandidateCount, reviewedCandidates, percentage,
                    ProcessedSegments: processedSegments, TotalSegments: totalSegments)),
                (checkedTargets, percentage) =>
                {
                    reviewedCandidates += checkedTargets.Count;
                    foreach (var segmentIndex in checkedTargets.Select(target => target.SegmentIndex).Distinct())
                    {
                        var work = workStates[segmentIndex];
                        var complete = work.Annotation.Targets.All(target => target.Reviewed);
                        PersistWork(
                            work,
                            complete ? PinyinBookSegmentStatus.Completed : PinyinBookSegmentStatus.Processing,
                            complete ? "读取原文 → 本地注音 → AI 复核 → 写入 EPUB"
                                : "读取原文 → 本地注音 → AI 部分复核，已保存",
                            percentage);
                    }
                },
                cancellationToken);

            var failedSegments = 0;
            foreach (var work in workStates.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work.Status == PinyinBookSegmentStatus.Completed) continue;
                var failed = options.EnableAiReview && work.Annotation.Targets.Any(target => !target.Reviewed);
                if (failed) failedSegments++;
                PersistWork(
                    work,
                    failed ? PinyinBookSegmentStatus.Failed : PinyinBookSegmentStatus.Completed,
                    failed ? "读取原文 → 本地注音 → AI 复核未完成，已保存" : "读取原文 → 本地注音 → 写入 EPUB",
                    90,
                    failed ? reviewError : null);
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

            generationCompleted = failedSegments == 0 && reviewError is null;
            var result = new PinyinBookResult(
                cacheSourcePath,
                destinationPath,
                xhtmlEntryNames.Count,
                renderedPages.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                reviewError,
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
        PinyinBookOptions options,
        CancellationToken cancellationToken)
    {
        var textNode = segment.TextNode;
        var value = segment.OriginalText;
        var parent = textNode.Parent;
        if (parent is null || value.Length == 0)
            return new PinyinAnnotationResult([], 0, [], string.Empty);

        var analysis = segment.Context.Analysis ??= AnalyzeContext(segment.Context.Text, options, cancellationToken);
        var nodes = new List<XNode>();
        var reviewTargets = new List<PinyinReviewTarget>();
        var annotatedCharacters = 0;
        var cursor = 0;
        while (cursor < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsHanCharacter(value[cursor]))
            {
                var plainStart = cursor++;
                while (cursor < value.Length && !IsHanCharacter(value[cursor])) cursor++;
                nodes.Add(new XText(value[plainStart..cursor]));
                continue;
            }

            var characterIndex = cursor++;
            var character = value[characterIndex];
            var contextIndex = segment.ContextOffset + characterIndex;
            var pinyin = analysis.Pinyins[contextIndex];
            if (string.IsNullOrWhiteSpace(pinyin) || !_pinyinEngine.ContainsChar(character))
            {
                nodes.Add(new XText(character.ToString()));
                continue;
            }

            var ns = parent.Name.Namespace;
            var ruby = new XElement(ns + "ruby",
                new XText(character.ToString()), new XElement(ns + "rt", pinyin));
            nodes.Add(ruby);
            if (analysis.Decisions[contextIndex] is { Candidates.Count: > 1 } decision)
            {
                var context = ExtractReviewContext(segment.Context.Text, contextIndex);
                reviewTargets.Add(new PinyinReviewTarget(
                    segment.Index, characterIndex, ruby, character, pinyin,
                    decision.Candidates, context.Text, context.CharacterIndex));
            }
            annotatedCharacters++;
        }

        textNode.ReplaceWith(nodes);
        return new PinyinAnnotationResult(
            nodes, annotatedCharacters, reviewTargets, CreateAnnotatedDisplayText(nodes));
    }

    private PinyinContextAnalysis AnalyzeContext(
        string text,
        PinyinBookOptions options,
        CancellationToken cancellationToken)
    {
        var pinyins = _pinyinEngine.ToPinyinList(text, options.OutputStyle, cancellationToken);
        if (pinyins.Length != text.Length)
            throw new InvalidDataException("注音引擎返回的字符位置与原文不一致。");
        var decisions = new LocalPinyinDecision?[text.Length];
        var cursor = 0;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsHanCharacter(text[cursor]))
            {
                cursor++;
                continue;
            }
            var start = cursor++;
            while (cursor < text.Length && IsHanCharacter(text[cursor])) cursor++;
            var runDecisions = AnalyzeHanRun(text[start..cursor], pinyins[start..cursor]);
            for (var index = 0; index < runDecisions.Count; index++)
                decisions[start + index] = runDecisions[index];
        }
        return new PinyinContextAnalysis(pinyins, decisions);
    }

    private static IReadOnlyList<PinyinSegment> GetPinyinSegments(XElement body, string entryName)
    {
        var segments = new List<PinyinSegment>();
        var pending = new List<XText>();
        void FlushContext()
        {
            if (pending.Count == 0) return;
            var context = new PinyinTextContext(string.Concat(pending.Select(node => node.Value)));
            var offset = 0;
            foreach (var node in pending)
            {
                if (node.Value.Any(IsHanCharacter))
                    segments.Add(new PinyinSegment(-1, entryName, node, node.Value, context, offset));
                offset += node.Value.Length;
            }
            pending.Clear();
        }

        void Walk(XNode node)
        {
            if (node is XText text)
            {
                pending.Add(text);
                return;
            }
            if (node is not XElement element) return;
            var name = element.Name.LocalName;
            if (ShouldSkipElement(element)
                || name.Equals("br", StringComparison.OrdinalIgnoreCase)
                || name.Equals("hr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                FlushContext();
                return;
            }
            var block = BlockElementNames.Contains(name);
            if (block) FlushContext();
            foreach (var child in element.Nodes()) Walk(child);
            if (block) FlushContext();
        }

        Walk(body);
        FlushContext();
        return segments;
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

    private static PinyinReviewContext ExtractReviewContext(string value, int characterIndex)
    {
        if (value.Length <= MaxAiReviewContextCharacters)
            return new PinyinReviewContext(value, characterIndex);
        var half = MaxAiReviewContextCharacters / 2;
        var start = Math.Clamp(characterIndex - half, 0, Math.Max(0, value.Length - MaxAiReviewContextCharacters));
        var end = Math.Min(value.Length, start + MaxAiReviewContextCharacters);
        if (start > 0 && char.IsSurrogatePair(value, start - 1)) start--;
        if (end < value.Length && char.IsSurrogatePair(value, end - 1)) end++;
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < value.Length ? "…" : string.Empty;
        return new PinyinReviewContext(prefix + value[start..end] + suffix, characterIndex - start + prefix.Length);
    }

    private async Task<string?> ReviewPinyinAsync(
        IReadOnlyList<PinyinReviewTarget> targets,
        PinyinBookOptions options,
        Action<string, string, double> report,
        Action<IReadOnlyList<PinyinReviewTarget>, double> persistBatch,
        CancellationToken cancellationToken)
    {
        if (!options.EnableAiReview) return null;
        var pendingTargets = targets.Where(target => !target.Reviewed).ToArray();
        if (pendingTargets.Length == 0) return null;
        if (_aiChatClient is null || _aiSettings is null || !_aiSettings.IsConfigured)
            return "AI 未配置";

        var requests = pendingTargets
            .GroupBy(CreateReviewKey, StringComparer.Ordinal)
            .Select((group, index) =>
            {
                var first = group.First();
                return new PinyinReviewRequest(
                    index + 1, first.Character, NormalizeToneMarkedPinyin(first.CurrentPinyin),
                    first.Candidates, first.Context, first.ContextCharacterIndex, group.ToArray());
            })
            .ToArray();

        for (var offset = 0; offset < requests.Length; offset += options.AiReviewBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = requests.Skip(offset).Take(options.AiReviewBatchSize).ToArray();
            var batchEnd = offset + batch.Length;
            report("正在复核疑问拼音",
                $"正在请求 AI：{offset + 1}-{batchEnd} / {requests.Length} 项",
                70 + offset * 20d / requests.Length);

            string answer;
            try
            {
                answer = await _aiChatClient.CompleteAsync(
                    _aiSettings, AiReviewInstructions, BuildAiReviewQuestion(batch),
                    Array.Empty<AiConversationTurn>(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return $"AI 请求失败：{exception.Message}";
            }

            if (!TryReadAiReview(answer, batch, out var decisions))
                return "AI 返回格式无法识别";

            var checkedTargets = new List<PinyinReviewTarget>();
            var missingDecisions = false;
            foreach (var request in batch)
            {
                if (!decisions.TryGetValue(request.Id, out var selectedPinyin))
                {
                    missingDecisions = true;
                    continue;
                }
                foreach (var target in request.Targets)
                {
                    if (selectedPinyin is not null)
                    {
                        var rt = target.Ruby.Elements().First(element =>
                            element.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase));
                        rt.Value = ApplyPinyinStyle(selectedPinyin, options.OutputStyle);
                    }
                    target.Reviewed = true;
                    checkedTargets.Add(target);
                }
            }

            // Commit each successful target before another request/cancellation.
            // A paragraph may span several batches; its index alone is insufficient.
            var percentage = 70 + batchEnd * 20d / requests.Length;
            if (checkedTargets.Count > 0) persistBatch(checkedTargets, percentage);
            if (missingDecisions)
                return "AI 返回缺少有效的复核结果，未完成项已保存。";
            report("正在复核疑问拼音", $"已处理 {batchEnd:N0} / {requests.Length:N0} 项", percentage);
        }
        return null;
    }

    private static string BuildAiReviewQuestion(IReadOnlyList<PinyinReviewRequest> requests)
    {
        var payload = requests.Select(request => new
        {
            id = request.Id,
            character = request.Character.ToString(),
            context = request.Context.Insert(request.ContextCharacterIndex + 1, "⟧")
                .Insert(request.ContextCharacterIndex, "⟦"),
            current = request.CurrentPinyin,
            candidates = request.Candidates
        });
        return "请判断下面这些汉字在各自上下文中的普通话拼音。"
            + "只判断 context 中 ⟦⟧ 标出的那个字；同一句中其他位置的同字可能读音不同。"
            + "每项只能从 candidates 中选择一个；无法确定时 pinyin 返回 null。"
            + "不要改写句子，不要添加候选之外的读音。只返回 JSON 数组："
            + Environment.NewLine
            + JsonSerializer.Serialize(payload);
    }

    private bool TryReadAiReview(
        string answer,
        IReadOnlyList<PinyinReviewRequest> requests,
        out Dictionary<int, string?> decisions)
    {
        decisions = new Dictionary<int, string?>();
        var json = ExtractJsonPayload(answer);
        if (json.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root : FindReviewItems(root);
            if (items.ValueKind != JsonValueKind.Array) return false;
            var requestById = requests.ToDictionary(request => request.Id);
            foreach (var item in items.EnumerateArray())
            {
                if (!TryReadReviewId(item, out var id)
                    || !requestById.TryGetValue(id, out var request)
                    || !item.TryGetProperty("pinyin", out var pinyinValue))
                    continue;
                if (pinyinValue.ValueKind == JsonValueKind.Null)
                {
                    // Explicit uncertainty is a reviewed result; omitted ids or
                    // invalid pronunciations must still be retried.
                    decisions[id] = null;
                    continue;
                }
                if (pinyinValue.ValueKind != JsonValueKind.String) continue;
                var selected = NormalizeToneMarkedPinyin(pinyinValue.GetString() ?? string.Empty);
                var allowed = request.Candidates.FirstOrDefault(candidate =>
                    NormalizeToneMarkedPinyin(candidate).Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (allowed is not null) decisions[id] = NormalizeToneMarkedPinyin(allowed);
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
        if (root.ValueKind != JsonValueKind.Object) return default;
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
        if (item.ValueKind != JsonValueKind.Object) return false;
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
        + $"{target.Context}\u001F{target.ContextCharacterIndex}\u001F{string.Join('\u001E', target.Candidates)}";

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


    private const string AiReviewInstructions = "你是中文普通话拼音校对器。只处理指定的多音字，严格遵守用户给出的候选读音。";

    private static bool ShouldSkipElement(XElement element) =>
        SkippedElementNames.Contains(element.Name.LocalName);

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

    private static bool TryRestoreAnnotation(
        PinyinSegment segment,
        PinyinCacheSegmentLine cached,
        out PinyinAnnotationResult annotation)
    {
        annotation = null!;
        if (segment.TextNode.Parent is null
            || !string.Equals(cached.EntryName, segment.EntryName, StringComparison.Ordinal)
            || !string.Equals(cached.OriginalText, segment.OriginalText, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(cached.AnnotatedMarkup)
            || cached.ReviewTargets is null
            || cached.ReviewTargets.Count != cached.ReviewCandidateCount)
            return false;
        try
        {
            var wrapper = XElement.Parse(
                $"<kkindle-fragment>{cached.AnnotatedMarkup}</kkindle-fragment>",
                LoadOptions.PreserveWhitespace);
            var nodes = wrapper.Nodes().ToList();
            var baseText = new StringBuilder();
            var rubies = new Dictionary<int, XElement>();
            foreach (var node in nodes)
            {
                if (node is XText text)
                {
                    baseText.Append(text.Value);
                    continue;
                }
                if (node is not XElement ruby || ruby.Name.LocalName != "ruby") return false;
                var character = string.Concat(ruby.Nodes().OfType<XText>().Select(text => text.Value));
                if (character.Length != 1 || ruby.Elements().All(element => element.Name.LocalName != "rt"))
                    return false;
                rubies.Add(baseText.Length, ruby);
                baseText.Append(character);
            }
            if (baseText.ToString() != segment.OriginalText
                || rubies.Count != cached.AnnotatedCharacterCount)
                return false;

            var targets = new List<PinyinReviewTarget>();
            var positions = new HashSet<int>();
            foreach (var target in cached.ReviewTargets)
            {
                if (!positions.Add(target.CharacterIndex)
                    || !rubies.TryGetValue(target.CharacterIndex, out var ruby)
                    || target.Candidates is not { Count: > 1 })
                    return false;
                var context = ExtractReviewContext(
                    segment.Context.Text, segment.ContextOffset + target.CharacterIndex);
                targets.Add(new PinyinReviewTarget(
                    segment.Index, target.CharacterIndex, ruby,
                    segment.OriginalText[target.CharacterIndex], target.CurrentPinyin,
                    target.Candidates, context.Text, context.CharacterIndex)
                {
                    Reviewed = target.Reviewed
                });
            }
            if (targets.Count(target => target.Reviewed) != cached.ReviewedCandidateCount)
                return false;

            // Avoid LINQ to XML cloning parented nodes: subsequent review must
            // update the same ruby objects that are attached to the output page.
            foreach (var node in nodes) node.Remove();
            segment.TextNode.ReplaceWith(nodes);
            annotation = new PinyinAnnotationResult(
                nodes, rubies.Count, targets, CreateAnnotatedDisplayText(nodes));
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

        var lines = await File.ReadAllLinesAsync(cachePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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
        if (cachedOptions.Engine != options.Engine
            || cachedOptions.OutputStyle != options.OutputStyle
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
        int CharacterIndex,
        XElement Ruby,
        char Character,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        string Context,
        int ContextCharacterIndex)
    {
        public bool Reviewed { get; set; }
    }

    private sealed record PinyinReviewRequest(
        int Id,
        char Character,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        string Context,
        int ContextCharacterIndex,
        IReadOnlyList<PinyinReviewTarget> Targets);

    private sealed record PinyinReviewContext(string Text, int CharacterIndex);

    private sealed class PinyinTextContext(string text)
    {
        public string Text { get; } = text;
        public PinyinContextAnalysis? Analysis { get; set; }
    }

    private sealed record PinyinContextAnalysis(string[] Pinyins, LocalPinyinDecision?[] Decisions);

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
        string OriginalText,
        PinyinTextContext Context,
        int ContextOffset);

    private sealed record PinyinWorkState(
        PinyinSegment Segment,
        int PageIndex,
        PinyinAnnotationResult Annotation)
    {
        public PinyinBookSegmentStatus Status { get; set; }
    }

    private sealed record PinyinCacheReviewTarget(
        int CharacterIndex,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        bool Reviewed);

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
        DateTimeOffset UpdatedAt,
        IReadOnlyList<PinyinCacheReviewTarget>? ReviewTargets = null);

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
            int reviewedCandidateCount,
            IReadOnlyList<PinyinReviewTarget> targets)
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
                DateTimeOffset.UtcNow,
                targets.Select(target => new PinyinCacheReviewTarget(
                    target.CharacterIndex,
                    target.CurrentPinyin,
                    target.Candidates,
                    target.Reviewed)).ToArray());
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
