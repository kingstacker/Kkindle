using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class SqliteBookLibraryService
{
    private readonly SemaphoreSlim _summaryCacheGate = new(1, 1);
    private (string Version, LibraryFilterOptions Value)? _filterCache;
    private (string Version, IReadOnlyList<LibraryBookMatch> Value)? _matchCache;

    private static async Task EnsureSummaryCacheVersionsAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LibrarySummaryVersions(Name TEXT PRIMARY KEY, Token TEXT NOT NULL);
            INSERT OR IGNORE INTO LibrarySummaryVersions VALUES('filters', hex(randomblob(16)));
            INSERT OR IGNORE INTO LibrarySummaryVersions VALUES('matches', hex(randomblob(16)));
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var table in new[] { "Books", "BookFiles" })
        foreach (var operation in new[] { "INSERT", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS LibrarySummary_{table}_{operation} AFTER {operation} ON {table}
                BEGIN UPDATE LibrarySummaryVersions SET Token=hex(randomblob(16)); END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        foreach (var (table, name, columns) in new[]
        {
            ("Books", "filters", new[] { "Authors", "Tags", "Category" }),
            ("Books", "matches", new[] { "Title", "Authors", "Isbn" }),
            ("BookFiles", "filters", new[] { "Format" }),
            ("BookFiles", "matches", new[] { "BookId", "RelativePath", "Sha256" })
        })
        {
            var condition = string.Join(" OR ", columns.Select(column => $"OLD.{column} IS NOT NEW.{column}"));
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS LibrarySummary_{table}_{name}_UPDATE AFTER UPDATE ON {table}
                WHEN {condition}
                BEGIN UPDATE LibrarySummaryVersions SET Token=hex(randomblob(16)) WHERE Name='{name}'; END;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private async Task<string?> ReadSummaryVersionAsync(string name, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Token FROM LibrarySummaryVersions WHERE Name=$name";
        command.Parameters.AddWithValue("$name", name);
        try { return await command.ExecuteScalarAsync(token) as string; }
        catch (SqliteException e) when (e.SqliteErrorCode == 1) { return null; }
    }

    public async Task<LibraryFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        await _summaryCacheGate.WaitAsync(cancellationToken);
        try
        {
            var version = await ReadSummaryVersionAsync("filters", cancellationToken);
            if (version is not null && _filterCache is { } cache && cache.Version == version) return cache.Value;
            var result = await GetFilterOptionsCoreAsync(cancellationToken);
            if (version is not null && version == await ReadSummaryVersionAsync("filters", cancellationToken))
                _filterCache = (version, result);
            return result;
        }
        finally { _summaryCacheGate.Release(); }
    }

    public async Task<IReadOnlyList<LibraryBookMatch>> GetLibraryMatchRecordsAsync(CancellationToken cancellationToken = default)
    {
        await _summaryCacheGate.WaitAsync(cancellationToken);
        try
        {
            var version = await ReadSummaryVersionAsync("matches", cancellationToken);
            if (version is not null && _matchCache is { } cache && cache.Version == version) return cache.Value;
            var result = await GetLibraryMatchRecordsCoreAsync(cancellationToken);
            if (version is not null && version == await ReadSummaryVersionAsync("matches", cancellationToken))
                _matchCache = (version, result);
            return result;
        }
        finally { _summaryCacheGate.Release(); }
    }
}
