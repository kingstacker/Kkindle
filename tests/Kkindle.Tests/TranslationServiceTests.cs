using System.Net;
using System.Text;
using System.Text.Json;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class TranslationServiceTests
{
    [Fact]
    public void NormalizesGoogleProxyAddress()
    {
        Assert.Equal(
            "http://127.0.0.1:7890",
            TranslationProxy.NormalizeAddress("  127.0.0.1:7890/  "));
        Assert.Equal(string.Empty, TranslationProxy.NormalizeAddress("  "));
        Assert.Throws<ArgumentException>(
            () => TranslationProxy.NormalizeAddress("socks5://127.0.0.1:1080"));
    }

    [Fact]
    public async Task TranslatesWithGoogleWebEndpoint()
    {
        using var aiClient = new AiChatClient();
        using var service = CreateService(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("translate.googleapis.com", request.RequestUri?.Host);
            Assert.Contains("client=gtx", request.RequestUri?.Query);
            Assert.Contains("tl=zh-CN", request.RequestUri?.Query);
            return JsonResponse("[[[\"你好，世界\",\"Hello world\",null,null,1]],null,\"en\"]");
        }, aiClient);

        var result = await service.TranslateAsync(
            "Hello world",
            ReaderTranslationProvider.Google,
            "zh-CN");

        Assert.Equal("你好，世界", result);
    }

    [Theory]
    [InlineData("[\"你好，世界\"]")]
    [InlineData("[[\"你好，世界\",\"en\"]]")]
    public async Task FallsBackToLegacyGoogleEndpointWhenPrimaryIsRateLimited(string responseBody)
    {
        var requests = new List<HttpRequestMessage>();
        using var aiClient = new AiChatClient();
        using var service = CreateService(request =>
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limited", Encoding.UTF8, "text/plain")
                };
            }

            Assert.Equal("clients5.google.com", request.RequestUri?.Host);
            Assert.Contains("dict-chrome-ex", request.RequestUri?.Query);
            return JsonResponse(responseBody);
        }, aiClient);

        var result = await service.TranslateAsync(
            "Hello world",
            ReaderTranslationProvider.Google,
            "zh-CN");

        Assert.Equal("你好，世界", result);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    [InlineData("[\"\"]")]
    [InlineData("[[\" \",\"en\"]]")]
    [InlineData("[[[\" \"]]]")]
    public async Task EmptyGoogleResponsesExplainHowToRetry(string responseBody)
    {
        using var aiClient = new AiChatClient();
        using var service = CreateService(_ => JsonResponse(responseBody), aiClient);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.TranslateAsync("Hello", ReaderTranslationProvider.Google, "zh-CN"));
        Assert.Contains("空结果", error.Message);
        Assert.Contains("代理设置", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoogleConnectionErrorsExplainNetworkAndProxySettings(bool timeout)
    {
        using var aiClient = new AiChatClient();
        using var service = CreateService(_ => throw (timeout
            ? new TaskCanceledException("timeout") : new HttpRequestException("connection failed")), aiClient);
        var error = await Record.ExceptionAsync(() =>
            service.TranslateAsync("Hello", ReaderTranslationProvider.Google, "zh-CN"));
        Assert.NotNull(error);
        Assert.Contains(timeout ? "超时" : "无法连接", error.Message);
        Assert.Contains("代理设置", error.Message);
    }

    [Fact]
    public async Task TranslatesWithBingFreeWebEndpointAndRefreshesCredentials()
    {
        var requests = new List<HttpRequestMessage>();
        using var aiClient = new AiChatClient();
        using var service = CreateService(request =>
        {
            requests.Add(request);
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "var params_AbusePreventionHelper = [178894, \"temporary-token\", 3600000];",
                        Encoding.UTF8,
                        "text/html")
                };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/ttranslatev3", request.RequestUri?.AbsolutePath);
            Assert.Contains("IID=translator.5024.1", request.RequestUri?.Query);
            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("fromLang=auto-detect", form);
            Assert.Contains("to=zh-Hans", form);
            Assert.Contains("token=temporary-token", form);
            Assert.Contains("key=178894", form);
            return JsonResponse(
                "[{\"translations\":[{\"text\":\"你好，世界\",\"to\":\"zh-Hans\"}]}]");
        }, aiClient);

        var result = await service.TranslateAsync(
            "Hello world",
            ReaderTranslationProvider.Bing,
            "zh-CN");

        Assert.Equal("你好，世界", result);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("cn.bing.com")]
    [InlineData("www.bing.com")]
    public async Task BingPostsToTheFinalTranslatorPageHost(string pageHost)
    {
        using var aiClient = new AiChatClient();
        using var service = CreateService(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"https://{pageHost}/translator"),
                    Content = new StringContent("var params_AbusePreventionHelper = [178894, \"regional-token\", 3600000];")
                };

            // A POST to the pre-redirect host becomes an empty GET on the
            // regional site, although the server still returns HTTP 200.
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(pageHost, request.RequestUri!.Host);
            Assert.Equal($"https://{pageHost}/translator", request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal($"https://{pageHost}", Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Contains("token=regional-token", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("[{\"translations\":[{\"text\":\"你好\",\"to\":\"zh-Hans\"}]}]");
        }, aiClient);

        Assert.Equal("你好", await service.TranslateAsync("Hello", ReaderTranslationProvider.Bing, "zh-CN"));
    }

    [Fact]
    public async Task TranslatesWithConfiguredAiWithoutAddingChatHistory()
    {
        using var aiClient = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            var messages = document.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Contains(
                messages.EnumerateArray(),
                message => message.GetProperty("content").GetString()!.Contains("Hello world"));
            Assert.DoesNotContain(
                messages.EnumerateArray(),
                message => message.GetProperty("content").GetString()!.Contains("旧对话"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"你好，世界\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));
        using var service = CreateService(_ => throw new InvalidOperationException("online provider should not be called"), aiClient);

        var result = await service.TranslateAsync(
            "Hello world",
            ReaderTranslationProvider.Ai,
            "zh-CN",
            new AiConnectionSettings
            {
                Provider = "custom",
                BaseUrl = "https://api.example.com/v1",
                Model = "translation-model",
                ApiKey = "sk-test"
            });

        Assert.Equal("你好，世界", result);
    }

    private static TranslationService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        AiChatClient? aiClient = null)
    {
        return new TranslationService(
            aiClient ?? new AiChatClient(),
            new TestHelpers.StubHttpMessageHandler(responder));
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
