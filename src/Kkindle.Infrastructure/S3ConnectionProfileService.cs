using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Reads and writes the standalone S3 connection profile used by the settings
/// UI. The file is intentionally portable JSON so it can be moved between
/// machines, but it contains the S3 credentials and must be protected like a
/// password file.
/// </summary>
public static class S3ConnectionProfileService
{
    public const string FileExtension = ".kkindle-s3.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task ExportAsync(
        string destinationPath,
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("请选择 S3 配置文件的保存位置。", nameof(destinationPath));

        destinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = destinationPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    S3ConnectionProfile.FromSettings(settings),
                    JsonOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public static async Task<S3SyncSettings> ImportAsync(
        string sourcePath,
        S3SyncSettings current,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("请选择要导入的 S3 配置文件。", nameof(sourcePath));

        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("S3 配置文件不存在。", sourcePath);

        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        var profile = await JsonSerializer.DeserializeAsync<S3ConnectionProfile>(
            stream,
            JsonOptions,
            cancellationToken);
        if (profile is null
            || !string.Equals(profile.ProfileFormat, S3ConnectionProfile.Format, StringComparison.Ordinal)
            || profile.Version != S3ConnectionProfile.CurrentVersion)
        {
            throw new InvalidDataException("这不是受支持的 Kkindle S3 配置文件。 ");
        }

        return profile.ApplyTo(current);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup must not hide the export result.
        }
    }
}
