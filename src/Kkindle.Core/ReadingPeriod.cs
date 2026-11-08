namespace Kkindle.Core;

public enum ReadingPeriod { Week, Month, Year }

/// <summary>A chart bucket; yearly charts combine the daily counters by month.</summary>
public sealed record ReadingPeriodBucket(DateOnly StartDate, DateOnly EndDate, long ActiveSeconds)
{
    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;
}

/// <summary>
/// Calendar periods shared by all frontends. Reading counters use UTC dates,
/// weeks start on Monday, and dates that have not arrived carry no reading time.
/// </summary>
public sealed record ReadingPeriodRange(ReadingPeriod Period, DateOnly StartDate, DateOnly EndDate)
{
    public static ReadingPeriodRange Create(ReadingPeriod period, DateOnly today) => period switch
    {
        ReadingPeriod.Week => Week(today),
        ReadingPeriod.Month => new(period, new(today.Year, today.Month, 1),
            new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month))),
        ReadingPeriod.Year => new(period, new(today.Year, 1, 1), new(today.Year, 12, 31)),
        _ => throw new ArgumentOutOfRangeException(nameof(period))
    };

    private static ReadingPeriodRange Week(DateOnly today)
    {
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        return new(ReadingPeriod.Week, monday, monday.AddDays(6));
    }

    public IReadOnlyList<ReadingPeriodBucket> BuildBuckets(IEnumerable<ReadingDashboardDay> days, DateOnly today)
    {
        var values = days.Where(day => day.Date >= StartDate && day.Date <= EndDate && day.Date <= today)
            .GroupBy(day => day.Date).ToDictionary(group => group.Key, group => group.Sum(day => day.ActiveSeconds));
        if (Period == ReadingPeriod.Year)
            return Enumerable.Range(1, 12).Select(month =>
            {
                var start = new DateOnly(StartDate.Year, month, 1);
                var end = new DateOnly(start.Year, month, DateTime.DaysInMonth(start.Year, month));
                return new ReadingPeriodBucket(start, end,
                    values.Where(day => day.Key >= start && day.Key <= end).Sum(day => day.Value));
            }).ToArray();
        return Enumerable.Range(0, EndDate.DayNumber - StartDate.DayNumber + 1)
            .Select(offset => StartDate.AddDays(offset))
            .Select(day => new ReadingPeriodBucket(day, day, values.GetValueOrDefault(day))).ToArray();
    }
}
