using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

internal static class BookFileSyncVersion
{
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(BookFiles);";
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }
        if (columns.Count == 0) return;

        using var command = connection.CreateCommand();
        if (!columns.Contains("SyncUpdatedAt"))
        {
            command.CommandText = "ALTER TABLE BookFiles ADD COLUMN SyncUpdatedAt TEXT NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Seed old rows once, then keep file versions independent of book
        // titles/covers. Explicit inserts/restores supply their own timestamp.
        command.CommandText = """
            UPDATE BookFiles SET SyncUpdatedAt = COALESCE(
                (SELECT UpdatedAt FROM Books WHERE Id = BookFiles.BookId),
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            WHERE SyncUpdatedAt IS NULL;
            CREATE TRIGGER IF NOT EXISTS BookFiles_SyncVersion_Insert
            AFTER INSERT ON BookFiles WHEN NEW.SyncUpdatedAt IS NULL
            BEGIN
                UPDATE BookFiles SET SyncUpdatedAt = COALESCE(
                    (SELECT UpdatedAt FROM Books WHERE Id = NEW.BookId),
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                WHERE Id = NEW.Id;
            END;
            CREATE TRIGGER IF NOT EXISTS BookFiles_SyncVersion_ContentUpdate
            AFTER UPDATE OF Sha256, Size, Format ON BookFiles
            WHEN NEW.Sha256 IS NOT OLD.Sha256 OR NEW.Size IS NOT OLD.Size OR NEW.Format IS NOT OLD.Format
            BEGIN
                UPDATE BookFiles SET SyncUpdatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE Id = NEW.Id;
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
