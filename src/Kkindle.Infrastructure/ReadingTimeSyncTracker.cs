using System.Globalization;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

// Grow-only counters per device make offline reading time and daily reading
// history additive and idempotent. The shared legacy component imports
// pre-counter totals once, using max rather than counting the same historical
// time on every device.
internal static class ReadingTimeSyncTracker
{
    public static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS S3SyncLocalMetadata (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS S3SyncReadingTimeCounters (
                BookFileId TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                Seconds INTEGER NOT NULL CHECK (Seconds >= 0),
                PRIMARY KEY (BookFileId, DeviceId)
            );
            CREATE TABLE IF NOT EXISTS S3SyncReadingDayCounters (
                BookFileId TEXT NOT NULL,
                ReadingDate TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                Seconds INTEGER NOT NULL CHECK (Seconds >= 0),
                PRIMARY KEY (BookFileId, ReadingDate, DeviceId)
            );
            INSERT OR IGNORE INTO S3SyncLocalMetadata (Key, Value)
                VALUES ('reading-time-device', $device);
            INSERT OR IGNORE INTO S3SyncReadingTimeCounters (BookFileId, DeviceId, Seconds)
                SELECT BookFileId, 'legacy', CumulativeSeconds FROM ReaderReadingStats
                WHERE CumulativeSeconds > 0
                  AND NOT EXISTS (SELECT 1 FROM S3SyncLocalMetadata WHERE Key = 'reading-time-v1');
            INSERT OR IGNORE INTO S3SyncLocalMetadata (Key, Value) VALUES ('reading-time-v1', '1');
            INSERT INTO S3SyncReadingDayCounters (BookFileId, ReadingDate, DeviceId, Seconds)
                SELECT BookFileId, substr(RecordedAt, 1, 10),
                       COALESCE((SELECT Value FROM S3SyncLocalMetadata WHERE Key = 'reading-time-device'), 'legacy'),
                       SUM(ActiveSeconds)
                FROM ReaderReadingSessions
                WHERE NOT EXISTS (SELECT 1 FROM S3SyncLocalMetadata WHERE Key = 'reading-day-v1')
                GROUP BY BookFileId, substr(RecordedAt, 1, 10)
                ON CONFLICT(BookFileId, ReadingDate, DeviceId) DO UPDATE SET
                    Seconds = MAX(S3SyncReadingDayCounters.Seconds, excluded.Seconds);
            INSERT OR IGNORE INTO S3SyncLocalMetadata (Key, Value) VALUES ('reading-day-v1', '1');
            CREATE TRIGGER IF NOT EXISTS S3SyncReadingTimeCounters_Delete
                AFTER DELETE ON ReaderReadingStats
                BEGIN
                    DELETE FROM S3SyncReadingTimeCounters WHERE BookFileId = OLD.BookFileId;
                END;
            """, ("$device", Guid.NewGuid().ToString("N")));
        await schema.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ConfigureDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction,
            "UPDATE S3SyncLocalMetadata SET Value = $device WHERE Key = 'reading-time-device';",
            ("$device", deviceId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<Dictionary<string, long>> CaptureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        long localTotal,
        CancellationToken cancellationToken)
    {
        var counters = await ReadAsync(connection, transaction, fileId, cancellationToken);
        var knownTotal = Total(counters);
        if (localTotal > knownTotal)
        {
            using var device = Command(connection, transaction,
                "SELECT Value FROM S3SyncLocalMetadata WHERE Key = 'reading-time-device';");
            var deviceId = (string)(await device.ExecuteScalarAsync(cancellationToken))!;
            var value = checked(counters.GetValueOrDefault(deviceId) + localTotal - knownTotal);
            await SetMaximumAsync(connection, transaction, fileId, deviceId, value, cancellationToken);
            counters[deviceId] = value;
        }
        return counters;
    }

    public static async Task RecordCurrentTotalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction,
            "SELECT CumulativeSeconds FROM ReaderReadingStats WHERE BookFileId = $file;",
            ("$file", fileId.ToString()));
        var total = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        var counters = await CaptureAsync(connection, transaction, fileId, total, cancellationToken);
        await UpdateTotalAsync(connection, transaction, fileId, Total(counters), cancellationToken);
    }

    public static async Task<long> MergeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        IReadOnlyDictionary<string, long>? remoteCounters,
        long legacyTotal,
        CancellationToken cancellationToken)
    {
        if (remoteCounters is null || remoteCounters.Count == 0)
        {
            await SetMaximumAsync(connection, transaction, fileId, "legacy", Math.Max(0, legacyTotal), cancellationToken);
        }
        else
        {
            foreach (var (rawDeviceId, seconds) in remoteCounters)
            {
                var deviceId = rawDeviceId == "legacy" ? rawDeviceId
                    : Guid.TryParse(rawDeviceId, out var parsed) ? parsed.ToString("N") : null;
                if (deviceId is null || seconds < 0)
                    throw new InvalidDataException(UiText.Get("S3 阅读时长计数无效。"));
                await SetMaximumAsync(connection, transaction, fileId, deviceId, seconds, cancellationToken);
            }
        }
        return Total(await ReadAsync(connection, transaction, fileId, cancellationToken));
    }

    public static async Task RecordReadingDayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        DateTimeOffset recordedAt,
        long activeSeconds,
        CancellationToken cancellationToken)
    {
        if (activeSeconds <= 0) return;
        var date = recordedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var command = Command(connection, transaction, """
            INSERT INTO S3SyncReadingDayCounters (BookFileId, ReadingDate, DeviceId, Seconds)
            VALUES (
                $file, $date,
                COALESCE((SELECT Value FROM S3SyncLocalMetadata WHERE Key = 'reading-time-device'), 'legacy'),
                $seconds)
            ON CONFLICT(BookFileId, ReadingDate, DeviceId) DO UPDATE SET
                Seconds = S3SyncReadingDayCounters.Seconds + excluded.Seconds;
            """,
            ("$file", fileId.ToString()),
            ("$date", date),
            ("$seconds", activeSeconds));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<Dictionary<string, Dictionary<string, long>>> CaptureReadingDaysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            SELECT DeviceId, ReadingDate, Seconds
            FROM S3SyncReadingDayCounters
            WHERE BookFileId = $file
            ORDER BY DeviceId, ReadingDate;
            """, ("$file", fileId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deviceId = reader.GetString(0);
            var date = reader.GetString(1);
            if (!result.TryGetValue(deviceId, out var dates))
            {
                dates = new Dictionary<string, long>(StringComparer.Ordinal);
                result[deviceId] = dates;
            }
            dates[date] = reader.GetInt64(2);
        }
        return result;
    }

    public static async Task MergeReadingDaysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        IReadOnlyDictionary<string, Dictionary<string, long>>? remoteCounters,
        CancellationToken cancellationToken)
    {
        if (remoteCounters is null) return;
        foreach (var (rawDeviceId, dates) in remoteCounters)
        {
            var deviceId = NormalizeDeviceId(rawDeviceId);
            if (deviceId is null)
                throw new InvalidDataException(UiText.Get("S3 阅读日期计数的设备标识无效。"));
            foreach (var (date, seconds) in dates)
            {
                if (!DateOnly.TryParseExact(
                        date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _)
                    || seconds < 0)
                    throw new InvalidDataException(UiText.Get("S3 阅读日期计数无效。"));
                await SetReadingDayMaximumAsync(
                    connection, transaction, fileId, date, deviceId, seconds, cancellationToken);
            }
        }
    }

    public static async Task MergeFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceFileId,
        Guid targetFileId,
        CancellationToken cancellationToken)
    {
        await RecordCurrentTotalAsync(connection, transaction, sourceFileId, cancellationToken);
        await RecordCurrentTotalAsync(connection, transaction, targetFileId, cancellationToken);
        var source = await ReadAsync(connection, transaction, sourceFileId, cancellationToken);
        foreach (var (deviceId, seconds) in source)
            await SetMaximumAsync(connection, transaction, targetFileId, deviceId, seconds, cancellationToken);

        var dayRows = new List<(string Date, string DeviceId, long Seconds)>();
        using (var readDays = Command(connection, transaction, """
            SELECT ReadingDate, DeviceId, Seconds
            FROM S3SyncReadingDayCounters
            WHERE BookFileId = $file;
            """, ("$file", sourceFileId.ToString())))
        await using (var reader = await readDays.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                dayRows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        foreach (var row in dayRows)
            await SetReadingDayMaximumAsync(
                connection, transaction, targetFileId, row.Date, row.DeviceId, row.Seconds, cancellationToken);
        using (var deleteDays = Command(
                   connection,
                   transaction,
                   "DELETE FROM S3SyncReadingDayCounters WHERE BookFileId = $file;",
                   ("$file", sourceFileId.ToString())))
            await deleteDays.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> UpdateTotalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        long total,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            UPDATE ReaderReadingStats SET CumulativeSeconds = $total
            WHERE BookFileId = $file AND CumulativeSeconds < $total;
            """, ("$file", fileId.ToString()), ("$total", total));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static long Total(IReadOnlyDictionary<string, long> counters)
    {
        long result = 0;
        foreach (var value in counters.Values) result = checked(result + value);
        return result;
    }

    private static async Task<Dictionary<string, long>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction,
            "SELECT DeviceId, Seconds FROM S3SyncReadingTimeCounters WHERE BookFileId = $file;",
            ("$file", fileId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static async Task SetMaximumAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        string deviceId,
        long seconds,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO S3SyncReadingTimeCounters (BookFileId, DeviceId, Seconds)
            VALUES ($file, $device, $seconds)
            ON CONFLICT(BookFileId, DeviceId) DO UPDATE SET Seconds = excluded.Seconds
            WHERE excluded.Seconds > S3SyncReadingTimeCounters.Seconds;
            """, ("$file", fileId.ToString()), ("$device", deviceId), ("$seconds", seconds));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetReadingDayMaximumAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        string date,
        string deviceId,
        long seconds,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO S3SyncReadingDayCounters (BookFileId, ReadingDate, DeviceId, Seconds)
            VALUES ($file, $date, $device, $seconds)
            ON CONFLICT(BookFileId, ReadingDate, DeviceId) DO UPDATE SET
                Seconds = MAX(S3SyncReadingDayCounters.Seconds, excluded.Seconds);
            """,
            ("$file", fileId.ToString()),
            ("$date", date),
            ("$device", deviceId),
            ("$seconds", seconds));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeDeviceId(string? value)
    {
        if (string.Equals(value, "legacy", StringComparison.Ordinal)) return "legacy";
        return Guid.TryParse(value, out var deviceId) ? deviceId.ToString("N") : null;
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
