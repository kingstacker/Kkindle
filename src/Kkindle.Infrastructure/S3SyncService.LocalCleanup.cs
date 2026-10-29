using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private static async Task EnsureLocalCleanupSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS LocalFileCleanupQueue (RelativePath TEXT PRIMARY KEY);";
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task EnqueueLocalCleanupAsync(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<string> paths, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction,
            "INSERT OR IGNORE INTO LocalFileCleanupQueue (RelativePath) VALUES ($path);");
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(_paths.Data, path).Replace('\\', '/');
            _ = ResolveCleanupPath(relative);
            AddParameter(command, "$path", relative);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private async Task<bool> CleanupPendingLocalFilesAsync(ICollection<string> warnings, CancellationToken token)
    {
        using var lease = await AppDataProcessLock.AcquireAsync(_paths, token);
        await using var connection = await OpenDatabaseConnectionAsync(token);
        var paths = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT RelativePath FROM LocalFileCleanupQueue;";
            await using var rows = await read.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token)) paths.Add(rows.GetString(0));
        }
        var pending = false;
        foreach (var relative in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var absolute = ResolveCleanupPath(relative);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
                if (!await IsRelativeDataPathReferencedAsync(connection, transaction, relative, token))
                {
                    if (File.Exists(absolute)) File.Delete(absolute);
                    TryDeleteEmptyParentDirectories(absolute);
                }
                // A restore/new reference cancels cleanup. The process lease
                // protects the reference check and deletion from file moves.
                using var remove = CreateCommand(connection, transaction,
                    "DELETE FROM LocalFileCleanupQueue WHERE RelativePath=$path;");
                AddParameter(remove, "$path", relative);
                await remove.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                pending = true;
                warnings.Add(UiText.Get("本地文件清理失败，下次同步将重试：{0}", UiText.Localize(exception.Message)));
            }
        }
        return pending;
    }

    private string ResolveCleanupPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || normalized.Split('/').Any(part => part is "." or "..")
            || !(normalized.StartsWith("library/", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("covers/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Local cleanup path is outside the managed library/covers.");
        var absolute = Path.GetFullPath(Path.Combine(_paths.Data, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = Path.GetFullPath(_paths.Data).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!absolute.StartsWith(boundary, comparison)) throw new InvalidDataException("Invalid local cleanup path.");
        for (var ancestor = absolute; ancestor is not null && !string.Equals(ancestor, _paths.Data, comparison); ancestor = Path.GetDirectoryName(ancestor))
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to follow a reparse point during local cleanup.");
        return absolute;
    }
}
