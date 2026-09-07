using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>
/// Keeps locally persisted reader annotations aligned with the library rows.
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
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'ReaderAnnotations');
            """;
        if (Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken)) != 1)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER IF NOT EXISTS Kkindle_DeleteReaderAnnotationsAfterBookDelete
            AFTER DELETE ON Books
            BEGIN
                DELETE FROM ReaderAnnotations WHERE BookId = OLD.Id;
            END;

            CREATE TRIGGER IF NOT EXISTS Kkindle_DeleteReaderAnnotationsAfterFileDelete
            AFTER DELETE ON BookFiles
            BEGIN
                DELETE FROM ReaderAnnotations WHERE BookFileId = OLD.Id;
            END;

            DELETE FROM ReaderAnnotations
            WHERE NOT EXISTS (
                SELECT 1 FROM Books WHERE Books.Id = ReaderAnnotations.BookId)
               OR NOT EXISTS (
                SELECT 1 FROM BookFiles WHERE BookFiles.Id = ReaderAnnotations.BookFileId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
