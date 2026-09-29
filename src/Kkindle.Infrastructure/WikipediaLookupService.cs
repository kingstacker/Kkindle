using System.Text.Json;

namespace Kkindle.Infrastructure;

public sealed record WikipediaArticle(string Title, string Extract, Uri Url, string Language);

/// <summary>
/// Looks up a short reading selection as a Wikipedia article and returns its
/// lead extract with a direct source link for attribution.
/// </summary>
public sealed class WikipediaLookupService
{
    private const int MaximumQueryLength = 120;
    private const int ExtractCharacterLimit = 420;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    public async Task<WikipediaArticle?> LookupAsync(
        string term,
        string preferredLanguage,
        CancellationToken cancellationToken = default)
    {
        var query = NormalizeQuery(term);
        if (query.Length == 0 || query.Length > MaximumQueryLength) return null;

        var primaryLanguage = preferredLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh"
            : "en";
        var fallbackLanguage = primaryLanguage == "zh" ? "en" : "zh";

        var article = await LookupInLanguageAsync(query, primaryLanguage, cancellationToken)
            .ConfigureAwait(false);
        if (article is not null) return article;

        return await LookupInLanguageAsync(query, fallbackLanguage, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<WikipediaArticle?> LookupInLanguageAsync(
        string query,
        string language,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["format"] = "json",
            ["formatversion"] = "2",
            ["titles"] = query,
            ["redirects"] = "1",
            ["prop"] = "extracts|info",
            ["exintro"] = "1",
            ["explaintext"] = "1",
            ["exchars"] = ExtractCharacterLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["inprop"] = "url"
        };
        if (language == "zh")
            parameters["variant"] = "zh-cn";

        var queryString = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var requestUri = new Uri($"https://{language}.wikipedia.org/w/api.php?{queryString}");

        using var response = await SharedHttpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("query", out var queryElement)
            || !queryElement.TryGetProperty("pages", out var pagesElement)
            || pagesElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var page in pagesElement.EnumerateArray())
        {
            if (page.TryGetProperty("missing", out _))
                continue;
            if (page.TryGetProperty("ns", out var namespaceElement)
                && namespaceElement.TryGetInt32(out var pageNamespace)
                && pageNamespace != 0)
                continue;

            var title = ReadString(page, "title");
            var extract = ReadString(page, "extract");
            var pageUrl = ReadString(page, "fullurl");
            if (title.Length == 0 || extract.Length == 0
                || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !uri.Host.Equals($"{language}.wikipedia.org", StringComparison.OrdinalIgnoreCase))
                continue;

            return new WikipediaArticle(title, extract, uri, language);
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var version = typeof(WikipediaLookupService).Assembly.GetName().Version?.ToString() ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Kkindle/{version} (+https://github.com/kingstacker/Kkindle)");
        return client;
    }

    private static string NormalizeQuery(string? term) => string.Join(
            ' ',
            (term ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Trim(' ', '.', ',', ';', ':', '!', '?', '…', '"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}');

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

}
