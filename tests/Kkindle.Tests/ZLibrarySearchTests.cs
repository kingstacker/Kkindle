using System.Net;
using System.Text;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class ZLibrarySearchTests
{
    [Fact]
    public async Task PageTwoRetriesConnectionFailureWithSameSearchAndFilters()
    {
        var forms = new List<string>();
        using var service = new ZLibraryService(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Json("{\"success\":1}");
            forms.Add(await request.Content!.ReadAsStringAsync(token));
            if (forms.Count == 1) throw new HttpRequestException("An error occurred while sending the request.");
            return Json("{\"success\":1,\"books\":[{\"id\":42,\"title\":\"Second page\"}],\"pagination\":{\"current\":2,\"total_pages\":3,\"total_items\":50}}");
        }));
        var result = await service.SearchAsync("test title", page: 2, extensions: ["epub"], languages: ["Chinese"]);
        Assert.Equal(2, result.Page);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(42, Assert.Single(result.Books).Id);
        Assert.Equal(2, forms.Count);
        Assert.Equal(forms[0], forms[1]);
        Assert.Contains("page=2", forms[1]);
        Assert.Contains("extensions%5B0%5D=epub", forms[1]);
        Assert.Contains("languages%5B0%5D=chinese", forms[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    public async Task RetriesAreBoundedAndDoNotRetryAuthenticationErrors(HttpStatusCode status, int expected)
    {
        var attempts = 0;
        using var service = new ZLibraryService(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"success\":1}"));
            attempts++;
            return Task.FromResult(Json("{\"success\":0}", status));
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => service.SearchAsync("test", page: 2));
        Assert.Equal(expected, attempts);
    }

    [Fact]
    public async Task CancellationStopsSearchWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        using var service = new ZLibraryService(new Handler((request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"success\":1}"));
            attempts++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("test", cancellationToken: cancellation.Token));
        Assert.Equal(1, attempts);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
