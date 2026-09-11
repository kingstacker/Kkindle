namespace Kkindle.Core;

public enum SyncProvider
{
    S3 = 0,
    WebDav = 1
}

/// <summary>
/// Sync configuration. The historical type and settings file names are kept
/// for compatibility. Provider credentials are independent and are protected
/// by <c>S3SyncSettingsStore</c>; scheduling and encryption settings are shared.
/// </summary>
public sealed record S3SyncSettings
{
    public SyncProvider Provider { get; init; } = SyncProvider.S3;
    public bool Enabled { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 30;
    public string Endpoint { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string WebDavEndpoint { get; init; } = string.Empty;
    public string WebDavUsername { get; init; } = string.Empty;
    public string WebDavPassword { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Region { get; init; } = "us-east-1";
    // AWS S3 uses virtual-hosted-style addressing by default. S3-compatible
    // services that require path-style can still opt in from the settings UI.
    public bool PathStyle { get; init; }
    public bool SkipTlsVerify { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public int ConcurrentRequests { get; init; } = 4;
    public string Prefix { get; init; } = "kkindle";
    public string EncryptionKey { get; init; } = string.Empty;

    public bool IsConfigured => Validate() is null;
    public string ProviderName => Provider == SyncProvider.WebDav ? "WebDAV" : "S3";

    public string? Validate()
    {
        if (Provider is not (SyncProvider.S3 or SyncProvider.WebDav))
            return "请选择受支持的同步方式。";
        if (Provider == SyncProvider.WebDav)
        {
            if (!Uri.TryCreate(WebDavEndpoint, UriKind.Absolute, out var webDav)
                || webDav.Scheme is not ("http" or "https") || webDav.Host.Length == 0
                || WebDavEndpoint.Contains('\\') || WebDavEndpoint.Any(char.IsControl))
                return "WebDAV 服务地址必须是 HTTP 或 HTTPS 地址。";
            if (webDav.UserInfo.Length > 0 || webDav.Query.Length > 0 || webDav.Fragment.Length > 0)
                return "WebDAV 服务地址不能包含账号密码、查询参数或片段，请在下方填写凭据。";
            if (WebDavUsername.Contains(':') || WebDavUsername.Any(char.IsControl))
                return "WebDAV 用户名不能包含冒号或控制字符。";
            if (WebDavPassword.Length > 0 && string.IsNullOrWhiteSpace(WebDavUsername))
                return "请输入 WebDAV 用户名。";
            if (Prefix.Any(char.IsControl)) return "同步子目录不能包含控制字符。";
            return string.IsNullOrWhiteSpace(Prefix) ? "请输入同步目录前缀。" : null;
        }
        if (string.IsNullOrWhiteSpace(AccessKey)) return "请输入 S3 Access Key。";
        if (string.IsNullOrWhiteSpace(SecretKey)) return "请输入 S3 Secret Key。";
        if (string.IsNullOrWhiteSpace(Bucket)) return "请输入 S3 Bucket。";
        if (!string.IsNullOrWhiteSpace(Endpoint)
            && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https")))
            return "S3 Endpoint 必须是 HTTP 或 HTTPS 地址。";
        if (string.IsNullOrWhiteSpace(Region)) return "请输入 S3 Region。";
        if (string.IsNullOrWhiteSpace(Prefix)) return "请输入同步目录前缀。";
        return null;
    }

    public static S3SyncSettings Normalize(S3SyncSettings? settings)
    {
        settings ??= new S3SyncSettings();
        var endpoint = (settings.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Equals("https://s3.amazonaws.com", StringComparison.OrdinalIgnoreCase))
            endpoint = string.Empty;

        var prefix = string.Join(
            "/",
            (settings.Prefix ?? string.Empty)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part is not "." and not ".."));

        return settings with
        {
            IntervalMinutes = Math.Clamp(settings.IntervalMinutes, 5, 24 * 60),
            Endpoint = endpoint,
            AccessKey = (settings.AccessKey ?? string.Empty).Trim(),
            SecretKey = settings.SecretKey ?? string.Empty,
            WebDavEndpoint = NormalizeWebDavEndpoint(settings.WebDavEndpoint),
            WebDavUsername = (settings.WebDavUsername ?? string.Empty).Trim(),
            WebDavPassword = settings.WebDavPassword ?? string.Empty,
            Bucket = (settings.Bucket ?? string.Empty).Trim(),
            Region = string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region.Trim(),
            TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 10, 600),
            ConcurrentRequests = Math.Clamp(settings.ConcurrentRequests, 1, 32),
            Prefix = string.IsNullOrWhiteSpace(prefix) ? "kkindle" : prefix,
            EncryptionKey = settings.EncryptionKey ?? string.Empty
        };
    }

    private static string NormalizeWebDavEndpoint(string? value)
    {
        var endpoint = (value ?? string.Empty).Trim().TrimEnd('/');
        // Preserve invalid input for validation instead of silently changing
        // a malformed path or embedded credential into another destination.
        return !endpoint.Contains('\\') && !endpoint.Any(char.IsControl) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri.TrimEnd('/') : endpoint;
    }
}

public sealed record S3SyncResult(
    int DeviceCount,
    int BooksAdded,
    int FilesDownloaded,
    int AnnotationsApplied,
    int SettingsApplied,
    bool Changed,
    string? Warning = null)
{
    public bool IsPartial { get; init; }
}

public sealed record S3SyncOptions
{
    public string? ConfirmedDeletionFingerprint { get; init; }
}

public sealed class S3SyncDeletionConfirmationRequiredException(
    string message,
    string deletionFingerprint) : InvalidOperationException(message)
{
    public string DeletionFingerprint { get; } = deletionFingerprint;
}

/// <summary>
/// Portable S3 connection profile. Unlike the regular application backup,
/// this deliberately includes the credentials needed to avoid re-entering a
/// connection on another machine. It excludes the local sync encryption key
/// and feature switches.
/// </summary>
public sealed record S3ConnectionProfile
{
    public const string Format = "KkindleS3ConnectionProfile";
    public const int CurrentVersion = 1;

    public string ProfileFormat { get; init; } = Format;
    public int Version { get; init; } = CurrentVersion;
    public string Endpoint { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Region { get; init; } = "us-east-1";
    public string Prefix { get; init; } = "kkindle";
    public bool PathStyle { get; init; }
    public bool SkipTlsVerify { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public int ConcurrentRequests { get; init; } = 4;

    public static S3ConnectionProfile FromSettings(S3SyncSettings settings)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        return new S3ConnectionProfile
        {
            Endpoint = normalized.Endpoint,
            AccessKey = normalized.AccessKey,
            SecretKey = normalized.SecretKey,
            Bucket = normalized.Bucket,
            Region = normalized.Region,
            Prefix = normalized.Prefix,
            PathStyle = normalized.PathStyle,
            SkipTlsVerify = normalized.SkipTlsVerify,
            TimeoutSeconds = normalized.TimeoutSeconds,
            ConcurrentRequests = normalized.ConcurrentRequests
        };
    }

    public S3SyncSettings ApplyTo(S3SyncSettings current) => S3SyncSettings.Normalize(current with
    {
        Provider = SyncProvider.S3,
        Endpoint = Endpoint,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        Bucket = Bucket,
        Region = Region,
        Prefix = Prefix,
        PathStyle = PathStyle,
        SkipTlsVerify = SkipTlsVerify,
        TimeoutSeconds = TimeoutSeconds,
        ConcurrentRequests = ConcurrentRequests
    });
}
