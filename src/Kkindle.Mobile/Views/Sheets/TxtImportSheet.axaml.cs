using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>Reviews the same chapter detection rules used by desktop imports.</summary>
public partial class TxtImportSheet : UserControl, IFullHeightSheet
{
    private const int PageSize = 20;
    private readonly string _path = string.Empty;
    private readonly Action<TxtImportRules>? _confirm;
    private CancellationTokenSource? _previewCancellation;
    private int _page;
    private bool _ready;

    public bool FillsHeight => true;

    public TxtImportSheet() => InitializeComponent();

    public TxtImportSheet(string path, Action<TxtImportRules> confirm)
    {
        _path = path;
        _confirm = confirm;
        InitializeComponent();
        FileText.Text = Path.GetFileName(path);
        EncodingBox.ItemsSource = new[] { "自动识别", "UTF-8", "UTF-16 LE", "UTF-16 BE", "GB18030" };
        RuleBox.ItemsSource = new[] { "自动识别", "中文章节", "英文章节", "序言和尾声", "不识别章节", "自定义正则" };
        EncodingBox.SelectedIndex = 0;
        RuleBox.SelectedIndex = 0;
        _ready = true;
        _ = RefreshAsync();
    }

    public void CancelPreview()
    {
        _ready = false;
        _previewCancellation?.Cancel();
    }

    private TxtImportRules Rules() => new()
    {
        Encoding = (TxtTextEncoding)Math.Max(0, EncodingBox.SelectedIndex),
        HeadingPreset = (TxtHeadingRulePreset)Math.Max(0, RuleBox.SelectedIndex),
        CustomHeadingPattern = PatternBox.Text,
        RecognizeVolumes = VolumesBox.IsChecked == true
    };

    private void OnRulesChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        PatternBox.IsVisible = RuleBox.SelectedIndex == (int)TxtHeadingRulePreset.CustomRegex;
        _page = 0;
        _ = RefreshAsync();
    }

    private void OnPatternChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready || !PatternBox.IsVisible) return;
        _page = 0;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        ImportButton.IsEnabled = false;
        PreviousButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        StatusText.Text = "正在识别章节…";
        try
        {
            var preview = await TxtToEpubService.PreviewAsync(_path, Rules(), _page, PageSize, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_ready) return;
            StatusText.Text = $"识别到 {preview.ChapterCount} 个目录项 · 编码 {preview.DetectedEncoding}";
            PreviewItems.ItemsSource = preview.Chapters.Select(chapter =>
                $"{(chapter.IsVolume ? "卷 · " : "")}{chapter.Title}").ToArray();
            PreviousButton.IsEnabled = preview.HasPreviousPage;
            NextButton.IsEnabled = preview.HasNextPage;
            ImportButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && _ready)
            {
                StatusText.Text = "识别失败：" + exception.Message;
                PreviewItems.ItemsSource = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e)
    {
        if (_page > 0) { _page--; _ = RefreshAsync(); }
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        _page++;
        _ = RefreshAsync();
    }

    private void OnImportClick(object? sender, RoutedEventArgs e) => _confirm?.Invoke(Rules());
}
