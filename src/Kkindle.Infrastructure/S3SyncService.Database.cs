using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private string DatabaseConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.Database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        DefaultTimeout = 30
    }.ToString();

    private async Task<SqliteConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(DatabaseConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        await foreignKeys.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Installs the local deletion journal used to preserve the time at which
    /// a row was actually removed. The journal is intentionally local and is
    /// never included in a sync snapshot.
    /// </summary>
    public async Task InitializeDeletionTrackingAsync(
        CancellationToken cancellationToken = default,
        string? deviceId = null)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        if (deviceId is not null)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ReadingTimeSyncTracker.ConfigureDeviceAsync(connection, transaction, NormalizeDeviceId(deviceId), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task EnsureDeletionTrackingSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using (var control = CreateCommand(connection, transaction, """
            CREATE TABLE IF NOT EXISTS S3SyncDeletionControl (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                SchemaVersion INTEGER NOT NULL DEFAULT 0,
                Suppressed INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO S3SyncDeletionControl (Id) VALUES (1);
            """))
            await control.ExecuteNonQueryAsync(cancellationToken);
        await ReadingTimeSyncTracker.EnsureSchemaAsync(connection, transaction, cancellationToken);
        using (var version = CreateCommand(connection, transaction,
                   "SELECT SchemaVersion FROM S3SyncDeletionControl WHERE Id = 1;"))
        {
            if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken)) >= 2)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_Books;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_BookFiles;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_BookCollections;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_BookCollectionItems;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderAnnotations;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderProgress;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderBookmarks;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderLayoutSettings;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderReadingStats;
            CREATE TABLE IF NOT EXISTS S3SyncDeletionLog (
                EntityType TEXT NOT NULL,
                EntityKey TEXT NOT NULL,
                DeletedAt TEXT NOT NULL,
                PRIMARY KEY (EntityType, EntityKey)
            );
            CREATE INDEX IF NOT EXISTS IX_S3SyncDeletionLog_DeletedAt
                ON S3SyncDeletionLog(DeletedAt);

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_Books
            AFTER DELETE ON Books
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('book', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_BookFiles
            AFTER DELETE ON BookFiles
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('file', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_BookCollections
            AFTER DELETE ON BookCollections
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('collection', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_BookCollectionItems
            AFTER DELETE ON BookCollectionItems
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('collection-item', OLD.CollectionId || '|' || OLD.BookId,
                        strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderAnnotations
            AFTER DELETE ON ReaderAnnotations
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('annotation', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderProgress
            AFTER DELETE ON ReaderProgress
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('progress', OLD.BookFileId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderBookmarks
            AFTER DELETE ON ReaderBookmarks
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('bookmark', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderLayoutSettings
            AFTER DELETE ON ReaderLayoutSettings
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('layout', OLD.BookFileId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderReadingStats
            AFTER DELETE ON ReaderReadingStats
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('stats', OLD.BookFileId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;
            UPDATE S3SyncDeletionControl SET SchemaVersion = 2 WHERE Id = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<string, DateTimeOffset>> ReadRecordedDeletionTimesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        return await ReadRecordedDeletionTimesInTransactionAsync(connection, null, cancellationToken);
    }

    private static async Task<Dictionary<string, DateTimeOffset>> ReadRecordedDeletionTimesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   "SELECT EntityType, EntityKey, DeletedAt FROM S3SyncDeletionLog;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var entityType = NormalizeTombstoneEntityType(reader.GetString(0));
                var entityKey = NormalizeTombstoneKey(entityType, reader.GetString(1));
                var deletedAt = ParseTimestamp(reader.GetString(2));
                if (entityType.Length == 0
                    || entityKey.Length == 0
                    || deletedAt == DateTimeOffset.MinValue)
                    continue;

                var key = VersionKey(entityType, entityKey);
                if (!result.TryGetValue(key, out var existing) || deletedAt > existing)
                    result[key] = deletedAt;
            }
        }

        // There is no device-retirement/acknowledgement protocol yet. Retain
        // deletions while old snapshots can still be read from the bucket.
        return result;
    }

    private static async Task SuppressDeletionTrackingAsync(
        SqliteConnection connection, SqliteTransaction transaction, bool suppressed, CancellationToken cancellationToken)
    {
        // This flag only exists inside the writer transaction. Other writers
        // see zero after commit, or the old zero if the transaction rolls back.
        using var command = CreateCommand(connection, transaction,
            "UPDATE S3SyncDeletionControl SET Suppressed = $value WHERE Id = 1;");
        AddParameter(command, "$value", suppressed ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ClearRecordedDeletionTimesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        using (var exists = CreateCommand(
                   connection,
                   null,
                   "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'S3SyncDeletionLog');"))
        {
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                return;
        }
        using var command = CreateCommand(connection, null, "DELETE FROM S3SyncDeletionLog;");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<S3SyncSnapshot> CaptureDatabaseSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        CancellationToken cancellationToken)
    {
        var snapshot = new S3SyncSnapshot
        {
            DeviceId = deviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Tombstones = tombstones.ToList()
        };

        var bookUpdatedAt = new Dictionary<Guid, DateTimeOffset>();

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Publisher,
                   PublishDate, Isbn, PageCount, Binding, DoubanRating,
                   DoubanRatingCount, Tags, Category, IsFavorite, ReadingStatus,
                   CreatedAt, UpdatedAt, CoverPath
            FROM Books;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = ParseGuid(reader.GetString(0), "Books.Id");
                var createdAt = ParseTimestamp(reader.GetString(17));
                var updatedAt = ParseTimestamp(reader.GetString(18));
                var coverPath = NullableString(reader, 19);
                bookUpdatedAt[id] = updatedAt;
                snapshot.Books.Add(new S3SyncBook
                {
                    Id = id,
                    Title = reader.GetString(1),
                    Authors = reader.GetString(2),
                    Series = NullableString(reader, 3),
                    SeriesIndex = NullableDouble(reader, 4),
                    Description = NullableString(reader, 5),
                    Publisher = NullableString(reader, 6),
                    PublishDate = NullableString(reader, 7),
                    Isbn = NullableString(reader, 8),
                    PageCount = NullableString(reader, 9),
                    Binding = NullableString(reader, 10),
                    DoubanRating = NullableDouble(reader, 11),
                    DoubanRatingCount = NullableInt(reader, 12),
                    Tags = reader.GetString(13),
                    Category = reader.GetString(14),
                    IsFavorite = reader.GetInt32(15) != 0,
                    ReadingStatus = (LibraryReadingStatus)reader.GetInt32(16),
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                    LocalCoverPath = coverPath
                });
                if (!string.IsNullOrWhiteSpace(coverPath))
                    snapshot.LocalCoverPaths[id] = coverPath;
            }
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, BookId, Format, RelativePath, Size, Sha256
            FROM BookFiles;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = ParseGuid(reader.GetString(0), "BookFiles.Id");
                var bookId = ParseGuid(reader.GetString(1), "BookFiles.BookId");
                var relativePath = reader.GetString(3);
                snapshot.LocalFilePaths[id] = relativePath;
                snapshot.Files.Add(new S3SyncBookFile
                {
                    Id = id,
                    BookId = bookId,
                    Format = reader.GetString(2),
                    FileName = Path.GetFileName(relativePath.Replace('\\', '/')),
                    Size = reader.GetInt64(4),
                    Sha256 = reader.GetString(5),
                    ModifiedAt = bookUpdatedAt.GetValueOrDefault(bookId, snapshot.CreatedAt)
                });
            }
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, Name, CreatedAt
            FROM BookCollections;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Collections.Add(new S3SyncCollection
                {
                    Id = ParseGuid(reader.GetString(0), "BookCollections.Id"),
                    Name = reader.GetString(1),
                    CreatedAt = ParseTimestamp(reader.GetString(2))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT CollectionId, BookId, AddedAt
            FROM BookCollectionItems;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.CollectionItems.Add(new S3SyncCollectionItem
                {
                    CollectionId = ParseGuid(reader.GetString(0), "BookCollectionItems.CollectionId"),
                    BookId = ParseGuid(reader.GetString(1), "BookCollectionItems.BookId"),
                    AddedAt = ParseTimestamp(reader.GetString(2))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Annotations.Add(new S3SyncAnnotation
                {
                    Id = ParseGuid(reader.GetString(0), "ReaderAnnotations.Id"),
                    BookId = ParseGuid(reader.GetString(1), "ReaderAnnotations.BookId"),
                    BookFileId = ParseGuid(reader.GetString(2), "ReaderAnnotations.BookFileId"),
                    ChapterPath = reader.GetString(3),
                    Fragment = NullableString(reader, 4),
                    StartOffset = reader.GetInt32(5),
                    EndOffset = reader.GetInt32(6),
                    SelectedText = reader.GetString(7),
                    Prefix = reader.GetString(8),
                    Suffix = reader.GetString(9),
                    Color = reader.GetString(10),
                    UnderlineStyle = reader.GetString(11),
                    Note = reader.GetString(12),
                    CreatedAt = ParseTimestamp(reader.GetString(13)),
                    UpdatedAt = ParseTimestamp(reader.GetString(14))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                   ProgressPercent, FlowMode, UpdatedAt
            FROM ReaderProgress;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Progress.Add(new S3SyncProgress
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderProgress.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderProgress.BookFileId"),
                    ChapterPath = reader.GetString(2),
                    Fragment = NullableString(reader, 3),
                    ChapterIndex = reader.GetInt32(4),
                    ScrollPosition = reader.GetInt32(5),
                    ProgressPercent = reader.GetDouble(6),
                    FlowMode = reader.GetInt32(7),
                    UpdatedAt = ParseTimestamp(reader.GetString(8))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                   ScrollPosition, FlowMode, Title, Quote, CreatedAt
            FROM ReaderBookmarks;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Bookmarks.Add(new S3SyncBookmark
                {
                    Id = ParseGuid(reader.GetString(0), "ReaderBookmarks.Id"),
                    BookId = ParseGuid(reader.GetString(1), "ReaderBookmarks.BookId"),
                    BookFileId = ParseGuid(reader.GetString(2), "ReaderBookmarks.BookFileId"),
                    ChapterPath = reader.GetString(3),
                    Fragment = NullableString(reader, 4),
                    ChapterIndex = reader.GetInt32(5),
                    ScrollPosition = NullableInt(reader, 6),
                    FlowMode = reader.GetInt32(7),
                    Title = reader.GetString(8),
                    Quote = reader.GetString(9),
                    CreatedAt = ParseTimestamp(reader.GetString(10))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT BookId, BookFileId, FontScale, LineHeight, MaxWidth, BodyPadding,
                   FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent, UpdatedAt
            FROM ReaderLayoutSettings;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Layouts.Add(new S3SyncLayout
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderLayoutSettings.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderLayoutSettings.BookFileId"),
                    FontScale = reader.GetDouble(2),
                    LineHeight = reader.GetDouble(3),
                    MaxWidth = reader.GetDouble(4),
                    BodyPadding = reader.GetDouble(5),
                    FontFamily = NullableString(reader, 6),
                    FlowMode = reader.GetInt32(7),
                    VerticalWriting = reader.GetInt32(8) != 0,
                    TwoPageMode = reader.GetInt32(9) != 0,
                    ParagraphIndent = reader.GetInt32(10) != 0,
                    UpdatedAt = ParseTimestamp(reader.GetString(11))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT BookId, BookFileId, CumulativeSeconds, ProgressPercent,
                   CompletedChapters, TotalChapters, UpdatedAt
            FROM ReaderReadingStats;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.ReadingStats.Add(new S3SyncReadingStats
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderReadingStats.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderReadingStats.BookFileId"),
                    CumulativeSeconds = reader.GetInt64(2),
                    ProgressPercent = reader.GetDouble(3),
                    CompletedChapters = reader.GetInt32(4),
                    TotalChapters = reader.GetInt32(5),
                    UpdatedAt = ParseTimestamp(reader.GetString(6))
                });
        }

        foreach (var stats in snapshot.ReadingStats)
        {
            stats.SecondsByDevice = await ReadingTimeSyncTracker.CaptureAsync(
                connection, transaction, stats.BookFileId, stats.CumulativeSeconds, cancellationToken);
            stats.CumulativeSeconds = ReadingTimeSyncTracker.Total(stats.SecondsByDevice);
            await ReadingTimeSyncTracker.UpdateTotalAsync(connection, transaction, stats.BookFileId, stats.CumulativeSeconds, cancellationToken);
        }
        var recorded = await ReadRecordedDeletionTimesInTransactionAsync(connection, transaction, cancellationToken);
        snapshot.Tombstones = MergeTombstones(tombstones, GetRecordedTombstones(recorded, snapshot));
        return snapshot;
    }

    private async Task<S3SyncSnapshot> CaptureSnapshotAsync(
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await CaptureDatabaseSnapshotAsync(connection, transaction, deviceId, tombstones, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var book in snapshot.Books)
        {
            var coverPath = book.LocalCoverPath;
            if (string.IsNullOrWhiteSpace(coverPath)) continue;
            var absolute = ResolveDataPath(coverPath);
            if (absolute is null || !File.Exists(absolute)) continue;
            try
            {
                book.CoverHash = await GetCachedFileHashAsync(absolute, cancellationToken);
                book.CoverFileName = Path.GetFileName(coverPath.Replace('\\', '/'));
            }
            catch (IOException)
            {
                // A cover can be in the middle of being replaced; metadata is
                // still useful and the next sync will retry its binary object.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat an inaccessible cover like a transiently missing file.
            }
        }

        snapshot.Settings = await CaptureSettingsAsync(cancellationToken);
        return snapshot;
    }

    private async Task<S3SyncSettingsSnapshot> CaptureSettingsAsync(CancellationToken cancellationToken)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        return await CaptureSettingsUnderLockAsync(cancellationToken);
    }

    private async Task<S3SyncSettingsSnapshot> CaptureSettingsUnderLockAsync(CancellationToken cancellationToken)
    {
        var app = AppSettings.Normalize(await _appSettingsStore.LoadAsync(cancellationToken));
        var ai = await _aiSettingsStore.LoadAsync(cancellationToken);
        var kindleEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        var zLibrary = await _zLibrarySettingsStore.LoadAsync(cancellationToken);
        var appUpdatedAt = GetSettingsUpdatedAt(_paths.Settings);
        var aiUpdatedAt = GetSettingsUpdatedAt(Path.Combine(_paths.Data, "ai-settings.json"));
        var emailUpdatedAt = GetSettingsUpdatedAt(Path.Combine(_paths.Data, "kindle-email-settings.json"));
        var zLibraryUpdatedAt = GetSettingsUpdatedAt(Path.Combine(_paths.Data, "zlibrary-settings.json"));
        return new S3SyncSettingsSnapshot
        {
            UpdatedAt = new[] { appUpdatedAt, aiUpdatedAt, emailUpdatedAt, zLibraryUpdatedAt }.Max(),
            AppUpdatedAt = appUpdatedAt,
            AiUpdatedAt = aiUpdatedAt,
            KindleEmailUpdatedAt = emailUpdatedAt,
            ZLibraryUpdatedAt = zLibraryUpdatedAt,
            App = new S3SyncAppSettings
            {
                UiLanguage = app.UiLanguage,
                PreferredOpenFormat = app.PreferredOpenFormat,
                AutoBackupEnabled = app.AutoBackupEnabled,
                AutoGenerateEpubAndAzw3OnImport = app.AutoGenerateEpubAndAzw3OnImport,
                CollectionsMutuallyExclusive = app.CollectionsMutuallyExclusive,
                AutoBackupRetention = app.AutoBackupRetention,
                AiEnabled = app.AiEnabled,
                NetworkEnabled = app.NetworkEnabled,
                AutoUpdateCheckEnabled = app.AutoUpdateCheckEnabled,
                AutoDoubanMatchOnImport = app.AutoDoubanMatchOnImport,
                CompareKindleLibraryEnabled = app.CompareKindleLibraryEnabled,
                GridGalleryDisplay = app.GridGalleryDisplay,
                ReadingMaterialsCollapsedByDefault = app.ReadingMaterialsCollapsedByDefault,
                DefaultReaderLayout = app.DefaultReaderLayout
            },
            Ai = new S3SyncAiSettings
            {
                Provider = ai.Provider,
                BaseUrl = ai.BaseUrl,
                Model = ai.Model
            },
            KindleEmail = new S3SyncKindleEmailSettings
            {
                KindleEmailAddress = kindleEmail.KindleEmailAddress,
                SenderEmailAddress = kindleEmail.SenderEmailAddress,
                SmtpHost = kindleEmail.SmtpHost,
                SmtpPort = kindleEmail.SmtpPort,
                SmtpUsername = kindleEmail.SmtpUsername,
                EnableSsl = kindleEmail.EnableSsl
            },
            ZLibrary = new S3SyncZLibrarySettings
            {
                Email = zLibrary.Email,
                BaseUrl = zLibrary.BaseUrl
            }
        };
    }

    private static DateTimeOffset GetSettingsUpdatedAt(string path)
    {
        try
        {
            if (File.Exists(path)) return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (IOException)
        {
        }
        return DateTimeOffset.MinValue;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private async Task<DatabaseMergeResult> ApplyRemoteSnapshotsAsync(
        IAmazonS3 client,
        S3SyncSettings settings,
        S3SyncSnapshot localSnapshot,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var snapshots = remoteSnapshots
            .Where(snapshot => snapshot.Version <= SnapshotVersion)
            .GroupBy(snapshot => NormalizeKnownDeviceId(snapshot.DeviceId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CreatedAt).First())
            .ToArray();

        // Callers normally merge remote tombstones in SyncAsync. Keeping the
        // merge here as well makes this database phase safe to invoke on its
        // own (and avoids ever preparing a stale remote row before its
        // tombstone has been considered).
        localSnapshot.Tombstones = MergeTombstones(
            localSnapshot.Tombstones ?? [],
            snapshots.SelectMany(snapshot => snapshot.Tombstones ?? []));

        var warnings = new List<string>();
        var isPartial = false;
        var pathsToDelete = new List<string>();
        var duplicateBooksMerged = await ConsolidateLocalDuplicateBooksAsync(
            pathsToDelete,
            cancellationToken);
        if (duplicateBooksMerged > 0)
            warnings.Add(UiText.Get("已自动合并 {0} 本重复书籍。", duplicateBooksMerged));

        if (snapshots.Length == 0)
        {
            DeleteScheduledPaths(pathsToDelete, warnings);
            return new DatabaseMergeResult(
                0,
                0,
                0,
                duplicateBooksMerged > 0,
                warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct()));
        }

        var localIdentity = await ReadLocalDatabaseIdentityAsync(cancellationToken);
        var allRemoteBooks = snapshots
            .SelectMany(snapshot => snapshot.Books)
            .GroupBy(book => book.Id)
            .Select(group => group.OrderByDescending(book => book.UpdatedAt).First())
            .ToArray();
        var allRemoteFiles = snapshots
            .SelectMany(snapshot => snapshot.Files)
            .GroupBy(file => file.Id)
            .Select(group => group.OrderByDescending(file => file.ModifiedAt).First())
            .ToArray();

        // Build mappings from the complete remote view before applying
        // tombstones. A stale row may be suppressed below, but its ID is still
        // needed to map the corresponding tombstone to an existing local row.
        var bookMap = BuildBookMap(allRemoteBooks, allRemoteFiles, localIdentity);
        var fileMap = BuildFileMap(allRemoteFiles, bookMap, localIdentity);
        var allRemoteCollections = snapshots
            .SelectMany(snapshot => snapshot.Collections)
            .GroupBy(collection => collection.Id)
            .Select(group => group.OrderByDescending(collection => collection.CreatedAt).First())
            .ToArray();
        var collectionMap = BuildCollectionMap(allRemoteCollections, localIdentity);
        // Use the identity read after local duplicate consolidation.  The
        // captured snapshot can still contain IDs that consolidation just
        // removed; adding those stale IDs back would let an old remote row
        // resurrect a duplicate book during this same merge.
        AddLocalIdentityMappings(localIdentity, bookMap, fileMap, collectionMap);

        var tombstoneIndex = BuildTombstoneIndex(localSnapshot.Tombstones);
        // A deleted remote identity must never be matched to an unrelated
        // local book/file by the duplicate-title or content-hash heuristics.
        // Otherwise a tombstone for a removed copy could delete the surviving
        // copy that happens to share its title or bytes.
        foreach (var book in allRemoteBooks)
            if (IsTombstoned(tombstoneIndex, "book", book.Id, book.UpdatedAt)
                && !localIdentity.BooksById.ContainsKey(book.Id))
                bookMap[book.Id] = book.Id;
        foreach (var file in allRemoteFiles)
            if ((IsTombstoned(tombstoneIndex, "file", file.Id, file.ModifiedAt)
                    || IsTombstoned(tombstoneIndex, "book", file.BookId, file.ModifiedAt))
                && !localIdentity.FilesById.ContainsKey(file.Id))
                fileMap[file.Id] = file.Id;
        foreach (var collection in allRemoteCollections)
            if (IsTombstoned(tombstoneIndex, "collection", collection.Id, collection.CreatedAt)
                && !localIdentity.CollectionsById.ContainsKey(collection.Id))
                collectionMap[collection.Id] = collection.Id;
        var tombstonedBooks = allRemoteBooks
            .Where(book => IsTombstoned(tombstoneIndex, "book", book.Id, book.UpdatedAt))
            .Select(book => book.Id)
            .ToHashSet();
        var remoteBooks = allRemoteBooks
            .Where(book => !tombstonedBooks.Contains(book.Id)
                && !IsTombstoned(tombstoneIndex, "book", book.Id, book.UpdatedAt))
            .ToArray();
        var remoteFiles = allRemoteFiles
            .Where(file => !tombstonedBooks.Contains(file.BookId)
                && !IsTombstoned(tombstoneIndex, "book", file.BookId, file.ModifiedAt)
                && !IsTombstoned(tombstoneIndex, "file", file.Id, file.ModifiedAt))
            .ToArray();
        var tombstonedCollections = allRemoteCollections
            .Where(collection => IsTombstoned(
                tombstoneIndex,
                "collection",
                collection.Id,
                collection.CreatedAt))
            .Select(collection => collection.Id)
            .ToHashSet();
        var remoteCollections = allRemoteCollections
            .Where(collection => !tombstonedCollections.Contains(collection.Id))
            .ToArray();
        var remoteItems = snapshots
            .SelectMany(snapshot => snapshot.CollectionItems)
            .GroupBy(item => CompositeKey(item.CollectionId, item.BookId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.AddedAt).First())
            .Where(item => !tombstonedCollections.Contains(item.CollectionId)
                && !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "collection", item.CollectionId, item.AddedAt)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.AddedAt)
                && !IsTombstoned(
                    tombstoneIndex,
                    "collection-item",
                    CompositeKey(item.CollectionId, item.BookId),
                    item.AddedAt))
            .ToArray();
        var remoteAnnotations = snapshots
            .SelectMany(snapshot => snapshot.Annotations)
            .GroupBy(annotation => annotation.Id)
            .Select(group => group.OrderByDescending(annotation => annotation.UpdatedAt).First())
            .Where(annotation => !tombstonedBooks.Contains(annotation.BookId)
                && !IsTombstoned(tombstoneIndex, "book", annotation.BookId, annotation.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "annotation", annotation.Id, annotation.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "file", annotation.BookFileId, annotation.UpdatedAt))
            .ToArray();
        var remoteProgress = snapshots
            .SelectMany(snapshot => snapshot.Progress)
            .GroupBy(item => item.BookFileId)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "progress", item.BookFileId, item.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId, item.UpdatedAt))
            .ToArray();
        var remoteBookmarks = snapshots
            .SelectMany(snapshot => snapshot.Bookmarks)
            .GroupBy(bookmark => bookmark.Id)
            .Select(group => group.OrderByDescending(bookmark => bookmark.CreatedAt).First())
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.CreatedAt)
                && !IsTombstoned(tombstoneIndex, "bookmark", item.Id, item.CreatedAt)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId, item.CreatedAt))
            .ToArray();
        var remoteLayouts = snapshots
            .SelectMany(snapshot => snapshot.Layouts)
            .GroupBy(layout => layout.BookFileId)
            .Select(group => group.OrderByDescending(layout => layout.UpdatedAt).First())
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "layout", item.BookFileId, item.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId, item.UpdatedAt))
            .ToArray();
        var remoteStats = snapshots
            .SelectMany(snapshot => snapshot.ReadingStats)
            .GroupBy(stats => stats.BookFileId)
            .Select(MergeRemoteReadingStats)
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "stats", item.BookFileId, item.UpdatedAt)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId, item.UpdatedAt))
            .ToArray();
        void RefilterRemoteRows()
        {
            var view = FilterRemoteRows(new S3SyncSnapshot
            {
                Books = remoteBooks.ToList(), Files = remoteFiles.ToList(), Collections = remoteCollections.ToList(),
                CollectionItems = remoteItems.ToList(), Annotations = remoteAnnotations.ToList(),
                Progress = remoteProgress.ToList(), Bookmarks = remoteBookmarks.ToList(),
                Layouts = remoteLayouts.ToList(), ReadingStats = remoteStats.ToList()
            }, tombstoneIndex, bookMap, fileMap, collectionMap);
            remoteBooks = view.Books.ToArray();
            remoteFiles = view.Files.ToArray();
            remoteCollections = view.Collections.ToArray();
            remoteItems = view.CollectionItems.ToArray();
            remoteAnnotations = view.Annotations.ToArray();
            remoteProgress = view.Progress.ToArray();
            remoteBookmarks = view.Bookmarks.ToArray();
            remoteLayouts = view.Layouts.ToArray();
            remoteStats = view.ReadingStats.ToArray();
        }
        RefilterRemoteRows();
        var preparedFilesByLocalId = new Dictionary<Guid, PreparedSyncFile>();
        var plannedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverUpdates = new Dictionary<Guid, (string RelativePath, DateTimeOffset UpdatedAt)>();
        var filesDownloaded = 0;
        var coversDownloaded = false;

        progress?.Report(UiText.Get("正在准备书籍文件…"));
        foreach (var remoteFile in remoteFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bookMap.TryGetValue(remoteFile.BookId, out var localBookId)
                || !fileMap.TryGetValue(remoteFile.Id, out var localFileId))
                continue;

            var existing = localIdentity.FilesById.GetValueOrDefault(localFileId);
            if (preparedFilesByLocalId.TryGetValue(localFileId, out var preparedForSameFile))
            {
                // Different devices can describe the same content with
                // different BookFile IDs. Keep one local row and let both
                // remote IDs point at that row instead of downloading and
                // inserting the same bytes twice.
                var rebound = preparedForSameFile with { Source = remoteFile };
                preparedFilesByLocalId[localFileId] = rebound;
                continue;
            }
            var relativePath = existing?.RelativePath
                ?? Path.Combine(
                    "library",
                    localBookId.ToString("N"),
                     GetAvailableFileName(remoteFile.FileName, remoteFile.Sha256, remoteFile.Format, localBookId, plannedRelativePaths));
            var absolutePath = ResolveDataPath(relativePath);
            if (absolutePath is null)
            {
                warnings.Add(UiText.Get("《{0}》的同步路径无效。", remoteFile.FileName));
                isPartial = true;
                continue;
            }

            try
            {
                var downloaded = await EnsureLocalBlobAsync(
                    client,
                    settings,
                    remoteFile.Sha256,
                    absolutePath,
                    progress,
                    cancellationToken);
                if (downloaded) filesDownloaded++;
                var prepared = new PreparedSyncFile(
                    remoteFile,
                    localBookId,
                    localFileId,
                    Path.GetRelativePath(_paths.Data, absolutePath));
                preparedFilesByLocalId[localFileId] = prepared;
                plannedRelativePaths.Add(prepared.RelativePath);
            }
            catch (AmazonS3Exception exception) when (IsMissingObjectForRead(exception))
            {
                warnings.Add(UiText.Get("《{0}》的 S3 文件对象不存在，已跳过。", remoteFile.FileName));
                isPartial = true;
            }
            catch (InvalidDataException exception)
            {
                warnings.Add(UiText.Get("《{0}》校验失败：{1}", remoteFile.FileName, UiText.Localize(exception.Message)));
                isPartial = true;
            }
        }

        foreach (var remoteBook in remoteBooks)
        {
            if (!IsSha256(remoteBook.CoverHash)
                || string.IsNullOrWhiteSpace(remoteBook.CoverFileName)
                || !bookMap.TryGetValue(remoteBook.Id, out var localBookId))
                continue;

            var existing = localIdentity.BooksById.GetValueOrDefault(localBookId);
            var localCoverPath = existing?.CoverPath;
            var remoteWins = existing is null || remoteBook.UpdatedAt >= existing.UpdatedAt;
            if (!remoteWins && !string.IsNullOrWhiteSpace(localCoverPath)) continue;
            if (coverUpdates.TryGetValue(localBookId, out var previousCover) && previousCover.UpdatedAt > remoteBook.UpdatedAt)
                continue;

            // Never replace the currently displayed cover during network I/O.
            // The database transaction below decides whether this version wins.
            var relativePath = Path.Combine("covers",
                $"{localBookId:N}-{remoteBook.CoverHash!.ToLowerInvariant()}{NormalizeCoverExtension(remoteBook.CoverFileName)}");
            var absolutePath = ResolveDataPath(relativePath);
            if (absolutePath is null) continue;
            try
            {
                coversDownloaded |= await EnsureLocalBlobAsync(
                    client,
                    settings,
                    remoteBook.CoverHash!,
                    absolutePath,
                    progress,
                    cancellationToken);
                if (coverUpdates.TryGetValue(localBookId, out previousCover))
                {
                    var previousPath = ResolveDataPath(previousCover.RelativePath);
                    if (previousPath is not null) pathsToDelete.Add(previousPath);
                }
                coverUpdates[localBookId] = (Path.GetRelativePath(_paths.Data, absolutePath), remoteBook.UpdatedAt);
            }
            catch (AmazonS3Exception exception) when (IsMissingObjectForRead(exception))
            {
                warnings.Add(UiText.Get("有封面对象不存在，已跳过封面同步。"));
                isPartial = true;
            }
            catch (InvalidDataException exception)
            {
                warnings.Add(UiText.Get("有封面校验失败：{0}", UiText.Localize(exception.Message)));
                isPartial = true;
            }
        }

        var locallyKnownFileIds = new HashSet<Guid>(localIdentity.FilesById.Keys);
        foreach (var prepared in preparedFilesByLocalId.Values)
            locallyKnownFileIds.Add(prepared.LocalFileId);

        var changed = duplicateBooksMerged > 0;
        var booksAdded = 0;
        var addedBookIds = new HashSet<Guid>();
        var annotationsApplied = 0;
        await using (var connection = await OpenDatabaseConnectionAsync(cancellationToken))
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            // Network I/O may have taken minutes. Rebase against the current
            // database and deletion journal under the same writer transaction
            // that applies the merge, so a just-deleted row cannot be revived.
            var currentLocal = await CaptureDatabaseSnapshotAsync(
                connection, transaction, localSnapshot.DeviceId, localSnapshot.Tombstones, cancellationToken);
            var currentBooks = currentLocal.Books.ToDictionary(book => book.Id);
            var currentFileIds = currentLocal.Files.Select(file => file.Id).ToHashSet();
            localSnapshot.Tombstones = currentLocal.Tombstones;
            tombstoneIndex = BuildTombstoneIndex(currentLocal.Tombstones);
            RefilterRemoteRows();
            foreach (var (id, prepared) in preparedFilesByLocalId.ToArray())
            {
                var source = remoteFiles.FirstOrDefault(file => fileMap.GetValueOrDefault(file.Id) == id);
                if (source is not null) preparedFilesByLocalId[id] = prepared with { Source = source };
                else
                {
                    preparedFilesByLocalId.Remove(id);
                    var unusedPath = ResolveDataPath(prepared.RelativePath);
                    if (unusedPath is not null) pathsToDelete.Add(unusedPath);
                }
            }
            var survivingBookIds = remoteBooks.Select(book => bookMap[book.Id]).ToHashSet();
            foreach (var (id, cover) in coverUpdates.ToArray())
            {
                if (!survivingBookIds.Contains(id)
                    || (currentBooks.TryGetValue(id, out var currentBook) && currentBook.UpdatedAt > cover.UpdatedAt
                        && !string.IsNullOrWhiteSpace(currentBook.LocalCoverPath)))
                {
                    var unusedPath = ResolveDataPath(cover.RelativePath);
                    if (unusedPath is not null) pathsToDelete.Add(unusedPath);
                    coverUpdates.Remove(id);
                }
            }
            locallyKnownFileIds.Clear();
            locallyKnownFileIds.UnionWith(currentLocal.Files.Select(file => file.Id));
            locallyKnownFileIds.UnionWith(preparedFilesByLocalId.Keys);
            await SuppressDeletionTrackingAsync(connection, transaction, true, cancellationToken);
            using var commandCache = new SqliteCommandCache(connection, transaction);
            foreach (var book in remoteBooks)
            {
                if (!bookMap.TryGetValue(book.Id, out var localBookId)) continue;
                if (!currentBooks.ContainsKey(localBookId)
                    && addedBookIds.Add(localBookId))
                    booksAdded++;
                var affected = await UpsertBookAsync(
                    commandCache.Get(UpsertBookSql),
                    book,
                    localBookId,
                    cancellationToken);
                changed |= affected > 0;
            }

            foreach (var collection in remoteCollections)
            {
                if (!collectionMap.TryGetValue(collection.Id, out var localCollectionId)) continue;
                var command = commandCache.Get(
                    """
                    INSERT OR IGNORE INTO BookCollections (Id, Name, CreatedAt)
                    VALUES ($id, $name, $createdAt);
                    """
                    );
                AddParameter(command, "$id", localCollectionId.ToString());
                AddParameter(command, "$name", collection.Name);
                AddParameter(command, "$createdAt", collection.CreatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var prepared in preparedFilesByLocalId.Values)
            {
                if (currentFileIds.Contains(prepared.LocalFileId)) continue;
                var command = commandCache.Get(
                    """
                    INSERT OR IGNORE INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($id, $bookId, $format, $relativePath, $size, $sha256);
                    """
                    );
                AddParameter(command, "$id", prepared.LocalFileId.ToString());
                AddParameter(command, "$bookId", prepared.LocalBookId.ToString());
                AddParameter(command, "$format", prepared.Source.Format);
                AddParameter(command, "$relativePath", prepared.RelativePath);
                AddParameter(command, "$size", prepared.Source.Size);
                AddParameter(command, "$sha256", prepared.Source.Sha256.ToLowerInvariant());
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
                locallyKnownFileIds.Add(prepared.LocalFileId);
            }

            foreach (var (bookId, cover) in coverUpdates)
            {
                if (currentBooks.TryGetValue(bookId, out var previous) && !string.IsNullOrWhiteSpace(previous.LocalCoverPath))
                {
                    var previousPath = ResolveDataPath(previous.LocalCoverPath);
                    if (previousPath is not null) pathsToDelete.Add(previousPath);
                }
                var command = commandCache.Get(
                    """
                    UPDATE Books SET CoverPath = $coverPath
                    WHERE Id = $bookId AND (CoverPath IS NULL OR CoverPath <> $coverPath)
                        AND (CoverPath IS NULL OR julianday(UpdatedAt) <= julianday($updatedAt));
                    """
                    );
                AddParameter(command, "$bookId", bookId.ToString());
                AddParameter(command, "$coverPath", cover.RelativePath);
                AddParameter(command, "$updatedAt", cover.UpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var item in remoteItems)
            {
                if (!collectionMap.TryGetValue(item.CollectionId, out var localCollectionId)
                    || !bookMap.TryGetValue(item.BookId, out var localBookId))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                    VALUES ($collectionId, $bookId, $addedAt);
                    """
                    );
                AddParameter(command, "$collectionId", localCollectionId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$addedAt", item.AddedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var annotation in remoteAnnotations)
            {
                if (!TryMapReaderRow(annotation.BookId, annotation.BookFileId, bookMap, fileMap, locallyKnownFileIds, out var localBookId, out var localFileId))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderAnnotations (
                        Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                        SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt)
                    VALUES (
                        $id, $bookId, $bookFileId, $chapterPath, $fragment, $startOffset, $endOffset,
                        $selectedText, $prefix, $suffix, $color, $underlineStyle, $note, $createdAt, $updatedAt)
                    ON CONFLICT(Id) DO UPDATE SET
                        BookId = excluded.BookId, BookFileId = excluded.BookFileId,
                        ChapterPath = excluded.ChapterPath, Fragment = excluded.Fragment,
                        StartOffset = excluded.StartOffset, EndOffset = excluded.EndOffset,
                        SelectedText = excluded.SelectedText, Prefix = excluded.Prefix,
                        Suffix = excluded.Suffix, Color = excluded.Color,
                        UnderlineStyle = excluded.UnderlineStyle, Note = excluded.Note,
                        UpdatedAt = excluded.UpdatedAt
                    WHERE julianday(excluded.UpdatedAt) > julianday(ReaderAnnotations.UpdatedAt);
                    """
                    );
                AddParameter(command, "$id", annotation.Id.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$chapterPath", annotation.ChapterPath);
                AddParameter(command, "$fragment", annotation.Fragment);
                AddParameter(command, "$startOffset", annotation.StartOffset);
                AddParameter(command, "$endOffset", annotation.EndOffset);
                AddParameter(command, "$selectedText", annotation.SelectedText);
                AddParameter(command, "$prefix", annotation.Prefix);
                AddParameter(command, "$suffix", annotation.Suffix);
                AddParameter(command, "$color", annotation.Color);
                AddParameter(command, "$underlineStyle", annotation.UnderlineStyle);
                AddParameter(command, "$note", annotation.Note);
                AddParameter(command, "$createdAt", annotation.CreatedAt.ToString("O"));
                AddParameter(command, "$updatedAt", annotation.UpdatedAt.ToString("O"));
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                changed |= affected > 0;
                annotationsApplied += affected > 0 ? 1 : 0;
            }

            foreach (var item in remoteProgress)
            {
                if (!TryMapReaderRow(item.BookId, item.BookFileId, bookMap, fileMap, locallyKnownFileIds, out var localBookId, out var localFileId))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderProgress (
                        BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                        ProgressPercent, FlowMode, UpdatedAt)
                    VALUES (
                        $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                        $progressPercent, $flowMode, $updatedAt)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId, ChapterPath = excluded.ChapterPath,
                        Fragment = excluded.Fragment, ChapterIndex = excluded.ChapterIndex,
                        ScrollPosition = excluded.ScrollPosition, ProgressPercent = excluded.ProgressPercent,
                        FlowMode = excluded.FlowMode, UpdatedAt = excluded.UpdatedAt
                    WHERE julianday(excluded.UpdatedAt) > julianday(ReaderProgress.UpdatedAt);
                    """
                    );
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$chapterPath", item.ChapterPath);
                AddParameter(command, "$fragment", item.Fragment);
                AddParameter(command, "$chapterIndex", item.ChapterIndex);
                AddParameter(command, "$scrollPosition", item.ScrollPosition);
                AddParameter(command, "$progressPercent", item.ProgressPercent);
                AddParameter(command, "$flowMode", item.FlowMode);
                AddParameter(command, "$updatedAt", item.UpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var bookmark in remoteBookmarks)
            {
                if (!TryMapReaderRow(bookmark.BookId, bookmark.BookFileId, bookMap, fileMap, locallyKnownFileIds, out var localBookId, out var localFileId))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderBookmarks (
                        Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                        ScrollPosition, FlowMode, Title, Quote, CreatedAt)
                    VALUES (
                        $id, $bookId, $bookFileId, $chapterPath, $fragment, $chapterIndex,
                        $scrollPosition, $flowMode, $title, $quote, $createdAt)
                    ON CONFLICT(Id) DO UPDATE SET
                        BookId = excluded.BookId, BookFileId = excluded.BookFileId,
                        ChapterPath = excluded.ChapterPath, Fragment = excluded.Fragment,
                        ChapterIndex = excluded.ChapterIndex, ScrollPosition = excluded.ScrollPosition,
                        FlowMode = excluded.FlowMode, Title = excluded.Title,
                        Quote = excluded.Quote, CreatedAt = excluded.CreatedAt
                    WHERE julianday(excluded.CreatedAt) > julianday(ReaderBookmarks.CreatedAt);
                    """
                    );
                AddParameter(command, "$id", bookmark.Id.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$chapterPath", bookmark.ChapterPath);
                AddParameter(command, "$fragment", bookmark.Fragment);
                AddParameter(command, "$chapterIndex", bookmark.ChapterIndex);
                AddParameter(command, "$scrollPosition", bookmark.ScrollPosition);
                AddParameter(command, "$flowMode", bookmark.FlowMode);
                AddParameter(command, "$title", bookmark.Title);
                AddParameter(command, "$quote", bookmark.Quote);
                AddParameter(command, "$createdAt", bookmark.CreatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var layout in remoteLayouts)
            {
                if (!TryMapReaderRow(layout.BookId, layout.BookFileId, bookMap, fileMap, locallyKnownFileIds, out var localBookId, out var localFileId))
                    continue;
                var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
                    layout.FontScale,
                    layout.LineHeight,
                    layout.MaxWidth,
                    layout.BodyPadding,
                    layout.FontFamily ?? ReaderFontDefaults.DefaultFamily,
                    layout.FlowMode,
                    layout.VerticalWriting,
                    layout.TwoPageMode)
                {
                    ParagraphIndent = layout.ParagraphIndent
                });
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderLayoutSettings (
                        BookFileId, BookId, FontScale, LineHeight, MaxWidth, BodyPadding,
                        FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent, UpdatedAt)
                    VALUES (
                        $bookFileId, $bookId, $fontScale, $lineHeight, $maxWidth, $bodyPadding,
                        $fontFamily, $flowMode, $verticalWriting, $twoPageMode, $paragraphIndent, $updatedAt)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId, FontScale = excluded.FontScale,
                        LineHeight = excluded.LineHeight, MaxWidth = excluded.MaxWidth,
                        BodyPadding = excluded.BodyPadding, FontFamily = excluded.FontFamily,
                        FlowMode = excluded.FlowMode, VerticalWriting = excluded.VerticalWriting,
                        TwoPageMode = excluded.TwoPageMode, ParagraphIndent = excluded.ParagraphIndent,
                        UpdatedAt = excluded.UpdatedAt
                    WHERE julianday(excluded.UpdatedAt) > julianday(ReaderLayoutSettings.UpdatedAt);
                    """
                    );
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$fontScale", normalized.FontScale);
                AddParameter(command, "$lineHeight", normalized.LineHeight);
                AddParameter(command, "$maxWidth", normalized.MaxWidth);
                AddParameter(command, "$bodyPadding", normalized.BodyPadding);
                AddParameter(command, "$fontFamily", normalized.FontFamily);
                AddParameter(command, "$flowMode", normalized.FlowMode);
                AddParameter(command, "$verticalWriting", normalized.VerticalWriting ? 1 : 0);
                AddParameter(command, "$twoPageMode", normalized.TwoPageMode ? 1 : 0);
                AddParameter(command, "$paragraphIndent", normalized.ParagraphIndent ? 1 : 0);
                AddParameter(command, "$updatedAt", layout.UpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var stats in remoteStats)
            {
                if (!TryMapReaderRow(stats.BookId, stats.BookFileId, bookMap, fileMap, locallyKnownFileIds, out var localBookId, out var localFileId))
                    continue;
                var mergedSeconds = await ReadingTimeSyncTracker.MergeAsync(
                    connection, transaction, localFileId, stats.SecondsByDevice, stats.CumulativeSeconds, cancellationToken);
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderReadingStats (
                        BookFileId, BookId, CumulativeSeconds, ProgressPercent,
                        CompletedChapters, TotalChapters, UpdatedAt)
                    VALUES (
                        $bookFileId, $bookId, $cumulativeSeconds, $progressPercent,
                        $completedChapters, $totalChapters, $updatedAt)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId, CumulativeSeconds = excluded.CumulativeSeconds,
                        ProgressPercent = excluded.ProgressPercent,
                        CompletedChapters = excluded.CompletedChapters,
                        TotalChapters = excluded.TotalChapters, UpdatedAt = excluded.UpdatedAt
                    WHERE julianday(excluded.UpdatedAt) > julianday(ReaderReadingStats.UpdatedAt);
                    """
                    );
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$cumulativeSeconds", mergedSeconds);
                AddParameter(command, "$progressPercent", stats.ProgressPercent);
                AddParameter(command, "$completedChapters", stats.CompletedChapters);
                AddParameter(command, "$totalChapters", stats.TotalChapters);
                AddParameter(command, "$updatedAt", stats.UpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
                changed |= await ReadingTimeSyncTracker.UpdateTotalAsync(
                    connection, transaction, localFileId, mergedSeconds, cancellationToken) > 0;
            }

            changed |= await ApplyTombstonesAsync(
                connection,
                transaction,
                localSnapshot.Tombstones,
                currentLocal,
                bookMap,
                fileMap,
                collectionMap,
                remoteBooks,
                remoteFiles,
                remoteCollections,
                remoteItems,
                remoteAnnotations,
                remoteProgress,
                remoteBookmarks,
                remoteLayouts,
                remoteStats,
                pathsToDelete,
                commandCache,
                cancellationToken);

            await RemoveReferencedScheduledPathsAsync(
                connection,
                transaction,
                pathsToDelete,
                cancellationToken);
            await SuppressDeletionTrackingAsync(connection, transaction, false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        DeleteScheduledPaths(pathsToDelete, warnings);

        return new DatabaseMergeResult(
            booksAdded,
            filesDownloaded,
            annotationsApplied,
            changed || filesDownloaded > 0 || coversDownloaded,
            warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct())) { IsPartial = isPartial };
    }

    private static S3SyncReadingStats MergeRemoteReadingStats(IEnumerable<S3SyncReadingStats> versions)
    {
        var rows = versions.ToArray();
        var latest = rows.OrderByDescending(row => row.UpdatedAt).First();
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var values = row.SecondsByDevice is { Count: > 0 } ? row.SecondsByDevice
                : new Dictionary<string, long> { ["legacy"] = Math.Max(0, row.CumulativeSeconds) };
            foreach (var (device, seconds) in values)
                counters[device] = Math.Max(counters.GetValueOrDefault(device), seconds);
        }
        return new S3SyncReadingStats
        {
            BookId = latest.BookId, BookFileId = latest.BookFileId,
            CumulativeSeconds = ReadingTimeSyncTracker.Total(counters), SecondsByDevice = counters,
            ProgressPercent = latest.ProgressPercent, CompletedChapters = latest.CompletedChapters,
            TotalChapters = latest.TotalChapters, UpdatedAt = latest.UpdatedAt
        };
    }

    private static S3SyncSnapshot FilterRemoteRows(
        S3SyncSnapshot view,
        IReadOnlyDictionary<string, DateTimeOffset> tombstones,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> collectionMap)
    {
        bool Deleted(string type, Guid id, DateTimeOffset version, IReadOnlyDictionary<Guid, Guid> map) =>
            IsTombstoned(tombstones, type, id, version)
            || (map.TryGetValue(id, out var mapped) && IsTombstoned(tombstones, type, mapped, version));
        var deletedBooks = view.Books.Where(book => Deleted("book", book.Id, book.UpdatedAt, bookMap))
            .Select(book => book.Id).ToHashSet();
        var deletedCollections = view.Collections.Where(row => Deleted("collection", row.Id, row.CreatedAt, collectionMap))
            .Select(row => row.Id).ToHashSet();
        bool LiveReader(Guid book, Guid file, DateTimeOffset version) => !deletedBooks.Contains(book)
            && !Deleted("book", book, version, bookMap) && !Deleted("file", file, version, fileMap);
        view.Books = view.Books.Where(book => !deletedBooks.Contains(book.Id)).ToList();
        view.Files = view.Files.Where(file => LiveReader(file.BookId, file.Id, file.ModifiedAt)).ToList();
        view.Collections = view.Collections.Where(row => !deletedCollections.Contains(row.Id)).ToList();
        view.CollectionItems = view.CollectionItems.Where(row =>
            !deletedBooks.Contains(row.BookId) && !deletedCollections.Contains(row.CollectionId)
            && !Deleted("book", row.BookId, row.AddedAt, bookMap)
            && !Deleted("collection", row.CollectionId, row.AddedAt, collectionMap)
            && !IsTombstoned(tombstones, "collection-item", CompositeKey(row.CollectionId, row.BookId), row.AddedAt)
            && !IsTombstoned(tombstones, "collection-item", CompositeKey(
                collectionMap.GetValueOrDefault(row.CollectionId, row.CollectionId),
                bookMap.GetValueOrDefault(row.BookId, row.BookId)), row.AddedAt)).ToList();
        view.Annotations = view.Annotations.Where(row => LiveReader(row.BookId, row.BookFileId, row.UpdatedAt)
            && !IsTombstoned(tombstones, "annotation", row.Id, row.UpdatedAt)).ToList();
        view.Progress = view.Progress.Where(row => LiveReader(row.BookId, row.BookFileId, row.UpdatedAt)
            && !Deleted("progress", row.BookFileId, row.UpdatedAt, fileMap)).ToList();
        view.Bookmarks = view.Bookmarks.Where(row => LiveReader(row.BookId, row.BookFileId, row.CreatedAt)
            && !IsTombstoned(tombstones, "bookmark", row.Id, row.CreatedAt)).ToList();
        view.Layouts = view.Layouts.Where(row => LiveReader(row.BookId, row.BookFileId, row.UpdatedAt)
            && !Deleted("layout", row.BookFileId, row.UpdatedAt, fileMap)).ToList();
        view.ReadingStats = view.ReadingStats.Where(row => LiveReader(row.BookId, row.BookFileId, row.UpdatedAt)
            && !Deleted("stats", row.BookFileId, row.UpdatedAt, fileMap)).ToList();
        return view;
    }

    private async Task<LocalDatabaseIdentity> ReadLocalDatabaseIdentityAsync(CancellationToken cancellationToken)
    {
        var identity = new LocalDatabaseIdentity();
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);

        using (var command = CreateCommand(connection, null, 
            """
            SELECT Id, Title, Authors, CreatedAt, UpdatedAt, CoverPath
            FROM Books;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var book = new LocalBookIdentity(
                    ParseGuid(reader.GetString(0), "Books.Id"),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseTimestamp(reader.GetString(3)),
                    ParseTimestamp(reader.GetString(4)),
                    NullableString(reader, 5));
                identity.BooksById[book.Id] = book;
            }
        }

        using (var command = CreateCommand(connection, null, 
            """
            SELECT Id, BookId, RelativePath, Size, Sha256
            FROM BookFiles;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var file = new LocalFileIdentity(
                    ParseGuid(reader.GetString(0), "BookFiles.Id"),
                    ParseGuid(reader.GetString(1), "BookFiles.BookId"),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4));
                identity.FilesById[file.Id] = file;
                if (IsSha256(file.Sha256)) identity.FilesByHash[file.Sha256] = file;
            }
        }

        using (var command = CreateCommand(connection, null, "SELECT Id, Name, CreatedAt FROM BookCollections;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var collection = new LocalCollectionIdentity(
                    ParseGuid(reader.GetString(0), "BookCollections.Id"),
                    reader.GetString(1),
                    ParseTimestamp(reader.GetString(2)));
                identity.CollectionsById[collection.Id] = collection;
            }
        }

        return identity;
    }

    private static Dictionary<Guid, Guid> BuildBookMap(
        IReadOnlyList<S3SyncBook> remoteBooks,
        IReadOnlyList<S3SyncBookFile> remoteFiles,
        LocalDatabaseIdentity localIdentity)
    {
        var result = new Dictionary<Guid, Guid>();
        var usedLocalIds = new HashSet<Guid>(localIdentity.BooksById.Keys);
        var localFileCounts = localIdentity.FilesById.Values
            .GroupBy(file => file.BookId)
            .ToDictionary(group => group.Key, group => group.Count());
        var localByTitle = localIdentity.BooksById.Values
            .GroupBy(book => BuildBookMatchKey(book.Title, book.Authors), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(book => localFileCounts.GetValueOrDefault(book.Id))
                    .ThenByDescending(book => book.UpdatedAt)
                    .ThenBy(book => book.CreatedAt)
                    .ThenBy(book => book.Id)
                    .First()
                    .Id,
                StringComparer.OrdinalIgnoreCase);
        var remoteByTitle = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var remoteFileBearing = remoteFiles
            .Where(file => IsSha256(file.Sha256))
            .Select(file => file.BookId)
            .ToHashSet();

        // A book imported on two devices can have different GUIDs. Process
        // file-bearing/newer rows first, then let later rows with the same
        // title+author key reuse the first local identity.
        foreach (var remoteBook in remoteBooks
                     .OrderByDescending(book => remoteFileBearing.Contains(book.Id))
                     .ThenByDescending(book => book.UpdatedAt)
                     .ThenBy(book => book.Id))
        {
            var matchKey = BuildBookMatchKey(remoteBook.Title, remoteBook.Authors);
            if (CanUseBookMatchKey(remoteBook.Title, remoteBook.Authors)
                && remoteByTitle.TryGetValue(matchKey, out var remoteMatch))
            {
                result[remoteBook.Id] = remoteMatch;
                continue;
            }

            Guid localId;
            if (localIdentity.BooksById.ContainsKey(remoteBook.Id))
            {
                localId = remoteBook.Id;
            }
            else
            {
                var fileMatch = remoteFiles
                    .Where(file => file.BookId == remoteBook.Id && IsSha256(file.Sha256))
                    .Select(file => localIdentity.FilesByHash.GetValueOrDefault(file.Sha256))
                    .FirstOrDefault(file => file is not null);
                if (fileMatch is not null)
                {
                    localId = fileMatch.BookId;
                }
                else if (localByTitle.TryGetValue(matchKey, out var titleMatch))
                {
                    localId = titleMatch;
                }
                else
                {
                    localId = remoteBook.Id;
                    if (!usedLocalIds.Add(localId))
                        localId = Guid.NewGuid();
                }
            }

            result[remoteBook.Id] = localId;
            if (CanUseBookMatchKey(remoteBook.Title, remoteBook.Authors))
                remoteByTitle[matchKey] = localId;
        }

        return result;
    }

    private static Dictionary<Guid, Guid> BuildFileMap(
        IReadOnlyList<S3SyncBookFile> remoteFiles,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        LocalDatabaseIdentity localIdentity)
    {
        var result = new Dictionary<Guid, Guid>();
        var usedLocalIds = new HashSet<Guid>(localIdentity.FilesById.Keys);
        var remoteByHash = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var remoteFile in remoteFiles)
        {
            if (localIdentity.FilesById.TryGetValue(remoteFile.Id, out var byId)
                && string.Equals(byId.Sha256, remoteFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                result[remoteFile.Id] = byId.Id;
                if (IsSha256(remoteFile.Sha256))
                    remoteByHash[remoteFile.Sha256] = byId.Id;
                continue;
            }

            if (IsSha256(remoteFile.Sha256)
                && localIdentity.FilesByHash.TryGetValue(remoteFile.Sha256, out var byHash))
            {
                result[remoteFile.Id] = byHash.Id;
                remoteByHash[remoteFile.Sha256] = byHash.Id;
                continue;
            }

            if (IsSha256(remoteFile.Sha256)
                && remoteByHash.TryGetValue(remoteFile.Sha256, out var remoteMatch))
            {
                result[remoteFile.Id] = remoteMatch;
                continue;
            }

            var localId = remoteFile.Id;
            if (!usedLocalIds.Add(localId))
                localId = Guid.NewGuid();
            result[remoteFile.Id] = localId;
            if (IsSha256(remoteFile.Sha256))
                remoteByHash[remoteFile.Sha256] = localId;
        }
        return result;
    }

    private static Dictionary<Guid, Guid> BuildCollectionMap(
        IEnumerable<S3SyncCollection> remoteCollections,
        LocalDatabaseIdentity localIdentity)
    {
        var result = new Dictionary<Guid, Guid>();
        var usedLocalIds = new HashSet<Guid>(localIdentity.CollectionsById.Keys);
        var localByName = localIdentity.CollectionsById.Values
            .GroupBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var remoteCollection in remoteCollections.GroupBy(collection => collection.Id)
                     .Select(group => group.OrderByDescending(collection => collection.CreatedAt).First()))
        {
            if (localIdentity.CollectionsById.ContainsKey(remoteCollection.Id))
            {
                result[remoteCollection.Id] = remoteCollection.Id;
                continue;
            }
            if (localByName.TryGetValue(remoteCollection.Name, out var byName))
            {
                result[remoteCollection.Id] = byName;
                continue;
            }

            var localId = remoteCollection.Id;
            if (!usedLocalIds.Add(localId)) localId = Guid.NewGuid();
            result[remoteCollection.Id] = localId;
            localByName[remoteCollection.Name] = localId;
        }
        return result;
    }

    private static void AddLocalIdentityMappings(
        LocalDatabaseIdentity localIdentity,
        IDictionary<Guid, Guid> bookMap,
        IDictionary<Guid, Guid> fileMap,
        IDictionary<Guid, Guid> collectionMap)
    {
        foreach (var bookId in localIdentity.BooksById.Keys)
            bookMap.TryAdd(bookId, bookId);
        foreach (var fileId in localIdentity.FilesById.Keys)
            fileMap.TryAdd(fileId, fileId);
        foreach (var collectionId in localIdentity.CollectionsById.Keys)
            collectionMap.TryAdd(collectionId, collectionId);
    }

    private static Dictionary<string, DateTimeOffset> BuildTombstoneIndex(
        IEnumerable<S3SyncTombstone> tombstones)
    {
        var index = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var tombstone in tombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityType)
                || string.IsNullOrWhiteSpace(tombstone.Key))
                continue;
            var entityType = NormalizeTombstoneEntityType(tombstone.EntityType);
            var key = VersionKey(entityType, NormalizeTombstoneKey(entityType, tombstone.Key));
            if (!index.TryGetValue(key, out var existing) || tombstone.DeletedAt > existing)
                index[key] = tombstone.DeletedAt;
        }
        return index;
    }

    private static bool IsTombstoned(
        IReadOnlyDictionary<string, DateTimeOffset> index,
        string entityType,
        Guid id,
        DateTimeOffset version) =>
        IsTombstoned(index, entityType, id.ToString("N"), version);

    private static bool IsTombstoned(
        IReadOnlyDictionary<string, DateTimeOffset> index,
        string entityType,
        string key,
        DateTimeOffset version) =>
        index.TryGetValue(
            VersionKey(
                NormalizeTombstoneEntityType(entityType),
                NormalizeTombstoneKey(entityType, key)),
            out var deletedAt)
        && deletedAt >= version;

    private static string NormalizeTombstoneEntityType(string? entityType) =>
        (entityType ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeTombstoneKey(string? entityType, string? key)
    {
        var normalizedType = NormalizeTombstoneEntityType(entityType);
        var value = (key ?? string.Empty).Trim();
        if (normalizedType == "collection-item")
        {
            var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && Guid.TryParse(parts[0], out var collectionId)
                && Guid.TryParse(parts[1], out var bookId))
                return CompositeKey(collectionId, bookId);
            return value;
        }

        return Guid.TryParse(value, out var id)
            ? id.ToString("N")
            : value;
    }

    private static string BuildBookMatchKey(string? title, string? authors) =>
        $"{title?.Trim()}\u001f{authors?.Trim()}";

    private static bool CanUseBookMatchKey(string? title, string? authors) =>
        !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(authors);

    private async Task<int> ConsolidateLocalDuplicateBooksAsync(
        ICollection<string> pathsToDelete,
        CancellationToken cancellationToken)
    {
        var books = new List<LocalDuplicateBook>();
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SuppressDeletionTrackingAsync(connection, transaction, true, cancellationToken);

        using (var command = CreateCommand(connection, transaction,
            """
            SELECT b.Id, b.Title, b.Authors, b.CreatedAt, b.UpdatedAt, b.CoverPath,
                   (SELECT COUNT(*) FROM BookFiles f WHERE f.BookId = b.Id)
            FROM Books b;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                books.Add(new LocalDuplicateBook(
                    ParseGuid(reader.GetString(0), "Books.Id"),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseTimestamp(reader.GetString(3)),
                    ParseTimestamp(reader.GetString(4)),
                    NullableString(reader, 5),
                    reader.GetInt64(6)));
            }
        }

        var merged = 0;
        using var commandCache = new SqliteCommandCache(connection, transaction);
        foreach (var group in books
                     .Where(book => CanUseBookMatchKey(book.Title, book.Authors))
                     .GroupBy(book => BuildBookMatchKey(book.Title, book.Authors), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var canonical = group
                .OrderByDescending(book => book.FileCount)
                .ThenByDescending(book => book.UpdatedAt)
                .ThenBy(book => book.CreatedAt)
                .ThenBy(book => book.Id)
                .First();

            foreach (var duplicate in group.Where(book => book.Id != canonical.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MergeDuplicateBookAsync(
                    connection,
                    transaction,
                    canonical,
                    duplicate,
                    pathsToDelete,
                    commandCache,
                    cancellationToken);
                merged++;
            }
        }

        await SuppressDeletionTrackingAsync(connection, transaction, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return merged;
    }

    private async Task MergeDuplicateBookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalDuplicateBook canonical,
        LocalDuplicateBook duplicate,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var canonicalCoverPath = string.IsNullOrWhiteSpace(canonical.CoverPath)
            ? duplicate.CoverPath
            : canonical.CoverPath;

        var mergeMetadata = commandCache.Get(
            """
            UPDATE Books
            SET Series = CASE WHEN NULLIF(Series, '') IS NULL
                              THEN (SELECT Series FROM Books WHERE Id = $duplicate)
                              ELSE Series END,
                SeriesIndex = COALESCE(SeriesIndex,
                              (SELECT SeriesIndex FROM Books WHERE Id = $duplicate)),
                Description = CASE WHEN NULLIF(Description, '') IS NULL
                                   THEN (SELECT Description FROM Books WHERE Id = $duplicate)
                                   ELSE Description END,
                Publisher = CASE WHEN NULLIF(Publisher, '') IS NULL
                                 THEN (SELECT Publisher FROM Books WHERE Id = $duplicate)
                                 ELSE Publisher END,
                PublishDate = CASE WHEN NULLIF(PublishDate, '') IS NULL
                                   THEN (SELECT PublishDate FROM Books WHERE Id = $duplicate)
                                   ELSE PublishDate END,
                Isbn = CASE WHEN NULLIF(Isbn, '') IS NULL
                            THEN (SELECT Isbn FROM Books WHERE Id = $duplicate)
                            ELSE Isbn END,
                PageCount = CASE WHEN NULLIF(PageCount, '') IS NULL
                                 THEN (SELECT PageCount FROM Books WHERE Id = $duplicate)
                                 ELSE PageCount END,
                Binding = CASE WHEN NULLIF(Binding, '') IS NULL
                               THEN (SELECT Binding FROM Books WHERE Id = $duplicate)
                               ELSE Binding END,
                DoubanRating = COALESCE(DoubanRating,
                              (SELECT DoubanRating FROM Books WHERE Id = $duplicate)),
                DoubanRatingCount = COALESCE(DoubanRatingCount,
                              (SELECT DoubanRatingCount FROM Books WHERE Id = $duplicate)),
                Tags = CASE WHEN NULLIF(Tags, '') IS NULL
                            THEN COALESCE((SELECT NULLIF(Tags, '') FROM Books WHERE Id = $duplicate), '')
                            ELSE Tags END,
                Category = CASE WHEN NULLIF(Category, '') IS NULL
                                THEN COALESCE((SELECT NULLIF(Category, '') FROM Books WHERE Id = $duplicate), '')
                                ELSE Category END,
                IsFavorite = CASE WHEN IsFavorite <> 0
                                       OR COALESCE((SELECT IsFavorite FROM Books WHERE Id = $duplicate), 0) <> 0
                                  THEN 1 ELSE 0 END,
                ReadingStatus = CASE WHEN julianday((SELECT UpdatedAt FROM Books WHERE Id = $duplicate))
                                           > julianday(UpdatedAt)
                                     THEN (SELECT ReadingStatus FROM Books WHERE Id = $duplicate)
                                     ELSE ReadingStatus END,
                CoverPath = CASE WHEN NULLIF(CoverPath, '') IS NULL
                                 THEN (SELECT CoverPath FROM Books WHERE Id = $duplicate)
                                 ELSE CoverPath END,
                CreatedAt = CASE WHEN julianday((SELECT CreatedAt FROM Books WHERE Id = $duplicate))
                                       < julianday(CreatedAt)
                                THEN (SELECT CreatedAt FROM Books WHERE Id = $duplicate)
                                ELSE CreatedAt END,
                UpdatedAt = CASE WHEN julianday((SELECT UpdatedAt FROM Books WHERE Id = $duplicate))
                                       > julianday(UpdatedAt)
                                THEN (SELECT UpdatedAt FROM Books WHERE Id = $duplicate)
                                ELSE UpdatedAt END
            WHERE Id = $canonical;
            """);
        AddParameter(mergeMetadata, "$canonical", canonical.Id.ToString());
        AddParameter(mergeMetadata, "$duplicate", duplicate.Id.ToString());
        await mergeMetadata.ExecuteNonQueryAsync(cancellationToken);

        var copyCollections = commandCache.Get(
            """
            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT CollectionId, $canonical, AddedAt
            FROM BookCollectionItems
            WHERE BookId = $duplicate;
            """);
        AddParameter(copyCollections, "$canonical", canonical.Id.ToString());
        AddParameter(copyCollections, "$duplicate", duplicate.Id.ToString());
        await copyCollections.ExecuteNonQueryAsync(cancellationToken);

        var deleteCollections = commandCache.Get(
            "DELETE FROM BookCollectionItems WHERE BookId = $duplicate;");
        AddParameter(deleteCollections, "$duplicate", duplicate.Id.ToString());
        await deleteCollections.ExecuteNonQueryAsync(cancellationToken);

        var duplicateFiles = await ReadLocalDuplicateFilesAsync(
            connection,
            transaction,
            duplicate.Id,
            commandCache,
            cancellationToken);
        var canonicalFiles = await ReadLocalDuplicateFilesAsync(
            connection,
            transaction,
            canonical.Id,
            commandCache,
            cancellationToken);
        var canonicalFilesByHash = canonicalFiles
            .Where(file => IsSha256(file.Sha256))
            .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicateFile in duplicateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSha256(duplicateFile.Sha256)
                && canonicalFilesByHash.TryGetValue(duplicateFile.Sha256, out var canonicalFile))
            {
                await MergeDuplicateFileRowsAsync(
                    connection,
                    transaction,
                    duplicateFile.Id,
                    canonicalFile.Id,
                    canonical.Id,
                    commandCache,
                    cancellationToken);

                var deleteFile = commandCache.Get(
                    "DELETE FROM BookFiles WHERE Id = $fileId;");
                AddParameter(deleteFile, "$fileId", duplicateFile.Id.ToString());
                await deleteFile.ExecuteNonQueryAsync(cancellationToken);

                if (!string.Equals(
                        duplicateFile.RelativePath,
                        canonicalFile.RelativePath,
                        StringComparison.OrdinalIgnoreCase)
                    && !await IsRelativeDataPathReferencedAsync(
                        connection,
                        transaction,
                        duplicateFile.RelativePath,
                        cancellationToken))
                {
                    var absolute = ResolveDataPath(duplicateFile.RelativePath);
                    if (absolute is not null) pathsToDelete.Add(absolute);
                }
            }
            else
            {
                await UpdateFileBookReferencesAsync(
                    connection,
                    transaction,
                    duplicateFile.Id,
                    canonical.Id,
                    commandCache,
                    cancellationToken);

                var rebindFile = commandCache.Get(
                    "UPDATE BookFiles SET BookId = $canonical WHERE Id = $fileId;");
                AddParameter(rebindFile, "$canonical", canonical.Id.ToString());
                AddParameter(rebindFile, "$fileId", duplicateFile.Id.ToString());
                await rebindFile.ExecuteNonQueryAsync(cancellationToken);
                if (IsSha256(duplicateFile.Sha256))
                    canonicalFilesByHash[duplicateFile.Sha256] = duplicateFile;
            }
        }

        foreach (var statement in new[]
        {
            "UPDATE ReaderAnnotations SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderProgress SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderBookmarks SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderLayoutSettings SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderReadingStats SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderReadingSessions SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE BookContentChunks SET BookId = $canonical WHERE BookId = $duplicate;"
        })
        {
            var update = commandCache.Get(statement);
            AddParameter(update, "$canonical", canonical.Id.ToString());
            AddParameter(update, "$duplicate", duplicate.Id.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var deleteBook = commandCache.Get("DELETE FROM Books WHERE Id = $duplicate;");
        AddParameter(deleteBook, "$duplicate", duplicate.Id.ToString());
        await deleteBook.ExecuteNonQueryAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(duplicate.CoverPath)
            && !string.Equals(duplicate.CoverPath, canonicalCoverPath, StringComparison.OrdinalIgnoreCase)
            && !await IsRelativeDataPathReferencedAsync(
                connection,
                transaction,
                duplicate.CoverPath,
                cancellationToken))
        {
            var absolute = ResolveDataPath(duplicate.CoverPath);
            if (absolute is not null) pathsToDelete.Add(absolute);
        }
    }

    private static async Task<List<LocalDuplicateFile>> ReadLocalDuplicateFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid bookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var files = new List<LocalDuplicateFile>();
        var command = commandCache.Get(
            "SELECT Id, RelativePath, Sha256 FROM BookFiles WHERE BookId = $bookId;");
        AddParameter(command, "$bookId", bookId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new LocalDuplicateFile(
                ParseGuid(reader.GetString(0), "BookFiles.Id"),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return files;
    }

    private static async Task MergeDuplicateFileRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceFileId,
        Guid targetFileId,
        Guid canonicalBookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        await ReadingTimeSyncTracker.MergeFilesAsync(connection, transaction, sourceFileId, targetFileId, cancellationToken);
        var copyAnnotations = commandCache.Get(
            """
            INSERT OR IGNORE INTO ReaderAnnotations (
                Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt)
            SELECT Id, $canonical, $target, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations
            WHERE BookFileId = $source;
            """);
        AddParameter(copyAnnotations, "$canonical", canonicalBookId.ToString());
        AddParameter(copyAnnotations, "$target", targetFileId.ToString());
        AddParameter(copyAnnotations, "$source", sourceFileId.ToString());
        await copyAnnotations.ExecuteNonQueryAsync(cancellationToken);

        var deleteAnnotations = commandCache.Get(
            "DELETE FROM ReaderAnnotations WHERE BookFileId = $source;");
        AddParameter(deleteAnnotations, "$source", sourceFileId.ToString());
        await deleteAnnotations.ExecuteNonQueryAsync(cancellationToken);

        var copyBookmarks = commandCache.Get(
            """
            INSERT OR IGNORE INTO ReaderBookmarks (
                Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                ScrollPosition, FlowMode, Title, Quote, CreatedAt)
            SELECT Id, $canonical, $target, ChapterPath, Fragment, ChapterIndex,
                   ScrollPosition, FlowMode, Title, Quote, CreatedAt
            FROM ReaderBookmarks
            WHERE BookFileId = $source;
            """);
        AddParameter(copyBookmarks, "$canonical", canonicalBookId.ToString());
        AddParameter(copyBookmarks, "$target", targetFileId.ToString());
        AddParameter(copyBookmarks, "$source", sourceFileId.ToString());
        await copyBookmarks.ExecuteNonQueryAsync(cancellationToken);

        var deleteBookmarks = commandCache.Get(
            "DELETE FROM ReaderBookmarks WHERE BookFileId = $source;");
        AddParameter(deleteBookmarks, "$source", sourceFileId.ToString());
        await deleteBookmarks.ExecuteNonQueryAsync(cancellationToken);

        await MergeVersionedFileRowAsync(
            connection,
            transaction,
            "ReaderProgress",
            sourceFileId,
            targetFileId,
            canonicalBookId,
            commandCache,
            cancellationToken,
            "ChapterPath",
            "Fragment",
            "ChapterIndex",
            "ScrollPosition",
            "ProgressPercent",
            "FlowMode",
            "UpdatedAt");
        await MergeVersionedFileRowAsync(
            connection,
            transaction,
            "ReaderLayoutSettings",
            sourceFileId,
            targetFileId,
            canonicalBookId,
            commandCache,
            cancellationToken,
            "FontScale",
            "LineHeight",
            "MaxWidth",
            "BodyPadding",
            "FontFamily",
            "FlowMode",
            "VerticalWriting",
            "TwoPageMode",
            "ParagraphIndent",
            "UpdatedAt");
        await MergeVersionedFileRowAsync(
            connection,
            transaction,
            "ReaderReadingStats",
            sourceFileId,
            targetFileId,
            canonicalBookId,
            commandCache,
            cancellationToken,
            "CumulativeSeconds",
            "ProgressPercent",
            "CompletedChapters",
            "TotalChapters",
            "UpdatedAt");
        await ReadingTimeSyncTracker.RecordCurrentTotalAsync(connection, transaction, targetFileId, cancellationToken);

        var moveSessions = commandCache.Get(
            """
            UPDATE ReaderReadingSessions
            SET BookId = $canonical, BookFileId = $target
            WHERE BookFileId = $source;
            """);
        AddParameter(moveSessions, "$canonical", canonicalBookId.ToString());
        AddParameter(moveSessions, "$target", targetFileId.ToString());
        AddParameter(moveSessions, "$source", sourceFileId.ToString());
        await moveSessions.ExecuteNonQueryAsync(cancellationToken);

        // Parsed content is a rebuildable cache. Dropping the duplicate file's
        // chunks avoids violating the unique position index on the retained ID.
        var deleteChunks = commandCache.Get(
            "DELETE FROM BookContentChunks WHERE BookFileId = $source;");
        AddParameter(deleteChunks, "$source", sourceFileId.ToString());
        await deleteChunks.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MergeVersionedFileRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        Guid sourceFileId,
        Guid targetFileId,
        Guid canonicalBookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken,
        params string[] columns)
    {
        var columnList = string.Join(", ", columns);
        var assignments = string.Join(
            ", ",
            columns.Select(column =>
                $"{column} = (SELECT {column} FROM {table} WHERE BookFileId = $source)"));

        var copy = commandCache.Get(
            $"""
            INSERT OR IGNORE INTO {table} (BookFileId, BookId, {columnList})
            SELECT $target, $canonical, {columnList}
            FROM {table}
            WHERE BookFileId = $source;
            """);
        AddParameter(copy, "$target", targetFileId.ToString());
        AddParameter(copy, "$canonical", canonicalBookId.ToString());
        AddParameter(copy, "$source", sourceFileId.ToString());
        await copy.ExecuteNonQueryAsync(cancellationToken);

        var update = commandCache.Get(
            $"""
            UPDATE {table}
            SET BookId = $canonical, {assignments}
            WHERE BookFileId = $target
              AND EXISTS (SELECT 1 FROM {table} WHERE BookFileId = $source)
              AND julianday((SELECT UpdatedAt FROM {table} WHERE BookFileId = $source))
                  > julianday(UpdatedAt);
            """);
        AddParameter(update, "$target", targetFileId.ToString());
        AddParameter(update, "$canonical", canonicalBookId.ToString());
        AddParameter(update, "$source", sourceFileId.ToString());
        await update.ExecuteNonQueryAsync(cancellationToken);

        var delete = commandCache.Get(
            $"DELETE FROM {table} WHERE BookFileId = $source;");
        AddParameter(delete, "$source", sourceFileId.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateFileBookReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        Guid bookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        foreach (var statement in new[]
        {
            "UPDATE ReaderAnnotations SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderProgress SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderBookmarks SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderLayoutSettings SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderReadingStats SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderReadingSessions SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE BookContentChunks SET BookId = $bookId WHERE BookFileId = $fileId;"
        })
        {
            var command = commandCache.Get(statement);
            AddParameter(command, "$bookId", bookId.ToString());
            AddParameter(command, "$fileId", fileId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> IsRelativeDataPathReferencedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction,
            """
            SELECT EXISTS(
                SELECT 1 FROM BookFiles
                WHERE lower(replace(RelativePath, '\', '/')) = lower(replace($path, '\', '/'))
                UNION ALL
                SELECT 1 FROM Books
                WHERE lower(replace(CoverPath, '\', '/')) = lower(replace($path, '\', '/')));
            """);
        AddParameter(command, "$path", relativePath);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private async Task RemoveReferencedScheduledPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ICollection<string> paths,
        CancellationToken cancellationToken)
    {
        var candidates = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return;

        using var command = CreateCommand(connection, transaction,
            """
            SELECT EXISTS(
                SELECT 1 FROM BookFiles
                WHERE lower(replace(RelativePath, '\', '/')) = lower(replace($path, '\', '/'))
                UNION ALL
                SELECT 1 FROM Books
                WHERE lower(replace(CoverPath, '\', '/')) = lower(replace($path, '\', '/')));
            """);
        var unreferenced = new List<string>(candidates.Length);
        foreach (var path in candidates)
        {
            var relative = Path.GetRelativePath(_paths.Data, path);
            AddParameter(command, "$path", relative);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                unreferenced.Add(path);
        }
        paths.Clear();
        foreach (var path in unreferenced)
            paths.Add(path);
    }

    private void DeleteScheduledPaths(
        IEnumerable<string> paths,
        ICollection<string> warnings)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                TryDeleteEmptyParentDirectories(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(UiText.Get("无法删除重复书籍留下的本地文件：{0}", UiText.Localize(exception.Message)));
            }
        }
    }

    private void TryDeleteEmptyParentDirectories(string filePath)
    {
        var libraryRoot = Path.GetFullPath(_paths.Library)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var coversRoot = Path.GetFullPath(_paths.Covers)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrWhiteSpace(directory)
            && !string.Equals(directory, libraryRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(directory, coversRoot, StringComparison.OrdinalIgnoreCase))
        {
            var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!normalized.StartsWith(libraryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith(coversRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                break;
            try
            {
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
                else
                    break;
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
            directory = Path.GetDirectoryName(directory);
        }
    }

    private string GetAvailableFileName(
        string? sourceName,
        string? hash,
        string? format,
        Guid bookId,
        IReadOnlySet<string> plannedRelativePaths)
    {
        var name = SanitizeFileName(sourceName);
        if (name.Length == 0)
            name = $"{(IsSha256(hash) ? hash![..12] : Guid.NewGuid().ToString("N"))}.{format?.Trim().TrimStart('.') ?? "bin"}";

        var relative = Path.Combine("library", bookId.ToString("N"), name);
        var path = ResolveDataPath(relative);
        var planned = plannedRelativePaths.Contains(relative);
        if (path is null || (!File.Exists(path) && !planned)) return name;

        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var suffix = IsSha256(hash) ? hash![..12] : Guid.NewGuid().ToString("N")[..12];
        var candidate = $"{stem}-{suffix}{extension}";
        var attempt = 1;
        while (true)
        {
            var candidateRelative = Path.Combine("library", bookId.ToString("N"), candidate);
            var candidatePath = ResolveDataPath(candidateRelative);
            if (candidatePath is not null
                && !File.Exists(candidatePath)
                && !plannedRelativePaths.Contains(candidateRelative))
                return candidate;
            candidate = $"{stem}-{suffix}-{attempt++}{extension}";
        }
    }

    private static string SanitizeFileName(string? sourceName)
    {
        var name = Path.GetFileName((sourceName ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        if (name.Length == 0 || name is "." or "..") return string.Empty;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length <= 180 ? name : name[..180];
    }

    private static string NormalizeCoverExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).Trim().ToLowerInvariant();
        return extension is ".png" or ".webp" or ".gif" or ".jpeg" or ".jpg" ? extension : ".jpg";
    }

    private async Task<bool> EnsureLocalBlobAsync(
        IAmazonS3 client,
        S3SyncSettings settings,
        string hash,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(hash))
            throw new InvalidDataException("同步文件缺少有效的 SHA-256 校验值。");

        if (File.Exists(targetPath))
        {
            try
            {
                var existingHash = await GetCachedFileHashAsync(targetPath, cancellationToken);
                if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (IOException)
            {
            }
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("同步文件目标目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = targetPath + $".sync-{Guid.NewGuid():N}.part";
        try
        {
            using var response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = settings.Bucket, Key = BlobKey(settings, hash) },
                cancellationToken);
            if (settings.EncryptionKey.Length == 0)
            {
                await using var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    useAsync: true);
                await response.ResponseStream.CopyToAsync(output, cancellationToken);
            }
            else
            {
                await DecryptBlobToPathAsync(
                    response.ResponseStream,
                    temporaryPath,
                    settings.EncryptionKey,
                    cancellationToken);
            }

            var actualHash = await Hashing.Sha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载后的同步文件校验值不匹配。");
            File.Move(temporaryPath, targetPath, overwrite: true);
            progress?.Report(UiText.Get("已下载 {0}", Path.GetFileName(targetPath)));
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private const string UpsertBookSql =
        """
        INSERT INTO Books (
            Id, Title, Authors, Series, SeriesIndex, Description, Publisher, PublishDate,
            Isbn, PageCount, Binding, DoubanRating, DoubanRatingCount, Tags, Category,
            IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt)
        VALUES (
            $id, $title, $authors, $series, $seriesIndex, $description, $publisher, $publishDate,
            $isbn, $pageCount, $binding, $doubanRating, $doubanRatingCount, $tags, $category,
            $isFavorite, $readingStatus, NULL, $createdAt, $updatedAt)
        ON CONFLICT(Id) DO UPDATE SET
            Title = excluded.Title, Authors = excluded.Authors, Series = excluded.Series,
            SeriesIndex = excluded.SeriesIndex, Description = excluded.Description,
            Publisher = excluded.Publisher, PublishDate = excluded.PublishDate,
            Isbn = excluded.Isbn, PageCount = excluded.PageCount, Binding = excluded.Binding,
            DoubanRating = excluded.DoubanRating, DoubanRatingCount = excluded.DoubanRatingCount,
            Tags = excluded.Tags, Category = excluded.Category, IsFavorite = excluded.IsFavorite,
            ReadingStatus = excluded.ReadingStatus, UpdatedAt = excluded.UpdatedAt
        WHERE julianday(excluded.UpdatedAt) > julianday(Books.UpdatedAt);
        """;

    private static async Task<int> UpsertBookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        S3SyncBook book,
        Guid localBookId,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, UpsertBookSql);
        return await UpsertBookAsync(command, book, localBookId, cancellationToken);
    }

    private static async Task<int> UpsertBookAsync(
        SqliteCommand command,
        S3SyncBook book,
        Guid localBookId,
        CancellationToken cancellationToken)
    {
        AddParameter(command, "$id", localBookId.ToString());
        AddParameter(command, "$title", book.Title);
        AddParameter(command, "$authors", book.Authors);
        AddParameter(command, "$series", book.Series);
        AddParameter(command, "$seriesIndex", book.SeriesIndex);
        AddParameter(command, "$description", book.Description);
        AddParameter(command, "$publisher", book.Publisher);
        AddParameter(command, "$publishDate", book.PublishDate);
        AddParameter(command, "$isbn", book.Isbn);
        AddParameter(command, "$pageCount", book.PageCount);
        AddParameter(command, "$binding", book.Binding);
        AddParameter(command, "$doubanRating", book.DoubanRating);
        AddParameter(command, "$doubanRatingCount", book.DoubanRatingCount);
        AddParameter(command, "$tags", book.Tags);
        AddParameter(command, "$category", book.Category);
        AddParameter(command, "$isFavorite", book.IsFavorite ? 1 : 0);
        AddParameter(command, "$readingStatus", (int)book.ReadingStatus);
        AddParameter(command, "$createdAt", book.CreatedAt.ToString("O"));
        AddParameter(command, "$updatedAt", book.UpdatedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryMapReaderRow(
        Guid remoteBookId,
        Guid remoteFileId,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlySet<Guid> locallyKnownFileIds,
        out Guid localBookId,
        out Guid localFileId)
    {
        localBookId = default;
        localFileId = default;
        return bookMap.TryGetValue(remoteBookId, out localBookId)
            && fileMap.TryGetValue(remoteFileId, out localFileId)
            && locallyKnownFileIds.Contains(localFileId);
    }

    private static bool TryMapTombstoneId(
        string entityType,
        Guid remoteId,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> collectionMap,
        out Guid localId)
    {
        switch (entityType)
        {
            case "book":
                return bookMap.TryGetValue(remoteId, out localId);
            case "file":
                return fileMap.TryGetValue(remoteId, out localId);
            case "collection":
                return collectionMap.TryGetValue(remoteId, out localId);
            case "annotation":
            case "progress":
            case "bookmark":
            case "layout":
            case "stats":
                localId = remoteId;
                return true;
            default:
                localId = default;
                return false;
        }
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameterValue = value ?? DBNull.Value;
        var index = command.Parameters.IndexOf(name);
        if (index >= 0)
            command.Parameters[index].Value = parameterValue;
        else
            command.Parameters.AddWithValue(name, parameterValue);
    }

    private async Task<bool> ApplyTombstonesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        S3SyncSnapshot localSnapshot,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> collectionMap,
        IReadOnlyCollection<S3SyncBook> remoteBooks,
        IReadOnlyCollection<S3SyncBookFile> remoteFiles,
        IReadOnlyCollection<S3SyncCollection> remoteCollections,
        IReadOnlyCollection<S3SyncCollectionItem> remoteItems,
        IReadOnlyCollection<S3SyncAnnotation> remoteAnnotations,
        IReadOnlyCollection<S3SyncProgress> remoteProgress,
        IReadOnlyCollection<S3SyncBookmark> remoteBookmarks,
        IReadOnlyCollection<S3SyncLayout> remoteLayouts,
        IReadOnlyCollection<S3SyncReadingStats> remoteStats,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var liveVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var localBookIds = localSnapshot.Books.Select(item => item.Id).ToHashSet();
        var localFileIds = localSnapshot.Files.Select(item => item.Id).ToHashSet();
        var localCollectionIds = localSnapshot.Collections.Select(item => item.Id).ToHashSet();
        var localCollectionItemKeys = localSnapshot.CollectionItems
            .Select(item => CompositeKey(item.CollectionId, item.BookId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localAnnotationIds = localSnapshot.Annotations.Select(item => item.Id).ToHashSet();
        var localProgressIds = localSnapshot.Progress.Select(item => item.BookFileId).ToHashSet();
        var localBookmarkIds = localSnapshot.Bookmarks.Select(item => item.Id).ToHashSet();
        var localLayoutIds = localSnapshot.Layouts.Select(item => item.BookFileId).ToHashSet();
        var localStatsIds = localSnapshot.ReadingStats.Select(item => item.BookFileId).ToHashSet();
        void AddLive(string type, Guid id, DateTimeOffset version)
        {
            AddLiveKey(type, id.ToString("N"), version);
        }

        void AddLiveKey(string type, string id, DateTimeOffset version)
        {
            var key = VersionKey(type, id);
            if (!liveVersions.TryGetValue(key, out var existing) || version > existing)
                liveVersions[key] = version;
        }

        // A local re-create/update must win over a remote tombstone when its
        // version is newer. Remote rows alone are not enough because the
        // current local snapshot is uploaded only after this merge.
        foreach (var item in localSnapshot.Books)
            AddLive("book", item.Id, item.UpdatedAt);
        foreach (var item in localSnapshot.Files)
            AddLive("file", item.Id, item.ModifiedAt);
        foreach (var item in localSnapshot.Collections)
            AddLive("collection", item.Id, item.CreatedAt);
        foreach (var item in localSnapshot.CollectionItems)
            AddLiveKey("collection-item", CompositeKey(item.CollectionId, item.BookId), item.AddedAt);
        foreach (var item in localSnapshot.Annotations)
            AddLive("annotation", item.Id, item.UpdatedAt);
        foreach (var item in localSnapshot.Progress)
            AddLive("progress", item.BookFileId, item.UpdatedAt);
        foreach (var item in localSnapshot.Bookmarks)
            AddLive("bookmark", item.Id, item.CreatedAt);
        foreach (var item in localSnapshot.Layouts)
            AddLive("layout", item.BookFileId, item.UpdatedAt);
        foreach (var item in localSnapshot.ReadingStats)
            AddLive("stats", item.BookFileId, item.UpdatedAt);

        foreach (var item in remoteBooks)
            if (bookMap.TryGetValue(item.Id, out var localId)) AddLive("book", localId, item.UpdatedAt);
        foreach (var item in remoteFiles)
            if (fileMap.TryGetValue(item.Id, out var localId)) AddLive("file", localId, item.ModifiedAt);
        foreach (var item in remoteCollections)
            if (collectionMap.TryGetValue(item.Id, out var localId)) AddLive("collection", localId, item.CreatedAt);
        foreach (var item in remoteItems)
        {
            if (collectionMap.TryGetValue(item.CollectionId, out var collectionId)
                && bookMap.TryGetValue(item.BookId, out var bookId))
                AddLiveKey("collection-item", CompositeKey(collectionId, bookId), item.AddedAt);
        }
        foreach (var item in remoteAnnotations) AddLive("annotation", item.Id, item.UpdatedAt);
        foreach (var item in remoteProgress)
            if (fileMap.TryGetValue(item.BookFileId, out var localId)) AddLive("progress", localId, item.UpdatedAt);
        foreach (var item in remoteBookmarks) AddLive("bookmark", item.Id, item.CreatedAt);
        foreach (var item in remoteLayouts)
            if (fileMap.TryGetValue(item.BookFileId, out var localId)) AddLive("layout", localId, item.UpdatedAt);
        foreach (var item in remoteStats)
            if (fileMap.TryGetValue(item.BookFileId, out var localId)) AddLive("stats", localId, item.UpdatedAt);

        var deleteCollectionItemCommand = commandCache.Get(
            """
            DELETE FROM BookCollectionItems
            WHERE CollectionId = $collectionId AND BookId = $bookId
              AND julianday(AddedAt) <= julianday($deletedAt);
            """);
        var deleteCollectionItemsCommand = commandCache.Get(
            "DELETE FROM BookCollectionItems WHERE CollectionId = $id;");
        var deleteCollectionCommand = commandCache.Get(
            """
            DELETE FROM BookCollections
            WHERE Id = $id AND julianday(CreatedAt) <= julianday($deletedAt);
            """);

        var changed = false;
        foreach (var tombstone in tombstones
                     .Where(item => !string.IsNullOrWhiteSpace(item.EntityType) && !string.IsNullOrWhiteSpace(item.Key))
                     .OrderBy(item => item.DeletedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityType = NormalizeTombstoneEntityType(tombstone.EntityType);
            if (entityType == "collection-item")
            {
                var normalizedItemKey = NormalizeTombstoneKey(entityType, tombstone.Key);
                var parts = normalizedItemKey.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !Guid.TryParse(parts[0], out var remoteCollectionId)
                    || !Guid.TryParse(parts[1], out var remoteBookId)
                    || !collectionMap.TryGetValue(remoteCollectionId, out var localCollectionId)
                    || !bookMap.TryGetValue(remoteBookId, out var localBookId))
                    continue;
                var liveKey = VersionKey(entityType, CompositeKey(localCollectionId, localBookId));
                if (liveVersions.TryGetValue(liveKey, out var live) && live > tombstone.DeletedAt) continue;
                var localItemKey = CompositeKey(localCollectionId, localBookId);
                if (!localCollectionItemKeys.Contains(localItemKey))
                    continue;
                AddParameter(deleteCollectionItemCommand, "$collectionId", localCollectionId.ToString());
                AddParameter(deleteCollectionItemCommand, "$bookId", localBookId.ToString());
                AddParameter(deleteCollectionItemCommand, "$deletedAt", tombstone.DeletedAt.ToString("O"));
                changed |= await deleteCollectionItemCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                continue;
            }

            var normalizedTombstoneKey = NormalizeTombstoneKey(entityType, tombstone.Key);
            if (!Guid.TryParse(normalizedTombstoneKey, out var remoteId)) continue;
            if (!TryMapTombstoneId(
                    entityType,
                    remoteId,
                    bookMap,
                    fileMap,
                    collectionMap,
                    out var localId))
                continue;
            var localExists = entityType switch
            {
                "book" => localBookIds.Contains(localId),
                "file" => localFileIds.Contains(localId),
                "collection" => localCollectionIds.Contains(localId),
                "annotation" => localAnnotationIds.Contains(localId),
                "progress" => localProgressIds.Contains(localId),
                "bookmark" => localBookmarkIds.Contains(localId),
                "layout" => localLayoutIds.Contains(localId),
                "stats" => localStatsIds.Contains(localId),
                _ => false
            };
            if (!localExists)
                continue;
            var versionKey = VersionKey(entityType, localId);
            if (liveVersions.TryGetValue(versionKey, out var newerLiveVersion)
                && newerLiveVersion > tombstone.DeletedAt)
                continue;

            switch (entityType)
            {
                case "book":
                    changed |= await DeleteBookIfOlderAsync(
                        connection,
                        transaction,
                        localId,
                        tombstone.DeletedAt,
                        pathsToDelete,
                        commandCache,
                        cancellationToken);
                    break;
                case "file":
                    changed |= await DeleteFileRowsAsync(
                        connection,
                        transaction,
                        localId,
                        pathsToDelete,
                        commandCache,
                        cancellationToken);
                    break;
                case "collection":
                {
                    AddParameter(deleteCollectionItemsCommand, "$id", localId.ToString());
                    changed |= await deleteCollectionItemsCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                    AddParameter(deleteCollectionCommand, "$id", localId.ToString());
                    AddParameter(deleteCollectionCommand, "$deletedAt", tombstone.DeletedAt.ToString("O"));
                    changed |= await deleteCollectionCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                    break;
                }
                case "annotation":
                    changed |= await DeleteVersionedRowAsync(
                        connection,
                        transaction,
                        "ReaderAnnotations",
                        "Id",
                        localId,
                        "UpdatedAt",
                        tombstone.DeletedAt,
                        commandCache,
                        cancellationToken);
                    break;
                case "progress":
                    changed |= await DeleteVersionedRowAsync(
                        connection,
                        transaction,
                        "ReaderProgress",
                        "BookFileId",
                        localId,
                        "UpdatedAt",
                        tombstone.DeletedAt,
                        commandCache,
                        cancellationToken);
                    break;
                case "bookmark":
                    changed |= await DeleteVersionedRowAsync(
                        connection,
                        transaction,
                        "ReaderBookmarks",
                        "Id",
                        localId,
                        "CreatedAt",
                        tombstone.DeletedAt,
                        commandCache,
                        cancellationToken);
                    break;
                case "layout":
                    changed |= await DeleteVersionedRowAsync(
                        connection,
                        transaction,
                        "ReaderLayoutSettings",
                        "BookFileId",
                        localId,
                        "UpdatedAt",
                        tombstone.DeletedAt,
                        commandCache,
                        cancellationToken);
                    break;
                case "stats":
                    changed |= await DeleteVersionedRowAsync(
                        connection,
                        transaction,
                        "ReaderReadingStats",
                        "BookFileId",
                        localId,
                        "UpdatedAt",
                        tombstone.DeletedAt,
                        commandCache,
                        cancellationToken);
                    break;
            }
        }

        return changed;
    }

    private async Task<bool> DeleteBookIfOlderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid bookId,
        DateTimeOffset deletedAt,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var inspect = commandCache.Get("SELECT UpdatedAt FROM Books WHERE Id = $id LIMIT 1;");
        AddParameter(inspect, "$id", bookId.ToString());
        var updatedValue = await inspect.ExecuteScalarAsync(cancellationToken);
        if (updatedValue is not string updatedText) return false;
        if (ParseTimestamp(updatedText) > deletedAt) return false;

        var cover = commandCache.Get("SELECT CoverPath FROM Books WHERE Id = $id LIMIT 1;");
        AddParameter(cover, "$id", bookId.ToString());
        if (await cover.ExecuteScalarAsync(cancellationToken) is string coverPath)
        {
            var absolute = ResolveDataPath(coverPath);
            if (absolute is not null) pathsToDelete.Add(absolute);
        }

        var paths = commandCache.Get("SELECT RelativePath FROM BookFiles WHERE BookId = $bookId;");
        AddParameter(paths, "$bookId", bookId.ToString());
        await using (var reader = await paths.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var absolute = ResolveDataPath(reader.GetString(0));
                if (absolute is not null) pathsToDelete.Add(absolute);
            }
        }

        var changed = false;
        foreach (var statement in new[]
        {
            "DELETE FROM ReaderAnnotations WHERE BookId = $bookId;",
            "DELETE FROM ReaderProgress WHERE BookId = $bookId;",
            "DELETE FROM ReaderBookmarks WHERE BookId = $bookId;",
            "DELETE FROM ReaderLayoutSettings WHERE BookId = $bookId;",
            "DELETE FROM ReaderReadingStats WHERE BookId = $bookId;",
            "DELETE FROM ReaderReadingSessions WHERE BookId = $bookId;",
            "DELETE FROM BookContentChunks WHERE BookId = $bookId;",
            "DELETE FROM BookCollectionItems WHERE BookId = $bookId;",
            "DELETE FROM BookFiles WHERE BookId = $bookId;",
            "DELETE FROM Books WHERE Id = $bookId;"
        })
        {
            var command = commandCache.Get(statement);
            AddParameter(command, "$bookId", bookId.ToString());
            changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        return changed;
    }

    private async Task<bool> DeleteFileRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var pathCommand = commandCache.Get("SELECT RelativePath FROM BookFiles WHERE Id = $id LIMIT 1;");
        AddParameter(pathCommand, "$id", fileId.ToString());
        var relativePath = await pathCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (relativePath is not null)
        {
            var absolute = ResolveDataPath(relativePath);
            if (absolute is not null) pathsToDelete.Add(absolute);
        }

        var changed = false;
        foreach (var statement in new[]
        {
            "DELETE FROM ReaderAnnotations WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderProgress WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderBookmarks WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderLayoutSettings WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderReadingStats WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderReadingSessions WHERE BookFileId = $fileId;",
            "DELETE FROM BookContentChunks WHERE BookFileId = $fileId;",
            "DELETE FROM BookFiles WHERE Id = $fileId;"
        })
        {
            var command = commandCache.Get(statement);
            AddParameter(command, "$fileId", fileId.ToString());
            changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        return changed;
    }

    private static async Task<bool> DeleteVersionedRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        Guid id,
        string versionColumn,
        DateTimeOffset deletedAt,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var command = commandCache.Get(
            $"DELETE FROM {table} WHERE {idColumn} = $id AND julianday({versionColumn}) <= julianday($deletedAt);");
        AddParameter(command, "$id", id.ToString());
        AddParameter(command, "$deletedAt", deletedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static string CompositeKey(Guid left, Guid right) => $"{left:N}|{right:N}";

    private static string VersionKey(string type, Guid id) => VersionKey(type, id.ToString("N"));

    private static string VersionKey(string type, string id) =>
        $"{NormalizeTombstoneEntityType(type)}:{NormalizeTombstoneKey(type, id)}";

    private async Task<bool> ApplyRemoteSettingsAsync(
        S3SyncSettingsSnapshot? localSettings,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        CancellationToken cancellationToken)
    {
        var candidates = remoteSnapshots
            .Select(snapshot => snapshot.Settings)
            .OfType<S3SyncSettingsSnapshot>()
            .ToArray();
        if (candidates.Length == 0) return false;
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        localSettings = await CaptureSettingsUnderLockAsync(cancellationToken);
        S3SyncSettingsSnapshot Latest(Func<S3SyncSettingsSnapshot, DateTimeOffset?> version) =>
            candidates.MaxBy(settings => version(settings) ?? settings.UpdatedAt)!;
        var applied = false;

        // Compare each settings file independently. A cancelled/failed write
        // can then resume without an already-applied file masking the others.
        var remoteSettings = Latest(settings => settings.AppUpdatedAt);
        var remoteUpdatedAt = remoteSettings.AppUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.AppUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentApp = AppSettings.Normalize(await _appSettingsStore.LoadAsync(cancellationToken));
            var remoteApp = remoteSettings.App ?? new S3SyncAppSettings();
            var mergedApp = AppSettings.Normalize(currentApp with
            {
                UiLanguage = remoteApp.UiLanguage,
                PreferredOpenFormat = remoteApp.PreferredOpenFormat,
                AutoBackupEnabled = remoteApp.AutoBackupEnabled,
                AutoGenerateEpubAndAzw3OnImport = remoteApp.AutoGenerateEpubAndAzw3OnImport,
                CollectionsMutuallyExclusive = remoteApp.CollectionsMutuallyExclusive,
                AutoBackupRetention = remoteApp.AutoBackupRetention,
                AiEnabled = remoteApp.AiEnabled,
                NetworkEnabled = remoteApp.NetworkEnabled,
                AutoUpdateCheckEnabled = remoteApp.AutoUpdateCheckEnabled,
                AutoDoubanMatchOnImport = remoteApp.AutoDoubanMatchOnImport,
                CompareKindleLibraryEnabled = remoteApp.CompareKindleLibraryEnabled,
                GridGalleryDisplay = remoteApp.GridGalleryDisplay,
                ReadingMaterialsCollapsedByDefault = remoteApp.ReadingMaterialsCollapsedByDefault,
                DefaultReaderLayout = remoteApp.DefaultReaderLayout ?? new ReaderLayoutSettings()
            });
            await _appSettingsStore.SaveUnderLockAsync(mergedApp, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }

        remoteSettings = Latest(settings => settings.AiUpdatedAt);
        remoteUpdatedAt = remoteSettings.AiUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.AiUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentAi = await _aiSettingsStore.LoadAsync(cancellationToken);
            var remoteAi = remoteSettings.Ai ?? new S3SyncAiSettings();
            await _aiSettingsStore.SaveUnderLockAsync(new AiConnectionSettings
            {
                Provider = string.IsNullOrWhiteSpace(remoteAi.Provider) ? currentAi.Provider : remoteAi.Provider,
                BaseUrl = string.IsNullOrWhiteSpace(remoteAi.BaseUrl) ? currentAi.BaseUrl : remoteAi.BaseUrl,
                Model = string.IsNullOrWhiteSpace(remoteAi.Model) ? currentAi.Model : remoteAi.Model,
                ApiKey = currentAi.ApiKey
            }, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }

        remoteSettings = Latest(settings => settings.KindleEmailUpdatedAt);
        remoteUpdatedAt = remoteSettings.KindleEmailUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.KindleEmailUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
            var remoteEmail = remoteSettings.KindleEmail ?? new S3SyncKindleEmailSettings();
            await _kindleEmailSettingsStore.SaveUnderLockAsync(new KindleEmailSettings
            {
                KindleEmailAddress = remoteEmail.KindleEmailAddress,
                SenderEmailAddress = remoteEmail.SenderEmailAddress,
                SmtpHost = remoteEmail.SmtpHost,
                SmtpPort = remoteEmail.SmtpPort,
                SmtpUsername = remoteEmail.SmtpUsername,
                SmtpPassword = currentEmail.SmtpPassword,
                EnableSsl = remoteEmail.EnableSsl
            }, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }

        remoteSettings = Latest(settings => settings.ZLibraryUpdatedAt);
        remoteUpdatedAt = remoteSettings.ZLibraryUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.ZLibraryUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentZLibrary = await _zLibrarySettingsStore.LoadAsync(cancellationToken);
            var remoteZLibrary = remoteSettings.ZLibrary ?? new S3SyncZLibrarySettings();
            await _zLibrarySettingsStore.SaveUnderLockAsync(new ZLibrarySettings
            {
                Email = remoteZLibrary.Email,
                BaseUrl = string.IsNullOrWhiteSpace(remoteZLibrary.BaseUrl)
                    ? currentZLibrary.BaseUrl
                    : remoteZLibrary.BaseUrl,
                Password = currentZLibrary.Password
            }, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }
        return applied;
    }

    private static List<S3SyncTombstone> DetectDeletedEntities(
        S3SyncSnapshot? previous,
        S3SyncSnapshot current)
        => DetectDeletedEntitiesCore(previous, current, null);

    private static Dictionary<string, DateTimeOffset> GetSnapshotEntityVersions(S3SyncSnapshot snapshot)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        void Add(string type, string key, DateTimeOffset version) => result[VersionKey(type, key)] = version;
        foreach (var row in snapshot.Books) Add("book", row.Id.ToString("N"), row.UpdatedAt);
        foreach (var row in snapshot.Files) Add("file", row.Id.ToString("N"), row.ModifiedAt);
        foreach (var row in snapshot.Collections) Add("collection", row.Id.ToString("N"), row.CreatedAt);
        foreach (var row in snapshot.CollectionItems) Add("collection-item", CompositeKey(row.CollectionId, row.BookId), row.AddedAt);
        foreach (var row in snapshot.Annotations) Add("annotation", row.Id.ToString("N"), row.UpdatedAt);
        foreach (var row in snapshot.Progress) Add("progress", row.BookFileId.ToString("N"), row.UpdatedAt);
        foreach (var row in snapshot.Bookmarks) Add("bookmark", row.Id.ToString("N"), row.CreatedAt);
        foreach (var row in snapshot.Layouts) Add("layout", row.BookFileId.ToString("N"), row.UpdatedAt);
        foreach (var row in snapshot.ReadingStats) Add("stats", row.BookFileId.ToString("N"), row.UpdatedAt);
        return result;
    }

    private static List<S3SyncTombstone> GetRecordedTombstones(
        IReadOnlyDictionary<string, DateTimeOffset> recorded,
        S3SyncSnapshot current,
        S3SyncSnapshot? previous = null)
    {
        var live = GetSnapshotEntityVersions(current);
        var previousVersions = previous is null ? null : GetSnapshotEntityVersions(previous);
        var result = new List<S3SyncTombstone>();
        foreach (var (key, deletedAt) in recorded)
        {
            if (live.ContainsKey(key)
                || (previousVersions?.TryGetValue(key, out var version) == true && version > deletedAt))
                continue;
            var separator = key.IndexOf(':');
            if (separator <= 0) continue;
            result.Add(new S3SyncTombstone { EntityType = key[..separator], Key = key[(separator + 1)..], DeletedAt = deletedAt });
        }
        return result;
    }

    private static List<S3SyncTombstone> DetectDeletedEntitiesWithRecordedTimes(
        S3SyncSnapshot? previous,
        S3SyncSnapshot current,
        IReadOnlyDictionary<string, DateTimeOffset> recordedDeletionTimes)
        => DetectDeletedEntitiesCore(previous, current, recordedDeletionTimes);

    private static List<S3SyncTombstone> DetectDeletedEntitiesCore(
        S3SyncSnapshot? previous,
        S3SyncSnapshot current,
        IReadOnlyDictionary<string, DateTimeOffset>? recordedDeletionTimes)
    {
        if (previous is null) return [];
        var result = new List<S3SyncTombstone>();

        AddMissing(
            "book",
            previous.Books.Select(item => (item.Id.ToString("N"), item.UpdatedAt)),
            current.Books.Select(item => item.Id.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "file",
            previous.Files.Select(item => (item.Id.ToString("N"), item.ModifiedAt)),
            current.Files.Select(item => item.Id.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "collection",
            previous.Collections.Select(item => (item.Id.ToString("N"), item.CreatedAt)),
            current.Collections.Select(item => item.Id.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "collection-item",
            previous.CollectionItems.Select(item => (CompositeKey(item.CollectionId, item.BookId), item.AddedAt)),
            current.CollectionItems.Select(item => CompositeKey(item.CollectionId, item.BookId)),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "annotation",
            previous.Annotations.Select(item => (item.Id.ToString("N"), item.UpdatedAt)),
            current.Annotations.Select(item => item.Id.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "progress",
            previous.Progress.Select(item => (item.BookFileId.ToString("N"), item.UpdatedAt)),
            current.Progress.Select(item => item.BookFileId.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "bookmark",
            previous.Bookmarks.Select(item => (item.Id.ToString("N"), item.CreatedAt)),
            current.Bookmarks.Select(item => item.Id.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "layout",
            previous.Layouts.Select(item => (item.BookFileId.ToString("N"), item.UpdatedAt)),
            current.Layouts.Select(item => item.BookFileId.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        AddMissing(
            "stats",
            previous.ReadingStats.Select(item => (item.BookFileId.ToString("N"), item.UpdatedAt)),
            current.ReadingStats.Select(item => item.BookFileId.ToString("N")),
            result,
            previous.CreatedAt,
            current.CreatedAt,
            recordedDeletionTimes);
        return result;
    }

    private static void AddMissing(
        string entityType,
        IEnumerable<(string Key, DateTimeOffset DeletedAt)> previous,
        IEnumerable<string> current,
        ICollection<S3SyncTombstone> output,
        DateTimeOffset fallbackDeletedAt,
        DateTimeOffset observationTime,
        IReadOnlyDictionary<string, DateTimeOffset>? recordedDeletionTimes)
    {
        var currentKeys = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous.Where(item => !currentKeys.Contains(item.Key)))
        {
            var recordedDeletionTime = DateTimeOffset.MinValue;
            var hasRecordedDeletionTime = recordedDeletionTimes is not null
                && recordedDeletionTimes.TryGetValue(
                    VersionKey(entityType, item.Key),
                    out recordedDeletionTime)
                && recordedDeletionTime != DateTimeOffset.MinValue;
            var deletedAt = hasRecordedDeletionTime
                ? recordedDeletionTime
                : item.DeletedAt == DateTimeOffset.MinValue
                    ? fallbackDeletedAt
                    : item.DeletedAt;

            if (deletedAt == DateTimeOffset.MinValue)
                deletedAt = observationTime;
            output.Add(new S3SyncTombstone
            {
                EntityType = NormalizeTombstoneEntityType(entityType),
                Key = item.Key,
                DeletedAt = deletedAt
            });
        }
    }

    private static void EnsureDeletionVolumeIsSafe(
        S3SyncSnapshot? previous,
        IReadOnlyCollection<S3SyncTombstone> detectedDeletions,
        string? confirmedDeletionFingerprint = null)
    {
        if (previous is null) return;
        var knownKeys = GetSnapshotEntityVersions(previous).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingKeys = detectedDeletions.Select(item => VersionKey(item.EntityType, item.Key))
            .Where(knownKeys.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", pendingKeys))));
        if (string.Equals(confirmedDeletionFingerprint, fingerprint, StringComparison.Ordinal)) return;
        var descriptions = new List<string>();
        var counts = new[]
        {
            (Name: "书籍", Type: "book", Total: previous.Books.Count),
            (Name: "文件", Type: "file", Total: previous.Files.Count),
            (Name: "收藏夹", Type: "collection", Total: previous.Collections.Count),
            (Name: "收藏夹条目", Type: "collection-item", Total: previous.CollectionItems.Count),
            (Name: "批注", Type: "annotation", Total: previous.Annotations.Count),
            (Name: "阅读进度", Type: "progress", Total: previous.Progress.Count),
            (Name: "书签", Type: "bookmark", Total: previous.Bookmarks.Count),
            (Name: "阅读布局", Type: "layout", Total: previous.Layouts.Count),
            (Name: "阅读统计", Type: "stats", Total: previous.ReadingStats.Count)
        };

        foreach (var count in counts)
        {
            if (count.Total < LargeDeletionMinimumEntities) continue;
            var deleted = detectedDeletions
                .Where(item => string.Equals(
                    NormalizeTombstoneEntityType(item.EntityType),
                    count.Type,
                    StringComparison.OrdinalIgnoreCase)
                    && knownKeys.Contains(VersionKey(item.EntityType, item.Key)))
                .Select(item => NormalizeTombstoneKey(count.Type, item.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (deleted <= count.Total * LargeDeletionRatio) continue;

            descriptions.Add(UiText.Get("{0}：删除 {1}/{2} 条", UiText.Get(count.Name), deleted, count.Total));
        }
        if (descriptions.Count > 0)
            throw new S3SyncDeletionConfirmationRequiredException(
                UiText.Get("本次同步将传播大量删除（{0}）。请确认这些删除由你主动执行。", string.Join("; ", descriptions)), fingerprint);
    }

    private static List<S3SyncTombstone> MergeTombstones(
        IEnumerable<S3SyncTombstone> first,
        IEnumerable<S3SyncTombstone> second,
        DateTimeOffset? now = null)
    {
        var merged = new Dictionary<string, S3SyncTombstone>(StringComparer.OrdinalIgnoreCase);
        foreach (var tombstone in first.Concat(second))
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityType)
                || string.IsNullOrWhiteSpace(tombstone.Key))
                continue;
            var entityType = NormalizeTombstoneEntityType(tombstone.EntityType);
            var normalizedKey = NormalizeTombstoneKey(entityType, tombstone.Key);
            if (entityType.Length == 0 || normalizedKey.Length == 0)
                continue;
            var key = VersionKey(entityType, normalizedKey);
            if (!merged.TryGetValue(key, out var existing) || tombstone.DeletedAt > existing.DeletedAt)
                merged[key] = new S3SyncTombstone
                {
                    EntityType = entityType,
                    Key = normalizedKey,
                    DeletedAt = tombstone.DeletedAt
                };
        }
        return merged.Values.OrderBy(item => item.DeletedAt).ToList();
    }

    private sealed class LocalDatabaseIdentity
    {
        public Dictionary<Guid, LocalBookIdentity> BooksById { get; } = [];
        public Dictionary<Guid, LocalFileIdentity> FilesById { get; } = [];
        public Dictionary<string, LocalFileIdentity> FilesByHash { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, LocalCollectionIdentity> CollectionsById { get; } = [];
    }

    private sealed record LocalBookIdentity(
        Guid Id,
        string Title,
        string Authors,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? CoverPath);

    private sealed record LocalFileIdentity(
        Guid Id,
        Guid BookId,
        string RelativePath,
        long Size,
        string Sha256);

    private sealed record LocalCollectionIdentity(Guid Id, string Name, DateTimeOffset CreatedAt);

    private sealed record LocalDuplicateBook(
        Guid Id,
        string Title,
        string Authors,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? CoverPath,
        long FileCount);

    private sealed record LocalDuplicateFile(Guid Id, string RelativePath, string Sha256);

    private sealed record PreparedSyncFile(
        S3SyncBookFile Source,
        Guid LocalBookId,
        Guid LocalFileId,
        string RelativePath);

    private sealed record DatabaseMergeResult(
        int BooksAdded,
        int FilesDownloaded,
        int AnnotationsApplied,
        bool Changed,
        string? Warning)
    {
        public bool IsPartial { get; init; }
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private sealed class SqliteCommandCache : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly Dictionary<string, SqliteCommand> _commands =
            new(StringComparer.Ordinal);

        public SqliteCommandCache(SqliteConnection connection, SqliteTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public SqliteCommand Get(string commandText)
        {
            if (_commands.TryGetValue(commandText, out var command))
                return command;
            command = CreateCommand(_connection, _transaction, commandText);
            _commands.Add(commandText, command);
            return command;
        }

        public void Dispose()
        {
            foreach (var command in _commands.Values)
                command.Dispose();
            _commands.Clear();
        }
    }

    private static Guid ParseGuid(string value, string field) =>
        Guid.TryParse(value, out var result)
            ? result
            : throw new InvalidDataException(UiText.Get("本地数据库字段 {0} 不是有效的 GUID。", field));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : DateTimeOffset.MinValue;

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
