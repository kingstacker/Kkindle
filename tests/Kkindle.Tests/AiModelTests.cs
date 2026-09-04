using System.Net;
using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class AiModelTests
{
    [Fact]
    public async Task ListsModelsFromOpenAiCompatibleModelsEndpoint()
    {
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/models", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer sk-test", request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"object":"list","data":[{"id":"deepseek-v4-pro"},{"id":"deepseek-v4-flash"},{"id":"deepseek-v4-pro"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var models = await client.ListModelsAsync(new AiConnectionSettings
        {
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-test"
        });

        Assert.Equal(["deepseek-v4-pro", "deepseek-v4-flash"], models);
    }

    [Fact]
    public async Task TestConnectionUsesARealSimpleQuestion()
    {
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer sk-test", request.Headers.Authorization?.ToString());
            var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var messages = document.RootElement.GetProperty("messages");
            Assert.Contains(
                messages.EnumerateArray(),
                message => message.GetProperty("content").GetString() == "请只回复：连接成功");
            Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"连接成功\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));

        var answer = await client.TestConnectionAsync(new AiConnectionSettings
        {
            Provider = "custom",
            BaseUrl = "https://api.example.com/v1",
            Model = "test-model",
            ApiKey = "sk-test"
        });

        Assert.Equal("连接成功", answer);
    }

    [Fact]
    public void NormalizesDeprecatedDeepSeekModelNames()
    {
        Assert.Equal(
            "deepseek-v4-flash",
            AiConnectionSettings.NormalizeModel("deepseek", "deepseek-chat"));
        Assert.Equal(
            "deepseek-v4-flash",
            AiConnectionSettings.NormalizeModel("deepseek", "deepseek-reasoner"));
        Assert.DoesNotContain(
            "deepseek-chat",
            AiConnectionSettings.GetModelOptions("deepseek", "deepseek-v4-flash"));
    }

}
