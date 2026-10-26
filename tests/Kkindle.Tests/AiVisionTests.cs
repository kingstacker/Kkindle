using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class AiVisionTests
{
    [Theory]
    [InlineData("custom")]
    [InlineData("openai")]
    public async Task MultiplePastedImagesAndImageHistoryReachTheAnswerModel(string provider)
    {
        string? requestBody = null;
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage
            {
                Content = new StringContent(provider == "openai"
                    ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n"
                    : "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }));
        var history = new AiConversationTurn[]
        {
            new("user", "previous image") { Images = [new("image/png", [1])] },
            new("assistant", "previous answer")
        };
        var settings = new AiConnectionSettings { Provider = provider, BaseUrl = "https://example.test/v1", Model = "vision", ApiKey = "test" };
        await foreach (var _ in client.StreamAsync(settings, "system", "compare images", history,
            images: [new("image/png", [2]), new("image/png", [3])])) { }

        using (var json = JsonDocument.Parse(requestBody!))
        {
            var turns = json.RootElement.GetProperty(provider == "openai" ? "input" : "messages");
            var current = turns[turns.GetArrayLength() - 1].GetProperty("content");
            Assert.Equal(3, current.GetArrayLength());
            Assert.Equal(provider == "openai" ? "input_image" : "image_url", current[1].GetProperty("type").GetString());
            Assert.Contains("data:image/png;base64,AQ==", requestBody);
            Assert.Contains("data:image/png;base64,Ag==", requestBody);
            Assert.Contains("data:image/png;base64,Aw==", requestBody);
        }

        await foreach (var _ in client.StreamAsync(settings, "system", "follow up", history)) { }
        Assert.Contains("data:image/png;base64,AQ==", requestBody);
        Assert.DoesNotContain("data:image/png;base64,Ag==", requestBody);
    }

    [Fact]
    public async Task ChatCompletionsVisionRequestContainsTextAndDataUriImage()
    {
        string? requestBody = null;
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n"
                        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));

        var output = new StringBuilder();
        await foreach (var chunk in client.StreamVisionAsync(
            new AiConnectionSettings
            {
                Provider = "custom",
                BaseUrl = "https://example.test/v1",
                Model = "vision-model",
                ApiKey = "test-key"
            },
            "Explain the image.",
            "What is shown?",
            [],
            new AiImageAttachment("image/png", [1, 2, 3]),
            cancellationToken: CancellationToken.None))
        {
            output.Append(chunk.Text);
        }

        Assert.Equal("ok", output.ToString());
        Assert.NotNull(requestBody);
        Assert.Contains("\"type\":\"image_url\"", requestBody);
        Assert.Contains("data:image/png;base64,AQID", requestBody);
        Assert.Contains("\"type\":\"text\"", requestBody);
    }
}
