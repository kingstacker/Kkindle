using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Cross-book highlight/note browser. Deliberately searches only annotation
/// text and notes (not book bodies), which keeps it fast and index-free.
/// </summary>
internal sealed class ReaderNotesWindow : Window
{
    private static readonly (string Value, string Name)[] Colors =
    [
        ("", "全部颜色"),
        ("#000000", "黑色"),
        ("#FFD54F", "黄色"),
        ("#81C784", "绿色"),
        ("#64B5F6", "蓝色"),
        ("#F48FB1", "粉色"),
        ("#B39DDB", "紫色"),
        ("#FFB74D", "橙色"),
    ];

    private static readonly (int Days, string Name)[] Ranges =
    [
        (0, "全部时间"),
        (7, "最近 7 天"),
        (30, "最近 30 天"),
        (90, "最近 90 天"),
    ];

    private readonly ReaderDataService _readerData;
    private readonly Func<ReaderAnnotation, Task> _openAnnotation;
    private readonly ObservableCollection<ReaderNoteItem> _items = [];
    private readonly ObservableCollection<ReaderNoteBookOption> _bookOptions = [];
    private readonly TextBox _search = new();
    private readonly ComboBox _bookBox = new();
    private readonly ComboBox _colorBox = new();
    private readonly ComboBox _rangeBox = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new();
    private CancellationTokenSource? _refreshCancellation;
    private bool _loading;

    public ReaderNotesWindow(
        ReaderDataService readerData,
        Func<ReaderAnnotation, Task> openAnnotation)
    {
        _readerData = readerData;
        _openAnnotation = openAnnotation;

        Title = UiText.Get("阅读笔记");
        Width = 1040;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppAppearanceResources.GetBrush("PaperBrush");

        _bookOptions.Add(new ReaderNoteBookOption(null, UiText.Get("全部书籍")));

        Content = BuildLayout();
        Opened += async (_, _) =>
        {
            await LoadBookOptionsAsync();
            await RefreshAsync(immediate: true);
        };
        Closed += (_, _) =>
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
        };
    }

    private Control BuildLayout()
    {
        var title = new TextBlock
        {
            Text = UiText.Get("阅读笔记"),
            FontSize = 26,
            FontWeight = FontWeight.Bold,
            Foreground = AppAppearanceResources.GetBrush("InkBrush")
        };
        var subtitle = new TextBlock
        {
            Text = UiText.Get("跨书查看划线、批注和笔记"),
            FontSize = 11,
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush")
        };

        _search.Width = 300;
        _search.Height = 38;
        _search.Padding = new Thickness(12, 7);
        _search.Watermark = UiText.Get("搜索划线、批注或书名…");
        _search.TextChanged += (_, _) => QueueRefresh();

        _bookBox.Width = 220;
        _bookBox.Height = 38;
        _bookBox.ItemsSource = _bookOptions;
        _bookBox.SelectedIndex = 0;
        _bookBox.ItemTemplate = new FuncDataTemplate<ReaderNoteBookOption>(
            (option, _) => new TextBlock { Text = option.Title, FontSize = 12 },
            supportsRecycling: true);
        _bookBox.SelectionChanged += (_, _) => QueueRefresh();

        _colorBox.Width = 130;
        _colorBox.Height = 38;
        _colorBox.ItemsSource = Colors.Select(item => item.Name).ToArray();
        _colorBox.SelectedIndex = 0;
        _colorBox.SelectionChanged += (_, _) => QueueRefresh();

        _rangeBox.Width = 130;
        _rangeBox.Height = 38;
        _rangeBox.ItemsSource = Ranges.Select(item => item.Name).ToArray();
        _rangeBox.SelectedIndex = 0;
        _rangeBox.SelectionChanged += (_, _) => QueueRefresh();

        var exportMarkdown = new Button
        {
            Content = UiText.Get("导出 Markdown"),
            Padding = new Thickness(12, 6)
        };
        exportMarkdown.Classes.Add("quiet");
        exportMarkdown.Click += async (_, _) => await ExportAsync(markdown: true);
        var exportCsv = new Button
        {
            Content = UiText.Get("导出 CSV"),
            Padding = new Thickness(12, 6)
        };
        exportCsv.Classes.Add("quiet");
        exportCsv.Click += async (_, _) => await ExportAsync(markdown: false);

        var filters = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemHeight = 38,
            Margin = new Thickness(0, 16, 0, 0)
        };
        filters.Children.Add(_search);
        filters.Children.Add(Spacer(8));
        filters.Children.Add(_bookBox);
        filters.Children.Add(Spacer(8));
        filters.Children.Add(_colorBox);
        filters.Children.Add(Spacer(8));
        filters.Children.Add(_rangeBox);
        filters.Children.Add(Spacer(16));
        filters.Children.Add(exportMarkdown);
        filters.Children.Add(Spacer(8));
        filters.Children.Add(exportCsv);

        _list.ItemsSource = _items;
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.ItemTemplate = new FuncDataTemplate<ReaderNoteItem>(
            (item, _) => BuildRow(item), supportsRecycling: true);
        _list.DoubleTapped += async (_, _) =>
        {
            if (_list.SelectedItem is ReaderNoteItem item)
                await OpenAsync(item);
        };
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || _list.SelectedItem is not ReaderNoteItem item) return;
            e.Handled = true;
            await OpenAsync(item);
        };
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MarginProperty, new Thickness(0)),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(Layoutable.MinHeightProperty, 0d),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });

        _status.FontSize = 11;
        _status.Foreground = AppAppearanceResources.GetBrush("MutedInkBrush");
        _status.VerticalAlignment = VerticalAlignment.Center;

        var close = new Button
        {
            Content = UiText.Get("关闭"),
            Padding = new Thickness(14, 6)
        };
        close.Classes.Add("quiet");
        close.Click += (_, _) => Close();

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(_status);
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);

        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(title);
        header.Children.Add(subtitle);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(22)
        };
        root.Children.Add(header);
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);
        Grid.SetRow(_list, 2);
        root.Children.Add(new Border
        {
            Child = _list,
            Margin = new Thickness(0, 14, 0, 10),
            BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 1)
        });
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private Control BuildRow(ReaderNoteItem item)
    {
        var colorBar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = item.ColorBrush,
            Margin = new Thickness(0, 2, 0, 2)
        };

        var quote = new TextBlock
        {
            Text = item.Quote,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            Foreground = AppAppearanceResources.GetBrush("InkBrush")
        };
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(quote);
        if (item.HasNote)
        {
            body.Children.Add(new TextBlock
            {
                Text = item.Note,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush")
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = item.MetaLabel,
            FontSize = 10,
            Foreground = AppAppearanceResources.GetBrush("SubtleInkBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var open = new Button
        {
            Content = UiText.Get("打开"),
            Padding = new Thickness(10, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        open.Classes.Add("quiet");
        open.Click += async (_, _) => await OpenAsync(item);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("8,*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(4, 10),
            Background = Brushes.Transparent
        };
        row.Children.Add(colorBar);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        Grid.SetColumn(open, 2);
        row.Children.Add(open);
        return row;
    }

    private static Control Spacer(double width) => new Border { Width = width };

    private async Task LoadBookOptionsAsync()
    {
        try
        {
            var options = await Task.Run(() => _readerData.GetAnnotationBookOptionsAsync());
            var selected = (_bookBox.SelectedItem as ReaderNoteBookOption)?.BookId;
            _bookOptions.Clear();
            _bookOptions.Add(new ReaderNoteBookOption(null, UiText.Get("全部书籍")));
            foreach (var option in options)
                _bookOptions.Add(new ReaderNoteBookOption(option.BookId, option.Title));
            _bookBox.SelectedIndex = 0;
            if (selected is { } id)
            {
                var index = _bookOptions
                    .Select((option, position) => (option, position))
                    .FirstOrDefault(pair => pair.option.BookId == id).position;
                if (index > 0) _bookBox.SelectedIndex = index;
            }
        }
        catch
        {
            // The book filter is optional; the notes list can still load.
        }
    }

    private void QueueRefresh()
    {
        if (_loading) return;
        _ = RefreshAsync(immediate: false);
    }

    private async Task RefreshAsync(bool immediate)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            if (!immediate)
                await Task.Delay(180, cancellation.Token);
            var query = _search.Text?.Trim();
            var bookId = (_bookBox.SelectedItem as ReaderNoteBookOption)?.BookId;
            var color = _colorBox.SelectedIndex > 0 ? Colors[_colorBox.SelectedIndex].Value : null;
            var rangeDays = _rangeBox.SelectedIndex > 0 ? Ranges[_rangeBox.SelectedIndex].Days : 0;
            var after = rangeDays > 0 ? DateTimeOffset.UtcNow.AddDays(-rangeDays) : (DateTimeOffset?)null;

            _status.Text = UiText.Get("正在加载…");
            var notes = await Task.Run(
                () => _readerData.SearchAnnotationsAsync(
                    query, bookId, color, after, 1000, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                _items.Clear();
                foreach (var note in notes) _items.Add(new ReaderNoteItem(note));
                _status.Text = notes.Count == 0
                    ? UiText.Get("没有找到笔记")
                    : UiText.Get("{0} 条笔记", notes.Count);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _status.Text = UiText.Get("加载笔记失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task OpenAsync(ReaderNoteItem item)
    {
        await _openAnnotation(item.Source.Annotation);
        Close();
    }

    private async Task ExportAsync(bool markdown)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = markdown ? UiText.Get("导出 Markdown") : UiText.Get("导出 CSV"),
            SuggestedFileName = markdown ? $"Kkindle-笔记-{stamp}.md" : $"Kkindle-笔记-{stamp}.csv",
            FileTypeChoices = markdown
                ? [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
                : [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var content = markdown ? BuildMarkdown(_items) : BuildCsv(_items);
        await File.WriteAllTextAsync(
            path,
            content,
            markdown ? new UTF8Encoding(false) : new UTF8Encoding(true));
        _status.Text = UiText.Get("已导出 {0} 条笔记到 {1}", _items.Count, path);
    }

    private static string BuildMarkdown(IEnumerable<ReaderNoteItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# " + UiText.Get("Kkindle 阅读笔记"));
        builder.AppendLine();
        builder.AppendLine(UiText.Get("导出时间：{0}", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")));
        builder.AppendLine();
        foreach (var item in items)
        {
            builder.AppendLine("## " + item.BookTitle);
            if (!string.IsNullOrWhiteSpace(item.Authors))
                builder.AppendLine("*" + item.Authors + "*");
            builder.AppendLine();
            builder.AppendLine("> " + item.Quote.Replace("\n", "\n> ", StringComparison.Ordinal));
            builder.AppendLine();
            if (item.HasNote)
            {
                builder.AppendLine("**" + UiText.Get("批注") + "：** " + item.Note);
                builder.AppendLine();
            }
            builder.AppendLine($"- {item.ChapterLabel} · {item.UpdatedLabel}");
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string BuildCsv(IEnumerable<ReaderNoteItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', new[]
        {
            Csv(UiText.Get("书名")), Csv(UiText.Get("作者")), Csv(UiText.Get("章节")),
            Csv(UiText.Get("划线")), Csv(UiText.Get("批注")), Csv(UiText.Get("颜色")), Csv(UiText.Get("更新时间"))
        }));
        foreach (var item in items)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(item.BookTitle), Csv(item.Authors), Csv(item.ChapterLabel),
                Csv(item.Quote), Csv(item.Note), Csv(item.Source.Annotation.Color), Csv(item.UpdatedLabel)
            }));
        }
        return builder.ToString();
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record ReaderNoteBookOption(Guid? BookId, string Title);

    private sealed class ReaderNoteItem(ReaderAnnotationWithBook source)
    {
        public ReaderAnnotationWithBook Source { get; } = source;
        public string BookTitle => string.IsNullOrWhiteSpace(Source.BookTitle)
            ? UiText.Get("未知书籍")
            : Source.BookTitle;
        public string Authors => Source.BookAuthors;
        public string Quote => string.IsNullOrWhiteSpace(Source.Annotation.SelectedText)
            ? UiText.Get("（页面批注）")
            : Source.Annotation.SelectedText.Trim();
        public string Note => Source.Annotation.Note?.Trim() ?? string.Empty;
        public bool HasNote => Note.Length > 0;
        public string ChapterLabel => string.IsNullOrWhiteSpace(Source.Annotation.ChapterPath)
            ? Source.BookFormat.ToUpperInvariant()
            : Path.GetFileNameWithoutExtension(Source.Annotation.ChapterPath);
        public string UpdatedLabel => Source.Annotation.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string MetaLabel => string.IsNullOrWhiteSpace(Authors)
            ? $"{BookTitle} · {ChapterLabel} · {UpdatedLabel}"
            : $"{BookTitle} · {Authors} · {ChapterLabel} · {UpdatedLabel}";
        public IBrush ColorBrush
        {
            get
            {
                try
                {
                    return new SolidColorBrush(Color.Parse(Source.Annotation.Color));
                }
                catch
                {
                    return Brushes.Black;
                }
            }
        }
    }
}
