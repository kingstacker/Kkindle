using System.Security.Cryptography;
using System.Text.Json;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

internal static class KindleAnnotationDatabaseWriter
{
    public static async Task<string> BuildCandidateAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Kindle 批注数据库快照不存在。", sourcePath);
        if (File.Exists(destinationPath)) throw new IOException("Kindle 批注候选文件已存在，拒绝覆盖。");
        File.Copy(sourcePath, destinationPath, overwrite: false);
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                using (var journal = connection.CreateCommand())
                {
                    journal.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL;";
                    await journal.ExecuteNonQueryAsync(cancellationToken);
                }

                var columns = await ColumnsAsync(connection, cancellationToken);
                string[] required =
                [
                    "annotation_id", "book_id", "dataset", "start_position", "end_position",
                    "created_time", "modified_time", "serialized_payload"
                ];
                if (required.Any(column => !columns.Contains(column)))
                    throw new NotSupportedException("当前 Kindle 批注数据库版本暂不支持回写。");

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var processed = 0;
                foreach (var mutation in mutations)
                {
                    if (processed % 32 == 0)
                        progress?.Report(new(KindleAnnotationSyncStage.PreparingWrite, processed, mutations.Count));
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateMutation(mutation);
                    using var exists = connection.CreateCommand();
                    exists.Transaction = (SqliteTransaction)transaction;
                    exists.CommandText = """
                        SELECT EXISTS(
                            SELECT 1 FROM nonsyncable_annotations
                            WHERE annotation_id = $annotationId AND book_id = $bookId);
                        """;
                    exists.Parameters.AddWithValue("$annotationId", mutation.AnnotationId);
                    exists.Parameters.AddWithValue("$bookId", mutation.BookId);
                    var rowExists = Convert.ToInt64(
                        await exists.ExecuteScalarAsync(cancellationToken),
                        System.Globalization.CultureInfo.InvariantCulture) != 0;

                    using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = rowExists
                        ? """
                            UPDATE nonsyncable_annotations SET
                                dataset = $dataset,
                                start_position = $startPosition,
                                end_position = $endPosition,
                                created_time = $createdTime,
                                modified_time = $modifiedTime,
                                serialized_payload = $payload
                            WHERE annotation_id = $annotationId AND book_id = $bookId;
                            """
                        : """
                            INSERT INTO nonsyncable_annotations (
                                annotation_id, book_id, dataset, start_position, end_position,
                                created_time, modified_time, serialized_payload)
                            VALUES (
                                $annotationId, $bookId, $dataset, $startPosition, $endPosition,
                                $createdTime, $modifiedTime, $payload);
                            """;
                    command.Parameters.AddWithValue("$annotationId", mutation.AnnotationId);
                    command.Parameters.AddWithValue("$bookId", mutation.BookId);
                    command.Parameters.AddWithValue("$dataset", mutation.Dataset);
                    command.Parameters.AddWithValue("$startPosition", PositionJson(mutation.Start));
                    command.Parameters.AddWithValue("$endPosition", PositionJson(mutation.End));
                    command.Parameters.AddWithValue("$createdTime", mutation.CreatedTime);
                    command.Parameters.AddWithValue("$modifiedTime", mutation.ModifiedTime);
                    command.Parameters.AddWithValue("$payload", mutation.SerializedPayload);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    processed++;
                }
                await transaction.CommitAsync(cancellationToken);
                progress?.Report(new(KindleAnnotationSyncStage.PreparingWrite, processed, mutations.Count));

                using (var integrity = connection.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check;";
                    var result = Convert.ToString(
                        await integrity.ExecuteScalarAsync(cancellationToken),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Kindle 批注候选数据库校验失败：{result}");
                }

                foreach (var mutation in mutations)
                {
                    using var verify = connection.CreateCommand();
                    verify.CommandText = """
                        SELECT dataset, start_position, end_position, created_time, modified_time, serialized_payload
                        FROM nonsyncable_annotations
                        WHERE annotation_id = $annotationId AND book_id = $bookId;
                        """;
                    verify.Parameters.AddWithValue("$annotationId", mutation.AnnotationId);
                    verify.Parameters.AddWithValue("$bookId", mutation.BookId);
                    await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidDataException("Kindle 批注候选数据库缺少刚写入的记录。");
                    if (reader.GetInt32(0) != mutation.Dataset
                        || reader.GetString(1) != PositionJson(mutation.Start)
                        || reader.GetString(2) != PositionJson(mutation.End)
                        || reader.GetInt64(3) != mutation.CreatedTime
                        || reader.GetInt64(4) != mutation.ModifiedTime
                        || reader.GetString(5) != mutation.SerializedPayload)
                        throw new InvalidDataException("Kindle 批注候选数据库读回内容不一致。");
                }
            }

            await using var stream = File.OpenRead(destinationPath);
            return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        catch
        {
            try { File.Delete(destinationPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void ValidateMutation(KindleAnnotationMutation mutation)
    {
        if (string.IsNullOrWhiteSpace(mutation.AnnotationId)
            || string.IsNullOrWhiteSpace(mutation.BookId)) throw new ArgumentException("Kindle 批注标识无效。");
        if (mutation.Dataset is not (1 or 3))
            throw new ArgumentOutOfRangeException(nameof(mutation), "仅允许同步普通划线和笔记。");
        if (mutation.Start < 0 || mutation.End < mutation.Start)
            throw new ArgumentOutOfRangeException(nameof(mutation), "Kindle 批注位置无效。");
        if (mutation.CreatedTime < 0 || mutation.ModifiedTime < 0)
            throw new ArgumentOutOfRangeException(nameof(mutation), "Kindle 批注时间无效。");
        try
        {
            using var _ = JsonDocument.Parse(mutation.SerializedPayload);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Kindle 批注载荷不是有效 JSON。", nameof(mutation), exception);
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(nonsyncable_annotations);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    internal static string PositionJson(long value) =>
        $"{{\"longPosition\":\"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}\",\"shortPosition\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
}
