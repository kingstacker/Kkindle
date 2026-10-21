using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class DictionaryService
{
    private readonly SemaphoreSlim _lookupIndexGate = new(1, 1);

    // Keep the portable JSON as the source of truth. This disposable index makes each
    // lookup independent of dictionary size, without retaining entire books in memory.
    private async Task<(string Term, string Definition)?> LookupIndexedAsync(string path, string term, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            var culture = CultureInfo.CurrentCulture;
            byte[] Key(string value) => culture.CompareInfo.GetSortKey(value, CompareOptions.IgnoreCase).KeyData;
            var info = new FileInfo(path);
            var stamp = $"1:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{culture.Name}";
            var indexPath = Path.ChangeExtension(path, "lookup.db");
            await _lookupIndexGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = indexPath, Pooling = false }.ToString());
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS Metadata(Stamp TEXT); CREATE TABLE IF NOT EXISTS Lookup(TermKey BLOB PRIMARY KEY,Term TEXT NOT NULL,Definition TEXT NOT NULL); SELECT Stamp FROM Metadata LIMIT 1";
                var currentStamp = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                if (currentStamp != stamp)
                {
                    await using var stream = File.OpenRead(path);
                    var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, token).ConfigureAwait(false);
                    using var transaction = connection.BeginTransaction();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM Lookup; DELETE FROM Metadata";
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    command.CommandText = "INSERT OR IGNORE INTO Lookup VALUES($key,$term,$definition)";
                    var keyParam = command.Parameters.Add("$key", SqliteType.Blob);
                    var termParam = command.Parameters.Add("$term", SqliteType.Text);
                    var definitionParam = command.Parameters.Add("$definition", SqliteType.Text);
                    command.Prepare();
                    foreach (var entry in entries ?? [])
                    {
                        token.ThrowIfCancellationRequested();
                        keyParam.Value = Key(entry.Key); termParam.Value = entry.Key; definitionParam.Value = entry.Value;
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO Metadata VALUES($stamp)";
                    command.Parameters.AddWithValue("$stamp", stamp);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    transaction.Commit();
                    command.Transaction = null;
                }
                command.Parameters.Clear();
                command.CommandText = "SELECT Term,Definition FROM Lookup WHERE TermKey=$key";
                command.Parameters.AddWithValue("$key", Key(term));
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false)
                    ? ((string Term, string Definition)?)(reader.GetString(0), reader.GetString(1)) : null;
            }
            finally { _lookupIndexGate.Release(); }
        }, token).ConfigureAwait(false);
    }
}
