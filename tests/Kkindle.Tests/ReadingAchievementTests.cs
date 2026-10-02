using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReadingAchievementRulesTests
{
    [Fact]
    public void BufferedReadingKeepsDatesAcrossInactivityAndOnlyRestoresUnsavedSlices()
    {
        var buffer = new ReadingTimeBuffer();
        buffer.AddSecond(DateTimeOffset.Parse("2026-10-01T23:59:50+08:00"));
        buffer.AddSecond(DateTimeOffset.Parse("2026-10-01T23:59:51+08:00"));
        buffer.AddSecond(DateTimeOffset.Parse("2026-10-02T12:00:00+08:00"));
        var slices = buffer.Drain();
        Assert.Equal(2, slices.Count);
        Assert.Equal(2, slices[0].Seconds);
        Assert.Equal(1, slices[1].Seconds);
        Assert.Equal(0, buffer.PendingSeconds);
        buffer.AddSecond(DateTimeOffset.Parse("2026-10-02T12:00:01+08:00"));
        buffer.Restore(slices.Skip(1));
        var retry = Assert.Single(buffer.Drain());
        Assert.Equal(2, retry.Seconds);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T12:00:01+08:00"), retry.EndedAt);
    }

    [Fact]
    public void StreakUsesFullHistoryFiveMinuteThresholdAndGraceForToday()
    {
        var today = new DateOnly(2026, 10, 2);
        var days = Enumerable.Range(1, 40).Select(i => new ReadingDashboardDay(today.AddDays(-i), 300)).ToList();
        days.Add(new(today, 299));
        Assert.Equal((40, 40, 40), ReadingAchievementRules.CountDays(days, today));
        days.Add(new(today, 1));
        Assert.Equal((41, 41, 41), ReadingAchievementRules.CountDays(days, today));
        Assert.Equal((41, 0, 41), ReadingAchievementRules.CountDays(days, today.AddDays(2)));
        days.Add(new(today.AddDays(3), 300));
        Assert.Equal((41, 41, 41), ReadingAchievementRules.CountDays(days, today));
    }

    [Fact]
    public void ReadingFlushSplitsLocalAndUtcMidnightWithoutLosingSeconds()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8 test", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var parts = ReadingLocalCalendar.Split(DateTimeOffset.Parse("2026-10-01T16:02:00Z"), 300, zone).ToArray();
        Assert.Equal(300, parts.Sum(p => p.Seconds));
        Assert.Contains(parts, p => p.LocalDate == "2026-10-01" && p.UtcDate == "2026-10-01" && p.Seconds == 180);
        Assert.Contains(parts, p => p.LocalDate == "2026-10-02" && p.UtcDate == "2026-10-01" && p.Seconds == 120);
        parts = ReadingLocalCalendar.Split(DateTimeOffset.Parse("2026-10-02T00:02:00Z"), 300, zone).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.All(parts, p => Assert.Equal("2026-10-02", p.LocalDate));
    }

    [Fact]
    public void FinishedBookDeduplicationHandlesTransitiveFormatMatches()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new string('a', 64);
        var b = new string('b', 64);
        var rows = new[]
        {
            new ReadingCompletion(Guid.NewGuid(), now, false, a),
            new ReadingCompletion(Guid.NewGuid(), now.AddMinutes(1), false, b),
            new ReadingCompletion(Guid.NewGuid(), now.AddMinutes(2), false, a + "," + b)
        };
        Assert.Single(ReaderAchievementStore.DistinctCompletions(rows));
    }

    [Fact]
    public async Task LocalCalendarRetainsLegacyDatesWithoutDoubleCountingNewData()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var clock = new AchievementClock(DateTimeOffset.Parse("2026-10-01T16:10:00Z"));
            var reader = new ReaderDataService(paths, clock);
            await reader.InitializeAsync();
            await reader.AddReadingTimeAsync(Guid.NewGuid(), Guid.NewGuid(), 300, 20, 2, 10);
            var board = await reader.GetReadingDashboardAsync();
            Assert.Equal(300, board.DailyReading.Single(d => d.Date == new DateOnly(2026, 10, 2)).ActiveSeconds);
            var badges = await reader.GetReadingAchievementsAsync();
            Assert.Equal(1, badges.ActiveDays);
            Assert.False(badges.HasLegacyDates);
            await using var c = new SqliteConnection($"Data Source={paths.Database}");
            await c.OpenAsync();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE S3SyncReadingDayCounters SET Seconds = Seconds + 60;";
            await cmd.ExecuteNonQueryAsync();
            board = await reader.GetReadingDashboardAsync();
            Assert.Equal(360, board.DailyReading.Sum(d => d.ActiveSeconds));
            Assert.Equal(60, board.DailyReading.Single(d => d.Date == new DateOnly(2026, 10, 1)).ActiveSeconds);
            Assert.True((await reader.GetReadingAchievementsAsync()).HasLegacyDates);
            await reader.AddReadingTimeAsync(Guid.NewGuid(), Guid.NewGuid(), 300, 20, 2, 10,
                intervalEnd: DateTimeOffset.Parse("2026-09-30T10:00:00-05:00"));
            board = await reader.GetReadingDashboardAsync();
            Assert.Equal(300, board.DailyReading.Single(d => d.Date == new DateOnly(2026, 9, 30)).ActiveSeconds);
            Assert.Equal(660, board.DailyReading.Sum(d => d.ActiveSeconds));
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    private sealed class AchievementClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("AchievementTest", TimeSpan.FromHours(8), "AchievementTest", "AchievementTest");
    }
}

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task AchievementsPersistAcrossRestartBacktrackingAndRemoval()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var book = await device.AddBookAsync();
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 3599, 100, 10, 10);
        Assert.DoesNotContain((await device.Reader.GetReadingAchievementsAsync()).Items, i => i.IsEarned);
        Assert.Equal(0, (await device.Reader.GetReadingDashboardAsync()).BooksFinished);
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 1, 100, 10, 10);
        await MarkAchievementBookFinishedAsync(device, book.BookId);
        var before = await device.Reader.GetReadingAchievementsAsync();
        Assert.Equal(2, before.Items.Count(i => i.IsEarned));
        Assert.All(before.Items.Where(i => i.IsEarned), i => Assert.False(i.Award!.Historical));
        await device.Reader.MarkReadingAchievementsSeenAsync(before.Items.Where(i => i.IsEarned).Select(i => i.Definition.Id));
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 10, 20, 2, 10);
        await device.SqlAsync("UPDATE Books SET ReadingStatus = 0 WHERE Id = $book;", ("$book", book.BookId.ToString()));
        await device.DeleteBookAsync(book.BookId);
        await device.Reader.InitializeAsync();
        var after = await device.Reader.GetReadingAchievementsAsync();
        Assert.Equal(1, after.FinishedBooks);
        Assert.Equal(1, (await device.Reader.GetReadingDashboardAsync()).BooksFinished);
        Assert.All(after.Items.Where(i => i.IsEarned), i => Assert.True(i.Award!.Seen));
        Assert.Equal(before.Items.Where(i => i.IsEarned).Select(i => i.Award!.EarnedAt), after.Items.Where(i => i.IsEarned).Select(i => i.Award!.EarnedAt));
    }

    [Fact]
    public async Task AchievementsMergeOfflineTimeAndSeenStateWithoutDuplicatingAwards()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 1800, 20, 2, 10);
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 1800, 40, 4, 10);
        await MarkAchievementBookFinishedAsync(a, book.BookId);
        await a.Reader.GetReadingAchievementsAsync();
        await a.Reader.MarkReadingAchievementsSeenAsync(["finished-1"]);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        foreach (var device in new[] { a, b })
        {
            var state = await device.Reader.GetReadingAchievementsAsync();
            Assert.Equal(2, state.Items.Count(i => i.IsEarned));
            Assert.True(state.Items.Single(i => i.Definition.Id == "finished-1").Award!.Seen);
            Assert.Equal(3600, (await device.Reader.GetReadingDashboardAsync()).TotalSeconds);
            Assert.Equal(3600, (await device.Reader.GetReadingDashboardAsync()).DailyReading.Sum(d => d.ActiveSeconds));
        }
    }

    [Fact]
    public async Task AchievementResetPreservesByDefaultAndClearWinsAgainstOfflineDevices()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 3600, 100, 10, 10);
        await MarkAchievementBookFinishedAsync(a, book.BookId);
        await a.Reader.GetReadingAchievementsAsync();
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.Reader.ResetReadingDataAsync();
        Assert.Equal(2, (await a.Reader.GetReadingAchievementsAsync()).Items.Count(i => i.IsEarned));
        await a.Reader.ResetReadingDataAsync(clearAchievements: true);
        await a.SyncAsync();
        // b has the old badges, completion and counters, and was offline at reset.
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 60, 60, 6, 10);
        await b.SyncAsync();
        await a.SyncAsync();
        foreach (var device in new[] { a, b })
        {
            await device.Reader.InitializeAsync();
            var badges = await device.Reader.GetReadingAchievementsAsync();
            Assert.DoesNotContain(badges.Items, i => i.IsEarned);
            Assert.Equal(0, badges.FinishedBooks);
            Assert.Equal(0, (await device.Reader.GetReadingDashboardAsync()).TotalSeconds);
        }
        Assert.Equal(4, bucket.Snapshot(a.SnapshotKey).Version);
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 3600, 10, 1, 10);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Single((await b.Reader.GetReadingAchievementsAsync()).Items, i => i.IsEarned);
    }

    [Fact]
    public async Task HistoricalStreaksBeyondFourteenDaysAreBackfilledOnce()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var book = await device.AddBookAsync();
        await device.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 10, 1, 0, 10);
        var first = DateOnly.FromDateTime(DateTime.Today).AddDays(-40);
        for (var i = 0; i < 40; i++)
            await device.SqlAsync("INSERT INTO S3SyncReadingDayCounters (BookFileId, ReadingDate, DeviceId, Seconds) VALUES ($file, $date, 'legacy-days', 300);",
                ("$file", book.FileId.ToString()), ("$date", first.AddDays(i).ToString("yyyy-MM-dd")));
        await device.SqlAsync("DELETE FROM ReaderAchievementMetadata WHERE Key = 'initialized';");
        await device.Reader.InitializeAsync();
        var badges = await device.Reader.GetReadingAchievementsAsync();
        Assert.Equal(40, badges.CurrentStreak);
        Assert.Equal(40, badges.LongestStreak);
        Assert.True(badges.Items.Single(i => i.Definition.Id == "streak-30").IsEarned);
        Assert.All(badges.Items.Where(i => i.IsEarned), i => Assert.True(i.Award!.Historical && i.Award.Seen));
        await device.Reader.InitializeAsync();
        Assert.Equal(badges.Items, (await device.Reader.GetReadingAchievementsAsync()).Items);
    }

    [Fact]
    public async Task LegacyHistoryDoesNotSuppressFutureLiveStreakAwards()
    {
        await using var device = await Device.CreateAsync(new MemoryBucket());
        var book = await device.AddBookAsync();
        for (var i = 1; i <= 2; i++)
            await device.SqlAsync("INSERT INTO S3SyncReadingDayCounters (BookFileId, ReadingDate, DeviceId, Seconds) VALUES ($file, $date, 'old-days', 300);",
                ("$file", book.FileId.ToString()), ("$date", DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd")));
        await device.Reader.InitializeAsync();
        // Use enough time after local midnight to make today's portion a full
        // five minutes even when this test runs immediately after midnight.
        var zone = TimeZoneInfo.Local;
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12), zone.GetUtcOffset(DateTime.Today.AddHours(12)));
        await using (var c = new SqliteConnection($"Data Source={device.Paths.Database}"))
        {
            await c.OpenAsync();
            await using var t = (SqliteTransaction)await c.BeginTransactionAsync();
            await ReadingLocalCalendar.RecordAsync(c, t, book.FileId, "new-days", noon, 300, zone, CancellationToken.None);
            var state = await ReaderAchievementStore.EvaluateAsync(c, t, noon, zone, false, CancellationToken.None);
            Assert.False(state.Items.Single(i => i.Definition.Id == "streak-3").Award!.Historical);
            Assert.False(state.Items.Single(i => i.Definition.Id == "streak-3").Award!.Seen);
            await t.CommitAsync();
        }
    }

    [Fact]
    public async Task IndependentlyFinishedCopiesAndFormatsCountOnlyOnceAfterSync()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var bookA = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2));
        var bookB = await AddMatchingReadingBookAsync(b, DateTimeOffset.UtcNow.AddMinutes(-1));
        await MarkAchievementBookFinishedAsync(a, bookA.BookId);
        await MarkAchievementBookFinishedAsync(b, bookB.BookId);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal(1, (await a.Reader.GetReadingAchievementsAsync()).FinishedBooks);
        Assert.Equal(1, (await b.Reader.GetReadingAchievementsAsync()).FinishedBooks);
    }

    private static Task MarkAchievementBookFinishedAsync(Device device, Guid book) => device.SqlAsync(
        "UPDATE Books SET ReadingStatus = 2, UpdatedAt = $time WHERE Id = $book;",
        ("$book", book.ToString()), ("$time", DateTimeOffset.UtcNow.ToString("O")));
}
