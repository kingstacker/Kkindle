using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Exports only the selected provider's credentials and imports either format,
/// including existing .kkindle-s3.json files. Encryption keys never travel here.
/// </summary>
public static class SyncConnectionProfileService
{
    public const string WebDavFileExtension = ".kkindle-webdav.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string FileExtension(SyncProvider provider) => provider == SyncProvider.WebDav
        ? WebDavFileExtension : S3ConnectionProfileService.FileExtension;

    public static async Task ExportAsync(string destinationPath, S3SyncSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Provider == SyncProvider.S3)
        {
            await S3ConnectionProfileService.ExportAsync(destinationPath, settings, cancellationToken);
            return;
        }
        if (settings.Provider != SyncProvider.WebDav)
            throw new InvalidOperationException(UiText.Get("请选择受支持的同步方式。"));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException(UiText.Get("请选择同步配置文件的保存位置。"), nameof(destinationPath));
        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, WebDavConnectionProfile.FromSettings(settings), JsonOptions, cancellationToken);
            File.Move(temporary, destinationPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<S3SyncSettings> ImportAsync(string sourcePath, S3SyncSettings current, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException(UiText.Get("请选择要导入的同步配置文件。"), nameof(sourcePath));
        await using var stream = new FileStream(Path.GetFullPath(sourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length > 1024 * 1024) throw UnsupportedProfile();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var header = document.RootElement.Deserialize<ProfileHeader>(JsonOptions);
        return header switch
        {
            { ProfileFormat: S3ConnectionProfile.Format, Version: S3ConnectionProfile.CurrentVersion } =>
                document.RootElement.Deserialize<S3ConnectionProfile>(JsonOptions)!.ApplyTo(current),
            { ProfileFormat: WebDavConnectionProfile.Format, Version: WebDavConnectionProfile.CurrentVersion } =>
                document.RootElement.Deserialize<WebDavConnectionProfile>(JsonOptions)!.ApplyTo(current),
            _ => throw UnsupportedProfile()
        };
    }

    private static InvalidDataException UnsupportedProfile() => new(UiText.Get("这不是受支持的 Kkindle 同步配置文件。"));

    private sealed class ProfileHeader
    {
        public string? ProfileFormat { get; set; }
        public int Version { get; set; }
    }
}
