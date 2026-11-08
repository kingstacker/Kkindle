using System.Text.Json.Serialization;

namespace Kkindle.Core;

public enum AccountRegion { China = 0, International = 1 }

public static class AccountRegions
{
    public static string Slug(AccountRegion region) => region switch
    { AccountRegion.China => "china", AccountRegion.International => "international", _ => throw new ArgumentOutOfRangeException(nameof(region)) };
    public static string DefaultServerUrl(AccountRegion region) => region switch
    { AccountRegion.China => "http://127.0.0.1:5088", AccountRegion.International => "http://127.0.0.1:5089", _ => throw new ArgumentOutOfRangeException(nameof(region)) };
    public static string DisplayName(AccountRegion region) => region switch
    { AccountRegion.China => "中国区", AccountRegion.International => "国际区", _ => throw new ArgumentOutOfRangeException(nameof(region)) };
}

public sealed record AccountUser(Guid Id, string Username);
public sealed record AccountSessionInfo(AccountUser User, DateTimeOffset ExpiresAt, AccountRegion Region = AccountRegion.China);
public sealed record AccountStatus(string ServerUrl, AccountUser? User, DateTimeOffset? ExpiresAt,
    AccountRegion Region = AccountRegion.China, bool RequiresDataRestart = false)
{
    public bool IsLoggedIn => User is not null;
}

// Secret-bearing DTOs deliberately use classes, whose ToString does not print fields.
public sealed class AccountCredentials
{
    public AccountRegion Region { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

public sealed class AccountSessionResponse
{
    public AccountRegion Region { get; init; }
    public AccountUser User { get; init; } = new(Guid.Empty, "");
    public string Token { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record AccountApiError(string Message);
public sealed record AccountSyncObjectMetadata(string Key, DateTimeOffset LastModified);

public static class AccountValidation
{
    public static bool IsUsername(string? value) => value is { Length: >= 3 and <= 32 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    public static bool IsPassword(string? value) => value is { Length: >= 8 and <= 128 }
        && !string.IsNullOrWhiteSpace(value);

    public static Uri ServerUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || !Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入有效的账号服务地址。", nameof(value));
        // Plain HTTP is limited to local verification, including Android's host alias.
        if (uri.Scheme == "http" && !uri.IsLoopback && uri.Host != "10.0.2.2")
            throw new ArgumentException("账号服务须使用 HTTPS；本机验证可使用 localhost 或 127.0.0.1。", nameof(value));
        return uri;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AccountCredentials))]
[JsonSerializable(typeof(AccountSessionResponse))]
[JsonSerializable(typeof(AccountSessionInfo))]
[JsonSerializable(typeof(AccountStatus))]
[JsonSerializable(typeof(AccountApiError))]
[JsonSerializable(typeof(AccountSyncObjectMetadata[]))]
public partial class AccountJsonContext : JsonSerializerContext;
