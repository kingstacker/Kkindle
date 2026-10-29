using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

internal sealed record ReaderRecoveryContext(Guid Epoch, ReadingDataReset? Reset, bool Imported = false);

internal static class ReaderRecoveryEpoch
{
    private const string EpochKey = "reader-recovery-epoch-v1";
    private const string ImportedKey = "reader-recovery-imported-v1";

    public static async Task EnsureAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS S3SyncLocalMetadata (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            INSERT OR IGNORE INTO S3SyncLocalMetadata (Key, Value) VALUES ($key, $epoch);
            CREATE TABLE IF NOT EXISTS ReaderReadingTimeRecoveryApplied (Id TEXT PRIMARY KEY);
            """;
        command.Parameters.AddWithValue("$key", EpochKey);
        command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task RotateForImportAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        await EnsureAsync(connection, transaction, token);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE S3SyncLocalMetadata SET Value=$epoch WHERE Key=$key;
            INSERT INTO S3SyncLocalMetadata (Key, Value) VALUES ($imported, '1')
                ON CONFLICT(Key) DO UPDATE SET Value='1';
            DELETE FROM ReaderReadingTimeRecoveryApplied;
            """;
        command.Parameters.AddWithValue("$key", EpochKey);
        command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$imported", ImportedKey);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public static async Task<ReaderRecoveryContext> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM S3SyncLocalMetadata WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", EpochKey);
        var value = await command.ExecuteScalarAsync(token) as string;
        if (!Guid.TryParse(value, out var epoch) || epoch == Guid.Empty)
            throw new InvalidDataException("Invalid local reader recovery epoch.");
        command.Parameters["$key"].Value = ImportedKey;
        var imported = await command.ExecuteScalarAsync(token) as string == "1";
        return new(epoch, await ReaderReadingDataReset.ReadAsync(connection, transaction, token), imported);
    }
}
