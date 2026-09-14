using System.Text.Json;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

// A reset starts a new generation for statistics and progress. Wall-clock
// timestamps cannot distinguish new reading from an offline device's old
// cumulative counters, so only data from the same generation can be merged.
internal sealed record ReadingDataReset(long Generation, Guid Id);

internal static class ReaderReadingDataReset
{
    private const string MetadataKey = "reading-data-reset-v1";

    public static void Validate(ReadingDataReset? reset)
    {
        if (reset is { Generation: <= 0 } || reset?.Id == Guid.Empty)
            throw new InvalidDataException(UiText.Get("阅读数据重置记录无效。"));
    }

    public static ReadingDataReset? Latest(IEnumerable<ReadingDataReset?> resets) => resets
        .OfType<ReadingDataReset>()
        .OrderByDescending(reset => reset.Generation)
        .ThenByDescending(reset => reset.Id)
        .FirstOrDefault();

    public static async Task<ReadingDataReset?> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM S3SyncLocalMetadata WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", MetadataKey);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string value) return null;
        var reset = JsonSerializer.Deserialize<ReadingDataReset>(value)
            ?? throw new InvalidDataException(UiText.Get("阅读数据重置记录无效。"));
        Validate(reset);
        return reset;
    }

    public static async Task ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ReadingDataReset reset, CancellationToken cancellationToken)
    {
        Validate(reset);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM ReaderProgress;
            DELETE FROM ReaderReadingSessions;
            DELETE FROM ReaderReadingStats;
            DELETE FROM ReaderReadingHistory;
            DELETE FROM S3SyncReadingTimeCounters;
            DELETE FROM S3SyncReadingDayCounters;
            INSERT INTO S3SyncLocalMetadata (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", MetadataKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(reset));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // The generation replaces per-row deletions for a reset. Clear both
        // pre-reset journal entries and those produced by the DELETE triggers;
        // neither should remove reading started after this reset. Sync may not
        // have been configured yet, in which case this table does not exist.
        using var journal = connection.CreateCommand();
        journal.Transaction = transaction;
        journal.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'S3SyncDeletionLog';";
        if (Convert.ToInt64(await journal.ExecuteScalarAsync(cancellationToken)) != 0)
        {
            journal.CommandText = "DELETE FROM S3SyncDeletionLog WHERE EntityType IN ('progress', 'stats');";
            await journal.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed partial class ReaderDataService
{
    /// <summary>
    /// Clears reading statistics and progress atomically, retaining library
    /// files, bookmarks, annotations, layout preferences and sync identity.
    /// Call only after the reader has finished saving its current session.
    /// </summary>
    public async Task ResetReadingDataAsync(CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var previous = await ReaderReadingDataReset.ReadAsync(connection, transaction, cancellationToken);
            var reset = new ReadingDataReset(checked((previous?.Generation ?? 0) + 1), Guid.NewGuid());
            await ReaderReadingDataReset.ApplyAsync(connection, transaction, reset, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.ReadingDataReset);
        }
        finally
        {
            _databaseGate.Release();
        }
    }
}
