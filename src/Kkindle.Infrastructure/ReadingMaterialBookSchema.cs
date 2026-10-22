using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

internal static class ReadingMaterialBookSchema
{
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS ReadingMaterialBooks (Id TEXT PRIMARY KEY, Title TEXT NOT NULL, CoverPath TEXT NULL, UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z');";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "PRAGMA table_info(ReadingMaterialBooks);";
        var hasVersion = false;
        var hasCoverHash = false;
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                hasVersion |= reader.GetString(1) == "UpdatedAt";
                hasCoverHash |= reader.GetString(1) == "CoverHash";
            }
        if (!hasVersion)
        {
            command.CommandText = "ALTER TABLE ReadingMaterialBooks ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z';";
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "UPDATE ReadingMaterialBooks SET UpdatedAt=$now;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token);
            command.Parameters.Clear();
        }
        if (!hasCoverHash)
        {
            command.CommandText = "ALTER TABLE ReadingMaterialBooks ADD COLUMN CoverHash TEXT NULL;";
            await command.ExecuteNonQueryAsync(token);
        }
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Books';";
        var hasBooks = Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='PreserveReadingMaterialBook';";
        var trigger = await command.ExecuteScalarAsync(token) as string;
        if (hasBooks && trigger?.Contains("OLD.UpdatedAt,NULL", StringComparison.Ordinal) != true)
        {
            command.CommandText = """
                DROP TRIGGER IF EXISTS PreserveReadingMaterialBook;
                CREATE TRIGGER PreserveReadingMaterialBook BEFORE DELETE ON Books
                BEGIN
                    INSERT INTO ReadingMaterialBooks(Id,Title,CoverPath,UpdatedAt,CoverHash)
                    VALUES(OLD.Id,OLD.Title,OLD.CoverPath,OLD.UpdatedAt,NULL)
                    ON CONFLICT(Id) DO UPDATE SET Title=excluded.Title,CoverPath=excluded.CoverPath,UpdatedAt=excluded.UpdatedAt,CoverHash=NULL;
                END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
}
