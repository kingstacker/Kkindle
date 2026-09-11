using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class AiStreamCompletionTests
{
    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("tool_calls")]
    [InlineData("eof")]
    [InlineData("error")]
    [InlineData("malformed")]
    [InlineData("refusal")]
    [InlineData("event-error")]
    public async Task RejectsIncompleteChatCompletionAfterTextWasReceived(string ending)
    {
        var suffix = ending switch
        {
            "eof" => string.Empty,
            "error" => "data: {\"error\":{\"message\":\"interrupted\"}}\n\ndata: [DONE]\n\n",
            "malformed" => "data: {broken\n\ndata: [DONE]\n\n",
            "refusal" => "data: {\"choices\":[{\"delta\":{\"refusal\":\"refused\"}}]}\n\ndata: [DONE]\n\n",
            "event-error" => "event: error\ndata: {\"message\":\"interrupted\"}\n\ndata: [DONE]\n\n",
            _ => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"" + ending + "\"}]}\n\ndata: [DONE]\n\n"
        };
        using var client = Client("data: {\"choices\":[{\"delta\":{\"content\":\"only the beginning\"}}]}\n\n" + suffix);
        await Assert.ThrowsAsync<InvalidDataException>(() => Complete(client));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("done")]
    [InlineData("json")]
    public async Task AcceptsExplicitlyCompletedChatResponses(string ending)
    {
        var response = ending == "json"
            ? "{\"choices\":[{\"message\":{\"content\":\"complete\"},\"finish_reason\":\"stop\"}]}"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"complete\"}}]}\n\n" + (ending == "done"
                ? "data: [DONE]\n\n"
                : "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        using var client = Client(response);
        Assert.Equal("complete", await Complete(client));
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("eof")]
    [InlineData("error")]
    [InlineData("response.refusal.delta")]
    public async Task RejectsIncompleteResponsesApiStream(string ending)
    {
        var suffix = ending == "eof" ? string.Empty
            : "data: {\"type\":\"" + ending + "\",\"response\":{\"status\":\""
                + (ending == "response.failed" ? "failed" : ending == "response.incomplete" ? "incomplete" : "in_progress")
                + "\"}}\n\n";
        using var client = Client("data: {\"type\":\"response.output_text.delta\",\"delta\":\"beginning\"}\n\n" + suffix);
        await Assert.ThrowsAsync<InvalidDataException>(() => Complete(client, "openai"));
    }

    [Fact]
    public async Task CompletedResponsesEventDoesNotDuplicateStreamedText()
    {
        using var client = Client("""
            data: {"type":"response.output_text.delta","delta":"hello"}

            data: {"type":"response.output_text.delta","delta":" world"}

            data: {"type":"response.completed","response":{"status":"completed","output_text":"hello world"}}


            """);
        Assert.Equal("hello world", await Complete(client, "openai"));
    }

    [Fact]
    public async Task ReadsAllTextItemsFromCompletedResponsesBody()
    {
        using var client = Client("""
            {"status":"completed","output":[
              {"type":"message","content":[{"type":"output_text","text":"first "}]},
              {"type":"message","content":[{"type":"output_text","text":"second"}]}]}
            """);
        Assert.Equal("first second", await Complete(client, "openai"));
    }

    private static Task<string> Complete(AiChatClient client, string provider = "custom") => client.CompleteAsync(
        new AiConnectionSettings
        {
            Provider = provider, BaseUrl = "https://example.test/v1", ApiKey = "test-key", Model = "test-model"
        }, "Translate", "Hello", []);

    private static AiChatClient Client(string response) => new(new TestHelpers.StubHttpMessageHandler(_ =>
        new HttpResponseMessage { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") }));
}
