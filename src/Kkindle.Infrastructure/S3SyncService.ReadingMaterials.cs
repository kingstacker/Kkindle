using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private static async Task CaptureReadingMaterialBooksAsync(SqliteConnection connection, SqliteTransaction transaction, S3SyncSnapshot snapshot, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ReadingMaterialBooks';";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) return;
        command.CommandText = """
            SELECT r.Id, r.Title, r.CoverPath, r.UpdatedAt FROM ReadingMaterialBooks r
            WHERE EXISTS (SELECT 1 FROM ReaderAnnotations a WHERE a.BookId=r.Id)
            AND NOT EXISTS (SELECT 1 FROM Books b WHERE b.Id=r.Id);
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            snapshot.ReadingMaterialBooks.Add(new S3SyncBook
            {
                Id = Guid.Parse(reader.GetString(0)), Title = reader.GetString(1),
                LocalCoverPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                UpdatedAt = ParseTimestamp(reader.GetString(3))
            });
    }

    private async Task<List<S3SyncBook>> PrepareReadingMaterialBooksAsync(
        ISyncObjectStore client, S3SyncSettings settings,
        IEnumerable<S3SyncSnapshot> snapshots, IReadOnlyDictionary<Guid, Guid> bookMap,
        IProgress<string>? progress, ICollection<string> warnings, CancellationToken token)
    {
        var sources = snapshots.ToArray();
        var annotated = sources.SelectMany(s => s.Annotations).Select(a => a.BookId).ToHashSet();
        // Live book metadata is also useful when an older peer has not yet seen a deletion.
        var books = sources.SelectMany(s => s.ReadingMaterialBooks.Concat(s.Books))
            .Where(b => annotated.Contains(b.Id)).GroupBy(b => b.Id)
            .Select(g => g.OrderByDescending(b => b.UpdatedAt)
                .ThenByDescending(b => b.Title, StringComparer.Ordinal)
                .ThenByDescending(b => b.CoverHash ?? "", StringComparer.Ordinal).First());
        var prepared = new List<S3SyncBook>();
        foreach (var book in books)
        {
            var local = new S3SyncBook { Id = bookMap.GetValueOrDefault(book.Id, book.Id), Title = book.Title, UpdatedAt = book.UpdatedAt };
            if (IsSha256(book.CoverHash) && !string.IsNullOrWhiteSpace(book.CoverFileName))
            {
                var relative = Path.Combine("reading-material-covers",
                    $"{local.Id:N}-{book.CoverHash!.ToLowerInvariant()}{NormalizeCoverExtension(book.CoverFileName)}");
                var absolute = ResolveDataPath(relative)!;
                try
                {
                    await EnsureLocalBlobAsync(client, settings, book.CoverHash!, absolute, progress, token);
                    local.LocalCoverPath = relative;
                    local.CoverHash = book.CoverHash;
                }
                catch (SyncObjectNotFoundException)
                {
                    warnings.Add(UiText.Get("有封面对象不存在，已跳过封面同步。"));
                    continue;
                }
                catch (InvalidDataException)
                {
                    warnings.Add(UiText.Get("有封面对象校验失败，已跳过封面同步。"));
                    continue;
                }
            }
            prepared.Add(local);
        }
        return prepared;
    }

    private static async Task<bool> SaveReadingMaterialBooksAsync(SqliteConnection connection,
        SqliteTransaction transaction, IEnumerable<S3SyncBook> books, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var changed = false;
        foreach (var book in books)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT Title,UpdatedAt,CoverHash FROM ReadingMaterialBooks WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", book.Id.ToString());
            await using (var existing = await command.ExecuteReaderAsync(token))
            {
                if (await existing.ReadAsync(token))
                {
                    var version = ParseTimestamp(existing.GetString(1));
                    var titleOrder = StringComparer.Ordinal.Compare(book.Title, existing.GetString(0));
                    var hashOrder = StringComparer.Ordinal.Compare(book.CoverHash ?? "",
                        existing.IsDBNull(2) ? "" : existing.GetString(2));
                    if (book.UpdatedAt < version || (book.UpdatedAt == version
                        && (titleOrder < 0 || (titleOrder == 0 && hashOrder <= 0)))) continue;
                }
            }
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO ReadingMaterialBooks(Id, Title, CoverPath, UpdatedAt, CoverHash)
                SELECT $id,$title,$cover,$updated,$hash WHERE EXISTS (SELECT 1 FROM ReaderAnnotations WHERE BookId=$id)
                AND NOT EXISTS (SELECT 1 FROM Books WHERE Id=$id)
                ON CONFLICT(Id) DO UPDATE SET Title=excluded.Title,
                    CoverPath=COALESCE(excluded.CoverPath,ReadingMaterialBooks.CoverPath), UpdatedAt=excluded.UpdatedAt,
                    CoverHash=COALESCE(excluded.CoverHash,ReadingMaterialBooks.CoverHash);
                """;
            command.Parameters.AddWithValue("$id", book.Id.ToString());
            command.Parameters.AddWithValue("$title", book.Title);
            command.Parameters.AddWithValue("$updated", book.UpdatedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$hash", (object?)book.CoverHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$cover", (object?)book.LocalCoverPath ?? DBNull.Value);
            changed |= await command.ExecuteNonQueryAsync(token) > 0;
        }
        return changed;
    }
}
