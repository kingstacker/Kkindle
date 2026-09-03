using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Persists S3 connection metadata as JSON while keeping credentials and the
/// optional client-side encryption key protected in the platform secret store.
/// The device id is intentionally stable and is used as the owner's snapshot
/// prefix in the shared bucket.
/// </summary>
public sealed class S3SyncSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;

    public S3SyncSettingsStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    public string SettingsPath => Path.Combine(_paths.Data, "s3-sync-settings.json");

    public async Task<S3SyncStoredSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath))
        {
            var initial = new S3SyncStoredSettings(Guid.NewGuid().ToString("N"), new S3SyncSettings());
            await SaveAsync(initial.DeviceId, initial.Settings, cancellationToken);
            return initial;
        }

        try
        {
            await using var stream = new FileStream(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedS3SyncSettings>(
                stream,
                JsonOptions,
                cancellationToken);
            if (persisted is null)
                return new S3SyncStoredSettings(Guid.NewGuid().ToString("N"), new S3SyncSettings());

            var deviceId = NormalizeDeviceId(persisted.DeviceId);
            var settings = S3SyncSettings.Normalize(new S3SyncSettings
            {
                Enabled = persisted.Enabled,
                AutomaticSyncEnabled = persisted.AutomaticSyncEnabled,
                IntervalMinutes = persisted.IntervalMinutes,
                Endpoint = persisted.Endpoint ?? string.Empty,
                AccessKey = Unprotect(persisted.ProtectedAccessKey),
                SecretKey = Unprotect(persisted.ProtectedSecretKey),
                Bucket = persisted.Bucket ?? string.Empty,
                Region = persisted.Region ?? "us-east-1",
                PathStyle = persisted.PathStyle,
                SkipTlsVerify = persisted.SkipTlsVerify,
                TimeoutSeconds = persisted.TimeoutSeconds,
                ConcurrentRequests = persisted.ConcurrentRequests,
                Prefix = persisted.Prefix ?? "kkindle",
                EncryptionKey = Unprotect(persisted.ProtectedEncryptionKey)
            });

            if (!string.Equals(deviceId, persisted.DeviceId, StringComparison.Ordinal))
                await SaveAsync(deviceId, settings, cancellationToken);
            return new S3SyncStoredSettings(deviceId, settings);
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or FormatException
            or CryptographicException
            or Win32Exception)
        {
            // A malformed or machine-bound secret must not prevent the app
            // from opening. The user can re-enter the S3 credentials.
            return new S3SyncStoredSettings(Guid.NewGuid().ToString("N"), new S3SyncSettings());
        }
    }

    public async Task SaveAsync(
        string deviceId,
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var normalized = S3SyncSettings.Normalize(settings);
        var persisted = new PersistedS3SyncSettings
        {
            DeviceId = NormalizeDeviceId(deviceId),
            Enabled = normalized.Enabled,
            AutomaticSyncEnabled = normalized.AutomaticSyncEnabled,
            IntervalMinutes = normalized.IntervalMinutes,
            Endpoint = normalized.Endpoint,
            ProtectedAccessKey = Protect(normalized.AccessKey),
            ProtectedSecretKey = Protect(normalized.SecretKey),
            Bucket = normalized.Bucket,
            Region = normalized.Region,
            PathStyle = normalized.PathStyle,
            SkipTlsVerify = normalized.SkipTlsVerify,
            TimeoutSeconds = normalized.TimeoutSeconds,
            ConcurrentRequests = normalized.ConcurrentRequests,
            Prefix = normalized.Prefix,
            ProtectedEncryptionKey = Protect(normalized.EncryptionKey)
        };

        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(value)));
    }

    private string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(value)));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or Win32Exception)
        {
            return string.Empty;
        }
    }

    private static string NormalizeDeviceId(string? deviceId) =>
        Guid.TryParseExact(deviceId, "N", out var parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");

    private sealed class PersistedS3SyncSettings
    {
        public string? DeviceId { get; set; }
        public bool Enabled { get; set; }
        public bool AutomaticSyncEnabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 30;
        public string? Endpoint { get; set; }
        public string? ProtectedAccessKey { get; set; }
        public string? ProtectedSecretKey { get; set; }
        public string? Bucket { get; set; }
        public string? Region { get; set; }
        public bool PathStyle { get; set; }
        public bool SkipTlsVerify { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        public int ConcurrentRequests { get; set; } = 4;
        public string? Prefix { get; set; }
        public string? ProtectedEncryptionKey { get; set; }
    }
}

public sealed record S3SyncStoredSettings(string DeviceId, S3SyncSettings Settings);
