using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderAiAnswerTests(SettingsUiSession session)
{
    [Fact]
    public Task ClipboardImageCanBePreviewedRemovedSentAloneAndRetried() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ShowReaderAiTab");
        await ReaderTests.Render();
        var handler = ConfigureAi(scope);
        var input = scope.Get<TextBox>("ReaderAiQuestionBox");
        const string pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        using var bitmap = new Bitmap(new MemoryStream(Convert.FromBase64String(pixel)));
        await scope.Window.Clipboard!.SetBitmapAsync(bitmap);
        // Exercise the same paste event used by Ctrl+V and the text context menu.
        input.RaiseEvent(new RoutedEventArgs(TextBox.PastingFromClipboardEvent));
        for (var i = 0; i < 100 && scope.Window.ReaderAiDraftImages.Count == 0; i++) await Task.Delay(10);
        var image = Assert.Single(scope.Window.ReaderAiDraftImages);
        await ReaderTests.Render();
        var remove = scope.Window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Tag, image));
        Assert.True(remove.IsEffectivelyVisible);
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(scope.Window.ReaderAiDraftImages);

        // Headless clipboard returns the same bitmap instance rather than a
        // newly decoded OS bitmap, so give the next paste its own instance.
        using var nextBitmap = new Bitmap(new MemoryStream(Convert.FromBase64String(pixel)));
        await scope.Window.Clipboard!.SetBitmapAsync(nextBitmap);
        await scope.Call<Task>("PasteReaderAiClipboardAsync");
        Assert.True(scope.Window.ReaderAiDraftImages.Count == 1, scope.Get<TextBlock>("ReaderAiImageStatus").Text);
        input.Text = string.Empty;
        scope.Get<Button>("ReaderAiSendButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && scope.Field<bool>("_readerAiBusy"); i++) await Task.Delay(10);
        Assert.Empty(scope.Window.ReaderAiDraftImages);
        Assert.NotEmpty(scope.Window.ReaderAiMessages);
        Assert.Single(scope.Window.ReaderAiMessages.First().Images);
        Assert.Contains("data:image/png;base64,", Assert.Single(handler.Requests));
        Assert.Empty(handler.PlanRequests);
        var answer = scope.Window.ReaderAiMessages.Last();
        Assert.True(answer.CanRetry);
        answer.RetryAction!();
        for (var i = 0; i < 100 && (handler.Requests.Count < 2 || scope.Field<bool>("_readerAiBusy")); i++) await Task.Delay(10);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);

        input.Text = "AB";
        input.SelectionStart = 1;
        input.SelectionEnd = 2;
        await scope.Window.Clipboard!.SetTextAsync("普通文字");
        await scope.Call<Task>("PasteReaderAiClipboardAsync");
        Assert.Equal("A普通文字", input.Text);
        scope.Call("ClearReaderAiConversation");
        Assert.Empty(scope.Window.ReaderAiDraftImages);
    });

    [Theory]
    [InlineData("BookQuestion")]
    [InlineData("SelectionExplain")]
    public Task MissingBookEvidenceStillSendsAnAnswerRequestAndCanRetry(string kind) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var handler = ConfigureAi(scope);
        scope.Set("_readerPendingSelection", "目标概念来自当前选文");

        await Send(scope, kind);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        var instructions = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("模型已有知识", instructions);
        Assert.Contains("补充知识", instructions);
        Assert.Contains("不附书籍引用", instructions);
        Assert.Contains("不得声称已联网核实", instructions);
        Assert.Contains("用户明确要求仅根据原文", instructions);
        Assert.Contains("本次没有可用的书籍证据", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.Contains("目标概念来自当前选文", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        var answer = scope.Window.ReaderAiMessages.Last();
        Assert.Equal(CapturingAiHandler.Answer, answer.Content);
        Assert.Empty(scope.Window.ReaderAiSources);
        Assert.True(answer.CanRetry);

        scope.Set("_readerPendingSelection", "重试时已经换了选文");
        answer.RetryAction!();
        for (var attempt = 0; attempt < 100 && (handler.Requests.Count < 2 || scope.Field<bool>("_readerAiBusy")); attempt++)
            await Task.Delay(10);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(request, handler.Requests[1]);
    });

    [Theory]
    [InlineData("ChapterSummary")]
    [InlineData("BookSummary")]
    public Task SummariesWithoutBookTextRemainBlocked(string kind) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var handler = ConfigureAi(scope);

        await Send(scope, kind);

        Assert.Empty(handler.Requests);
        Assert.Empty(handler.PlanRequests);
        Assert.False(scope.Window.ReaderAiMessages.Last().CanRetry);
        Assert.Contains("没有可用的书籍文本", scope.Get<TextBlock>("ReaderAiStatusText").Text);
        var instructions = scope.Call<string>("BuildReaderAiInstructions", RequestKind(kind));
        Assert.Contains("不要用模型知识补造书籍内容", instructions);
        Assert.DoesNotContain("仍应使用模型已有知识", instructions);
    });

    [Fact]
    public Task TypedQuestionKeepsSelectedPassageWhenGlobalHitsFillTheBudget() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var handler = ConfigureAi(scope);
        var bookId = Guid.NewGuid();
        var file = new BookFile { Id = Guid.NewGuid(), BookId = bookId, Format = "epub", Sha256 = new string('a', 64) };
        using var card = new BookCardViewModel(new Book { Id = bookId, Title = "阅读上下文", Files = [file] }, scope.Paths.Data);
        scope.Set("_readerBookCard", card);
        scope.Set("_readerBookFile", file);
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache,
            [Path.Combine(scope.Paths.ReaderCache, "other.xhtml"), Path.Combine(scope.Paths.ReaderCache, "current.xhtml")], [], []));
        scope.Set("_readerChapterIndex", 1);
        scope.Set("_readerPendingSelection", "目标概念");
        scope.Set("_readerPendingSelectionStartOffset", 100);
        scope.Set("_readerPendingSelectionEndOffset", 104);
        var readerData = scope.Field<ReaderDataService>("_readerData");
        const string passage = "正在阅读的正文解释了目标概念的背景。";
        var drafts = Enumerable.Range(0, 12).Select(index => new BookContentChunkDraft(
            0, index, "其他章节", "other.xhtml", index * 2000, (index + 1) * 2000,
            $"其他检索结果{index}。" + new string((char)('甲' + index), 1980))).ToList();
        drafts.Add(new BookContentChunkDraft(1, 0, "当前章节", "current.xhtml", 0, 1000, passage));
        await readerData.ReplaceBookChunksAsync(bookId, file.Id, file.Sha256, drafts);
        var hits = (await readerData.GetBookChapterChunksAsync(file.Id, 0))
            .Select((chunk, index) => new ReaderRetrievalResult(chunk, 1d / (index + 1), VectorRank: index + 1)).ToArray();
        scope.Set("_readerRetriever", new StaticRetriever(hits));

        await Send(scope, "BookQuestion");

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Contains(passage, prompt);
        Assert.Contains(scope.Window.ReaderAiSources, source => source.Content.Contains(passage, StringComparison.Ordinal));
        Assert.Equal(CapturingAiHandler.Answer, scope.Window.ReaderAiMessages.Last().Content);
    });

    [Fact]
    public Task NoMatchingOrReadingChunksDoesNotSubstituteTheBookOpening() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var handler = ConfigureAi(scope);
        var bookId = Guid.NewGuid();
        var file = new BookFile { Id = Guid.NewGuid(), BookId = bookId, Format = "epub", Sha256 = new string('b', 64) };
        using var card = new BookCardViewModel(new Book { Id = bookId, Title = "检索未命中", Files = [file] }, scope.Paths.Data);
        scope.Set("_readerBookCard", card);
        scope.Set("_readerBookFile", file);
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache,
            [Path.Combine(scope.Paths.ReaderCache, "toc.xhtml"), Path.Combine(scope.Paths.ReaderCache, "empty.xhtml")], [], []));
        scope.Set("_readerChapterIndex", 1);
        const string unrelated = "无关目录：买车、维生素、恋爱。";
        await scope.Field<ReaderDataService>("_readerData").ReplaceBookChunksAsync(bookId, file.Id, file.Sha256,
            [new BookContentChunkDraft(0, 0, "目录", "toc.xhtml", 0, unrelated.Length, unrelated)]);
        scope.Set("_readerRetriever", new StaticRetriever([]));

        await Send(scope, "BookQuestion");

        Assert.Single(handler.Requests);
        Assert.Empty(scope.Window.ReaderAiSources);
        using var body = JsonDocument.Parse(handler.Requests[0]);
        var messages = body.RootElement.GetProperty("messages");
        Assert.DoesNotContain("无关目录", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.Equal(CapturingAiHandler.Answer, scope.Window.ReaderAiMessages.Last().Content);
    });

    [Fact]
    public Task AutomaticSearchAddsExternalEvidenceAndRetryReusesResults() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var ai = ConfigureAi(scope);
        ai.Plan = "{\"search\":true,\"query\":\"目标概念 官方解释\"}";
        var search = ConfigureSearch(scope);
        scope.Set("_readerPendingSelection", "这段正文不会整段发给搜索引擎");

        await Send(scope, "BookQuestion");

        Assert.Single(ai.PlanRequests);
        Assert.Single(search.Requests);
        Assert.Contains(Uri.EscapeDataString("目标概念 官方解释"), search.Requests[0]);
        Assert.DoesNotContain(Uri.EscapeDataString("这段正文不会整段发给搜索引擎"), search.Requests[0]);
        var request = Assert.Single(ai.Requests);
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Contains("https://example.org/definition", prompt);
        Assert.Contains("权威资料中的概念定义", prompt);
        Assert.Contains("仅搜索摘要", prompt);
        var answer = scope.Window.ReaderAiMessages.Last();
        Assert.Contains("已联网搜索", answer.Status);
        Assert.Empty(answer.Sources);

        answer.RetryAction!();
        for (var attempt = 0; attempt < 100 && (ai.Requests.Count < 2 || scope.Field<bool>("_readerAiBusy")); attempt++)
            await Task.Delay(10);
        Assert.Equal(2, ai.Requests.Count);
        Assert.Equal(request, ai.Requests[1]);
        Assert.Single(search.Requests);
        Assert.Single(ai.PlanRequests);
    });

    [Theory]
    [InlineData("不要联网，解释这个术语")]
    [InlineData("仅根据原文解释这个术语")]
    public Task OriginalOnlyRequestsNeverSearch(string question) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var ai = ConfigureAi(scope);
        var search = ConfigureSearch(scope);

        await scope.Call<Task>("SendReaderAiQuestionAsync", question, RequestKind("BookQuestion"), false, null, null);

        Assert.Empty(ai.PlanRequests);
        Assert.Empty(search.Requests);
        Assert.Single(ai.Requests);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SearchFailureOrNoResultsStillAllowsAnHonestAnswer(bool fail) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var ai = ConfigureAi(scope);
        ai.Plan = "{\"search\":true,\"query\":\"目标概念\"}";
        var search = ConfigureSearch(scope);
        search.Fail = fail;
        search.Html = "<html><body>No results</body></html>";

        await Send(scope, "BookQuestion");

        Assert.Equal(2, search.Requests.Count);
        using var body = JsonDocument.Parse(Assert.Single(ai.Requests));
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains(fail ? "本次联网搜索失败" : "没有找到可用结果", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        var answer = scope.Window.ReaderAiMessages.Last();
        Assert.Equal(CapturingAiHandler.Answer, answer.Content);
        Assert.Contains(fail ? "暂不可用" : "未找到可用结果", answer.Status);
    });

    [Fact]
    public Task NetworkDisabledDoesNotPlanSearchOrGenerate() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var ai = ConfigureAi(scope);
        var search = ConfigureSearch(scope);
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { NetworkEnabled = false });

        await Send(scope, "BookQuestion");

        Assert.Empty(search.Requests);
        Assert.Empty(ai.PlanRequests);
        Assert.Empty(ai.Requests);
        Assert.Contains("网络访问已关闭", scope.Get<TextBlock>("ReaderAiStatusText").Text);
    });

    [Fact]
    public Task StopCancelsSearchBeforeAnswerGeneration() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var ai = ConfigureAi(scope);
        ai.Plan = "{\"search\":true,\"query\":\"目标概念\"}";
        var search = ConfigureSearch(scope);
        search.WaitForCancellation = true;
        var send = Send(scope, "BookQuestion");
        await search.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        scope.Field<CancellationTokenSource>("_readerAiCancellation").Cancel();
        await send;

        Assert.Empty(ai.Requests);
        Assert.Contains("已停止", scope.Window.ReaderAiMessages.Last().Status);
        Assert.False(scope.Field<bool>("_readerAiBusy"));
    });

    private static SearchHandler ConfigureSearch(ReaderTestWindow scope)
    {
        var handler = new SearchHandler();
        scope.Field<ReaderWebSearchService>("_readerWebSearch").Dispose();
        scope.Set("_readerWebSearch", new ReaderWebSearchService(handler));
        return handler;
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool Fail { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Html { get; set; } = "<table><tr><td><a class='result-link' href='https://example.org/definition'>官方概念解释</a></td></tr><tr><td class='result-snippet'>权威资料中的概念定义</td></tr></table>";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Html, Encoding.UTF8, "text/html")
            };
        }
    }

    private static object RequestKind(string name) => Enum.Parse(
        typeof(MainWindow).GetNestedType("ReaderAiRequestKind", BindingFlags.NonPublic)!, name);

    private static Task Send(ReaderTestWindow scope, string kind) => scope.Call<Task>(
        "SendReaderAiQuestionAsync", "目标概念是什么意思？", RequestKind(kind), false, null, null);

    private static CapturingAiHandler ConfigureAi(ReaderTestWindow scope)
    {
        var handler = new CapturingAiHandler();
        scope.Field<AiChatClient>("_aiChatClient").Dispose();
        scope.Set("_aiChatClient", new AiChatClient(handler));
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { AiEnabled = true, NetworkEnabled = true });
        scope.Set("_readerAiSettings", new AiConnectionSettings
        {
            Provider = "custom", BaseUrl = "https://example.test/v1", Model = "test-model", ApiKey = "test-key"
        });
        return handler;
    }

    private sealed class CapturingAiHandler : HttpMessageHandler
    {
        public const string Answer = "补充知识：这是概念解释。";
        public List<string> Requests { get; } = [];
        public List<string> PlanRequests { get; } = [];
        public string Plan { get; set; } = "{\"search\":false,\"query\":\"\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var planning = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("联网搜索规划器", StringComparison.Ordinal);
            (planning ? PlanRequests : Requests).Add(body);
            var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = planning ? Plan : Answer } } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class StaticRetriever(IReadOnlyList<ReaderRetrievalResult> results) : IReaderRetriever
    {
        public Task<IReadOnlyList<ReaderRetrievalResult>> RetrieveAsync(Guid bookId, string query, int topK, CancellationToken cancellationToken = default)
            => Task.FromResult(results);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}
