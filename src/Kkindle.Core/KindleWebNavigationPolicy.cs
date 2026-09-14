namespace Kkindle.Core;

/// <summary>Navigation boundary for the official Send to Kindle window.</summary>
public static class KindleWebNavigationPolicy
{
    public static Uri HomeUri { get; } = new("https://www.amazon.com/sendtokindle");
    // Public sign-in link used by Send to Kindle itself, retaining its return destination.
    public static Uri SignInUri { get; } = new("https://www.amazon.com/sendtokindle/dnd/signin");

    private static readonly string[] AmazonDomains =
    [
        "amazon.com", "amazon.co.uk", "amazon.co.jp", "amazon.de", "amazon.fr",
        "amazon.it", "amazon.es", "amazon.ca", "amazon.com.au", "amazon.in",
        "amazon.com.br", "amazon.com.mx", "amazon.nl"
    ];

    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true, IsDefaultPort: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && AmazonDomains.Any(domain =>
            uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    // Automation is narrower than the login navigation boundary.
    public static bool IsUploadPage(Uri? uri) => IsAllowed(uri)
        && uri!.Host.Equals(HomeUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.TrimEnd('/').Equals("/sendtokindle", StringComparison.OrdinalIgnoreCase);

    // Credential automation has its own boundary. Other Amazon pages can still
    // be displayed, but registration, password resets and regional sites are manual.
    public static bool IsAuthenticationPage(Uri? uri) => IsAllowed(uri)
        && uri!.Host.Equals(HomeUri.Host, StringComparison.OrdinalIgnoreCase)
        && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
            @"^/(?:-/[a-z]{2}(?:-[a-z]{2})?/)?(?:ap/(?:signin|mfa)/?|ap/cvf(?:/.*)?|ax/claim/?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
