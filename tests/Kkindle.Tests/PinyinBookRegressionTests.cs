using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;

namespace Kkindle.Tests;

public sealed class PinyinBookRegressionTests : IDisposable
{
    private readonly string _root = TestHelpers.CreateTempDirectory();
    private static readonly PinyinBookOptions LocalOnly = new() { EnableAiReview = false };
    private static readonly AiConnectionSettings AiSettings = new()
    {
        Provider = "custom", BaseUrl = "https://api.example.com/v1",
        Model = "pinyin-regression", ApiKey = "test-only"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumesOnlyUnreviewedTargetsAfterPartialBatchFailureOrCancellation(bool cancel)
    {
        var (source, output) = CreateBook("<p>行，重，长，乐，数，藏。</p>");
        var options = new PinyinBookOptions { EnableAiReview = true, AiReviewBatchSize = 4 };
        using var cancellation = new CancellationTokenSource();
        var firstHandler = new ReplyHandler((call, items) => call == 2
            ? throw new HttpRequestException("Second batch unavailable")
            : Answer(items, item => Character(item) == "行" ? "háng" : Current(item)));
        using var firstAi = new AiChatClient(firstHandler);
        using var firstEngine = new RecordingEngine();
        var progress = new TestHelpers.InlineProgress<PinyinBookProgress>(value =>
        {
            if (cancel && value.ReviewedCandidates == 4) cancellation.Cancel();
        });
        var generation = new PinyinBookService(null, firstAi, AiSettings, firstEngine)
            .GenerateAsync(source, output, options, progress, cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
            Assert.False(File.Exists(output));
        }
        else
        {
            var partial = await generation;
            Assert.Equal(6, partial.ReviewCandidateCount);
            Assert.Equal(4, partial.AiReviewedCandidateCount);
            Assert.Equal(1, partial.FailedSegmentCount);
            Assert.NotNull(partial.AiReviewError);
            Assert.Contains("行=háng", ReadRuby(output));
        }

        var resume = await new PinyinBookService().FindResumeAsync(source, output, options);
        Assert.NotNull(resume);
        Assert.Equal(0, resume.CompletedSegments);
        Assert.Equal(1, resume.IncompleteSegments);
        Assert.Equal(cancel ? 0 : 1, resume.FailedSegments);

        var retryHandler = new ReplyHandler((_, items) => Answer(items,
            item => Character(item) == "数" ? "shǔ" : "zàng"));
        using var retryAi = new AiChatClient(retryHandler);
        using var retryEngine = new RecordingEngine();
        var result = await new PinyinBookService(null, retryAi, AiSettings, retryEngine).GenerateAsync(
            source, output, options, resumeMode: PinyinBookResumeMode.Resume);

        Assert.Empty(retryEngine.Contexts);
        Assert.Equal(new[] { "数", "藏" }, retryHandler.Items.Select(Character));
        Assert.Equal(6, result.AiReviewedCandidateCount);
        Assert.Equal(0, result.FailedSegmentCount);
        Assert.Null(result.AiReviewError);
        Assert.Contains("行=háng", ReadRuby(output));
        Assert.Contains("数=shǔ", ReadRuby(output));
        Assert.Contains("藏=zàng", ReadRuby(output));
        Assert.False(File.Exists(CachePath(output)));
    }

    [Fact]
    public async Task CancelingFirstLocalProgressKeepsItsAnnotationForResume()
    {
        var (source, output) = CreateBook("<p>银行。</p><p>你好。</p>");
        using var cancellation = new CancellationTokenSource();
        using var firstEngine = new RecordingEngine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PinyinBookService(pinyinEngine: firstEngine).GenerateAsync(source, output, LocalOnly,
                new TestHelpers.InlineProgress<PinyinBookProgress>(value =>
                {
                    if (value.Segment is { Index: 0, Status: PinyinBookSegmentStatus.Processing, AnnotatedText.Length: > 0 })
                        cancellation.Cancel();
                }), cancellation.Token));

        Assert.Equal(new[] { "银行。" }, firstEngine.Contexts);
        Assert.False(File.Exists(output));
        var resume = await new PinyinBookService().FindResumeAsync(source, output, LocalOnly);
        Assert.NotNull(resume);
        Assert.Equal(1, resume.CompletedSegments);
        Assert.Equal(2, resume.TotalSegments);
        // An interrupted final write must not discard earlier flushed work.
        await File.AppendAllTextAsync(CachePath(output), "{\"Kind\":\"segment\"");

        using var retryEngine = new RecordingEngine();
        await new PinyinBookService(pinyinEngine: retryEngine).GenerateAsync(
            source, output, LocalOnly, resumeMode: PinyinBookResumeMode.Resume);
        Assert.Equal(new[] { "你好。" }, retryEngine.Contexts);
        Assert.Contains("行=háng", ReadRuby(output));
        Assert.Contains("你=ní", ReadRuby(output));
        Assert.False(File.Exists(CachePath(output)));
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[null, 12, {\"id\":1,\"pinyin\":\"invalid\"}]", 0)]
    [InlineData("[{\"id\":1}]", 0)]
    [InlineData("[{\"id\":1,\"pinyin\":null}]", 1)]
    [InlineData("[{\"id\":1,\"pinyin\":\"hang2\"}]", 1)]
    public async Task OnlyExplicitValidAiDecisionsAreMarkedReviewed(string answer, int reviewed)
    {
        var (source, output) = CreateBook("<p>行。</p>");
        using var ai = new AiChatClient(new ReplyHandler((_, _) => answer));
        var result = await new PinyinBookService(null, ai, AiSettings).GenerateAsync(
            source, output, new PinyinBookOptions { EnableAiReview = true });

        Assert.Equal(1, result.ReviewCandidateCount);
        Assert.Equal(reviewed, result.AiReviewedCandidateCount);
        Assert.Equal(1 - reviewed, result.FailedSegmentCount);
        Assert.Equal(reviewed == 0, result.AiReviewError is not null);
        Assert.Equal(reviewed == 0, File.Exists(CachePath(output)));
        if (answer.Contains("null}]", StringComparison.Ordinal))
            Assert.Contains("行=xíng", ReadRuby(output));
        if (answer.Contains("hang2", StringComparison.Ordinal))
            Assert.Contains("行=háng", ReadRuby(output));
    }

    [Theory]
    [InlineData("<p>银行</p>")]
    [InlineData("<p>银<strong>行</strong></p>")]
    [InlineData("<p><span>银<a href=\"#note\"><em>行</em></a></span></p>")]
    public async Task InlineFormattingPreservesWordContextAndMarkup(string body)
    {
        var (source, output) = CreateBook(body);
        using var engine = new RecordingEngine();
        var result = await new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output, LocalOnly);

        Assert.Equal(new[] { "银行" }, engine.Contexts);
        Assert.Equal(new[] { "银=yín", "行=háng" }, ReadRuby(output));
        Assert.Equal(0, result.ReviewCandidateCount);
        var chapterPath = Path.Combine(_root, "generated.xhtml");
        using (var archive = ZipFile.OpenRead(output))
            archive.GetEntry("OEBPS/chapter.xhtml")!.ExtractToFile(chapterPath);
        var content = new XhtmlChapterLoader().Load(chapterPath);
        Assert.Single(ReaderSearchTextPolicy.FindMatches(content.BodyText, "银行", content.RubyAnnotationRanges));
        var before = XDocument.Parse("<body>" + body + "</body>").Descendants()
            .Where(element => element.Name.LocalName is "strong" or "span" or "a" or "em")
            .Select(element => element.Name.LocalName).ToArray();
        var after = ReadChapter(output).Descendants()
            .Where(element => element.Name.LocalName is "strong" or "span" or "a" or "em")
            .Select(element => element.Name.LocalName).ToArray();
        Assert.Equal(before, after);
        if (before.Contains("a"))
            Assert.Equal("#note", ReadChapter(output).Descendants().Single(e => e.Name.LocalName == "a").Attribute("href")?.Value);
    }

    [Theory]
    [InlineData("<p>银</p><p>行</p>")]
    [InlineData("<p>银<br/>行</p>")]
    [InlineData("<p>银<code>代码</code>行</p>")]
    [InlineData("<p>银<ruby>中<rt>zhōng</rt></ruby>行</p>")]
    public async Task DoesNotJoinContextAcrossBlocksOrSkippedText(string body)
    {
        var (source, output) = CreateBook(body);
        using var engine = new RecordingEngine();
        await new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output, LocalOnly);
        Assert.Equal(new[] { "银", "行" }, engine.Contexts);
        Assert.Contains("行=xíng", ReadRuby(output));
        Assert.DoesNotContain("代=dài", ReadRuby(output));
    }

    [Fact]
    public async Task EmojiBeforeInlineWordKeepsRubyAlignedToOriginalCharacters()
    {
        var (source, output) = CreateBook("<p>😀银<strong>行</strong>中文🚀</p>");
        using var engine = new RecordingEngine();
        var result = await new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output, LocalOnly);
        Assert.Equal(new[] { "😀银行中文🚀" }, engine.Contexts);
        Assert.Equal(new[] { "银=yín", "行=háng", "中=zhōng", "文=wén" }, ReadRuby(output));
        Assert.Equal(4, result.AnnotatedCharacterCount);
        Assert.Contains("😀", ReadChapter(output).ToString());
        Assert.Contains("🚀", ReadChapter(output).ToString());
    }

    [Theory]
    [InlineData("<p>请看第1行，行！</p>")]
    [InlineData("<p>请看第1<strong>行</strong>，<em>行</em>！</p>")]
    public async Task SameCharacterAtDifferentPositionsGetsDistinctAiContext(string body)
    {
        var (source, output) = CreateBook(body);
        var handler = new ReplyHandler((_, items) => Answer(items, item => Character(item) != "行"
            ? Current(item)
            : item.GetProperty("context").GetString()!.Contains("第1⟦行⟧", StringComparison.Ordinal)
                ? "háng" : "xíng"));
        using var ai = new AiChatClient(handler);
        var result = await new PinyinBookService(null, ai, AiSettings).GenerateAsync(
            source, output, new PinyinBookOptions { EnableAiReview = true });

        var contexts = handler.Items.Where(item => Character(item) == "行")
            .Select(item => item.GetProperty("context").GetString()).ToArray();
        Assert.Equal(new[] { "请看第1⟦行⟧，行！", "请看第1行，⟦行⟧！" }, contexts);
        Assert.Equal(new[] { "行=háng", "行=xíng" }, ReadRuby(output).Where(ruby => ruby.StartsWith("行=")));
        Assert.Null(result.AiReviewError);
        Assert.Equal(result.ReviewCandidateCount, result.AiReviewedCandidateCount);
    }

    [Fact]
    public void AnnotationRunsOffTheCallingSynchronizationContext()
    {
        var (source, output) = CreateBook("<p>银行</p><p>书籍</p>");
        using var engine = new RecordingEngine();
        using var context = new PumpContext();
        var callerThread = Environment.CurrentManagedThreadId;
        var ranOnCaller = false;
        engine.OnCall = () => ranOnCaller |= Environment.CurrentManagedThreadId == callerThread
            || ReferenceEquals(SynchronizationContext.Current, context);

        context.Run(() => new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output, LocalOnly));

        Assert.Equal(2, engine.Contexts.Count);
        Assert.False(ranOnCaller);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task ReportsEachActiveParagraphBeforeLocalInference()
    {
        var (source, output) = CreateBook("<p>银行。</p><p>你好。</p>");
        using var engine = new RecordingEngine();
        var updates = new List<PinyinBookProgress>();
        engine.OnCall = () =>
        {
            var active = updates.Last();
            Assert.Equal(PinyinBookSegmentStatus.Processing, active.Segment!.Status);
            Assert.Equal(engine.Contexts.Last(), active.Segment.OriginalText);
            Assert.Empty(active.Segment.AnnotatedText);
            Assert.Equal(engine.Contexts.Count - 1, active.ProcessedSegments);
            Assert.Equal(2, active.TotalSegments);
            Assert.True(active.Percentage < 100);
        };

        await new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output, LocalOnly,
            new TestHelpers.InlineProgress<PinyinBookProgress>(updates.Add));

        Assert.Equal(2, engine.Contexts.Count);
        var completed = updates.Where(value => value.Segment?.Status == PinyinBookSegmentStatus.Completed).ToArray();
        Assert.Equal(new[] { 0, 1 }, completed.Select(value => value.Segment!.Index));
        Assert.Equal(new[] { 1, 2 }, completed.Select(value => value.ProcessedSegments));
        Assert.Equal(90, completed.Last().Percentage);
        Assert.Equal(updates.Select(value => value.Percentage).Order(), updates.Select(value => value.Percentage));
        Assert.Equal(100, updates.Last().Percentage);
        Assert.All(updates.SkipLast(1), value => Assert.True(value.Percentage < 100));
    }

    [Fact]
    public async Task CancelingActiveParagraphBeforeInferenceKeepsItUnprocessed()
    {
        var (source, output) = CreateBook("<p>银行。</p><p>你好。</p>");
        using var engine = new RecordingEngine();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PinyinBookService(pinyinEngine: engine).GenerateAsync(source, output, LocalOnly,
                new TestHelpers.InlineProgress<PinyinBookProgress>(value =>
                {
                    if (value.Segment is { Status: PinyinBookSegmentStatus.Processing, AnnotatedText.Length: 0 })
                        cancellation.Cancel();
                }), cancellation.Token));

        Assert.Empty(engine.Contexts);
        Assert.False(File.Exists(output));
        var resume = await new PinyinBookService().FindResumeAsync(source, output, LocalOnly);
        Assert.NotNull(resume);
        Assert.Equal(0, resume.CompletedSegments);
        Assert.Equal(2, resume.IncompleteSegments);
    }

    [Fact]
    public async Task OldCacheIsRegeneratedInsteadOfTrustingIncorrectCompletionState()
    {
        var (source, output) = CreateBook("<p>银行。</p><p>你好。</p>");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PinyinBookService().GenerateAsync(
            source, output, LocalOnly, new TestHelpers.InlineProgress<PinyinBookProgress>(value =>
            {
                if (value.Segment is { Index: 0, Status: PinyinBookSegmentStatus.Completed }) cancellation.Cancel();
            }), cancellation.Token));
        var lines = await File.ReadAllLinesAsync(CachePath(output));
        var header = JsonNode.Parse(lines[0])!;
        header["Version"] = 1;
        lines[0] = header.ToJsonString();
        await File.WriteAllLinesAsync(CachePath(output), lines);

        Assert.Null(await new PinyinBookService().FindResumeAsync(source, output, LocalOnly));
        using var engine = new RecordingEngine();
        await new PinyinBookService(pinyinEngine: engine).GenerateAsync(
            source, output, LocalOnly, resumeMode: PinyinBookResumeMode.Resume);
        Assert.Equal(new[] { "银行。", "你好。" }, engine.Contexts);
        Assert.False(File.Exists(CachePath(output)));
    }

    private (string Source, string Output) CreateBook(string body)
    {
        var source = Path.Combine(_root, "source.epub");
        var output = Path.Combine(_root, "pinyin.epub");
        using var archive = ZipFile.Open(source, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml",
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>" + body + "</body></html>");
        return (source, output);
    }

    private static XDocument ReadChapter(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry("OEBPS/chapter.xhtml")!.Open();
        return XDocument.Load(stream);
    }

    private static string[] ReadRuby(string path) => ReadChapter(path).Descendants()
        .Where(element => element.Name.LocalName == "ruby")
        .Select(element => string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value))
            + "=" + element.Elements().Single(child => child.Name.LocalName == "rt").Value).ToArray();

    private static string CachePath(string output) => output + ".kkindle-pinyin-cache.jsonl";
    private static string Character(JsonElement item) => item.GetProperty("character").GetString()!;
    private static string Current(JsonElement item) => item.GetProperty("current").GetString()!;
    private static string Answer(JsonElement[] items, Func<JsonElement, string> choose) =>
        JsonSerializer.Serialize(items.Select(item => new { id = item.GetProperty("id").GetInt32(), pinyin = choose(item) }));

    private sealed class ReplyHandler(Func<int, JsonElement[], string> answer) : HttpMessageHandler
    {
        private int _calls;
        public List<JsonElement> Items { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var question = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(question[question.IndexOf('[')..]);
            var items = payload.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
            Items.AddRange(items);
            var content = answer(++_calls, items);
            var eventData = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + eventData + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class RecordingEngine : IPinyinEngine
    {
        private readonly DotNetG2PPinyinEngine _engine = new();
        public List<string> Contexts { get; } = [];
        public Action? OnCall { get; set; }
        public string EngineId => _engine.EngineId;
        public string[] ToPinyinList(string text, PinyinBookOutputStyle style)
        {
            Contexts.Add(text);
            OnCall?.Invoke();
            return _engine.ToPinyinList(text, style);
        }
        public bool ContainsChar(char character) => _engine.ContainsChar(character);
        public IReadOnlyList<string> LookupChar(char character) => _engine.LookupChar(character);
        public void Dispose() => _engine.Dispose();
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Add((callback, state));
        public void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var task = action();
                while (!task.IsCompleted)
                {
                    if (_callbacks.TryTake(out var next, 100)) next.Callback(next.State);
                }
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() => _callbacks.Dispose();
    }

    public void Dispose() => TestHelpers.TryDelete(_root);
}
