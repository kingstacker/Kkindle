namespace Kkindle.Core;

/// <summary>One milestone with the reader's current standing against it.</summary>
public sealed record ReadingMedal(string Name, string Rule, long Value, long Target, string Icon)
{
    public bool IsUnlocked => Value >= Target;
    public double Fraction => Math.Clamp((double)Value / Target, 0, 1);
}

/// <summary>Milestones shared by every shell, derived only from the reading dashboard.</summary>
public static class ReadingMedalCatalog
{
    public static List<ReadingMedal> Build(ReadingDashboard dashboard)
    {
        string L(string chinese, string english) => UiText.IsEnglish ? english : chinese;
        var medals = new (string Name, string Rule, long Value, long Target, string Icon)[]
        {
            (L("初见书页", "First pages"), L("累计阅读 1 小时", "Read for 1 hour"), dashboard.TotalSeconds, 3600,
                "M3,5 Q8,3 12,6 Q16,3 21,5 L21,20 Q16,18 12,21 Q8,18 3,20 Z M12,6 L12,21"),
            (L("读完一程", "First finish"), L("读完 1 本书", "Finish 1 book"), dashboard.BooksFinished, 1,
                "M5,3 L19,3 L19,21 L5,21 Z M8,12 L11,15 L16,9"),
            (L("时光藏书", "Time well read"), L("累计阅读 10 小时", "Read for 10 hours"), dashboard.TotalSeconds, 36000,
                "M12,2 A10,10 0 1 1 11.99,2 M12,6 L12,12 L16,14"),
            (L("字里拾光", "Collected thoughts"), L("留下 10 条批注", "Write 10 annotations"), dashboard.AnnotationCount, 10,
                "M4,20 L8,19 L20,7 Q22,5 20,3 Q18,1 16,3 L4,15 Z M14,5 L18,9 M4,20 L20,20"),
            (L("书海漫游", "Book wanderer"), L("读完 10 本书", "Finish 10 books"), dashboard.BooksFinished, 10,
                "M3,5 L7,5 L7,21 L3,21 Z M10,3 L14,3 L14,21 L10,21 Z M17,6 L21,5 L24,20 L20,21 Z"),
            (L("百时之光", "A hundred hours"), L("累计阅读 100 小时", "Read for 100 hours"), dashboard.TotalSeconds, 360000,
                "M12,3 L14.8,8.7 L21,9.6 L16.5,14 L17.6,20.2 L12,17.3 L6.4,20.2 L7.5,14 L3,9.6 L9.2,8.7 Z"),
            (L("阅读萌芽", "First sprout"), L("累计阅读 15 分钟", "Read for 15 minutes"), dashboard.TotalSeconds, 900,
                "M12,21 L12,11 M12,15 Q3,15 3,6 Q12,6 12,15 M12,11 Q12,3 21,3 Q21,11 12,11"),
            (L("半日闲读", "Quiet hours"), L("累计阅读 5 小时", "Read for 5 hours"), dashboard.TotalSeconds, 18000,
                "M17,3 A9,9 0 1 0 21,16 Q11,18 10,8 Q10,4 12,2"),
            (L("沉浸之境", "Deep reading"), L("累计阅读 25 小时", "Read for 25 hours"), dashboard.TotalSeconds, 90000,
                "M3,20 L9,8 L13,15 L17,5 L23,20 Z M15,9 L19,9"),
            (L("光阴书签", "Hours treasured"), L("累计阅读 50 小时", "Read for 50 hours"), dashboard.TotalSeconds, 180000,
                "M6,3 L18,3 L18,21 L12,17 L6,21 Z M9,8 L15,8 M9,11 L15,11"),
            (L("长读成河", "River of words"), L("累计阅读 200 小时", "Read for 200 hours"), dashboard.TotalSeconds, 720000,
                "M2,6 Q7,2 12,6 Q17,10 22,6 M2,12 Q7,8 12,12 Q17,16 22,12 M2,18 Q7,14 12,18 Q17,22 22,18"),
            (L("千时星辰", "Thousand hours"), L("累计阅读 1000 小时", "Read for 1000 hours"), dashboard.TotalSeconds, 3600000,
                "M12,2 L14,9 L21,12 L14,15 L12,22 L10,15 L3,12 L10,9 Z M20,2 L20,6 M18,4 L22,4"),
            (L("三卷余香", "Three volumes"), L("读完 3 本书", "Finish 3 books"), dashboard.BooksFinished, 3,
                "M4,4 L20,4 L20,9 L4,9 Z M4,10 L18,10 L18,15 L4,15 Z M4,16 L21,16 L21,21 L4,21 Z"),
            (L("五卷入心", "Five stories"), L("读完 5 本书", "Finish 5 books"), dashboard.BooksFinished, 5,
                "M12,20 L3,11 Q0,3 7,3 Q10,3 12,7 Q14,3 17,3 Q24,3 21,11 Z"),
            (L("书山拾级", "Twenty summits"), L("读完 20 本书", "Finish 20 books"), dashboard.BooksFinished, 20,
                "M3,21 L3,16 L8,16 L8,11 L13,11 L13,6 L18,6 L18,2 M18,2 L23,4 L18,6"),
            (L("五十风景", "Fifty journeys"), L("读完 50 本书", "Finish 50 books"), dashboard.BooksFinished, 50,
                "M4,5 L20,5 L18,15 L6,15 Z M7,15 L5,21 M17,15 L19,21 M12,5 L12,1 M8,21 L16,21"),
            (L("一笔心声", "First thought"), L("留下 1 条批注", "Write 1 annotation"), dashboard.AnnotationCount, 1,
                "M4,3 L20,3 L20,16 L10,16 L4,21 Z M8,7 L16,7 M8,11 L13,11"),
            (L("随手留痕", "Saved places"), L("收藏 10 枚书签", "Save 10 bookmarks"), dashboard.BookmarkCount, 10,
                "M7,2 L19,2 L19,19 L13,15 L7,19 Z M4,6 L4,22 L10,18"),
            (L("百句回响", "Hundred thoughts"), L("留下 100 条批注", "Write 100 annotations"), dashboard.AnnotationCount, 100,
                "M3,4 L9,4 L9,11 Q9,17 3,19 L3,16 Q6,14 6,11 L3,11 Z M15,4 L21,4 L21,11 Q21,17 15,19 L15,16 Q18,14 18,11 L15,11 Z"),
            (L("百卷书心", "Hundred books"), L("读完 100 本书", "Finish 100 books"), dashboard.BooksFinished, 100,
                "M3,7 L7,11 L12,3 L17,11 L21,7 L19,20 L5,20 Z M6,16 L18,16")
        }.ToList();
        // Milestone families can grow independently without changing rendering or sync storage.
        void AddMilestones(long value, int[] targets, string[] names, string[] englishNames,
            string chineseRule, string englishRule, long multiplier, string icon)
        {
            for (var i = 0; i < targets.Length; i++)
                medals.Add((L(names[i], englishNames[i]),
                    L(string.Format(chineseRule, targets[i]), string.Format(englishRule, targets[i])),
                    value, targets[i] * multiplier, icon));
        }
        AddMilestones(dashboard.TotalSeconds, [2, 75, 300, 500, 2000],
            ["两时清欢", "静读流年", "三百晨昏", "五百灯火", "岁月书航"],
            ["Two quiet hours", "Seventy-five hours", "Three hundred hours", "Five hundred hours", "Two thousand hours"],
            "累计阅读 {0} 小时", "Read for {0} hours", 3600,
            "M12,2 A10,10 0 1 1 11.99,2 M12,5 L12,12 L17,12 M12,19 L12,21 M3,12 L5,12 M19,12 L21,12");
        AddMilestones(dashboard.BooksFinished, [2, 15, 30, 75, 200],
            ["双卷同行", "十五扇窗", "三十重山", "七五书旅", "二百世界"],
            ["Two volumes", "Fifteen windows", "Thirty summits", "Seventy-five journeys", "Two hundred worlds"],
            "读完 {0} 本书", "Finish {0} books", 1,
            "M4,3 L18,3 L18,19 L6,19 Q2,19 2,21 Q2,23 6,23 L20,23 M4,3 L4,19 M8,7 L14,7 M8,11 L14,11");
        AddMilestones(dashboard.BookmarkCount, [1, 5, 25, 50, 100],
            ["一页珍藏", "五处书影", "页间足迹", "五十路标", "百页珍藏"],
            ["First bookmark", "Five places", "Twenty-five places", "Fifty signposts", "Hundred bookmarks"],
            "收藏 {0} 枚书签", "Save {0} bookmarks", 1,
            "M5,2 L19,2 L19,22 L12,17 L5,22 Z M9,7 L15,7 M12,4 L12,10");
        AddMilestones(dashboard.AnnotationCount, [5, 25, 50, 250, 500],
            ["五笔灵感", "廿五回声", "五十思绪", "字间对话", "五百心语"],
            ["Five thoughts", "Twenty-five echoes", "Fifty reflections", "Conversations in print", "Five hundred thoughts"],
            "留下 {0} 条批注", "Write {0} annotations", 1,
            "M3,4 L21,4 L21,17 L15,17 L12,21 L9,17 L3,17 Z M7,8 L17,8 M7,12 L14,12");
        return medals.Select(m => new ReadingMedal(m.Name, m.Rule, m.Value, m.Target, m.Icon)).ToList();
    }

    /// <summary>
    /// Consecutive active days inside the window, ending today or yesterday:
    /// today alone may simply not have started yet without breaking the run.
    /// </summary>
    public static int ComputeStreakDays(IReadOnlyList<ReadingDashboardDay> days)
    {
        var streak = 0;
        for (var i = days.Count - 1; i >= 0; i--)
        {
            if (days[i].ActiveSeconds > 0) streak++;
            else if (streak == 0 && i == days.Count - 1) continue;
            else break;
        }
        return streak;
    }
}
