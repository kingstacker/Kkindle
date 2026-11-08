using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReadingPeriodTests
{
    [Theory]
    [InlineData(2026, 11, 8, 2026, 11, 2, 2026, 11, 8)]
    [InlineData(2026, 11, 9, 2026, 11, 9, 2026, 11, 15)]
    [InlineData(2027, 1, 1, 2026, 12, 28, 2027, 1, 3)]
    public void WeekStartsOnMondayAndCrossesYearBoundaries(int year, int month, int day,
        int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        var range = ReadingPeriodRange.Create(ReadingPeriod.Week, new(year, month, day));
        Assert.Equal(new DateOnly(fromYear, fromMonth, fromDay), range.StartDate);
        Assert.Equal(new DateOnly(toYear, toMonth, toDay), range.EndDate);
    }

    [Theory]
    [InlineData(2024, 2, 29)]
    [InlineData(2025, 2, 28)]
    [InlineData(2026, 11, 30)]
    [InlineData(2026, 12, 31)]
    public void MonthUsesTheActualNumberOfDays(int year, int month, int count)
    {
        var today = new DateOnly(year, month, 1);
        var range = ReadingPeriodRange.Create(ReadingPeriod.Month, today);
        Assert.Equal(count, range.BuildBuckets([], today).Count);
        Assert.Equal(new DateOnly(year, month, count), range.EndDate);
    }

    [Fact]
    public void YearCombinesAllDaysIncludingLeapDayAndExcludesOtherYearsAndFutureDays()
    {
        var today = new DateOnly(2024, 12, 30);
        var range = ReadingPeriodRange.Create(ReadingPeriod.Year, today);
        var buckets = range.BuildBuckets([
            new(new(2023, 12, 31), 9), new(new(2024, 1, 1), 60),
            new(new(2024, 2, 28), 100), new(new(2024, 2, 29), 200),
            new(new(2024, 11, 1), 400), new(new(2024, 12, 30), 500),
            new(new(2024, 12, 31), 999), new(new(2025, 1, 1), 999)], today);
        Assert.Equal(12, buckets.Count);
        Assert.Equal(300, buckets[1].ActiveSeconds);
        Assert.Equal(new DateOnly(2024, 2, 29), buckets[1].EndDate);
        Assert.Equal(1260, buckets.Sum(bucket => bucket.ActiveSeconds));
        Assert.Equal(0, buckets[2].ActiveSeconds);
    }

    [Fact]
    public async Task DashboardLoadsAll366DaysAndKeepsTheDefaultWindowCompatible()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var book = Guid.NewGuid(); var file = Guid.NewGuid();
            await service.AddReadingTimeAsync(book, file, 77, 1, 0, 1);
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE S3SyncReadingDayCounters SET ReadingDate = '2024-01-01';";
                await command.ExecuteNonQueryAsync();
            }
            var dashboard = await service.GetReadingDashboardAsync(dayCount: 366, throughDate: new(2024, 12, 31));
            Assert.Equal(366, dashboard.DailyReading.Count);
            Assert.Equal(77, dashboard.DailyReading[0].ActiveSeconds);
            Assert.Equal(new DateOnly(2024, 1, 1), dashboard.DailyReading[0].Date);
            Assert.Contains(dashboard.DailyReading, day => day.Date == new DateOnly(2024, 2, 29));
            Assert.Equal(14, (await service.GetReadingDashboardAsync()).DailyReading.Count);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }
}
