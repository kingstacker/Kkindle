using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>
/// Keeps locally persisted reader annotations and book reflections aligned
/// with the library rows.
/// The library and reader data services share one SQLite database but are
/// initialized independently, so the relationship is enforced with triggers
/// once both schemas are present.
/// </summary>
internal static class ReaderAnnotationCascade
{
    public static async Task EnsureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Books')
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'BookFiles')
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'LibraryTrash')
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'ReaderAnnotations')
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'ReaderBookReflections');
            """;
        if (Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken)) != 1)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS Kkindle_DeleteReaderAnnotationsAfterBookDelete;
            DROP TRIGGER IF EXISTS Kkindle_DeleteReaderAnnotationsAfterFileDelete;
            DROP TRIGGER IF EXISTS Kkindle_DeleteReaderBookReflectionAfterBookDelete;

            CREATE TRIGGER Kkindle_DeleteReaderBookReflectionAfterBookDelete
            AFTER DELETE ON Books
            WHEN NOT EXISTS (
                SELECT 1 FROM LibraryTrash
                WHERE Kind = 0 AND BookId = OLD.Id)
            BEGIN
                DELETE FROM ReaderBookReflections WHERE BookId = OLD.Id;
            END;

            DELETE FROM ReaderBookReflections
            WHERE NOT EXISTS (SELECT 1 FROM Books WHERE Books.Id = ReaderBookReflections.BookId)
              AND NOT EXISTS (
                SELECT 1 FROM LibraryTrash
                WHERE Kind = 0 AND BookId = ReaderBookReflections.BookId);
            """;
        // Trash is device-local. Missing files cannot prove that their notes
        // were purged; explicit note deletion/purge owns annotation tombstones.
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
