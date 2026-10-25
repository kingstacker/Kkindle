using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace Kkindle.Infrastructure;

public sealed record ReaderWebSearchResult(string Title, string Url, string Snippet);

/// <summary>Retrieves public search snippets without requiring a separate API key.</summary>
public sealed class ReaderWebSearchService : IDisposable
{
    private readonly HttpClient _httpClient;
    private const int MaximumResponseBytes = 1024 * 1024;

    public ReaderWebSearchService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Kkindle/1.0)");
    }

    public async Task<IReadOnlyList<ReaderWebSearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        query = query.Trim();
        if (query.Length > 300) query = query[..300];
        var encoded = Uri.EscapeDataString(query);
        var endpoints = new[]
        {
            (Url: "https://lite.duckduckgo.com/lite/?q=" + encoded, Is360: false),
            (Url: "https://www.so.com/s?q=" + encoded, Is360: true)
        };
        Exception? lastFailure = null;
        var receivedResultsPage = false;
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var html = await FetchAsync(endpoint.Url, cancellationToken).ConfigureAwait(false);
                var results = endpoint.Is360 ? Parse360Results(html) : ParseResults(html);
                if (results.Count > 0) return results;
                receivedResultsPage = true;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException
                                              && !cancellationToken.IsCancellationRequested)
            {
                lastFailure = exception;
            }
        }
        if (!receivedResultsPage && lastFailure is not null)
            throw new HttpRequestException("Web search is unavailable.", lastFailure);
        return [];
    }

    private async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellationToken = timeout.Token;
        using var response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // A challenge is not a successful search, even if the server returns 2xx.
        if (response.StatusCode == HttpStatusCode.Accepted)
            throw new InvalidDataException("Search service requires verification.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Search response is too large.");
            body.Write(buffer, 0, read);
        }
        var html = Encoding.UTF8.GetString(body.ToArray());
        if (html.Contains("anomaly.js", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<title>360搜索访问异常", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<title>安全验证", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Search service requires verification.");
        return html;
    }

    internal static IReadOnlyList<ReaderWebSearchResult> ParseResults(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var results = new List<ReaderWebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in document.DocumentNode.SelectNodes("//a[contains(concat(' ',normalize-space(@class),' '),' result-link ')]")
                     ?? Enumerable.Empty<HtmlNode>())
        {
            var url = ResolveResultUrl(link.GetAttributeValue("href", ""));
            if (url is null || !seen.Add(url)) continue;
            var title = CleanText(link.InnerText, 180);
            var snippetRow = link.Ancestors("tr").FirstOrDefault()?.SelectSingleNode("following-sibling::tr[1]");
            var snippet = CleanText(snippetRow?.SelectSingleNode(".//td[contains(@class,'result-snippet')]")?.InnerText ?? "", 1000);
            if (title.Length == 0 || snippet.Length == 0) continue;
            results.Add(new ReaderWebSearchResult(title, url, snippet));
            if (results.Count == 5) break;
        }
        return results;
    }

    internal static IReadOnlyList<ReaderWebSearchResult> Parse360Results(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var results = new List<ReaderWebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.DocumentNode.SelectNodes("//li[contains(concat(' ',normalize-space(@class),' '),' res-list ')]")
                     ?? Enumerable.Empty<HtmlNode>())
        {
            var link = item.SelectSingleNode(".//h3/a[@data-mdurl]");
            if (link is null) continue;
            var url = ResolveResultUrl(link.GetAttributeValue("data-mdurl", ""));
            var title = CleanText(link.InnerText, 180);
            var snippet = CleanText(item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' res-desc ')]")?.InnerText ?? "", 1000);
            if (url is null || title.Length == 0 || snippet.Length == 0 || !seen.Add(url)) continue;
            results.Add(new ReaderWebSearchResult(title, url, snippet));
            if (results.Count == 5) break;
        }
        return results;
    }

    private static string? ResolveResultUrl(string href)
    {
        href = WebUtility.HtmlDecode(href);
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var target = uri.Query.TrimStart('?').Split('&')
                .FirstOrDefault(value => value.StartsWith("uddg=", StringComparison.Ordinal));
            if (target is null || !Uri.TryCreate(Uri.UnescapeDataString(target[5..]), UriKind.Absolute, out uri)) return null;
        }
        return (uri.Scheme is "https" or "http") && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }

    private static string CleanText(string value, int limit)
    {
        var text = string.Join(' ', WebUtility.HtmlDecode(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > limit ? text[..limit] + "…" : text;
    }

    public void Dispose() => _httpClient.Dispose();
}
