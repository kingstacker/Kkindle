using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Kkindle;

/// <summary>
/// Modeless progress window for book pinyin generation. Its layout mirrors
/// the translation window: a total progress header followed by a reordered
/// waterfall of original text, pinyin output and the processing flow.
/// </summary>
internal sealed class PinyinBookProgressWindow : Window
{
    private const string White = "#FFFFFF";
    private const string Ink = "#111111";
    private const string MutedInk = "#737373";
    private const string Hairline = "#E1E1E1";
    private const string SoftGray = "#F5F5F5";
    private const string GrayDot = "#B5B5B5";

    private readonly TextBlock _bookText = new();
    private readonly TextBlock _engineText = new();
    private readonly TextBlock _stageText = new();
    private readonly TextBlock _currentText = new();
    private readonly TextBlock _detailsText = new();
    private readonly TextBlock _resultText = new();
    private readonly TextBlock _percentageText = new();
    private readonly TextBlock _segmentSummaryText = new();
    private readonly TextBlock _emptySegmentText = new();
    private readonly ProgressBar _progressBar = new();
    private readonly ListBox _segmentList = new();
    private readonly ObservableCollection<SegmentRow> _orderedSegmentRows = [];
    private readonly Button _cancelButton = new();
    private readonly Button _closeButton = new();
    private readonly Dictionary<int, SegmentRow> _segmentRows = [];
    private readonly SortedSet<int> _processingSegmentIndices = [];
    private int _completedSegmentCount;
    private int _failedSegmentCount;
    private bool _finished;
    private bool _cancelRaised;
    private bool _scrollQueued;
    private int? _currentSegmentIndex;
    private int? _pendingCenterSegmentIndex;

    public PinyinBookProgressWindow(string bookTitle, bool aiReviewEnabled)
    {
        Title = "书籍注音";
        Width = 920;
        Height = 720;
        MinWidth = 680;
        MinHeight = 460;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse(White));

        _bookText.Text = bookTitle;
        _bookText.FontSize = 20;
        _bookText.FontWeight = FontWeight.SemiBold;
        _bookText.Foreground = new SolidColorBrush(Color.Parse(Ink));
        _bookText.TextWrapping = TextWrapping.Wrap;

        _engineText.Text = aiReviewEnabled
            ? "本地注音  ·  AI 疑难复核"
            : "本地注音  ·  仅本地生成";
        _engineText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
        _engineText.FontSize = 12;
        _engineText.TextWrapping = TextWrapping.Wrap;

        _stageText.Text = "准备注音";
        _stageText.FontSize = 14;
        _stageText.FontWeight = FontWeight.SemiBold;

        _currentText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
        _currentText.FontSize = 12;
        _currentText.TextWrapping = TextWrapping.Wrap;
        _currentText.MaxLines = 2;

        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Height = 8;
        _progressBar.Margin = new Thickness(0, 8, 0, 0);
        _progressBar.Foreground = new SolidColorBrush(Color.Parse(Ink));
        _progressBar.Background = new SolidColorBrush(Color.Parse(Hairline));

        _percentageText.Text = "0%";
        _percentageText.HorizontalAlignment = HorizontalAlignment.Right;
        _percentageText.FontSize = 13;
        _percentageText.FontWeight = FontWeight.SemiBold;

        _detailsText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
        _detailsText.FontSize = 11;
        _detailsText.TextWrapping = TextWrapping.Wrap;

        var streamTitle = new TextBlock
        {
            Text = "实时段落处理",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        };
        _segmentSummaryText.Text = "等待扫描…";
        _segmentSummaryText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
        _segmentSummaryText.FontSize = 11;
        _segmentSummaryText.HorizontalAlignment = HorizontalAlignment.Right;
        _segmentSummaryText.VerticalAlignment = VerticalAlignment.Center;

        _emptySegmentText.Text = "扫描到正文段落后，会在这里显示注音瀑布流。";
        _emptySegmentText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
        _emptySegmentText.FontSize = 12;
        _emptySegmentText.TextWrapping = TextWrapping.Wrap;
        _emptySegmentText.Margin = new Thickness(4, 6, 4, 6);
        _emptySegmentText.VerticalAlignment = VerticalAlignment.Top;
        _segmentList.ItemsSource = _orderedSegmentRows;
        _segmentList.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _segmentList.ItemTemplate = new FuncDataTemplate<SegmentRow>((_, _) => new SegmentRowControl
        {
            [!SegmentRowControl.SegmentProperty] = new Binding(nameof(SegmentRow.Segment))
        }, supportsRecycling: true);
        _segmentList.Background = Brushes.Transparent;
        _segmentList.BorderThickness = new Thickness(0);
        _segmentList.Padding = new Thickness(0, 2, 8, 2);
        ScrollViewer.SetVerticalScrollBarVisibility(_segmentList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_segmentList, ScrollBarVisibility.Disabled);
        _segmentList.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(ListBoxItem.FocusableProperty, false)
            }
        });
        var streamBody = new Grid { Children = { _segmentList, _emptySegmentText } };

        _resultText.Foreground = new SolidColorBrush(Color.Parse(Ink));
        _resultText.FontSize = 11;
        _resultText.TextWrapping = TextWrapping.Wrap;

        _cancelButton.Content = "取消";
        _cancelButton.Classes.Add("quiet");
        _cancelButton.Click += (_, _) => RequestCancel();

        _closeButton.Content = "关闭";
        _closeButton.Classes.Add("quiet");
        _closeButton.IsEnabled = false;
        _closeButton.Click += (_, _) => Close();

        var progressHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Children = { _stageText, _percentageText }
        };
        Grid.SetColumn(_percentageText, 1);

        var streamHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Children = { streamTitle, _segmentSummaryText }
        };
        Grid.SetColumn(_segmentSummaryText, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _cancelButton, _closeButton }
        };

        Content = new Border
        {
            Padding = new Thickness(26, 24, 26, 20),
            Background = Background,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions(
                    "Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto,Auto"),
                RowSpacing = 8,
                Children =
                {
                    _bookText,
                    _engineText,
                    progressHeader,
                    _progressBar,
                    _currentText,
                    _detailsText,
                    streamHeader,
                    streamBody,
                    _resultText,
                    buttons
                }
            }
        };
        Grid.SetRow(_engineText, 1);
        Grid.SetRow(progressHeader, 2);
        Grid.SetRow(_progressBar, 3);
        Grid.SetRow(_currentText, 4);
        Grid.SetRow(_detailsText, 5);
        Grid.SetRow(streamHeader, 6);
        Grid.SetRow(streamBody, 7);
        Grid.SetRow(_resultText, 8);
        Grid.SetRow(buttons, 9);

        Closed += (_, _) => RequestCancel();
    }

    public event EventHandler? CancelRequested;

    public void Update(PinyinBookProgress progress) => UpdateBatch([progress]);

    public void UpdateBatch(IReadOnlyList<PinyinBookProgress> updates)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateBatch(updates), DispatcherPriority.Background);
            return;
        }
        if (_finished || updates.Count == 0) return;

        var progress = updates[^1];
        _stageText.Text = progress.Stage;
        _currentText.Text = progress.CurrentItem;
        _progressBar.Value = Math.Clamp(progress.Percentage, 0, 100);
        _percentageText.Text = $"{progress.Percentage:0}%";
        _detailsText.Text = BuildProgressDetails(progress);

        // A fast local engine may finish a paragraph within one frame. Apply
        // its latest state once, while retaining every paragraph in the stream.
        var segments = new Dictionary<int, PinyinBookSegmentProgress>();
        foreach (var update in updates)
            if (update.Segment is { } segment) segments[segment.Index] = segment;
        foreach (var segment in segments.Values)
            UpdateSegment(segment);
        if (segments.Count == 0) return;

        _segmentSummaryText.Text = $"已显示 {_segmentRows.Count:N0} 段  ·  已完成 {_completedSegmentCount:N0} 段"
            + (_failedSegmentCount > 0 ? $"  ·  待复核 {_failedSegmentCount:N0} 段" : string.Empty);
        // Local-only work often has no Processing row by the next render.
        // Follow the latest completed row in that case instead of staying put.
        QueueScrollToSegment(_currentSegmentIndex ?? segments.Values.Last().Index);
    }

    public void MarkAddingToLibrary()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(MarkAddingToLibrary);
            return;
        }

        _stageText.Text = "正在加入书库";
        _currentText.Text = "正在登记书籍注音文件，自动加入当前书籍。";
    }

    public void MarkCompleted(PinyinBookResult result)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkCompleted(result));
            return;
        }

        _finished = true;
        _progressBar.Value = 100;
        _percentageText.Text = "100%";
        _stageText.Text = "注音完成";
        _currentText.Text = "已生成书籍注音 EPUB 文件。";
        _detailsText.Text =
            $"扫描 {result.ScannedPageCount:N0} 页，注音 {result.AnnotatedCharacterCount:N0} 字，"
            + $"发现疑问 {result.ReviewCandidateCount:N0} 项，AI 已复核 {result.AiReviewedCandidateCount:N0} 项。";
        _segmentSummaryText.Text = result.FailedSegmentCount > 0
            ? $"已完成 {_segmentRows.Count - result.FailedSegmentCount:N0} 段，待复核 {result.FailedSegmentCount:N0} 段"
            : $"全部 {_segmentRows.Count:N0} 段已完成";
        _resultText.Text = result.AiReviewError is null
            ? string.Empty
            : $"提示：{result.AiReviewError}，缓存已保留，可下次继续复核。";
        _currentSegmentIndex = null;
        ReorderSegmentRows();
        QueueScrollToTop();
        SetFinishedState();
    }

    public void MarkLibraryImportResult(ImportBatchResult result)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkLibraryImportResult(result));
            return;
        }

        var addedCount = result.Items.Count(item => item.Succeeded && item.Added);
        var alreadyInLibraryCount = result.Items.Count(item => item.Succeeded && !item.Added);
        var failedCount = result.FailureCount;
        var librarySummary = failedCount == 0
            ? $"书库：已加入 {addedCount:N0} 项"
                + (alreadyInLibraryCount > 0 ? $"，已有 {alreadyInLibraryCount:N0} 项" : string.Empty)
            : $"书库：已加入 {addedCount:N0} 项，{failedCount:N0} 项失败";
        _currentText.Text = failedCount == 0
            ? "书籍注音文件已自动加入书库。"
            : "书籍注音文件已部分加入书库。";
        _resultText.Text = string.IsNullOrWhiteSpace(_resultText.Text)
            ? librarySummary
            : $"{_resultText.Text}\n{librarySummary}";
    }

    public void MarkLibraryAdded()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(MarkLibraryAdded);
            return;
        }

        _currentText.Text = "书籍注音文件已自动加入书库。";
        _resultText.Text = string.IsNullOrWhiteSpace(_resultText.Text)
            ? "书库：已加入当前书籍"
            : $"{_resultText.Text}\n书库：已加入当前书籍";
    }

    public void MarkLibraryImportFailed(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkLibraryImportFailed(message));
            return;
        }

        _currentText.Text = "书籍注音文件已生成，但自动加入书库失败。";
        _resultText.Text = string.IsNullOrWhiteSpace(_resultText.Text)
            ? $"书库：{message}"
            : $"{_resultText.Text}\n书库：{message}";
    }

    public void MarkCanceled()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(MarkCanceled);
            return;
        }

        _finished = true;
        _stageText.Text = "已取消";
        _currentText.Text = "注音任务已取消，已处理段落的缓存会保留，下次可选择继续或重新生成。";
        _resultText.Text = string.Empty;
        foreach (var row in _segmentRows.Values)
            row.MarkCanceled();
        _segmentSummaryText.Text = $"已显示 {_segmentRows.Count:N0} 段";
        _currentSegmentIndex = null;
        ReorderSegmentRows();
        SetFinishedState();
    }

    public void MarkFailed(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkFailed(message));
            return;
        }

        _finished = true;
        _stageText.Text = "注音失败";
        _currentText.Text = message;
        _resultText.Text = _segmentRows.Count > 0
            ? "已保存当前注音缓存；下次点击注音时可继续，并重新进行疑难段 AI 复核。"
            : string.Empty;
        foreach (var row in _segmentRows.Values)
            row.MarkCanceled();
        _currentSegmentIndex = null;
        ReorderSegmentRows();
        SetFinishedState();
    }

    private void UpdateSegment(PinyinBookSegmentProgress segment)
    {
        if (!_segmentRows.TryGetValue(segment.Index, out var row))
        {
            _emptySegmentText.IsVisible = false;
            row = new SegmentRow(segment);
            _segmentRows[segment.Index] = row;
        }
        else
        {
            if (row.IsCompleted) _completedSegmentCount--;
            if (row.IsFailed) _failedSegmentCount--;
            _processingSegmentIndices.Remove(row.Index);
            row.Update(segment);
        }

        if (row.IsCompleted) _completedSegmentCount++;
        if (row.IsFailed) _failedSegmentCount++;
        if (row.IsProcessing) _processingSegmentIndices.Add(row.Index);
        _currentSegmentIndex = _processingSegmentIndices.Count > 0 ? _processingSegmentIndices.Min : null;
        PlaceSegmentRow(row);
    }

    private void PlaceSegmentRow(SegmentRow row)
    {
        var previous = row.Position;
        // Sequential local annotation normally appends a row, then completes
        // it in place. Avoid sorting/scanning the whole book for either update.
        if (previous >= 0
            && (previous == 0 || CompareRows(_orderedSegmentRows[previous - 1], row) <= 0)
            && (previous == _orderedSegmentRows.Count - 1 || CompareRows(row, _orderedSegmentRows[previous + 1]) <= 0))
            return;

        var low = 0;
        var high = _orderedSegmentRows.Count - (previous >= 0 ? 1 : 0);
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var candidate = previous >= 0 && middle >= previous ? middle + 1 : middle;
            if (CompareRows(_orderedSegmentRows[candidate], row) < 0) low = middle + 1;
            else high = middle;
        }
        if (previous >= 0)
        {
            _orderedSegmentRows.Move(previous, low);
            UpdateRowPositions(Math.Min(previous, low), Math.Max(previous, low));
        }
        else
        {
            _orderedSegmentRows.Insert(low, row);
            UpdateRowPositions(low, _orderedSegmentRows.Count - 1);
        }
    }

    private static int CompareRows(SegmentRow left, SegmentRow right)
    {
        static int Group(SegmentRow row) => row.IsCompleted ? 0 : row.IsProcessing ? 1 : 2;
        var group = Group(left).CompareTo(Group(right));
        return group != 0 ? group : left.Index.CompareTo(right.Index);
    }

    private void UpdateRowPositions(int first, int last)
    {
        for (var index = first; index <= last; index++)
            _orderedSegmentRows[index].Position = index;
    }

    private void ReorderSegmentRows()
    {
        if (_segmentRows.Count == 0) return;

        var orderedRows = ProgressWaterfallOrder.Order(
            _segmentRows.Values,
            _currentSegmentIndex,
            item => item.Index,
            item => item.IsCompleted,
            item => item.IsProcessing);
        for (var position = 0; position < orderedRows.Length; position++)
        {
            var previous = orderedRows[position].Position;
            if (previous == position) continue;
            _orderedSegmentRows.Move(previous, position);
            UpdateRowPositions(Math.Min(previous, position), Math.Max(previous, position));
        }
    }

    private void QueueScrollToSegment(int segmentIndex)
    {
        _pendingCenterSegmentIndex = segmentIndex;
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (!IsVisible || _pendingCenterSegmentIndex is not { } currentIndex)
            {
                _pendingCenterSegmentIndex = null;
                return;
            }

            _pendingCenterSegmentIndex = null;
            if (!_segmentRows.TryGetValue(currentIndex, out var row))
            {
                return;
            }

            _segmentList.ScrollIntoView(row);
        }, DispatcherPriority.Render);
    }

    private void QueueScrollToTop()
    {
        _pendingCenterSegmentIndex = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && _orderedSegmentRows.Count > 0)
                _segmentList.ScrollIntoView(_orderedSegmentRows[0]);
        }, DispatcherPriority.Render);
    }

    private static string BuildProgressDetails(PinyinBookProgress progress)
    {
        var details = progress.TotalSegments > 0
            ? $"已处理 {progress.ProcessedSegments:N0} / {progress.TotalSegments:N0} 段"
                + (progress.TotalPages > 0
                    ? $"  ·  {progress.ProcessedPages:N0} / {progress.TotalPages:N0} 页"
                    : string.Empty)
                + $"  ·  已注音 {progress.AnnotatedCharacters:N0} 字"
            : progress.TotalPages > 0
                ? $"已扫描 {progress.ProcessedPages:N0} / {progress.TotalPages:N0} 页"
                : "正在准备…";
        if (progress.ReviewCandidates > 0)
            details += $"  ·  疑问 {progress.ReviewCandidates:N0} 项"
                + $"  ·  AI 已复核 {progress.ReviewedCandidates:N0} 项";
        return details;
    }

    private void SetFinishedState()
    {
        _cancelButton.IsEnabled = false;
        _closeButton.IsEnabled = true;
    }

    private void RequestCancel()
    {
        if (_finished || _cancelRaised) return;
        _cancelRaised = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SegmentRow(PinyinBookSegmentProgress segment) : ObservableObject
    {
        private PinyinBookSegmentProgress _segment = segment;
        public PinyinBookSegmentProgress Segment => _segment;
        public int Index => Segment.Index;
        public int Position { get; set; } = -1;
        public bool IsProcessing => Segment.Status == PinyinBookSegmentStatus.Processing;
        public bool IsCompleted => Segment.Status == PinyinBookSegmentStatus.Completed;
        public bool IsFailed => Segment.Status == PinyinBookSegmentStatus.Failed;

        public void Update(PinyinBookSegmentProgress value) => SetProperty(ref _segment, value, nameof(Segment));

        public void MarkCanceled()
        {
            if (IsProcessing)
                Update(Segment with { Status = PinyinBookSegmentStatus.Canceled, ProcessFlow = "任务取消", ErrorMessage = null });
        }
    }

    private sealed class SegmentRowControl : Border
    {
        public static readonly StyledProperty<PinyinBookSegmentProgress?> SegmentProperty =
            AvaloniaProperty.Register<SegmentRowControl, PinyinBookSegmentProgress?>(nameof(Segment));

        private readonly Border _card;
        private readonly Ellipse _statusDot = new();
        private readonly TextBlock _indexText = new();
        private readonly TextBlock _statusText = new();
        private readonly TextBlock _originalText = new();
        private readonly TextBlock _annotatedText = new();
        private readonly TextBlock _flowText = new();
        private readonly TextBlock _errorText = new();
        private readonly Border _originalBlock;
        private readonly Border _annotatedBlock;

        public SegmentRowControl()
        {
            _statusDot.Width = 8;
            _statusDot.Height = 8;
            _statusDot.HorizontalAlignment = HorizontalAlignment.Center;
            _statusDot.VerticalAlignment = VerticalAlignment.Center;

            _indexText.FontSize = 11;
            _indexText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
            _indexText.TextTrimming = TextTrimming.CharacterEllipsis;

            _statusText.FontSize = 11;
            _statusText.HorizontalAlignment = HorizontalAlignment.Right;

            _originalText.FontSize = 13;
            _originalText.TextWrapping = TextWrapping.Wrap;
            _annotatedText.FontSize = 13;
            _annotatedText.TextWrapping = TextWrapping.Wrap;

            _flowText.FontSize = 11;
            _flowText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
            _flowText.TextWrapping = TextWrapping.Wrap;

            _errorText.FontSize = 11;
            _errorText.Foreground = new SolidColorBrush(Color.Parse(MutedInk));
            _errorText.TextWrapping = TextWrapping.Wrap;

            _originalBlock = new Border
            {
                Padding = new Thickness(8, 6),
                Child = _originalText
            };
            _annotatedBlock = new Border
            {
                Padding = new Thickness(8, 6),
                Child = _annotatedText
            };

            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 10,
                Children = { _indexText, _statusText }
            };
            Grid.SetColumn(_statusText, 1);

            var body = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    header,
                    CreateLabel("原文"),
                    _originalBlock,
                    CreateLabel("注音"),
                    _annotatedBlock,
                    _flowText,
                    _errorText
                }
            };

            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("18,*"),
                ColumnSpacing = 12,
                Children = { _statusDot, body }
            };
            Grid.SetColumn(body, 1);

            _card = this;
            Padding = new Thickness(12, 11);
            Margin = new Thickness(0, 0, 0, 8);
            CornerRadius = new CornerRadius(0);
            BorderThickness = new Thickness(1);
            Child = content;
        }

        public PinyinBookSegmentProgress? Segment
        {
            get => GetValue(SegmentProperty);
            set => SetValue(SegmentProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SegmentProperty && Segment is { } segment)
                Update(segment);
        }

        private void Update(PinyinBookSegmentProgress segment)
        {
            var entryName = string.IsNullOrWhiteSpace(segment.EntryName)
                ? "正文"
                : Path.GetFileName(segment.EntryName);
            _indexText.Text = $"第 {segment.Index + 1:N0} 段  ·  {entryName}";
            _originalText.Text = segment.OriginalText;
            _annotatedText.Text = segment.Status switch
            {
                PinyinBookSegmentStatus.Completed => segment.AnnotatedText,
                PinyinBookSegmentStatus.Failed => string.IsNullOrWhiteSpace(segment.AnnotatedText)
                    ? "未完成注音"
                    : segment.AnnotatedText,
                PinyinBookSegmentStatus.Canceled => "未完成",
                _ => string.IsNullOrWhiteSpace(segment.AnnotatedText)
                    ? "正在生成注音…"
                    : segment.AnnotatedText
            };
            _flowText.Text = $"流程：{segment.ProcessFlow}";
            _errorText.Text = string.IsNullOrWhiteSpace(segment.ErrorMessage)
                ? string.Empty
                : $"原因：{segment.ErrorMessage}";
            _errorText.IsVisible = !string.IsNullOrWhiteSpace(segment.ErrorMessage);

            var label = segment.Status switch
            {
                PinyinBookSegmentStatus.Completed => "已完成",
                PinyinBookSegmentStatus.Failed => "失败",
                PinyinBookSegmentStatus.Canceled => "已取消",
                _ => "处理中"
            };
            var completed = segment.Status == PinyinBookSegmentStatus.Completed;
            var statusBrush = new SolidColorBrush(Color.Parse(completed ? Ink : MutedInk));
            _statusDot.Fill = new SolidColorBrush(Color.Parse(completed ? Ink : GrayDot));
            _statusText.Text = label;
            _statusText.Foreground = statusBrush;
            _card.BorderBrush = segment.Status == PinyinBookSegmentStatus.Processing
                ? statusBrush
                : new SolidColorBrush(Color.Parse(Hairline));
            _card.Background = new SolidColorBrush(Color.Parse(White));
            _originalBlock.Background = new SolidColorBrush(Color.Parse(White));
            _annotatedBlock.Background = new SolidColorBrush(Color.Parse(SoftGray));
        }

        private static TextBlock CreateLabel(string text) => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(MutedInk))
        };
    }
}
