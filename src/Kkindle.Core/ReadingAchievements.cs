namespace Kkindle.Core;

public enum ReadingAchievementKind { Time, Days, Streak, Finished }

public sealed record ReadingAchievementDefinition(string Id, string Name, ReadingAchievementKind Kind, long Target, int Level);

public sealed record ReadingAchievementAward(string Id, int RuleVersion, DateTimeOffset EarnedAt, bool Historical, bool Seen);

public sealed record ReadingAchievement(ReadingAchievementDefinition Definition, long Progress, ReadingAchievementAward? Award)
{
    public bool IsEarned => Award is not null;
    public double Percent => IsEarned ? 100 : Math.Clamp(Progress * 100d / Definition.Target, 0, 100);
}

public sealed record ReadingAchievements(IReadOnlyList<ReadingAchievement> Items, int ActiveDays, int CurrentStreak,
    int LongestStreak, int FinishedBooks, bool HasLegacyDates);

public static class ReadingAchievementRules
{
    public const int Version = 1;
    public const int DailyMinimumSeconds = 300;
    public static IReadOnlyList<ReadingAchievementDefinition> Definitions { get; } = Array.AsReadOnly<ReadingAchievementDefinition>(
    [
        new("time-1", "初入书海", ReadingAchievementKind.Time, 3600, 1),
        new("time-10", "渐入佳境", ReadingAchievementKind.Time, 36000, 2),
        new("time-100", "百时书香", ReadingAchievementKind.Time, 360000, 3),
        new("days-7", "七日留痕", ReadingAchievementKind.Days, 7, 1),
        new("days-30", "三十日光", ReadingAchievementKind.Days, 30, 2),
        new("days-100", "百日相伴", ReadingAchievementKind.Days, 100, 3),
        new("streak-3", "三日之约", ReadingAchievementKind.Streak, 3, 1),
        new("streak-7", "一周相伴", ReadingAchievementKind.Streak, 7, 2),
        new("streak-30", "月读不辍", ReadingAchievementKind.Streak, 30, 3),
        new("finished-1", "第一卷", ReadingAchievementKind.Finished, 1, 1),
        new("finished-5", "五卷书香", ReadingAchievementKind.Finished, 5, 2),
        new("finished-20", "二十卷行", ReadingAchievementKind.Finished, 20, 3)
    ]);

    public static (int ActiveDays, int Current, int Longest) CountDays(IEnumerable<ReadingDashboardDay> days, DateOnly today)
    {
        var active = days.Where(d => d.Date <= today).GroupBy(d => d.Date)
            .Where(g => g.Sum(d => d.ActiveSeconds) >= DailyMinimumSeconds).Select(g => g.Key).Order().ToArray();
        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in active)
        {
            run = previous?.AddDays(1) == day ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }
        var current = previous == today || previous == today.AddDays(-1) ? run : 0;
        return (active.Length, current, longest);
    }
}
