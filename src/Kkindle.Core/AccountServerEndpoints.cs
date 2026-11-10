namespace Kkindle.Core;

/// <summary>
/// Shared deployment defaults for every client frontend. Change only the two
/// DefaultServerUrl constants for a release; process environment overrides are
/// useful for local verification without rebuilding. No secrets belong here.
/// </summary>
public static class AccountServerEndpoints
{
    public const string ChinaDefaultServerUrl = "http://127.0.0.1:5088";
    public const string InternationalDefaultServerUrl = "http://127.0.0.1:5089";

    public const string ChinaEnvironmentVariable = "KKINDLE_ACCOUNT_SERVER_CHINA";
    public const string InternationalEnvironmentVariable = "KKINDLE_ACCOUNT_SERVER_INTERNATIONAL";

    public static string DefaultServerUrl(AccountRegion region) =>
        Resolve(region, Environment.GetEnvironmentVariable(EnvironmentVariable(region)));

    /// <summary>Pure resolver, also used to test overrides without changing process-wide environment.</summary>
    public static string Resolve(AccountRegion region, string? environmentOverride)
    {
        var variable = EnvironmentVariable(region);
        var configured = region == AccountRegion.China ? ChinaDefaultServerUrl : InternationalDefaultServerUrl;
        // Only a missing override uses the compiled default. An empty or invalid
        // supplied value must not silently connect to a different server.
        try { return AccountValidation.ServerUri(environmentOverride ?? configured).AbsoluteUri.TrimEnd('/'); }
        catch (ArgumentException)
        {
            // Do not echo a malformed value, which could accidentally contain credentials.
            throw new InvalidOperationException($"账号服务器地址配置无效，请检查 {variable} 或 AccountServerEndpoints 中的对应默认值。公网地址必须使用 HTTPS，且不能含凭据、查询参数或片段。");
        }
    }

    public static string EnvironmentVariable(AccountRegion region) => region switch
    {
        AccountRegion.China => ChinaEnvironmentVariable,
        AccountRegion.International => InternationalEnvironmentVariable,
        _ => throw new ArgumentOutOfRangeException(nameof(region))
    };

    // These are protocol-fixture listening addresses, not deployment defaults.
    // Keep them fixed when changing the client defaults to real HTTPS domains.
    public static string LocalDevelopmentServerUrl(AccountRegion region) => region switch
    {
        AccountRegion.China => "http://127.0.0.1:5088",
        AccountRegion.International => "http://127.0.0.1:5089",
        _ => throw new ArgumentOutOfRangeException(nameof(region))
    };

    /// <summary>Recognises only the old, unmarked local defaults; custom HTTPS endpoints stay pinned.</summary>
    public static bool IsLegacyDevelopmentDefault(AccountRegion region, string serverUrl)
    {
        var local = new Uri(LocalDevelopmentServerUrl(region));
        var candidate = AccountValidation.ServerUri(serverUrl);
        return candidate.Scheme == "http" && (candidate.IsLoopback || candidate.Host == "10.0.2.2")
            && candidate.Port == local.Port && candidate.AbsolutePath == "/";
    }
}
