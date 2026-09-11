using System.Net;

namespace Kkindle.Infrastructure;

internal static class TranslationProxy
{
    public static string NormalizeAddress(string? address)
    {
        var value = (address ?? string.Empty).Trim().Trim('"');
        if (value.Length == 0) return string.Empty;

        if (!value.Contains("://", StringComparison.Ordinal))
            value = $"http://{value}";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException(
                "Google 翻译代理地址无效，请填写 http://127.0.0.1:7890 这样的 HTTP/HTTPS 地址。",
                nameof(address));
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static HttpClientHandler CreateHandler(string normalizedAddress)
    {
        var proxy = new WebProxy(new Uri(normalizedAddress, UriKind.Absolute))
        {
            BypassProxyOnLocal = false
        };
        return new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            Proxy = proxy,
            UseProxy = true
        };
    }
}
