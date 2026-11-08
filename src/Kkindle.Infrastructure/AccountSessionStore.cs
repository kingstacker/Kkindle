using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal sealed class PersistedAccountSettings
{
    public AccountRegion ActiveRegion { get; init; }
    public List<PersistedRegionalAccount> Connections { get; init; } = [];
}

internal sealed class PersistedRegionalAccount
{
    public AccountRegion Region { get; init; }
    public string ServerUrl { get; init; } = "";
    public string ProtectedSession { get; init; } = "";
}

internal sealed class RegionalAccountConnection(string serverUrl, AccountSessionResponse? session)
{
    public string ServerUrl { get; } = serverUrl;
    public AccountSessionResponse? Session { get; } = session;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PersistedAccountSettings))]
internal partial class AccountStorageJsonContext : JsonSerializerContext;

/// <summary>Account sessions stay on this device and are never part of sync or backups.</summary>
internal sealed class AccountSessionStore(AppPaths paths, ISecretProtector protector)
{
    public string SettingsPath => Path.Combine(paths.Data, "account-session.json");

    public async Task<(AccountRegion ActiveRegion, Dictionary<AccountRegion, RegionalAccountConnection> Connections)> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(SettingsPath)) return (AccountRegion.China, []);
        PersistedAccountSettings? settings;
        try
        {
            await using var input = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, true);
            settings = await JsonSerializer.DeserializeAsync(input, AccountStorageJsonContext.Default.PersistedAccountSettings, token);
        }
        catch (JsonException) { return (AccountRegion.China, []); }
        if (settings is null) return (AccountRegion.China, []);
        var connections = new Dictionary<AccountRegion, RegionalAccountConnection>();
        foreach (var item in settings.Connections ?? [])
        {
            if (!Enum.IsDefined(item.Region)) continue;
            string server;
            try { server = AccountValidation.ServerUri(item.ServerUrl).AbsoluteUri.TrimEnd('/'); }
            catch (ArgumentException) { continue; }
            AccountSessionResponse? session = null;
            byte[]? clear = null;
            try
            {
                if (!string.IsNullOrEmpty(item.ProtectedSession))
                {
                    clear = protector.Unprotect(Convert.FromBase64String(item.ProtectedSession));
                    session = JsonSerializer.Deserialize(clear, AccountJsonContext.Default.AccountSessionResponse);
                    if (session?.Region != item.Region) session = null;
                }
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException) { }
            finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
            connections[item.Region] = new(server, session);
        }
        return (Enum.IsDefined(settings.ActiveRegion) ? settings.ActiveRegion : AccountRegion.China, connections);
    }

    public async Task SaveAsync(AccountRegion activeRegion, IReadOnlyDictionary<AccountRegion, RegionalAccountConnection> connections, CancellationToken token)
    {
        paths.EnsureDirectories();
        using var lease = await SettingsWriteLock.AcquireAsync(paths, token);
        var values = new List<PersistedRegionalAccount>();
        foreach (var (region, connection) in connections)
        {
            var encrypted = "";
            if (connection.Session is { } session)
            {
                var clear = JsonSerializer.SerializeToUtf8Bytes(session, AccountJsonContext.Default.AccountSessionResponse);
                try { encrypted = Convert.ToBase64String(protector.Protect(clear)); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            values.Add(new() { Region = region, ServerUrl = connection.ServerUrl, ProtectedSession = encrypted });
        }
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = new PersistedAccountSettings { ActiveRegion = activeRegion, Connections = values };
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(output, settings, AccountStorageJsonContext.Default.PersistedAccountSettings, token);
            SettingsFile.Publish(temporary, SettingsPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
