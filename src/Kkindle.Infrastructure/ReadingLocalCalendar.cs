using System.Globalization;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

// Keep the UTC bucket as provenance so old clients' day counters can still be
// merged. Only the portion without local-date provenance uses the legacy date.
internal static class ReadingLocalCalendar
{
    public static async Task EnsureAsync(SqliteConnection c, SqliteTransaction t, CancellationToken ct)
    {
        using var cmd = Command(c, t, """
            CREATE TABLE IF NOT EXISTS ReaderLocalDayCounters (
                BookFileId TEXT NOT NULL, DeviceId TEXT NOT NULL, DatePair TEXT NOT NULL,
                Seconds INTEGER NOT NULL CHECK (Seconds >= 0),
                PRIMARY KEY (BookFileId, DeviceId, DatePair));
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static IEnumerable<(string UtcDate, string LocalDate, long Seconds)> Split(DateTimeOffset end, long seconds, TimeZoneInfo zone)
    {
        var cursor = end.AddSeconds(-seconds);
        var buckets = new Dictionary<(string Utc, string Local), long>();
        while (seconds > 0)
        {
            var count = Math.Min(seconds, 60);
            var last = cursor.AddSeconds(count).AddTicks(-1);
            var local = TimeZoneInfo.ConvertTime(cursor, zone).Date;
            // At either midnight, split precisely rather than assigning the
            // whole flush to the day on which it happened to be saved.
            if (cursor.UtcDateTime.Date != last.UtcDateTime.Date || local != TimeZoneInfo.ConvertTime(last, zone).Date)
                count = 1;
            var key = (cursor.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            buckets[key] = buckets.GetValueOrDefault(key) + count;
            cursor = cursor.AddSeconds(count);
            seconds -= count;
        }
        return buckets.Select(b => (b.Key.Utc, b.Key.Local, b.Value));
    }

    public static async Task RecordAsync(SqliteConnection c, SqliteTransaction t, Guid file, string device,
        DateTimeOffset end, long seconds, TimeZoneInfo zone, CancellationToken ct)
    {
        foreach (var part in Split(end, seconds, zone))
        {
            using var cmd = Command(c, t, """
                INSERT INTO S3SyncReadingDayCounters (BookFileId, ReadingDate, DeviceId, Seconds)
                    VALUES ($file, $utc, $device, $seconds)
                    ON CONFLICT(BookFileId, ReadingDate, DeviceId) DO UPDATE SET Seconds = Seconds + excluded.Seconds;
                INSERT INTO ReaderLocalDayCounters (BookFileId, DeviceId, DatePair, Seconds)
                    VALUES ($file, $device, $pair, $seconds)
                    ON CONFLICT(BookFileId, DeviceId, DatePair) DO UPDATE SET Seconds = Seconds + excluded.Seconds;
                """);
            cmd.Parameters.AddWithValue("$file", file.ToString());
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$utc", part.UtcDate);
            cmd.Parameters.AddWithValue("$pair", part.UtcDate + "/" + part.LocalDate);
            cmd.Parameters.AddWithValue("$seconds", part.Seconds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<(IReadOnlyList<ReadingDashboardDay> Days, bool Legacy)> ReadAsync(SqliteConnection c, SqliteTransaction? t, CancellationToken ct)
    {
        using var cmd = Command(c, t, """
            WITH local AS (
                SELECT BookFileId, DeviceId, substr(DatePair, 1, 10) UtcDate, SUM(Seconds) Seconds
                FROM ReaderLocalDayCounters GROUP BY BookFileId, DeviceId, UtcDate),
            days AS (
                SELECT substr(DatePair, 12, 10) Date, Seconds, 0 Legacy FROM ReaderLocalDayCounters
                UNION ALL
                SELECT u.ReadingDate, MAX(0, u.Seconds - COALESCE(l.Seconds, 0)), 1
                FROM S3SyncReadingDayCounters u LEFT JOIN local l
                ON u.BookFileId = l.BookFileId AND u.DeviceId = l.DeviceId AND u.ReadingDate = l.UtcDate)
            SELECT Date, SUM(Seconds), MAX(CASE WHEN Seconds > 0 THEN Legacy ELSE 0 END)
            FROM days GROUP BY Date ORDER BY Date;
            """);
        var days = new List<ReadingDashboardDay>();
        var legacy = false;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            days.Add(new(date, reader.GetInt64(1)));
            legacy |= reader.GetInt32(2) != 0;
        }
        return (days, legacy);
    }

    public static async Task<Dictionary<string, Dictionary<string, long>>> CaptureAsync(SqliteConnection c, SqliteTransaction t, Guid file, CancellationToken ct)
    {
        using var cmd = Command(c, t, "SELECT DeviceId, DatePair, Seconds FROM ReaderLocalDayCounters WHERE BookFileId = $file;");
        cmd.Parameters.AddWithValue("$file", file.ToString());
        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var device = reader.GetString(0);
            if (!result.TryGetValue(device, out var days)) result[device] = days = new(StringComparer.Ordinal);
            days[reader.GetString(1)] = reader.GetInt64(2);
        }
        return result;
    }

    public static Dictionary<string, Dictionary<string, long>> Merge(IEnumerable<Dictionary<string, Dictionary<string, long>>?> versions)
    {
        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var version in versions)
        foreach (var (device, days) in version ?? [])
        {
            if (!result.TryGetValue(device, out var target)) result[device] = target = new(StringComparer.Ordinal);
            foreach (var (pair, seconds) in days)
            {
                if (string.IsNullOrWhiteSpace(device) || device.Length > 256 || pair.Length != 21 || pair[10] != '/'
                    || !DateOnly.TryParseExact(pair[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || !DateOnly.TryParseExact(pair[11..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || seconds < 0) throw new InvalidDataException("Invalid reading calendar counter.");
                target[pair] = Math.Max(target.GetValueOrDefault(pair), seconds);
            }
        }
        return result;
    }

    public static async Task MergeAsync(SqliteConnection c, SqliteTransaction t, Guid file,
        Dictionary<string, Dictionary<string, long>>? values, CancellationToken ct)
    {
        foreach (var (device, days) in Merge([values]))
        foreach (var (pair, seconds) in days)
        {
            using var cmd = Command(c, t, """
                INSERT INTO ReaderLocalDayCounters (BookFileId, DeviceId, DatePair, Seconds) VALUES ($file, $device, $pair, $seconds)
                ON CONFLICT(BookFileId, DeviceId, DatePair) DO UPDATE SET Seconds = MAX(Seconds, excluded.Seconds);
                """);
            cmd.Parameters.AddWithValue("$file", file.ToString());
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$pair", pair);
            cmd.Parameters.AddWithValue("$seconds", seconds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        return cmd;
    }
}
