using System.Text.Json;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

// Reading history outlives a library entry and its recycle-bin contents.
// Keep just the identity needed to name and merge those records.
internal static class ReaderReadingHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var schema = Command(connection, transaction, "SELECT name FROM sqlite_master WHERE type = 'table';"))
        await using (var reader = await schema.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        if (!tables.Contains("ReaderReadingStats")) return;

        using (var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS ReaderReadingHistory (
                BookFileId TEXT PRIMARY KEY,
                BookId TEXT NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Sha256 TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_ReaderReadingHistory_Sha256
                ON ReaderReadingHistory(Sha256 COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_ReaderReadingStats_BookId ON ReaderReadingStats(BookId);
            CREATE TRIGGER IF NOT EXISTS ReaderReadingHistory_Delete
            AFTER DELETE ON ReaderReadingStats
            BEGIN
                DELETE FROM ReaderReadingHistory WHERE BookFileId = OLD.BookFileId;
            END;
            """))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        if (tables.Contains("Books") && tables.Contains("BookFiles"))
        {
            using var capture = Command(connection, transaction, """
                UPDATE ReaderReadingStats
                SET BookId = (SELECT BookId FROM BookFiles WHERE Id = ReaderReadingStats.BookFileId)
                WHERE EXISTS (
                    SELECT 1 FROM BookFiles f JOIN Books b ON b.Id = f.BookId
                    WHERE f.Id = ReaderReadingStats.BookFileId AND f.BookId <> ReaderReadingStats.BookId);

                INSERT INTO ReaderReadingHistory (BookFileId, BookId, Title, Sha256)
                    SELECT s.BookFileId, s.BookId, COALESCE(b.Title, ''), COALESCE(f.Sha256, '')
                    FROM ReaderReadingStats s
                    LEFT JOIN Books b ON b.Id = s.BookId
                    LEFT JOIN BookFiles f ON f.Id = s.BookFileId
                    WHERE 1
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId = excluded.BookId,
                    Title = COALESCE(NULLIF(excluded.Title, ''), ReaderReadingHistory.Title),
                    Sha256 = COALESCE(NULLIF(excluded.Sha256, ''), ReaderReadingHistory.Sha256)
                WHERE ReaderReadingHistory.BookId <> excluded.BookId
                    OR (excluded.Title <> '' AND ReaderReadingHistory.Title <> excluded.Title)
                    OR (excluded.Sha256 <> '' AND ReaderReadingHistory.Sha256 <> excluded.Sha256);

                CREATE TRIGGER IF NOT EXISTS ReaderReadingHistory_Insert
                AFTER INSERT ON ReaderReadingStats
                BEGIN
                    INSERT INTO ReaderReadingHistory (BookFileId, BookId, Title, Sha256)
                    VALUES (NEW.BookFileId, NEW.BookId,
                        COALESCE((SELECT Title FROM Books WHERE Id = NEW.BookId), ''),
                        COALESCE((SELECT Sha256 FROM BookFiles WHERE Id = NEW.BookFileId), ''))
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId,
                        Title = COALESCE(NULLIF(excluded.Title, ''), ReaderReadingHistory.Title),
                        Sha256 = COALESCE(NULLIF(excluded.Sha256, ''), ReaderReadingHistory.Sha256);
                END;

                CREATE TRIGGER IF NOT EXISTS ReaderReadingHistory_Rebind
                AFTER UPDATE OF BookId, BookFileId ON ReaderReadingStats
                BEGIN
                    UPDATE ReaderReadingHistory
                    SET BookId = NEW.BookId,
                        Title = COALESCE((SELECT Title FROM Books WHERE Id = NEW.BookId), Title),
                        Sha256 = COALESCE((SELECT Sha256 FROM BookFiles WHERE Id = NEW.BookFileId), Sha256)
                    WHERE BookFileId = NEW.BookFileId;
                END;

                CREATE TRIGGER IF NOT EXISTS ReaderReadingHistory_Rename
                AFTER UPDATE OF Title ON Books
                BEGIN
                    UPDATE ReaderReadingHistory SET Title = NEW.Title WHERE BookId = NEW.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS ReaderReadingHistory_ArchiveBook
                BEFORE DELETE ON Books
                BEGIN
                    UPDATE ReaderReadingHistory SET Title = OLD.Title WHERE BookId = OLD.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS ReaderReadingHistory_ArchiveFile
                BEFORE DELETE ON BookFiles
                BEGIN
                    UPDATE ReaderReadingHistory SET Sha256 = OLD.Sha256 WHERE BookFileId = OLD.Id;
                END;
                """);
            await capture.ExecuteNonQueryAsync(cancellationToken);
        }

        if (tables.Contains("LibraryTrash"))
        {
            var entries = new List<(string Title, string? Book, string? File)>();
            using (var read = Command(connection, transaction, """
                SELECT t.Title, t.BookJson, t.FileJson FROM LibraryTrash t
                WHERE EXISTS (
                    SELECT 1 FROM ReaderReadingStats s
                    LEFT JOIN ReaderReadingHistory h ON h.BookFileId = s.BookFileId
                    WHERE s.BookId = t.BookId AND (COALESCE(h.Title, '') = '' OR COALESCE(h.Sha256, '') = ''))
                ORDER BY t.DeletedAt DESC;
                """))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    entries.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));

            foreach (var entry in entries)
            {
                try
                {
                    var files = entry.Book is not null
                        ? JsonSerializer.Deserialize<Book>(entry.Book, JsonOptions)?.Files ?? []
                        : entry.File is not null && JsonSerializer.Deserialize<BookFile>(entry.File, JsonOptions) is { } trashedFile
                            ? [trashedFile] : [];
                    foreach (var file in files)
                        await RememberAsync(connection, transaction, file.BookId, file.Id, entry.Title, file.Sha256, cancellationToken);
                }
                catch (JsonException)
                {
                    // A damaged trash entry must not prevent opening the library.
                    // Its reading time remains intact even if its title is unavailable.
                }
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<bool> RememberAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        Guid bookId, Guid fileId, string? title, string? sha256, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO ReaderReadingHistory (BookFileId, BookId, Title, Sha256)
                SELECT $file, $book, $title, $hash
                WHERE EXISTS (SELECT 1 FROM ReaderReadingStats WHERE BookFileId = $file)
            ON CONFLICT(BookFileId) DO UPDATE SET
                BookId = excluded.BookId,
                Title = COALESCE(NULLIF(ReaderReadingHistory.Title, ''), excluded.Title),
                Sha256 = COALESCE(NULLIF(ReaderReadingHistory.Sha256, ''), excluded.Sha256)
            WHERE ReaderReadingHistory.BookId <> excluded.BookId
                OR (ReaderReadingHistory.Title = '' AND excluded.Title <> '')
                OR (ReaderReadingHistory.Sha256 = '' AND excluded.Sha256 <> '');
            """);
        command.Parameters.AddWithValue("$file", fileId.ToString());
        command.Parameters.AddWithValue("$book", bookId.ToString());
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$hash", sha256 ?? string.Empty);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static async Task MergeFilesAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        Guid sourceFileId, Guid targetFileId, Guid bookId, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO ReaderReadingHistory (BookFileId, BookId, Title, Sha256)
                SELECT $target, $book, Title, Sha256 FROM ReaderReadingHistory WHERE BookFileId = $source
            ON CONFLICT(BookFileId) DO UPDATE SET
                BookId = excluded.BookId,
                Title = COALESCE(NULLIF(ReaderReadingHistory.Title, ''), excluded.Title),
                Sha256 = COALESCE(NULLIF(ReaderReadingHistory.Sha256, ''), excluded.Sha256);
            """);
        command.Parameters.AddWithValue("$source", sourceFileId.ToString());
        command.Parameters.AddWithValue("$target", targetFileId.ToString());
        command.Parameters.AddWithValue("$book", bookId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
