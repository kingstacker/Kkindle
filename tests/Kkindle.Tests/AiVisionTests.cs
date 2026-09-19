using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class AiVisionTests
{
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
