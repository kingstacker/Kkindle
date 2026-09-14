using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private async Task<bool> ConsolidateLocalReadingHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SuppressDeletionTrackingAsync(connection, transaction, true, cancellationToken);
        using var commands = new SqliteCommandCache(connection, transaction);
        var changed = await ConsolidateReadingHistoryRowsAsync(connection, transaction, commands, cancellationToken);
        await SuppressDeletionTrackingAsync(connection, transaction, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task<bool> ConsolidateReadingHistoryRowsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        SqliteCommandCache commands, CancellationToken cancellationToken)
    {
        var identities = new List<ReadingHistoryIdentity>();
        using (var read = CreateCommand(connection, transaction, """
            SELECT f.Id, f.BookId, f.Sha256, 1,
                   EXISTS (SELECT 1 FROM ReaderReadingStats s WHERE s.BookFileId = f.Id)
            FROM BookFiles f JOIN Books b ON b.Id = f.BookId
            UNION ALL
            SELECT s.BookFileId, s.BookId, COALESCE(h.Sha256, ''), 0, 1
            FROM ReaderReadingStats s
            LEFT JOIN ReaderReadingHistory h ON h.BookFileId = s.BookFileId
            WHERE NOT EXISTS (SELECT 1 FROM BookFiles f WHERE f.Id = s.BookFileId);
            """))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                identities.Add(new ReadingHistoryIdentity(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                    reader.GetInt32(3) != 0, reader.GetInt32(4) != 0));

        var changed = false;
        foreach (var group in identities.Where(row => IsSha256(row.Sha256))
                     .GroupBy(row => row.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            // Prefer a current library file so reimporting the same book also
            // restores its accumulated time. With only archived identities,
            // the stable order makes every device choose the same survivor.
            var canonical = group.OrderByDescending(row => row.InLibrary).ThenBy(row => row.FileId).First();
            foreach (var source in group.Where(row => row.HasStats && row.FileId != canonical.FileId))
            {
                await MergeDuplicateFileRowsAsync(connection, transaction, source.FileId, canonical.FileId,
                    canonical.BookId, commands, cancellationToken);
                if (!source.InLibrary && source.BookId != canonical.BookId)
                {
                    var rebind = commands.Get("UPDATE ReaderReadingStats SET BookId = $target WHERE BookId = $source;");
                    AddParameter(rebind, "$target", canonical.BookId.ToString());
                    AddParameter(rebind, "$source", source.BookId.ToString());
                    await rebind.ExecuteNonQueryAsync(cancellationToken);
                }
                changed = true;
            }
        }
        return changed;
    }

    private sealed record ReadingHistoryIdentity(Guid FileId, Guid BookId, string Sha256, bool InLibrary, bool HasStats);
}
