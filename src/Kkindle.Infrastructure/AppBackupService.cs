using System.IO.Compression;
using System.Text.Json;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>
/// Creates and restores a portable Kkindle backup package.
///
/// The package deliberately contains only non-secret settings. API keys, S3
/// credentials, the S3 client-side encryption key and SMTP passwords stay
/// protected by Windows on the current machine and are never written to an
/// export file.
/// </summary>
public sealed class AppBackupService
{
    public const string FileExtension = ".kkindle";

    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "database/kkindle.db";
    private const string DatabaseWalEntryName = "database/kkindle.db-wal";
    private const string DatabaseShmEntryName = "database/kkindle.db-shm";
    private const string SettingsEntryName = "settings/settings.json";
    private const int CurrentFormatVersion = 1;
    private const string BackupFormat = "KkindleBackup";
    private const string AiSettingsPath = "ai-settings.json";
    private const string KindleEmailSettingsPath = "kindle-email-settings.json";
    private const string S3SyncSettingsPath = "s3-sync-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppPaths _paths;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly S3SyncSettingsStore _s3SyncSettingsStore;

    public AppBackupService(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _aiSettingsStore = new AiSettingsStore(paths, protector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, protector);
        _s3SyncSettingsStore = new S3SyncSettingsStore(paths, protector);
    }

    public async Task<AppBackupExportResult> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("请选择备份文件的保存位置。", nameof(destinationPath));

        _paths.EnsureDirectories();
        destinationPath = Path.GetFullPath(destinationPath);
        EnsureBackupOutsideDataDirectory(destinationPath);

        var stagingRoot = CreateWorkingDirectory(".kkindle-export-");
        var databaseSnapshotPath = Path.Combine(stagingRoot, "kkindle.db");
        try
        {
            var summary = await ReadLibrarySummaryAsync(_paths.Database, cancellationToken);
            await CreateDatabaseSnapshotAsync(databaseSnapshotPath, cancellationToken);
            var settings = await BuildExportSettingsAsync(cancellationToken);
            var manifest = new BackupManifest
            {
                Format = BackupFormat,
                Version = CurrentFormatVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                BookCount = summary.BookCount,
                FileCount = summary.FileCount
            };

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using (var output = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await AddJsonEntryAsync(archive, ManifestEntryName, manifest, cancellationToken);
                await AddFileEntryAsync(archive, databaseSnapshotPath, DatabaseEntryName, cancellationToken);
                await AddOptionalFileEntryAsync(archive, databaseSnapshotPath + "-wal", DatabaseWalEntryName, cancellationToken);
                await AddOptionalFileEntryAsync(archive, databaseSnapshotPath + "-shm", DatabaseShmEntryName, cancellationToken);
                await AddDirectoryEntriesAsync(archive, _paths.Library, "library", cancellationToken);
                await AddDirectoryEntriesAsync(archive, _paths.Covers, "covers", cancellationToken);
                await AddDirectoryEntriesAsync(archive, _paths.Trash, "trash", cancellationToken);
                await AddJsonEntryAsync(archive, SettingsEntryName, settings, cancellationToken);
            }

            return new AppBackupExportResult(
                manifest.BookCount,
                manifest.FileCount,
                new FileInfo(destinationPath).Length);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task<AppBackupImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("请选择要导入的备份文件。", nameof(sourcePath));

        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("备份文件不存在。", sourcePath);

        _paths.EnsureDirectories();
        var stagingRoot = CreateWorkingDirectory(".kkindle-import-");
        try
        {
            BackupManifest manifest;
            BackupSettings? backupSettings;
            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                manifest = await ReadManifestAsync(archive, cancellationToken);
                await ExtractSupportedEntriesAsync(archive, stagingRoot, cancellationToken);
            }

            ValidateManifest(manifest);
            var stagedDatabasePath = Path.Combine(stagingRoot, DatabaseEntryName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stagedDatabasePath))
                throw new InvalidDataException("备份包缺少书库数据库。 ");

            var summary = await ValidateDatabaseAsync(stagedDatabasePath, cancellationToken);
            backupSettings = await ReadBackupSettingsAsync(
                Path.Combine(stagingRoot, SettingsEntryName.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken);

            var currentAiSettings = await _aiSettingsStore.LoadAsync(cancellationToken);
            var currentKindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
            var currentS3Settings = await _s3SyncSettingsStore.LoadAsync(cancellationToken);
            var importedAiSettings = BuildImportedAiSettings(backupSettings?.Ai, currentAiSettings);
            var importedKindleEmailSettings = BuildImportedKindleEmailSettings(
                backupSettings?.KindleEmail,
                currentKindleEmailSettings);
            var importedS3Settings = BuildImportedS3Settings(backupSettings?.S3, currentS3Settings);

            var stagedLibraryPath = Path.Combine(stagingRoot, "library");
            var stagedCoversPath = Path.Combine(stagingRoot, "covers");
            var stagedTrashPath = Path.Combine(stagingRoot, "trash");
            Directory.CreateDirectory(stagedLibraryPath);
            Directory.CreateDirectory(stagedCoversPath);
            Directory.CreateDirectory(stagedTrashPath);

            var rollbackRoot = CreateWorkingDirectory(".kkindle-rollback-");
            var currentDataMoved = false;
            try
            {
                await CreateDatabaseSnapshotAsync(
                    Path.Combine(rollbackRoot, "kkindle.db"),
                    cancellationToken);
                MoveCurrentDataToRollback(rollbackRoot);
                currentDataMoved = true;
                Directory.Move(stagedLibraryPath, _paths.Library);
                Directory.Move(stagedCoversPath, _paths.Covers);
                Directory.Move(stagedTrashPath, _paths.Trash);
                await ReplaceDatabaseFromSnapshotAsync(stagedDatabasePath, cancellationToken);

                await _aiSettingsStore.SaveAsync(importedAiSettings, cancellationToken);
                await _kindleEmailSettingsStore.SaveAsync(importedKindleEmailSettings, cancellationToken);
                await _s3SyncSettingsStore.SaveAsync(
                    currentS3Settings.DeviceId,
                    importedS3Settings,
                    cancellationToken);

                TryDeleteDirectory(rollbackRoot);
                return new AppBackupImportResult(
                    summary.BookCount,
                    summary.FileCount,
                    importedAiSettings,
                    importedKindleEmailSettings)
                {
                    S3Settings = importedS3Settings
                };
            }
            catch
            {
                if (currentDataMoved)
                {
                    try
                    {
                        await RestoreCurrentDataFromRollbackAsync(rollbackRoot, cancellationToken);
                    }
                    catch
                    {
                        // Preserve the original import exception. The rollback
                        // is best effort because an external SQLite handle may
                        // still be releasing at this point.
                    }
                }
                throw;
            }
            finally
            {
                TryDeleteDirectory(rollbackRoot);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<BackupSettings> BuildExportSettingsAsync(CancellationToken cancellationToken)
    {
        var ai = await _aiSettingsStore.LoadAsync(cancellationToken);
        var kindleEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        var s3 = await _s3SyncSettingsStore.LoadAsync(cancellationToken);
        return new BackupSettings
        {
            Ai = new BackupAiSettings
            {
                Provider = ai.Provider,
                BaseUrl = ai.BaseUrl,
                Model = ai.Model
            },
            KindleEmail = new BackupKindleEmailSettings
            {
                KindleEmailAddress = kindleEmail.KindleEmailAddress,
                SenderEmailAddress = kindleEmail.SenderEmailAddress,
                SmtpHost = kindleEmail.SmtpHost,
                SmtpPort = kindleEmail.SmtpPort,
                SmtpUsername = kindleEmail.SmtpUsername,
                EnableSsl = kindleEmail.EnableSsl
            },
            S3 = new BackupS3Settings
            {
                Enabled = s3.Settings.Enabled,
                AutomaticSyncEnabled = s3.Settings.AutomaticSyncEnabled,
                IntervalMinutes = s3.Settings.IntervalMinutes,
                Endpoint = s3.Settings.Endpoint,
                Bucket = s3.Settings.Bucket,
                Region = s3.Settings.Region,
                PathStyle = s3.Settings.PathStyle,
                SkipTlsVerify = s3.Settings.SkipTlsVerify,
                TimeoutSeconds = s3.Settings.TimeoutSeconds,
                ConcurrentRequests = s3.Settings.ConcurrentRequests,
                Prefix = s3.Settings.Prefix,
                EncryptionEnabled = !string.IsNullOrWhiteSpace(s3.Settings.EncryptionKey)
            }
        };
    }

    private async Task CreateDatabaseSnapshotAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.Database))
            throw new InvalidDataException("书库数据库不存在，无法导出。 ");

        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.Database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        await source.OpenAsync(cancellationToken);
        source.Close();
        File.Copy(_paths.Database, destinationPath, overwrite: true);
        CopyOptionalFile(_paths.Database + "-wal", destinationPath + "-wal");
        CopyOptionalFile(_paths.Database + "-shm", destinationPath + "-shm");
    }

    private async Task ReplaceDatabaseFromSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task<(int BookCount, int FileCount)> ReadLibrarySummaryAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return (0, 0);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Books), (SELECT COUNT(*) FROM BookFiles);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<(int BookCount, int FileCount)> ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var integrityResult = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份包中的书库数据库校验失败。 ");

            return await ReadLibrarySummaryAsync(databasePath, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("备份包中的书库数据库无法读取。 ", exception);
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("这不是有效的 Kkindle 备份包。 ");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("备份包清单为空。 ");
    }

    private static async Task<BackupSettings?> ReadBackupSettingsAsync(
        string settingsPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath)) return null;
        try
        {
            await using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<BackupSettings>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("备份包中的设置无法读取。 ", exception);
        }
    }

    private static AiConnectionSettings BuildImportedAiSettings(
        BackupAiSettings? imported,
        AiConnectionSettings current)
    {
        if (imported is null) return current.Clone();

        var provider = imported.Provider?.Trim().ToLowerInvariant() ?? "deepseek";
        if (provider is not ("deepseek" or "openai" or "custom")) provider = "custom";
        var defaults = AiConnectionSettings.GetDefaults(provider);
        return new AiConnectionSettings
        {
            Provider = provider,
            BaseUrl = string.IsNullOrWhiteSpace(imported.BaseUrl) ? defaults.BaseUrl : imported.BaseUrl.Trim(),
            Model = AiConnectionSettings.NormalizeModel(
                provider,
                string.IsNullOrWhiteSpace(imported.Model) ? defaults.Model : imported.Model),
            ApiKey = current.ApiKey
        };
    }

    private static KindleEmailSettings BuildImportedKindleEmailSettings(
        BackupKindleEmailSettings? imported,
        KindleEmailSettings current)
    {
        if (imported is null) return current.Clone();

        return KindleEmailSettings.Normalize(new KindleEmailSettings
        {
            KindleEmailAddress = imported.KindleEmailAddress ?? string.Empty,
            SenderEmailAddress = imported.SenderEmailAddress ?? string.Empty,
            SmtpHost = imported.SmtpHost ?? string.Empty,
            SmtpPort = imported.SmtpPort,
            SmtpUsername = imported.SmtpUsername ?? string.Empty,
            SmtpPassword = current.SmtpPassword,
            EnableSsl = imported.EnableSsl
        });
    }

    private static S3SyncSettings BuildImportedS3Settings(
        BackupS3Settings? imported,
        S3SyncStoredSettings current)
    {
        if (imported is null) return current.Settings;

        var currentSettings = current.Settings;
        var credentialsReady = !string.IsNullOrWhiteSpace(currentSettings.AccessKey)
            && !string.IsNullOrWhiteSpace(currentSettings.SecretKey);
        var encryptionReady = !imported.EncryptionEnabled
            || !string.IsNullOrWhiteSpace(currentSettings.EncryptionKey);
        return S3SyncSettings.Normalize(currentSettings with
        {
            // A portable package does not contain credentials. Keep an
            // imported endpoint/configuration visible, but avoid enabling an
            // automatic sync that cannot authenticate on a new machine.
            Enabled = imported.Enabled && credentialsReady && encryptionReady,
            AutomaticSyncEnabled = imported.AutomaticSyncEnabled,
            IntervalMinutes = imported.IntervalMinutes,
            Endpoint = imported.Endpoint ?? string.Empty,
            Bucket = imported.Bucket ?? string.Empty,
            Region = imported.Region ?? "us-east-1",
            PathStyle = imported.PathStyle,
            SkipTlsVerify = imported.SkipTlsVerify,
            TimeoutSeconds = imported.TimeoutSeconds,
            ConcurrentRequests = imported.ConcurrentRequests,
            Prefix = imported.Prefix ?? "kkindle"
        });
    }

    private void MoveCurrentDataToRollback(string rollbackRoot)
    {
        try
        {
            MoveIfExists(_paths.Library, Path.Combine(rollbackRoot, "library"));
            MoveIfExists(_paths.Covers, Path.Combine(rollbackRoot, "covers"));
            MoveIfExists(_paths.Trash, Path.Combine(rollbackRoot, "trash"));
            MoveIfExists(Path.Combine(_paths.Data, AiSettingsPath), Path.Combine(rollbackRoot, AiSettingsPath));
            MoveIfExists(Path.Combine(_paths.Data, KindleEmailSettingsPath), Path.Combine(rollbackRoot, KindleEmailSettingsPath));
            MoveIfExists(Path.Combine(_paths.Data, S3SyncSettingsPath), Path.Combine(rollbackRoot, S3SyncSettingsPath));
        }
        catch
        {
            RestoreMovedFilesFromRollback(rollbackRoot);
            throw;
        }
    }

    private void RestoreMovedFilesFromRollback(string rollbackRoot)
    {
        MoveIfExists(Path.Combine(rollbackRoot, "library"), _paths.Library);
        MoveIfExists(Path.Combine(rollbackRoot, "covers"), _paths.Covers);
        MoveIfExists(Path.Combine(rollbackRoot, "trash"), _paths.Trash);
        MoveIfExists(Path.Combine(rollbackRoot, AiSettingsPath), Path.Combine(_paths.Data, AiSettingsPath));
        MoveIfExists(Path.Combine(rollbackRoot, KindleEmailSettingsPath), Path.Combine(_paths.Data, KindleEmailSettingsPath));
        MoveIfExists(Path.Combine(rollbackRoot, S3SyncSettingsPath), Path.Combine(_paths.Data, S3SyncSettingsPath));
    }

    private async Task RestoreCurrentDataFromRollbackAsync(
        string rollbackRoot,
        CancellationToken cancellationToken)
    {
        DeletePath(_paths.Library);
        DeletePath(_paths.Covers);
        DeletePath(_paths.Trash);
        DeletePath(Path.Combine(_paths.Data, AiSettingsPath));
        DeletePath(Path.Combine(_paths.Data, KindleEmailSettingsPath));
        DeletePath(Path.Combine(_paths.Data, S3SyncSettingsPath));

        MoveIfExists(Path.Combine(rollbackRoot, "library"), _paths.Library);
        MoveIfExists(Path.Combine(rollbackRoot, "covers"), _paths.Covers);
        MoveIfExists(Path.Combine(rollbackRoot, "trash"), _paths.Trash);
        MoveIfExists(Path.Combine(rollbackRoot, AiSettingsPath), Path.Combine(_paths.Data, AiSettingsPath));
        MoveIfExists(Path.Combine(rollbackRoot, KindleEmailSettingsPath), Path.Combine(_paths.Data, KindleEmailSettingsPath));
        MoveIfExists(Path.Combine(rollbackRoot, S3SyncSettingsPath), Path.Combine(_paths.Data, S3SyncSettingsPath));
        await ReplaceDatabaseFromSnapshotAsync(
            Path.Combine(rollbackRoot, "kkindle.db"),
            cancellationToken);
    }

    private static async Task AddDirectoryEntriesAsync(
        ZipArchive archive,
        string directory,
        string entryPrefix,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filePath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(directory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            await AddFileEntryAsync(archive, filePath, $"{entryPrefix}/{relativePath}", cancellationToken);
        }
    }

    private static async Task AddFileEntryAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, 81920, cancellationToken);
    }

    private static Task AddOptionalFileEntryAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken) =>
        File.Exists(sourcePath)
            ? AddFileEntryAsync(archive, sourcePath, entryName, cancellationToken)
            : Task.CompletedTask;

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task ExtractSupportedEntriesAsync(
        ZipArchive archive,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = NormalizeEntryName(entry.FullName);
            if (normalizedName.Length == 0 || normalizedName.EndsWith('/')) continue;
            if (!IsSupportedEntry(normalizedName)) continue;

            var destinationPath = Path.GetFullPath(Path.Combine(
                stagingRoot,
                normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            EnsureContainedPath(stagingRoot, destinationPath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await source.CopyToAsync(destination, 81920, cancellationToken);
        }
    }

    private static bool IsSupportedEntry(string entryName) =>
        entryName.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(DatabaseEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(DatabaseWalEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(DatabaseShmEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(SettingsEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("library/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("covers/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("trash/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEntryName(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("备份包包含无效路径。 ");
        return string.Join('/', segments);
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (!string.Equals(manifest.Format, BackupFormat, StringComparison.Ordinal))
            throw new InvalidDataException("这不是 Kkindle 备份包。 ");
        if (manifest.Version != CurrentFormatVersion)
            throw new InvalidDataException($"备份包版本 {manifest.Version} 不受当前版本支持。 ");
        if (manifest.BookCount < 0 || manifest.FileCount < 0)
            throw new InvalidDataException("备份包清单中的数量无效。 ");
    }

    private static string CreateWorkingDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private void EnsureBackupOutsideDataDirectory(string path)
    {
        var dataRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(_paths.Data));
        if (path.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份文件请保存到应用数据目录以外。 ");
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var rootWithSeparator = EnsureTrailingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份包包含越界路径。 ");
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: false);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
    }

    private static void CopyOptionalFile(string source, string destination)
    {
        if (File.Exists(source))
            File.Copy(source, destination, overwrite: true);
    }

    private static void DeletePath(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup is best effort and must not hide the actual backup error.
        }
    }

    private sealed class BackupManifest
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int BookCount { get; set; }
        public int FileCount { get; set; }
    }

    private sealed class BackupSettings
    {
        public BackupAiSettings? Ai { get; set; }
        public BackupKindleEmailSettings? KindleEmail { get; set; }
        public BackupS3Settings? S3 { get; set; }
    }

    private sealed class BackupAiSettings
    {
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
    }

    private sealed class BackupKindleEmailSettings
    {
        public string? KindleEmailAddress { get; set; }
        public string? SenderEmailAddress { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public string? SmtpUsername { get; set; }
        public bool EnableSsl { get; set; } = true;
    }

    private sealed class BackupS3Settings
    {
        public bool Enabled { get; set; }
        public bool AutomaticSyncEnabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 30;
        public string? Endpoint { get; set; }
        public string? Bucket { get; set; }
        public string? Region { get; set; }
        public bool PathStyle { get; set; }
        public bool SkipTlsVerify { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        public int ConcurrentRequests { get; set; } = 4;
        public string? Prefix { get; set; }
        public bool EncryptionEnabled { get; set; }
    }
}

public sealed record AppBackupExportResult(int BookCount, int FileCount, long ArchiveSize);

public sealed record AppBackupImportResult(
    int BookCount,
    int FileCount,
    AiConnectionSettings AiSettings,
    KindleEmailSettings KindleEmailSettings)
{
    public S3SyncSettings? S3Settings { get; init; }
}
