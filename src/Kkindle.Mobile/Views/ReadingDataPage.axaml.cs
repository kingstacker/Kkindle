using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.ApplicationServices;
using Kkindle.Core;

namespace Kkindle.Mobile.Views;

/// <param name="IsStrong">Today, drawn in solid ink.</param>
/// <param name="IsPicked">The day whose value the header shows.</param>
public sealed record ReadingDayBar(
    DateOnly Date, string Label, double BarHeight, long Seconds, bool IsStrong, bool IsPicked);

public sealed record ReadingBucket(string Label, int Count, double Shade)
{
    public string CountText => $"{Count} 本";
}

/// <summary>A book on the dashboard with its cover, which loads after the page.</summary>
public sealed class ReadingBookRow(Book? book, string title, string detail, double percent) : ObservableObject
{
    private Bitmap? _cover;

    public Book? Book { get; } = book;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public double Percent { get; } = percent;
    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title[..1];
    public bool HasCover => Cover is not null;

    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (SetProperty(ref _cover, value)) OnPropertyChanged(nameof(HasCover));
        }
    }
}

public sealed class ReadingMedalTile(ReadingMedal medal)
{
    private const double GridSize = 24;
    private const double ArtworkSize = 20;

    public ReadingMedal Medal { get; } = medal;

    /// <summary>Degrees of the progress ring; a full ring marks an unlocked medal.</summary>
    public double Sweep => Medal.IsUnlocked ? 360 : Medal.Fraction * 360;

    public double IconOpacity => Medal.IsUnlocked ? 1 : 0.35;
    public Geometry Icon { get; } = Fit(medal.Icon);

    // Medal paths use different origins, so fit the artwork rather than its canvas.
    private static Geometry Fit(string path)
    {
        var geometry = Geometry.Parse(path);
        var bounds = geometry.Bounds;
        var scale = ArtworkSize / Math.Max(1, Math.Max(bounds.Width, bounds.Height));
        var center = GridSize / 2;
        geometry.Transform = new MatrixTransform(new Avalonia.Matrix(scale, 0, 0, scale,
            center - bounds.Center.X * scale, center - bounds.Center.Y * scale));
        return geometry;
    }
}

/// <summary>
/// The desktop reading dashboard laid out for a phone: totals, daily time,
/// recent and most-read books, progress distribution and medals.
/// </summary>
public partial class ReadingDataPage : UserControl
{
    private const int RecentLimit = 100;
    private const int VisibleRecentBooks = 10;
    private const int VisibleBookTimes = 5;
    private const double MaxBarHeight = 120;
    private const double MinBarHeight = 4;
    private const double FinishedPercent = 99.5;
    private const int CoverWidth = 192;

    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private int _dayCount = 14;
    private bool _showAllMedals;
    private DateOnly _pickedDay = ReadingToday;
    private IReadOnlyList<ReadingDashboardDay> _days = [];
    private IReadOnlyList<ReadingMedal> _medals = [];

    /// <summary>Design-time only.</summary>
    public ReadingDataPage() => InitializeComponent();

    public ReadingDataPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        // Also runs when a book opened from this page is closed.
        Loaded += async (_, _) => await RefreshAsync();
    }

    // Reading days are recorded and synced as UTC dates; "today" must use the
    // same calendar or the newest bar is labelled with the wrong day.
    private static DateOnly ReadingToday => DateOnly.FromDateTime(DateTime.UtcNow);

    // Quick 7/14/30 taps overlap; only the newest refresh may paint.
    private int _refreshGeneration;

    private async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules!.Library.Books.InitializeAsync();
            await _shell!.EnsureReaderDataAsync();
            var dashboard = await _modules.Reader.Data.GetReadingDashboardAsync(RecentLimit, dayCount: _dayCount);
            var books = (await _modules.Library.Books.SearchAsync()).ToDictionary(book => book.Id);
            if (generation != _refreshGeneration) return;
            StatusText.IsVisible = false;
            PresentOverview(dashboard);
            _days = dashboard.DailyReading;
            PresentDays();
            PresentRecent(dashboard, books);
            PresentBookTimes(dashboard, books);
            PresentDistribution(dashboard);
            _medals = ReadingMedalCatalog.Build(dashboard);
            PresentMedals();
        }
        catch (Exception exception) when (generation == _refreshGeneration)
        {
            StatusText.Text = "阅读数据暂时不可用：" + UiText.Localize(exception.Message);
            StatusText.IsVisible = true;
        }
    }

    private void PresentOverview(ReadingDashboard dashboard)
    {
        var (value, unit) = SplitDuration(dashboard.TotalSeconds);
        TotalValueText.Text = value;
        TotalUnitText.Text = unit;
        var weekSeconds = dashboard.DailyReading.TakeLast(7).Sum(day => day.ActiveSeconds);
        var average = weekSeconds == 0 ? "—" : MobileFormat.ReadingTime((long)Math.Round(weekSeconds / 7d));
        HeroDetailText.Text =
            $"近 7 天日均 {average} · 连续阅读 {ReadingMedalCatalog.ComputeStreakDays(dashboard.DailyReading)} 天";

        BooksStartedText.Text = $"{dashboard.BooksStarted} 本";
        AverageProgressText.Text = $"平均进度 {dashboard.AverageProgress:0.#}%";
        BooksFinishedText.Text = $"{dashboard.BooksFinished} 本";
        FinishedRateText.Text = dashboard.BooksStarted > 0
            ? $"完成率 {100d * dashboard.BooksFinished / dashboard.BooksStarted:0}%"
            : "完成率 —";
        BookmarksText.Text = dashboard.BookmarkCount.ToString();
        AnnotationsText.Text = dashboard.AnnotationCount.ToString();
    }

    private static (string Value, string Unit) SplitDuration(long seconds)
    {
        if (seconds < 3600) return ($"{seconds / 60d:0.#}", "分钟");
        return ($"{seconds / 3600d:0.#}", "小时");
    }

    private void PresentDays()
    {
        DailyTitleText.Text = $"近 {_days.Count} 天";
        var longest = Math.Max(1, _days.Count == 0 ? 1 : _days.Max(day => day.ActiveSeconds));
        var today = ReadingToday;
        var labelInterval = Math.Max(1, (int)Math.Ceiling(_days.Count / 5d));
        var lastIndex = _days.Count - 1;
        var bars = _days.Select((day, index) =>
        {
            var showLabel = index == lastIndex || (index % labelInterval == 0 && lastIndex - index >= labelInterval);
            return new ReadingDayBar(
                day.Date,
                showLabel ? (day.Date == today ? "今天" : day.Date.ToString("M/d")) : string.Empty,
                Math.Max(MinBarHeight, MaxBarHeight * day.ActiveSeconds / longest),
                day.ActiveSeconds,
                day.Date == today,
                day.Date == _pickedDay && day.Date != today);
        }).ToList();
        DaysChart.ItemsSource = bars;
        DaysLabels.ItemsSource = bars;

        var picked = _days.FirstOrDefault(day => day.Date == _pickedDay);
        var name = _pickedDay == today ? "今天" : _pickedDay.ToString("M 月 d 日");
        PickedDayText.Text = $"{name} · {(picked is null || picked.ActiveSeconds == 0 ? "未阅读" : MobileFormat.ReadingTime(picked.ActiveSeconds))}";
    }

    private static string BookTitle(ReadingDashboardBook item)
    {
        var title = string.IsNullOrWhiteSpace(item.Title) ? "未知书名" : item.Title;
        return item.IsInLibrary ? title : $"{title}（已移出书库）";
    }

    private void PresentRecent(ReadingDashboard dashboard, IReadOnlyDictionary<Guid, Book> books)
    {
        var rows = dashboard.RecentBooks
            .Take(VisibleRecentBooks)
            .Select(item => new ReadingBookRow(
                books.GetValueOrDefault(item.BookId),
                BookTitle(item),
                $"{item.ProgressPercent:0}%",
                item.ProgressPercent))
            .ToList();
        RecentList.ItemsSource = rows;
        RecentEmptyText.IsVisible = rows.Count == 0;
        LoadCovers(rows);
    }

    private void PresentBookTimes(ReadingDashboard dashboard, IReadOnlyDictionary<Guid, Book> books)
    {
        var items = dashboard.MostReadBooks
            .Where(item => item.CumulativeSeconds > 0)
            .Take(VisibleBookTimes)
            .ToList();
        var longest = Math.Max(1, items.Count == 0 ? 1 : items.Max(item => item.CumulativeSeconds));
        var rows = items
            .Select(item => new ReadingBookRow(
                books.GetValueOrDefault(item.BookId),
                BookTitle(item),
                MobileFormat.ReadingTime(item.CumulativeSeconds),
                100d * item.CumulativeSeconds / longest))
            .ToList();
        BookTimesList.ItemsSource = rows;
        BookTimesEmptyText.IsVisible = rows.Count == 0;
        LoadCovers(rows);
    }

    private void LoadCovers(IEnumerable<ReadingBookRow> rows)
    {
        foreach (var row in rows.Where(row => row.Book is not null))
            _ = LoadCoverAsync(row);
    }

    private async Task LoadCoverAsync(ReadingBookRow row) =>
        row.Cover = await _shell!.Covers.GetAsync(row.Book!, CoverWidth);

    /// <summary>One stacked bar in shades of ink, from untouched to finished.</summary>
    private void PresentDistribution(ReadingDashboard dashboard)
    {
        var values = dashboard.Books.Select(item => item.ProgressPercent).ToArray();
        var buckets = new[]
        {
            new ReadingBucket("0–24%", values.Count(value => value < 25), 0.3),
            new ReadingBucket("25–49%", values.Count(value => value is >= 25 and < 50), 0.45),
            new ReadingBucket("50–74%", values.Count(value => value is >= 50 and < 75), 0.6),
            new ReadingBucket("75–99%", values.Count(value => value is >= 75 and < FinishedPercent), 0.8),
            new ReadingBucket("已读完", values.Count(value => value >= FinishedPercent), 1)
        };
        DistributionTotalText.Text = $"共 {values.Length} 本";
        DistributionLegend.ItemsSource = buckets;

        DistributionBar.ColumnDefinitions.Clear();
        DistributionBar.Children.Clear();
        foreach (var bucket in buckets.Where(bucket => bucket.Count > 0))
        {
            DistributionBar.ColumnDefinitions.Add(new ColumnDefinition(bucket.Count, GridUnitType.Star));
            var segment = new Border { Opacity = bucket.Shade, Margin = new Avalonia.Thickness(0, 0, 1, 0) };
            segment[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextPrimaryBrush");
            Grid.SetColumn(segment, DistributionBar.ColumnDefinitions.Count - 1);
            DistributionBar.Children.Add(segment);
        }
    }

    private void PresentMedals()
    {
        var unlocked = _medals.Count(medal => medal.IsUnlocked);
        MedalSummaryText.Text = $"已解锁 {unlocked} / {_medals.Count}";
        MedalsUnlocked.Classes.Set("on", !_showAllMedals);
        MedalsAll.Classes.Set("on", _showAllMedals);
        var visible = _medals
            .Where(medal => medal.IsUnlocked || _showAllMedals)
            .OrderByDescending(medal => medal.IsUnlocked)
            .ThenByDescending(medal => medal.Fraction)
            .Select(medal => new ReadingMedalTile(medal))
            .ToList();
        MedalsList.ItemsSource = visible;
        MedalsEmptyText.IsVisible = visible.Count == 0;
    }


    private async void OnRangeClick(object? sender, RoutedEventArgs e)
    {
        _dayCount = sender switch
        {
            Button { Name: nameof(Range7) } => 7,
            Button { Name: nameof(Range30) } => 30,
            _ => 14
        };
        Range7.Classes.Set("on", _dayCount == 7);
        Range14.Classes.Set("on", _dayCount == 14);
        Range30.Classes.Set("on", _dayCount == 30);
        await RefreshAsync();
    }

    private void OnDayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReadingDayBar bar }) return;
        _pickedDay = bar.Date;
        PresentDays();
    }

    private void OnMedalFilterClick(object? sender, RoutedEventArgs e)
    {
        _showAllMedals = sender is Button { Name: nameof(MedalsAll) };
        PresentMedals();
    }

    private void OnMedalClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReadingMedalTile { Medal: var medal } }) return;
        _shell?.ShowToast(medal.IsUnlocked ? $"{medal.Name}：{medal.Rule}" : $"{medal.Rule}（{medal.Fraction:P0}）");
    }

    private void OnBookClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReadingBookRow row }) return;
        if (row.Book is { } book) _shell?.OpenBook(book);
        else _shell?.ShowToast("这本书已不在书库中");
    }
}
