namespace Kkindle.Core;

/// <summary>
/// Configuration for the S3-compatible synchronisation backend. The access
/// key, secret key and optional encryption key are stored through the platform
/// secret protector by <c>S3SyncSettingsStore</c>; this model only describes
/// the values used by the sync service.
/// </summary>
public sealed record S3SyncSettings
{
    public bool Enabled { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 30;
    public string Endpoint { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
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

    public string? Validate()
    {
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
            Bucket = (settings.Bucket ?? string.Empty).Trim(),
            Region = string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region.Trim(),
            TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 10, 600),
            ConcurrentRequests = Math.Clamp(settings.ConcurrentRequests, 1, 32),
            Prefix = string.IsNullOrWhiteSpace(prefix) ? "kkindle" : prefix,
            EncryptionKey = settings.EncryptionKey ?? string.Empty
        };
    }
}

public sealed record S3SyncResult(
    int DeviceCount,
    int BooksAdded,
    int FilesDownloaded,
    int AnnotationsApplied,
    int SettingsApplied,
    bool Changed,
    string? Warning = null);

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
