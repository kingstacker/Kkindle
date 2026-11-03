using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kkindle.Core;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>
/// Mounted storage supports real SQLite transactions. Lock writers before the
/// final freshness check, then update rows instead of replacing an open file.
/// </summary>
internal static class KindleAnnotationMountedWriter
{
    public static async Task<(string Hash, string DeviceBackup)> ApplyAsync(
        string database, string expectedHash, string localBackup,
        IReadOnlyList<KindleAnnotationMutation> mutations, CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        RejectSideFiles(database);
        var header = new byte[20];
        using (var stream = File.OpenRead(database)) stream.ReadExactly(header);
        if (header[18] == 2 || header[19] == 2)
            throw new IOException("设备批注数据库使用 WAL，暂不能安全同步。");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        // BEGIN IMMEDIATE prevents another SQLite writer from changing the
        // source between this check and the commit.
        RejectSideFiles(database);
        if (!await MatchesAsync(database, expectedHash, cancellationToken)
            || !await MatchesAsync(localBackup, expectedHash, cancellationToken))
            throw new IOException("Kindle 批注数据库在同步期间已变化，请重新检测。");
        var deviceBackup = database + ".kkindle-backup-" + Guid.NewGuid().ToString("N");
        using (var source = OpenSharedRead(database))
        using (var backup = new FileStream(deviceBackup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(backup, cancellationToken);
            backup.Flush(flushToDisk: true);
        }
        if (!await MatchesAsync(deviceBackup, expectedHash, cancellationToken))
            throw new IOException("Kindle 批注设备备份校验失败。");
        var processed = 0;
        foreach (var mutation in mutations)
        {
            if (processed % 32 == 0)
                progress?.Report(new(KindleAnnotationSyncStage.WritingToDevice, processed, mutations.Count));
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE nonsyncable_annotations SET dataset=$dataset,
                    start_position=$start, end_position=$end,
                    created_time=$created, modified_time=$modified, serialized_payload=$payload
                WHERE annotation_id=$id AND book_id=$book;
                INSERT INTO nonsyncable_annotations(annotation_id,book_id,dataset,start_position,
                    end_position,created_time,modified_time,serialized_payload)
                SELECT $id,$book,$dataset,$start,$end,$created,$modified,$payload
                WHERE NOT EXISTS(SELECT 1 FROM nonsyncable_annotations WHERE annotation_id=$id AND book_id=$book);
                """;
            command.Parameters.AddWithValue("$id", mutation.AnnotationId);
            command.Parameters.AddWithValue("$book", mutation.BookId);
            command.Parameters.AddWithValue("$dataset", mutation.Dataset);
            command.Parameters.AddWithValue("$start", KindleAnnotationDatabaseWriter.PositionJson(mutation.Start));
            command.Parameters.AddWithValue("$end", KindleAnnotationDatabaseWriter.PositionJson(mutation.End));
            command.Parameters.AddWithValue("$created", mutation.CreatedTime);
            command.Parameters.AddWithValue("$modified", mutation.ModifiedTime);
            command.Parameters.AddWithValue("$payload", mutation.SerializedPayload);
            await command.ExecuteNonQueryAsync(cancellationToken);
            processed++;
        }
        progress?.Report(new(KindleAnnotationSyncStage.Verifying));
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(Convert.ToString(await verify.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Kindle 批注数据库校验失败，已回滚本次事务。");
        }
        await transaction.CommitAsync(cancellationToken);
        await using var readback = OpenSharedRead(database);
        return (Convert.ToHexString(await SHA256.HashDataAsync(readback, cancellationToken)).ToLowerInvariant(), deviceBackup);
    }

    private static void RejectSideFiles(string database)
    {
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(database + suffix)))
            throw new IOException("设备批注数据库带有 SQLite 侧文件，暂不能安全同步。");
    }
    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = OpenSharedRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    private static FileStream OpenSharedRead(string path)
    {
        if (OperatingSystem.IsWindows())
            return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // On Unix, .NET emulates FileShare with flock(). SQLite holds an
        // advisory lock on the database while our IMMEDIATE transaction is
        // open, so any FileStream open is rejected as "in use by another
        // process" even though plain readers share the file freely. A raw
        // POSIX open takes no lock and matches SQLite's own read semantics.
        var descriptor = Libc.open(path, Libc.O_RDONLY);
        if (descriptor < 0)
            throw new IOException($"无法打开文件 {path}（errno {Marshal.GetLastPInvokeError()}）。");
        try
        {
            _ = Libc.fcntl(descriptor, Libc.F_SETFD, Libc.FD_CLOEXEC);
            return new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Read, 81920);
        }
        catch
        {
            _ = Libc.close(descriptor);
            throw;
        }
    }

    private static class Libc
    {
        public const int O_RDONLY = 0;
        public const int F_SETFD = 2;
        public const int FD_CLOEXEC = 1;

        [DllImport("libc", SetLastError = true)]
        public static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int fcntl(int descriptor, int command, int argument);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int descriptor);
    }
}
