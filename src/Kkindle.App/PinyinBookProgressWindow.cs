using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Modeless progress window for book pinyin generation. Its layout mirrors
/// the translation window: a total progress header followed by an append-only
/// waterfall of original text, pinyin output and the processing flow.
/// </summary>
internal sealed class PinyinBookProgressWindow : Window
{
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
    private readonly ScrollViewer _segmentScrollViewer = new();
    private readonly StackPanel _segmentPanel = new();
    private readonly Button _cancelButton = new();
    private readonly Button _closeButton = new();
    private readonly Dictionary<int, SegmentRowView> _segmentRows = [];
    private bool _finished;
    private bool _cancelRaised;
    private bool _scrollQueued;

    public PinyinBookProgressWindow(string bookTitle, bool aiReviewEnabled)
    {
        Title = "书籍注音";
        Width = 920;
        Height = 720;
        MinWidth = 680;
        MinHeight = 460;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFFDFC"));

        _bookText.Text = bookTitle;
        _bookText.FontSize = 20;
        _bookText.FontWeight = FontWeight.SemiBold;
        _bookText.TextWrapping = TextWrapping.Wrap;

        _engineText.Text = aiReviewEnabled
            ? "本地注音  ·  AI 疑难复核"
            : "本地注音  ·  仅本地生成";
        _engineText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
        _engineText.FontSize = 12;
        _engineText.TextWrapping = TextWrapping.Wrap;

        _stageText.Text = "准备注音";
        _stageText.FontSize = 14;
        _stageText.FontWeight = FontWeight.SemiBold;

        _currentText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
        _currentText.FontSize = 12;
        _currentText.TextWrapping = TextWrapping.Wrap;
        _currentText.MaxLines = 2;

        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Height = 8;
        _progressBar.Margin = new Thickness(0, 8, 0, 0);

        _percentageText.Text = "0%";
        _percentageText.HorizontalAlignment = HorizontalAlignment.Right;
        _percentageText.FontSize = 13;
        _percentageText.FontWeight = FontWeight.SemiBold;

        _detailsText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
        _detailsText.FontSize = 11;
        _detailsText.TextWrapping = TextWrapping.Wrap;

        var streamTitle = new TextBlock
        {
            Text = "实时段落处理",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        };
        _segmentSummaryText.Text = "等待扫描…";
        _segmentSummaryText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
        _segmentSummaryText.FontSize = 11;
        _segmentSummaryText.HorizontalAlignment = HorizontalAlignment.Right;
        _segmentSummaryText.VerticalAlignment = VerticalAlignment.Center;

        _emptySegmentText.Text = "扫描到正文段落后，会在这里按处理顺序显示原文、注音结果和流程。";
        _emptySegmentText.Foreground = new SolidColorBrush(Color.Parse("#888880"));
        _emptySegmentText.FontSize = 12;
        _emptySegmentText.TextWrapping = TextWrapping.Wrap;
        _emptySegmentText.Margin = new Thickness(4, 6, 4, 6);
        _segmentPanel.Orientation = Orientation.Vertical;
        _segmentPanel.Spacing = 10;
        _segmentPanel.Children.Add(_emptySegmentText);

        _segmentScrollViewer.Content = _segmentPanel;
        _segmentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _segmentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _segmentScrollViewer.Padding = new Thickness(0, 2, 8, 2);

        _resultText.Foreground = new SolidColorBrush(Color.Parse("#3F6B4A"));
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
                    _segmentScrollViewer,
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
        Grid.SetRow(_segmentScrollViewer, 7);
        Grid.SetRow(_resultText, 8);
        Grid.SetRow(buttons, 9);

        Closed += (_, _) => RequestCancel();
    }

    public event EventHandler? CancelRequested;

    public void Update(PinyinBookProgress progress)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(progress));
            return;
        }
        if (_finished) return;

        _stageText.Text = progress.Stage;
        _currentText.Text = progress.CurrentItem;
        _progressBar.Value = Math.Clamp(progress.Percentage, 0, 100);
        _percentageText.Text = $"{progress.Percentage:0}%";
        _detailsText.Text = BuildProgressDetails(progress);

        if (progress.Segment is { } segment)
            UpdateSegment(segment);
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
        QueueScrollToEnd();
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
        SetFinishedState();
    }

    private void UpdateSegment(PinyinBookSegmentProgress segment)
    {
        if (!_segmentRows.TryGetValue(segment.Index, out var row))
        {
            if (_emptySegmentText.Parent is Panel parent)
                parent.Children.Remove(_emptySegmentText);

            row = new SegmentRowView(segment);
            _segmentRows[segment.Index] = row;
            _segmentPanel.Children.Add(row.Control);
        }

        row.Update(segment);
        var completedCount = _segmentRows.Values.Count(item => item.IsCompleted);
        var failedCount = _segmentRows.Values.Count(item => item.IsFailed);
        _segmentSummaryText.Text = $"已显示 {_segmentRows.Count:N0} 段  ·  已完成 {completedCount:N0} 段"
            + (failedCount > 0 ? $"  ·  待复核 {failedCount:N0} 段" : string.Empty);
        QueueScrollToEnd();
    }

    private void QueueScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (IsVisible)
                _segmentScrollViewer.ScrollToEnd();
        });
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

    private sealed class SegmentRowView
    {
        private readonly Border _card;
        private readonly TextBlock _statusGlyph = new();
        private readonly TextBlock _indexText = new();
        private readonly TextBlock _statusText = new();
        private readonly TextBlock _originalText = new();
        private readonly TextBlock _annotatedText = new();
        private readonly TextBlock _flowText = new();
        private readonly TextBlock _errorText = new();
        private readonly Border _originalBlock;
        private readonly Border _annotatedBlock;
        private PinyinBookSegmentStatus _status;

        public SegmentRowView(PinyinBookSegmentProgress segment)
        {
            _statusGlyph.FontSize = 20;
            _statusGlyph.FontWeight = FontWeight.Bold;
            _statusGlyph.Width = 28;
            _statusGlyph.TextAlignment = TextAlignment.Center;
            _statusGlyph.VerticalAlignment = VerticalAlignment.Top;

            _indexText.FontSize = 11;
            _indexText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
            _indexText.TextTrimming = TextTrimming.CharacterEllipsis;

            _statusText.FontSize = 11;
            _statusText.HorizontalAlignment = HorizontalAlignment.Right;

            _originalText.FontSize = 13;
            _originalText.TextWrapping = TextWrapping.Wrap;
            _annotatedText.FontSize = 13;
            _annotatedText.TextWrapping = TextWrapping.Wrap;

            _flowText.FontSize = 11;
            _flowText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
            _flowText.TextWrapping = TextWrapping.Wrap;

            _errorText.FontSize = 11;
            _errorText.Foreground = new SolidColorBrush(Color.Parse("#A2382A"));
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
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 10,
                Children = { _statusGlyph, body }
            };
            Grid.SetColumn(body, 1);

            _card = new Border
            {
                Padding = new Thickness(10, 9),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(3, 1, 1, 1),
                Child = content
            };
            Control = _card;
            Update(segment);
        }

        public Control Control { get; }

        public bool IsCompleted => _status == PinyinBookSegmentStatus.Completed;

        public bool IsFailed => _status == PinyinBookSegmentStatus.Failed;

        public void Update(PinyinBookSegmentProgress segment)
        {
            _status = segment.Status;
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

            var (glyph, label, accent, background, annotatedBackground) = segment.Status switch
            {
                PinyinBookSegmentStatus.Completed =>
                    ("√", "已完成", "#3F6B4A", "#F4FAF5", "#EDF7EF"),
                PinyinBookSegmentStatus.Failed =>
                    ("×", "待复核", "#A2382A", "#FFF4F2", "#FDEBE8"),
                PinyinBookSegmentStatus.Canceled =>
                    ("—", "已取消", "#777770", "#F3F3F0", "#ECECE8"),
                _ =>
                    ("…", "处理中", "#8A5A00", "#FFFAF0", "#FFF5D9")
            };
            var accentBrush = new SolidColorBrush(Color.Parse(accent));
            _statusGlyph.Text = glyph;
            _statusGlyph.Foreground = accentBrush;
            _statusText.Text = label;
            _statusText.Foreground = accentBrush;
            _card.BorderBrush = accentBrush;
            _card.Background = new SolidColorBrush(Color.Parse(background));
            _originalBlock.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
            _annotatedBlock.Background = new SolidColorBrush(Color.Parse(annotatedBackground));
        }

        public void MarkCanceled()
        {
            if (_status is not PinyinBookSegmentStatus.Processing) return;
            _status = PinyinBookSegmentStatus.Canceled;
            _annotatedText.Text = "未完成";
            _flowText.Text = "流程：任务取消";
            _errorText.Text = string.Empty;
            _errorText.IsVisible = false;
            var accentBrush = new SolidColorBrush(Color.Parse("#777770"));
            _statusGlyph.Text = "—";
            _statusGlyph.Foreground = accentBrush;
            _statusText.Text = "已取消";
            _statusText.Foreground = accentBrush;
            _card.BorderBrush = accentBrush;
            _card.Background = new SolidColorBrush(Color.Parse("#F3F3F0"));
            _annotatedBlock.Background = new SolidColorBrush(Color.Parse("#ECECE8"));
        }

        private static TextBlock CreateLabel(string text) => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#777770"))
        };
    }
}
