using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kkindle.Infrastructure;

public sealed record DoubanBookCandidate(
    long SubjectId,
    string Title,
    string Abstract,
    string CoverUrl,
    string Url,
    double? Rating,
    int RatingCount)
{
    public string DisplayText => Rating is null
        ? $"{Title}\n{Abstract}"
        : $"{Title}　豆瓣 {Rating:0.0}（{RatingCount} 人评价）\n{Abstract}";
}

public sealed record DoubanBookMetadata(
    string Title,
    string Authors,
    string? Translators,
    string? Publisher,
    string? PublishDate,
    string? Isbn,
    string? Pages,
    string? Binding,
    string? Price,
    string? OriginalTitle,
    string? Series,
    string? Description,
    string? CoverUrl,
    string Url,
    double? Rating,
    int RatingCount);

/// <summary>
/// Retrieves public book metadata from Douban's normal search and subject pages.
/// Calls are deliberately serialized and rate-limited because this feature is
/// intended for an explicit, one-book-at-a-time user action.
/// </summary>
public sealed class DoubanMetadataService : IDisposable
{
    private static readonly RegexOptions HtmlOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly TimeSpan _minimumInterval;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private bool _disposed;

    public DoubanMetadataService(HttpMessageHandler? handler = null, TimeSpan? minimumInterval = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Kkindle/1.0");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
        _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(1.2);
    }

    public async Task<IReadOnlyList<DoubanBookCandidate>> SearchAsync(
        string title,
        string? authors = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        var query = string.Join(' ', new[] { title.Trim(), authors?.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var uri = new Uri($"https://book.douban.com/subject_search?search_text={Uri.EscapeDataString(query)}&cat=1001");
        var html = await GetStringAsync(uri, cancellationToken);
        var dataMatch = Regex.Match(
            html,
            @"window\.__DATA__\s*=\s*(?<json>\{.*?\})\s*;\s*window\.__USER__",
            HtmlOptions);
        if (!dataMatch.Success)
            throw new InvalidOperationException("豆瓣搜索页未返回可识别的书籍数据，可能触发了访问验证，请稍后重试。");

        using var document = JsonDocument.Parse(dataMatch.Groups["json"].Value);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<DoubanBookCandidate>();
        foreach (var item in items.EnumerateArray())
        {
            var candidateTitle = GetString(item, "title");
            var url = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(candidateTitle) || string.IsNullOrWhiteSpace(url)) continue;

            var subjectId = item.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id) ? id : 0;
            var rating = default(double?);
            var ratingCount = 0;
            if (item.TryGetProperty("rating", out var ratingElement))
            {
                if (ratingElement.TryGetProperty("value", out var valueElement) && valueElement.TryGetDouble(out var value)) rating = value;
                if (ratingElement.TryGetProperty("count", out var countElement)) countElement.TryGetInt32(out ratingCount);
            }

            results.Add(new DoubanBookCandidate(
                subjectId,
                candidateTitle,
                GetString(item, "abstract") ?? string.Empty,
                GetString(item, "cover_url") ?? string.Empty,
                url,
                rating,
                ratingCount));
        }
        return results;
    }

    public async Task<DoubanBookMetadata> GetDetailsAsync(
        DoubanBookCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var html = await GetStringAsync(new Uri(candidate.Url), cancellationToken);
        var jsonTitle = string.Empty;
        var authors = new List<string>();
        var jsonIsbn = string.Empty;
        foreach (Match match in Regex.Matches(html, """<script[^>]*type=["']application/ld\+json["'][^>]*>(?<json>.*?)</script>""", HtmlOptions))
        {
            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["json"].Value));
                var root = document.RootElement;
                var type = GetString(root, "@type");
                if (!string.Equals(type, "Book", StringComparison.OrdinalIgnoreCase)) continue;
                jsonTitle = GetString(root, "name") ?? string.Empty;
                jsonIsbn = GetString(root, "isbn") ?? string.Empty;
                if (root.TryGetProperty("author", out var authorElement))
                {
                    if (authorElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var author in authorElement.EnumerateArray())
                        {
                            var name = author.ValueKind == JsonValueKind.Object ? GetString(author, "name") : author.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) authors.Add(name);
                        }
                    }
                    else if (authorElement.ValueKind == JsonValueKind.Object)
                    {
                        var name = GetString(authorElement, "name");
                        if (!string.IsNullOrWhiteSpace(name)) authors.Add(name);
                    }
                }
                break;
            }
            catch (JsonException)
            {
                // Ignore unrelated or malformed structured-data blocks.
            }
        }

        var info = Regex.Match(html, """<div\s+id=["']info["'][^>]*>(?<value>.*?)</div>""", HtmlOptions).Groups["value"].Value;
        var ratingText = Regex.Match(html, """class=["'][^"']*rating_num[^"']*["'][^>]*>\s*(?<value>[\d.]+)""", HtmlOptions).Groups["value"].Value;
        var votesText = Regex.Match(html, """property=["']v:votes["'][^>]*>\s*(?<value>\d+)""", HtmlOptions).Groups["value"].Value;
        double? rating = double.TryParse(ratingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRating)
            ? parsedRating
            : candidate.Rating;
        var ratingCount = int.TryParse(votesText, out var parsedVotes) ? parsedVotes : candidate.RatingCount;

        return new DoubanBookMetadata(
            FirstNotBlank(jsonTitle, ReadMeta(html, "og:title"), candidate.Title),
            authors.Count > 0 ? string.Join(" / ", authors.Distinct(StringComparer.OrdinalIgnoreCase)) : ReadInfoValue(info, "作者") ?? string.Empty,
            ReadInfoValue(info, "译者"),
            ReadInfoValue(info, "出版社"),
            ReadInfoValue(info, "出版年"),
            FirstNotBlank(jsonIsbn, ReadMeta(html, "book:isbn"), ReadInfoValue(info, "ISBN")),
            ReadInfoValue(info, "页数"),
            ReadInfoValue(info, "装帧"),
            ReadInfoValue(info, "定价"),
            ReadInfoValue(info, "原作名"),
            ReadInfoValue(info, "丛书"),
            ReadIntroduction(html) ?? ReadMeta(html, "og:description"),
            FirstNotBlank(ReadMeta(html, "og:image"), candidate.CoverUrl),
            FirstNotBlank(ReadMeta(html, "og:url"), candidate.Url),
            rating,
            ratingCount);
    }

    public async Task<byte[]> DownloadCoverAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("豆瓣封面地址无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://book.douban.com/");
        // Cover resources are loaded in parallel by a normal browser as well;
        // keep metadata requests rate-limited while allowing the small card
        // images to appear together.
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("豆瓣未返回有效的封面图片。");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("豆瓣封面文件为空或过大。");
        return bytes;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        EnsureSuccess(response);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastRequest + _minimumInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("豆瓣暂时限制了访问，请稍后再试；应用不会进行批量抓取。");
        throw new HttpRequestException($"豆瓣请求失败（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
    }

    private static string? ReadMeta(string html, string property)
    {
        var escaped = Regex.Escape(property);
        var first = Regex.Match(html, $"""<meta[^>]*(?:property|name)=["']{escaped}["'][^>]*content=["'](?<value>.*?)["'][^>]*>""", HtmlOptions);
        if (first.Success) return CleanText(first.Groups["value"].Value);
        var reversed = Regex.Match(html, $"""<meta[^>]*content=["'](?<value>.*?)["'][^>]*(?:property|name)=["']{escaped}["'][^>]*>""", HtmlOptions);
        return reversed.Success ? CleanText(reversed.Groups["value"].Value) : null;
    }

    private static string? ReadInfoValue(string infoHtml, string label)
    {
        if (string.IsNullOrWhiteSpace(infoHtml)) return null;
        var match = Regex.Match(
            infoHtml,
            $"""<span[^>]*class=["'][^"']*pl[^"']*["'][^>]*>\s*{Regex.Escape(label)}\s*:?\s*</span>\s*(?<value>.*?)(?:<br\s*/?>|$)""",
            HtmlOptions);
        return match.Success ? CleanText(match.Groups["value"].Value) : null;
    }

    private static string? ReadIntroduction(string html)
    {
        var start = html.IndexOf("内容简介", StringComparison.Ordinal);
        if (start < 0) return null;
        var match = Regex.Match(html[start..], """<div[^>]*class=["'][^"']*\bintro\b[^"']*["'][^>]*>(?<value>.*?)</div>""", HtmlOptions);
        return match.Success ? CleanText(match.Groups["value"].Value, preserveParagraphs: true) : null;
    }

    private static string? CleanText(string? value, bool preserveParagraphs = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = preserveParagraphs
            ? Regex.Replace(value, @"</p>\s*<p[^>]*>", "\n\n", HtmlOptions)
            : value;
        text = Regex.Replace(text, @"<br\s*/?>", preserveParagraphs ? "\n" : " ", HtmlOptions);
        text = Regex.Replace(text, @"<[^>]+>", " ", HtmlOptions);
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = preserveParagraphs
            ? Regex.Replace(text, @"[ \t]+", " ").Trim()
            : Regex.Replace(text, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requestGate.Dispose();
        _httpClient.Dispose();
    }
}
