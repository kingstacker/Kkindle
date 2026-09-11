namespace Kkindle.Core;

/// <summary>A portable WebDAV connection, without local scheduling or encryption secrets.</summary>
public sealed record WebDavConnectionProfile
{
    public const string Format = "KkindleWebDavConnectionProfile";
    public const int CurrentVersion = 1;
    public string ProfileFormat { get; init; } = Format;
    public int Version { get; init; } = CurrentVersion;
    public string Endpoint { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Prefix { get; init; } = "kkindle";
    public bool SkipTlsVerify { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public int ConcurrentRequests { get; init; } = 4;

    public static WebDavConnectionProfile FromSettings(S3SyncSettings settings)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        return new WebDavConnectionProfile
        {
            Endpoint = normalized.WebDavEndpoint,
            Username = normalized.WebDavUsername,
            Password = normalized.WebDavPassword,
            Prefix = normalized.Prefix,
            SkipTlsVerify = normalized.SkipTlsVerify,
            TimeoutSeconds = normalized.TimeoutSeconds,
            ConcurrentRequests = normalized.ConcurrentRequests
        };
    }

    public S3SyncSettings ApplyTo(S3SyncSettings current) => S3SyncSettings.Normalize(current with
    {
        Provider = SyncProvider.WebDav,
        WebDavEndpoint = Endpoint,
        WebDavUsername = Username,
        WebDavPassword = Password,
        Prefix = Prefix,
        SkipTlsVerify = SkipTlsVerify,
        TimeoutSeconds = TimeoutSeconds,
        ConcurrentRequests = ConcurrentRequests
    });
}
