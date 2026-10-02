using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public sealed class ReadingAchievementViewModel(ReadingAchievement item)
{
    public ReadingAchievement Item { get; } = item;
    public string Name => UiText.Get(Item.Definition.Name);
    public bool IsEarned => Item.IsEarned;
    public double IconOpacity => IsEarned ? 1 : 0.3;
    public double Percent => Item.Percent;
    public string Level => new('•', Item.Definition.Level);
    public string Category => UiText.Get(Item.Definition.Kind switch
    {
        ReadingAchievementKind.Time => "阅读时光", ReadingAchievementKind.Days => "阅读足迹",
        ReadingAchievementKind.Streak => "阅读习惯", _ => "完读旅程"
    });
    public string Rule => Item.Definition.Kind switch
    {
        ReadingAchievementKind.Time => UiText.Get("累计阅读 {0} 小时", Item.Definition.Target / 3600),
        ReadingAchievementKind.Days => UiText.Get("累计阅读 {0} 天，每天至少 5 分钟", Item.Definition.Target),
        ReadingAchievementKind.Streak => UiText.Get("连续阅读 {0} 天，每天至少 5 分钟", Item.Definition.Target),
        _ => UiText.Get("将 {0} 本不同书籍标记为已读", Item.Definition.Target)
    };
    public string ProgressLabel => IsEarned ? UiText.Get("已获得") : Item.Definition.Kind switch
    {
        ReadingAchievementKind.Time => UiText.Get("{0:0.#} / {1} 小时", Item.Progress / 3600d, Item.Definition.Target / 3600),
        ReadingAchievementKind.Finished => UiText.Get("{0} / {1} 本", Item.Progress, Item.Definition.Target),
        _ => UiText.Get("{0} / {1} 天", Item.Progress, Item.Definition.Target)
    };
    public string EarnedLabel => Item.Award is not { } award ? UiText.Get("尚未获得，继续积累阅读足迹。")
        : award.Historical ? UiText.Get("根据历史记录获得")
        : UiText.Get("获得于 {0}", award.EarnedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    public string AccessibleLabel => $"{Name} · {Rule} · {ProgressLabel}";
    public Geometry Icon => Geometry.Parse(Item.Definition.Kind switch
    {
        ReadingAchievementKind.Time => "M12,3 A9,9,0,1,1,11.99,3 M12,7 L12,12 L16,14",
        ReadingAchievementKind.Days => "M5,5 L19,5 L19,21 L5,21 Z M8,2 L8,8 M16,2 L16,8 M5,10 L19,10 M8,14 L10,14 M14,14 L16,14 M8,17 L10,17",
        ReadingAchievementKind.Streak => "M12,22 L12,12 M12,16 C4,16,3,10,3,7 C9,7,12,10,12,16 M12,12 C12,6,16,3,21,3 C21,9,18,12,12,12",
        _ => "M12,6 C9,3,5,3,2,4 L2,20 C6,19,9,19,12,22 C15,19,18,19,22,20 L22,4 C19,3,15,3,12,6 Z M12,6 L12,22 M16,8 L19,8 M16,11 L19,11"
    });
}

public partial class MainWindow
{
    public ObservableCollection<ReadingAchievementViewModel> RecentAchievements { get; } = [];
    public ObservableCollection<ReadingAchievementViewModel> VisibleAchievements { get; } = [];
    private IReadOnlyList<ReadingAchievementViewModel> _achievementItems = [];
    private bool _updatingAchievements;
    private bool _achievementNoticeBusy;
    private IDisposable? _achievementToastHide;

    private void PopulateReadingAchievements(ReadingAchievements achievements)
    {
        _achievementItems = achievements.Items.Select(i => new ReadingAchievementViewModel(i)).ToArray();
        var earned = _achievementItems.Where(i => i.IsEarned).OrderByDescending(i => i.Item.Award!.EarnedAt).ToArray();
        AchievementCountText.Text = T("已获得 {0} / {1}", earned.Length, achievements.Items.Count);
        AchievementHistoryText.IsVisible = achievements.HasLegacyDates;
        AchievementStreakDetailText.Text = T("累计 {0} 天 · 最长连续 {1} 天", achievements.ActiveDays, achievements.LongestStreak);
        DashboardStreakText.Text = T("{0} 天", achievements.CurrentStreak);
        var next = _achievementItems.Where(i => !i.IsEarned).OrderByDescending(i => i.Percent).ThenBy(i => i.Item.Definition.Level).FirstOrDefault();
        AchievementNextText.Text = next is null ? T("本期勋章已全部获得，愿阅读继续相伴。")
            : T("下一枚：{0} · {1}", next.Name, next.ProgressLabel);
        RecentAchievements.Clear();
        foreach (var item in earned.Take(3)) RecentAchievements.Add(item);
        if (RecentAchievements.Count == 0)
            foreach (var item in _achievementItems.Where(i => i.Item.Definition.Level == 1).Take(3)) RecentAchievements.Add(item);
        _updatingAchievements = true;
        try { AchievementNotificationsCheckBox.IsChecked = _appSettings.ReadingAchievementNotificationsEnabled; }
        finally { _updatingAchievements = false; }
        ApplyAchievementFilter();
    }

    private void ApplyAchievementFilter()
    {
        if (AchievementFilterBox is null) return;
        VisibleAchievements.Clear();
        foreach (var item in _achievementItems.Where(i => AchievementFilterBox.SelectedIndex switch
                 { 1 => i.IsEarned, 2 => !i.IsEarned, _ => true }))
            VisibleAchievements.Add(item);
        AchievementFilterEmptyText.IsVisible = VisibleAchievements.Count == 0;
    }

    private void AchievementFilterBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyAchievementFilter();
        if (AchievementDetails is not null) AchievementDetails.IsVisible = false;
    }

    private void AchievementViewAllButton_Click(object? sender, RoutedEventArgs e)
    {
        AchievementWall.IsVisible = !AchievementWall.IsVisible;
        AchievementPreview.IsVisible = !AchievementWall.IsVisible;
        AchievementViewAllButton.Content = AchievementWall.IsVisible ? T("收起勋章") : T("查看全部");
        AchievementDetails.IsVisible = false;
        if (AchievementWall.IsVisible) AchievementFilterBox.Focus();
    }

    private void AchievementCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReadingAchievementViewModel item }) return;
        AchievementDetails.IsVisible = true;
        AchievementDetailName.Text = item.Name + " · " + item.Category;
        AchievementDetailRule.Text = item.Rule;
        AchievementDetailProgress.Text = item.ProgressLabel + " · " + item.EarnedLabel;
    }

    private async void AchievementNotificationsCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingAchievements || !_stage3Ready) return;
        var enabled = AchievementNotificationsCheckBox.IsChecked == true;
        if (_appSettings.ReadingAchievementNotificationsEnabled == enabled) return;
        try
        {
            _appSettings = _appSettings with { ReadingAchievementNotificationsEnabled = enabled };
            await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DashboardStatusText.Text = UiText.Localize(exception.Message);
            DashboardStatusText.IsVisible = true;
        }
    }

    private async Task ShowPendingAchievementNoticeAsync(ReadingAchievements? achievements = null)
    {
        if (_achievementNoticeBusy || !_stage3Ready || ReaderRoot.IsVisible || _readingDataResetBusy) return;
        _achievementNoticeBusy = true;
        try
        {
            achievements ??= await _readerData.GetReadingAchievementsAsync(_lifetimeCancellation.Token);
            if (ReaderRoot.IsVisible || _readingDataResetBusy) return;
            var pending = achievements.Items.Where(i => i.Award is { Seen: false }).ToArray();
            if (pending.Length == 0) return;
            if (_appSettings.ReadingAchievementNotificationsEnabled)
            {
                var names = string.Join("、", pending.Take(3).Select(i => T(i.Definition.Name)));
                var notice = T("获得阅读勋章：{0}", names);
                if (pending.Length > 3) notice = T("获得 {0} 枚阅读勋章：{1}…", pending.Length, names);
                AchievementToastText.Text = notice;
                AchievementToast.IsVisible = true;
                _achievementToastHide?.Dispose();
                _achievementToastHide = DispatcherTimer.RunOnce(() => AchievementToast.IsVisible = false, TimeSpan.FromSeconds(4));
            }
            await _readerData.MarkReadingAchievementsSeenAsync(pending.Select(i => i.Definition.Id), _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine("Reading achievement notice: " + exception.Message);
        }
        finally { _achievementNoticeBusy = false; }
    }
}
