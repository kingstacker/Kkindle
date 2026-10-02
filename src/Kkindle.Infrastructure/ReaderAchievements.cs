using System.Globalization;
using System.Text.Json;
using Kkindle.Core;
using Microsoft.Data.Sqlite;
using static Kkindle.Infrastructure.ReadingLocalCalendar;

namespace Kkindle.Infrastructure;

internal sealed record ReadingAchievementReset(long Generation, Guid Id, DateTimeOffset ClearedAt);
internal sealed record ReadingCompletion(Guid BookId, DateTimeOffset CompletedAt, bool Historical, string FileHashes);
internal sealed class ReadingAchievementSnapshot
{
    public ReadingAchievementReset? Reset { get; set; }
    public List<ReadingAchievementAward> Awards { get; set; } = [];
    public List<ReadingCompletion> Completions { get; set; } = [];
}

internal static class ReaderAchievementStore
{
    public static async Task<bool> EnsureAsync(SqliteConnection c, SqliteTransaction t, CancellationToken ct)
    {
        using var schema = Command(c, t, """
            CREATE TABLE IF NOT EXISTS ReaderAchievementMetadata (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ReaderAchievementAwards (
                Id TEXT PRIMARY KEY, RuleVersion INTEGER NOT NULL, EarnedAt TEXT NOT NULL,
                Historical INTEGER NOT NULL, Seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ReaderCompletions (
                BookId TEXT PRIMARY KEY, CompletedAt TEXT NOT NULL, Historical INTEGER NOT NULL, FileHashes TEXT NOT NULL DEFAULT '');
            SELECT EXISTS(SELECT 1 FROM ReaderAchievementMetadata WHERE Key = 'initialized');
            """);
        var initialized = Convert.ToInt32(await schema.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0;
        using var tables = Command(c, t, "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Books');");
        if (Convert.ToInt32(await tables.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0)
        {
            using var triggers = Command(c, t, """
                INSERT OR IGNORE INTO ReaderCompletions (BookId, CompletedAt, Historical, FileHashes)
                    SELECT Id, UpdatedAt, 1, COALESCE((SELECT group_concat(lower(Sha256), ',') FROM BookFiles WHERE BookId = Books.Id), '')
                    FROM Books WHERE ReadingStatus = 2 AND NOT EXISTS(SELECT 1 FROM ReaderAchievementMetadata WHERE Key = 'initialized');
                CREATE TRIGGER IF NOT EXISTS ReaderCompletions_Status AFTER UPDATE OF ReadingStatus ON Books
                WHEN NEW.ReadingStatus = 2 AND OLD.ReadingStatus <> 2
                    AND julianday(NEW.UpdatedAt) > COALESCE((SELECT julianday(Value) FROM ReaderAchievementMetadata WHERE Key = 'cleared-at'), 0)
                BEGIN
                    INSERT OR IGNORE INTO ReaderCompletions (BookId, CompletedAt, Historical, FileHashes)
                    VALUES (NEW.Id, NEW.UpdatedAt, 0, COALESCE((SELECT group_concat(lower(Sha256), ',') FROM BookFiles WHERE BookId = NEW.Id), ''));
                END;
                CREATE TRIGGER IF NOT EXISTS ReaderCompletions_Insert AFTER INSERT ON Books
                WHEN NEW.ReadingStatus = 2
                    AND julianday(NEW.UpdatedAt) > COALESCE((SELECT julianday(Value) FROM ReaderAchievementMetadata WHERE Key = 'cleared-at'), 0)
                BEGIN
                    INSERT OR IGNORE INTO ReaderCompletions (BookId, CompletedAt, Historical, FileHashes) VALUES (NEW.Id, NEW.UpdatedAt, 1, '');
                END;
                CREATE TRIGGER IF NOT EXISTS ReaderCompletions_FileDelete BEFORE DELETE ON BookFiles
                BEGIN
                    UPDATE ReaderCompletions SET FileHashes = FileHashes || ',' || lower(OLD.Sha256) WHERE BookId = OLD.BookId;
                END;
                """);
            await triggers.ExecuteNonQueryAsync(ct);
        }
        using var mark = Command(c, t, "INSERT OR IGNORE INTO ReaderAchievementMetadata (Key, Value) VALUES ('initialized', '1');");
        await mark.ExecuteNonQueryAsync(ct);
        return !initialized;
    }

    public static async Task<ReadingAchievementSnapshot> CaptureAsync(SqliteConnection c, SqliteTransaction? t, CancellationToken ct)
    {
        var result = new ReadingAchievementSnapshot();
        using (var cmd = Command(c, t, "SELECT Value FROM ReaderAchievementMetadata WHERE Key = 'reset';"))
            if (await cmd.ExecuteScalarAsync(ct) is string json) result.Reset = JsonSerializer.Deserialize<ReadingAchievementReset>(json);
        using (var cmd = Command(c, t, "SELECT Id, RuleVersion, EarnedAt, Historical, Seen FROM ReaderAchievementAwards ORDER BY Id;"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                result.Awards.Add(new(reader.GetString(0), reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetBoolean(3), reader.GetBoolean(4)));
        using var tables = Command(c, t, "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'BookFiles');");
        var hasFiles = Convert.ToInt32(await tables.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0;
        using (var cmd = Command(c, t, hasFiles ? """
            SELECT BookId, CompletedAt, Historical, FileHashes || ',' || COALESCE(
                (SELECT group_concat(lower(Sha256), ',') FROM BookFiles WHERE BookId = r.BookId), '')
            FROM ReaderCompletions r ORDER BY BookId;
            """ : "SELECT BookId, CompletedAt, Historical, FileHashes FROM ReaderCompletions ORDER BY BookId;"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                result.Completions.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetBoolean(2), NormalizeHashes(reader.GetString(3))));
        return result;
    }

    private static string NormalizeHashes(string hashes) => string.Join(',', hashes.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Where(h => h.Length == 64 && h.All(Uri.IsHexDigit)).Select(h => h.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));

    // Hash-connected components also count independently imported copies only
    // once, including archived copies whose BookId can no longer be rebound.
    internal static IReadOnlyList<ReadingCompletion> DistinctCompletions(IEnumerable<ReadingCompletion> completions)
    {
        var groups = new List<ReadingCompletion>();
        foreach (var row in completions.OrderBy(r => r.CompletedAt).ThenBy(r => r.BookId))
        {
            var hashes = NormalizeHashes(row.FileHashes).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var matching = groups.Where(g => g.BookId == row.BookId || g.FileHashes.Split(',').Any(hashes.Contains)).ToArray();
            var first = matching.Append(row).OrderBy(r => r.CompletedAt).ThenBy(r => r.BookId).First();
            foreach (var match in matching) { groups.Remove(match); hashes.UnionWith(match.FileHashes.Split(',', StringSplitOptions.RemoveEmptyEntries)); }
            groups.Add(first with { FileHashes = string.Join(',', hashes.Order(StringComparer.Ordinal)), Historical = matching.Any(r => r.Historical) || row.Historical });
        }
        return groups.OrderBy(r => r.CompletedAt).ThenBy(r => r.BookId).ToArray();
    }

    public static async Task<ReadingAchievements> EvaluateAsync(SqliteConnection c, SqliteTransaction t,
        DateTimeOffset now, TimeZoneInfo zone, bool historical, CancellationToken ct)
    {
        var snapshot = await CaptureAsync(c, t, ct);
        var calendar = await ReadAsync(c, t, ct);
        var counts = ReadingAchievementRules.CountDays(calendar.Days, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).Date));
        using var sum = Command(c, t, "SELECT COALESCE(SUM(CumulativeSeconds), 0) FROM ReaderReadingStats;");
        var seconds = Convert.ToInt64(await sum.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        var completed = DistinctCompletions(snapshot.Completions);
        var awards = snapshot.Awards.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var items = new List<ReadingAchievement>();
        foreach (var rule in ReadingAchievementRules.Definitions)
        {
            var progress = rule.Kind switch
            {
                ReadingAchievementKind.Time => seconds,
                ReadingAchievementKind.Days => counts.ActiveDays,
                ReadingAchievementKind.Streak => counts.Longest,
                _ => completed.Count
            };
            awards.TryGetValue(rule.Id, out var award);
            if (award is null && progress >= rule.Target)
            {
                var fromHistory = historical;
                var earnedAt = now;
                if (rule.Kind == ReadingAchievementKind.Finished)
                {
                    var completion = completed[(int)rule.Target - 1];
                    earnedAt = completion.CompletedAt;
                    fromHistory |= completed.Take((int)rule.Target).Any(r => r.Historical);
                }
                award = new(rule.Id, ReadingAchievementRules.Version, earnedAt, fromHistory, fromHistory);
                await SaveAwardAsync(c, t, award, ct);
            }
            // The next consecutive milestone reflects the current run. A past
            // run still unlocks a badge permanently through the check above.
            items.Add(new(rule, rule.Kind == ReadingAchievementKind.Streak && award is null ? counts.Current : progress, award));
        }
        return new(items, counts.ActiveDays, counts.Current, counts.Longest, completed.Count, calendar.Legacy);
    }

    private static async Task SaveAwardAsync(SqliteConnection c, SqliteTransaction t, ReadingAchievementAward a, CancellationToken ct)
    {
        using var cmd = Command(c, t, """
            INSERT INTO ReaderAchievementAwards (Id, RuleVersion, EarnedAt, Historical, Seen) VALUES ($id, $version, $time, $history, $seen)
            ON CONFLICT(Id) DO UPDATE SET RuleVersion = excluded.RuleVersion, EarnedAt = excluded.EarnedAt, Historical = excluded.Historical, Seen = excluded.Seen;
            """);
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$version", a.RuleVersion);
        cmd.Parameters.AddWithValue("$time", a.EarnedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$history", a.Historical);
        cmd.Parameters.AddWithValue("$seen", a.Seen);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> MergeAsync(SqliteConnection c, SqliteTransaction t,
        IEnumerable<ReadingAchievementSnapshot?> remote, IReadOnlyDictionary<Guid, Guid> bookMap, CancellationToken ct)
    {
        var local = await CaptureAsync(c, t, ct);
        var sources = remote.OfType<ReadingAchievementSnapshot>().Prepend(local).ToArray();
        foreach (var source in sources) Validate(source);
        var reset = sources.Select(s => s.Reset).OfType<ReadingAchievementReset>().OrderByDescending(r => r.Generation).ThenByDescending(r => r.Id).FirstOrDefault();
        var current = sources.Where(s => s.Reset == reset).ToArray();
        if (local.Reset != reset) await ApplyResetAsync(c, t, reset!, ct);
        var changed = local.Reset != reset;
        foreach (var group in current.SelectMany(s => s.Awards).GroupBy(a => a.Id))
        {
            var first = group.OrderBy(a => a.EarnedAt).First();
            var merged = first with { Seen = group.Any(a => a.Seen), Historical = group.Any(a => a.Historical) };
            if (local.Reset == reset && local.Awards.Contains(merged)) continue;
            await SaveAwardAsync(c, t, merged, ct);
            changed = true;
        }
        foreach (var group in current.SelectMany(s => s.Completions)
            .Select(r => r with { BookId = bookMap.GetValueOrDefault(r.BookId, r.BookId) }).GroupBy(r => r.BookId))
        {
            var first = group.OrderBy(r => r.CompletedAt).First();
            var merged = first with { Historical = group.Any(r => r.Historical), FileHashes = NormalizeHashes(string.Join(',', group.Select(r => r.FileHashes))) };
            if (local.Reset == reset && local.Completions.Contains(merged)) continue;
            using var cmd = Command(c, t, """
                INSERT INTO ReaderCompletions (BookId, CompletedAt, Historical, FileHashes) VALUES ($book, $time, $history, $hashes)
                ON CONFLICT(BookId) DO UPDATE SET CompletedAt = excluded.CompletedAt, Historical = excluded.Historical, FileHashes = excluded.FileHashes;
                """);
            cmd.Parameters.AddWithValue("$book", merged.BookId.ToString());
            cmd.Parameters.AddWithValue("$time", merged.CompletedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$history", merged.Historical);
            cmd.Parameters.AddWithValue("$hashes", merged.FileHashes);
            await cmd.ExecuteNonQueryAsync(ct);
            changed = true;
        }
        return changed;
    }

    public static void Validate(ReadingAchievementSnapshot? state)
    {
        if (state is null) return;
        if (state.Reset is { } reset && (reset.Generation <= 0 || reset.Id == Guid.Empty))
            throw new InvalidDataException("Invalid achievement reset.");
        if (state.Awards.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 100 || a.RuleVersion < 1)
            || state.Completions.Any(c => c.BookId == Guid.Empty || c.FileHashes is null))
            throw new InvalidDataException("Invalid reading achievement.");
    }

    public static bool IsCovered(ReadingAchievementSnapshot? source, IEnumerable<ReadingAchievementSnapshot?> targets)
    {
        if (source is null) return true;
        var states = targets.OfType<ReadingAchievementSnapshot>().ToArray();
        if (states.Any(s => s.Reset is { } reset && (reset.Generation > (source.Reset?.Generation ?? 0)
            || reset.Generation == source.Reset?.Generation && reset.Id.CompareTo(source.Reset.Id) > 0))) return true;
        var current = states.Where(s => s.Reset == source.Reset).ToArray();
        if (source.Reset is not null && current.Length == 0) return false;
        return source.Awards.All(a => current.SelectMany(s => s.Awards).Any(b => a.Id == b.Id && b.EarnedAt <= a.EarnedAt
                && (!a.Seen || b.Seen) && (!a.Historical || b.Historical)))
            && source.Completions.All(a => current.SelectMany(s => s.Completions).Any(b => a.BookId == b.BookId
                && b.CompletedAt <= a.CompletedAt && (!a.Historical || b.Historical)
                && a.FileHashes.Split(',', StringSplitOptions.RemoveEmptyEntries).All(h => b.FileHashes.Split(',').Contains(h))));
    }

    public static async Task ResetAsync(SqliteConnection c, SqliteTransaction t, DateTimeOffset now, CancellationToken ct)
    {
        var old = await CaptureAsync(c, t, ct);
        await ApplyResetAsync(c, t, new(checked((old.Reset?.Generation ?? 0) + 1), Guid.NewGuid(), now), ct);
    }

    private static async Task ApplyResetAsync(SqliteConnection c, SqliteTransaction t, ReadingAchievementReset reset, CancellationToken ct)
    {
        using var cmd = Command(c, t, """
            DELETE FROM ReaderAchievementAwards;
            DELETE FROM ReaderCompletions;
            INSERT INTO ReaderAchievementMetadata (Key, Value) VALUES ('reset', $reset) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            INSERT INTO ReaderAchievementMetadata (Key, Value) VALUES ('cleared-at', $time) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """);
        cmd.Parameters.AddWithValue("$reset", JsonSerializer.Serialize(reset));
        cmd.Parameters.AddWithValue("$time", reset.ClearedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed partial class ReaderDataService
{
    public async Task<ReadingAchievements> GetReadingAchievementsAsync(CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var c = await OpenConnectionAsync(cancellationToken);
            await using var t = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken);
            var result = await ReaderAchievementStore.EvaluateAsync(c, t, _timeProvider.GetUtcNow(), _timeProvider.LocalTimeZone, false, cancellationToken);
            await t.CommitAsync(cancellationToken);
            return result;
        }
        finally { _databaseGate.Release(); }
    }

    public async Task MarkReadingAchievementsSeenAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var c = await OpenConnectionAsync(cancellationToken);
            await using var t = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken);
            var changed = false;
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                using var cmd = Command(c, t, "UPDATE ReaderAchievementAwards SET Seen = 1 WHERE Id = $id AND Seen = 0;");
                cmd.Parameters.AddWithValue("$id", id);
                changed |= await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
            }
            await t.CommitAsync(cancellationToken);
            if (changed) NotifyDataChanged(LocalDataChangeKind.ReadingStats);
        }
        finally { _databaseGate.Release(); }
    }
}
