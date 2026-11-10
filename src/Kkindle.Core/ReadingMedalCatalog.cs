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
        // Give every milestone its own silhouette, including milestones in the same family.
        void AddMilestones(long value, int[] targets, string[] names, string[] englishNames,
            string chineseRule, string englishRule, long multiplier, string[] icons)
        {
            for (var i = 0; i < targets.Length; i++)
                medals.Add((L(names[i], englishNames[i]),
                    L(string.Format(chineseRule, targets[i]), string.Format(englishRule, targets[i])),
                    value, targets[i] * multiplier, icons[i]));
        }
        AddMilestones(dashboard.TotalSeconds, [2, 75, 300, 500, 2000],
            ["两时清欢", "静读流年", "三百晨昏", "五百灯火", "岁月书航"],
            ["Two quiet hours", "Seventy-five hours", "Three hundred hours", "Five hundred hours", "Two thousand hours"],
            "累计阅读 {0} 小时", "Read for {0} hours", 3600,
            [
                // Tea, hourglass, sunrise, reading lamp, and a sailboat.
                "M3,8 H17 V14 Q17,19 10,19 Q3,19 3,14 Z M17,9 H19 Q23,9 23,12 Q23,15 17,15 M2,22 H19 M7,2 V5 M12,2 V5",
                "M5,2 H19 M5,22 H19 M6,2 V6 Q6,9 12,12 Q18,9 18,6 V2 M6,22 V18 Q6,15 12,12 Q18,15 18,18 V22 M8,5 H16 M8,19 L12,15 L16,19 Z",
                "M2,16 H22 M4,21 H20 M6,16 A6,6 0 0 1 18,16 M12,2 V5 M4,7 L6,9 M20,7 L18,9 M1,12 H3 M21,12 H23",
                "M6,11 L8,3 H16 L18,11 Z M12,11 V20 M7,21 H17 M3,15 L5,13 M21,15 L19,13",
                "M12,2 V16 M12,3 L3,14 H12 M15,6 L21,14 H15 Z M2,17 H22 L18,21 H6 Z"
            ]);
        AddMilestones(dashboard.BooksFinished, [2, 15, 30, 75, 200],
            ["双卷同行", "十五扇窗", "三十重山", "七五书旅", "二百世界"],
            ["Two volumes", "Fifteen windows", "Thirty summits", "Seventy-five journeys", "Two hundred worlds"],
            "读完 {0} 本书", "Finish {0} books", 1,
            [
                // Linked journeys, a window, compass, travel case, and the world.
                "M9,15 L15,9 M11,6 L14,3 Q18,0 21,4 Q24,8 21,11 L18,14 M6,11 L3,14 Q0,18 4,21 Q8,24 11,21 L14,18",
                "M5,21 V9 A7,7 0 0 1 19,9 V21 Z M12,2 V21 M5,12 H19",
                "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M16,8 L14,14 L8,16 L10,10 Z",
                "M8,6 V4 Q8,2 10,2 H14 Q16,2 16,4 V6 M5,6 H19 Q21,6 21,8 V19 Q21,21 19,21 H5 Q3,21 3,19 V8 Q3,6 5,6 Z M8,6 V21 M16,6 V21",
                "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M2,12 H22 M12,2 C6,7 6,17 12,22 C18,17 18,7 12,2"
            ]);
        AddMilestones(dashboard.BookmarkCount, [1, 5, 25, 50, 100],
            ["一页珍藏", "五处书影", "页间足迹", "五十路标", "百页珍藏"],
            ["First bookmark", "Five places", "Twenty-five places", "Fifty signposts", "Hundred bookmarks"],
            "收藏 {0} 枚书签", "Save {0} bookmarks", 1,
            [
                // A saved page, picture, footsteps, signpost, and a treasured gem.
                "M14,2 H5 V22 H19 V7 Z M14,2 V7 H19 M8,12 H16 M8,16 H13",
                "M3,4 H21 V20 H3 Z M3,16 L8,11 L13,16 L17,12 L21,16 M14,8 A1.5,1.5 0 1 0 17,8 A1.5,1.5 0 1 0 14,8",
                "M7,2 C3,2 3,7 4,10 L5,12 Q6,14 8,13 Q10,12 9,10 V6 Q9,2 7,2 Z M5,16 Q7,15 9,16 V19 Q7,21 5,19 Z M17,6 C21,6 21,11 20,14 L19,16 Q18,18 16,17 Q14,16 15,14 V10 Q15,6 17,6 Z M15,20 Q17,19 19,20 V22 H15 Z",
                "M12,2 V22 M4,5 H18 L22,8 L18,11 H4 Z M20,13 H6 L2,16 L6,19 H20 Z",
                "M3,8 L7,3 H17 L21,8 L12,21 Z M3,8 H21 M7,3 L12,21 L17,3"
            ]);
        AddMilestones(dashboard.AnnotationCount, [5, 25, 50, 250, 500],
            ["五笔灵感", "廿五回声", "五十思绪", "字间对话", "五百心语"],
            ["Five thoughts", "Twenty-five echoes", "Fifty reflections", "Conversations in print", "Five hundred thoughts"],
            "留下 {0} 条批注", "Write {0} annotations", 1,
            [
                // Inspiration, echoes, reflection, conversation, and a letter.
                "M8,15 C3,11 5,3 12,3 C19,3 21,11 16,15 V18 H8 Z M9,21 H15 M12,7 V12",
                "M3,8 H7 L12,4 V20 L7,16 H3 Z M16,8 Q20,12 16,16 M19,4 Q27,12 19,20",
                "M12,5 C9,1 5,2 5,6 C1,7 2,12 4,13 C1,17 5,22 9,20 Q12,22 12,18 V5 M12,5 C15,1 19,2 19,6 C23,7 22,12 20,13 C23,17 19,22 15,20 Q12,22 12,18 M5,6 Q8,6 8,9 M4,13 Q8,11 8,15 M19,6 Q16,6 16,9 M20,13 Q16,11 16,15",
                "M12,3 H4 Q2,3 2,5 V11 Q2,13 4,13 H6 L9,16 V13 H12 Q14,13 14,11 V5 Q14,3 12,3 Z M17,8 H20 Q22,8 22,10 V16 Q22,18 20,18 H18 L15,21 V18 H12 Q10,18 10,16",
                "M3,5 H21 V19 H3 Z M3,5 L12,12 L21,5 M3,19 L9,12 M21,19 L15,12"
            ]);
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
