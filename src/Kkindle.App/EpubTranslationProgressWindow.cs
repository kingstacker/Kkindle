using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// A separate, modeless window keeps long-running EPUB translation visible
/// while the library window remains usable. The lower half is a live,
/// append-only paragraph stream so the original text, translated text and
/// provider flow can be inspected without waiting for the whole book.
/// </summary>
internal sealed class EpubTranslationProgressWindow : Window
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
    private readonly Button _openFolderButton = new();
    private readonly Button _closeButton = new();
    private readonly Dictionary<int, SegmentRowView> _segmentRows = [];
    private bool _finished;
    private bool _cancelRaised;
    private bool _scrollQueued;

    public EpubTranslationProgressWindow(
        string bookTitle,
        string provider,
        string sourceLanguage,
        string targetLanguage,
        string outputDirectory)
    {
        OutputDirectory = outputDirectory;
        Title = "书籍翻译";
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

        _engineText.Text = $"{provider}  ·  {sourceLanguage} → {targetLanguage}";
        _engineText.Foreground = new SolidColorBrush(Color.Parse("#6B6B66"));
        _engineText.FontSize = 12;
        _engineText.TextWrapping = TextWrapping.Wrap;

        _stageText.Text = "准备翻译";
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

        _emptySegmentText.Text = "扫描到正文段落后，会在这里按处理顺序显示原文、译文和流程。";
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

        _openFolderButton.Content = "打开输出目录";
        _openFolderButton.Classes.Add("quiet");
        _openFolderButton.IsEnabled = false;
        _openFolderButton.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);

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
            Children = { _openFolderButton, _cancelButton, _closeButton }
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

    public string OutputDirectory { get; }

    public event EventHandler? CancelRequested;

    public event EventHandler? OpenFolderRequested;

    public void Update(BookTranslationProgress progress)
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
        _detailsText.Text = progress.TotalSegments > 0
            ? $"已处理 {progress.ProcessedSegments:N0} / {progress.TotalSegments:N0} 段"
                + (progress.TotalCharacters > 0
                    ? $"  ·  {progress.ProcessedCharacters:N0} / {progress.TotalCharacters:N0} 字"
                    : string.Empty)
            : "正在准备…";

        if (progress.Segment is { } segment)
            UpdateSegment(segment);
    }

    public void MarkAddingToLibrary(int fileCount)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkAddingToLibrary(fileCount));
            return;
        }

        _stageText.Text = "正在加入书库";
        _currentText.Text = $"正在登记 {fileCount:N0} 个翻译文件，自动合并为同一本书的不同格式。";
    }

    public void MarkCompleted(BookTranslationResult result)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkCompleted(result));
            return;
        }

        _finished = true;
        _progressBar.Value = 100;
        _percentageText.Text = "100%";
        _stageText.Text = "翻译完成";
        _currentText.Text = "已生成 EPUB 文件。";
        _detailsText.Text = $"共处理 {result.SegmentCount:N0} 段，{result.SourceCharacters:N0} 个字符";
        _segmentSummaryText.Text = $"全部 {result.SegmentCount:N0} 段已完成";
        _resultText.Text = result.OutputPaths.Count == 0
            ? string.Empty
            : string.Join("\n", result.OutputPaths.Select(path => $"• {Path.GetFileName(path)}"));
        QueueScrollToEnd();
        SetFinishedState(canOpenFolder: result.OutputPaths.Count > 0);
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
            ? "翻译文件已自动加入书库。"
            : "翻译文件已部分加入书库。";
        _resultText.Text = string.IsNullOrWhiteSpace(_resultText.Text)
            ? librarySummary
            : $"{_resultText.Text}\n{librarySummary}";
    }

    public void MarkLibraryImportFailed(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkLibraryImportFailed(message));
            return;
        }

        _currentText.Text = "翻译文件已生成，但自动加入书库失败。";
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
        _currentText.Text = "翻译任务已取消，已处理段落的缓存会保留，下次可选择继续或重新翻译。";
        _resultText.Text = string.Empty;
        foreach (var row in _segmentRows.Values)
            row.MarkCanceled();
        _segmentSummaryText.Text = $"已显示 {_segmentRows.Count:N0} 段";
        SetFinishedState(canOpenFolder: Directory.Exists(OutputDirectory));
    }

    public void MarkFailed(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkFailed(message));
            return;
        }

        _finished = true;
        _stageText.Text = "翻译失败";
        _currentText.Text = message;
        _resultText.Text = _segmentRows.Count > 0
            ? "已保存当前翻译缓存；下次点击翻译时可继续，并为未完成段选择其他模型。"
            : string.Empty;
        SetFinishedState(canOpenFolder: Directory.Exists(OutputDirectory));
    }

    private void UpdateSegment(BookTranslationSegmentProgress segment)
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
        _segmentSummaryText.Text = $"已显示 {_segmentRows.Count:N0} 段  ·  已完成 {completedCount:N0} 段";
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

    private void SetFinishedState(bool canOpenFolder)
    {
        _cancelButton.IsEnabled = false;
        _closeButton.IsEnabled = true;
        _openFolderButton.IsEnabled = canOpenFolder;
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
        private readonly TextBlock _translatedText = new();
        private readonly TextBlock _flowText = new();
        private readonly TextBlock _errorText = new();
        private readonly Border _originalBlock;
        private readonly Border _translatedBlock;
        private BookTranslationSegmentStatus _status;

        public SegmentRowView(BookTranslationSegmentProgress segment)
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
            _translatedText.FontSize = 13;
            _translatedText.TextWrapping = TextWrapping.Wrap;

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
            _translatedBlock = new Border
            {
                Padding = new Thickness(8, 6),
                Child = _translatedText
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
                    CreateLabel("译文"),
                    _translatedBlock,
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

        public bool IsCompleted => _status == BookTranslationSegmentStatus.Completed;

        public void Update(BookTranslationSegmentProgress segment)
        {
            _status = segment.Status;
            var entryName = string.IsNullOrWhiteSpace(segment.EntryName)
                ? "正文"
                : Path.GetFileName(segment.EntryName);
            _indexText.Text = $"第 {segment.Index + 1:N0} 段  ·  {entryName}";
            _originalText.Text = segment.OriginalText;
            _translatedText.Text = segment.Status switch
            {
                BookTranslationSegmentStatus.Completed => segment.TranslatedText,
                BookTranslationSegmentStatus.Failed => "未生成译文",
                BookTranslationSegmentStatus.Canceled => "未完成",
                _ => "等待翻译结果…"
            };
            _flowText.Text = $"流程：{segment.ProcessFlow}";
            _errorText.Text = string.IsNullOrWhiteSpace(segment.ErrorMessage)
                ? string.Empty
                : $"原因：{segment.ErrorMessage}";
            _errorText.IsVisible = !string.IsNullOrWhiteSpace(segment.ErrorMessage);

            var (glyph, label, accent, background, translatedBackground) = segment.Status switch
            {
                BookTranslationSegmentStatus.Completed =>
                    ("√", "已完成", "#3F6B4A", "#F4FAF5", "#EDF7EF"),
                BookTranslationSegmentStatus.Failed =>
                    ("×", "失败", "#A2382A", "#FFF4F2", "#FDEBE8"),
                BookTranslationSegmentStatus.Canceled =>
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
            _translatedBlock.Background = new SolidColorBrush(Color.Parse(translatedBackground));
        }

        public void MarkCanceled()
        {
            if (_status is not BookTranslationSegmentStatus.Processing) return;
            _status = BookTranslationSegmentStatus.Canceled;
            _translatedText.Text = "未完成";
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
            _translatedBlock.Background = new SolidColorBrush(Color.Parse("#ECECE8"));
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
