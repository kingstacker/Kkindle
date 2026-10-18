using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed record TxtImportPreviewDisplayItem(
    string Number,
    string Title,
    string Detail);

public partial class MainWindow
{
    private const int TxtImportPreviewPageSize = 50;
    private TaskCompletionSource<IReadOnlyDictionary<string, TxtImportRules>?>? _txtImportRulesCompletion;
    private CancellationTokenSource? _txtImportPreviewCancellation;
    private readonly Dictionary<string, TxtImportRules> _txtImportRulesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<TxtImportPreviewDisplayItem> _txtImportPreviewItems = [];
    private IReadOnlyList<string> _txtImportFiles = [];
    private int _txtImportFileIndex;
    private int _txtImportPreviewPageIndex;
    private bool _suppressTxtImportUi;
    private int _txtImportPreviewVersion;

    private async Task<IReadOnlyDictionary<string, TxtImportRules>?> ChooseTxtImportRulesAsync(
        IReadOnlyList<string> txtFiles,
        CancellationToken cancellationToken)
    {
        if (txtFiles.Count == 0)
            return new Dictionary<string, TxtImportRules>(StringComparer.OrdinalIgnoreCase);

        _txtImportRulesCompletion?.TrySetResult(null);
        CancelTxtImportPreview();
        _txtImportFiles = txtFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _txtImportRulesByPath.Clear();
        foreach (var path in _txtImportFiles)
            _txtImportRulesByPath[path] = TxtImportRules.Default;
        _txtImportFileIndex = 0;
        _txtImportPreviewItems.Clear();
        TxtImportPreviewItems.ItemsSource = _txtImportPreviewItems;
        TxtImportCorrectionLineBox.Text = string.Empty;
        TxtImportCorrectionTitleBox.Text = string.Empty;
        TxtImportCorrectionHeadingBox.SelectedIndex = 0;
        TxtImportCorrectionVolumeBox.SelectedIndex = 0;
        _txtImportRulesCompletion =
            new TaskCompletionSource<IReadOnlyDictionary<string, TxtImportRules>?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _txtImportRulesCompletion;

        LoadTxtImportFile();
        ShowOverlay(TxtImportRulesOverlay);
        TxtImportRulesOverlay.Focus();
        using var registration = cancellationToken.Register(() =>
        {
            if (!completion.TrySetResult(null)) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_txtImportRulesCompletion, completion)) return;
                _txtImportRulesCompletion = null;
                HideTxtImportRulesOverlay();
            });
        });
        _ = ScheduleTxtImportPreviewAsync(cancellationToken);
        return await completion.Task;
    }

    private void LoadTxtImportFile()
    {
        if (_txtImportFiles.Count == 0 || _txtImportFileIndex < 0
            || _txtImportFileIndex >= _txtImportFiles.Count)
            return;

        var path = _txtImportFiles[_txtImportFileIndex];
        var rules = _txtImportRulesByPath.GetValueOrDefault(path) ?? TxtImportRules.Default;
        _txtImportPreviewPageIndex = 0;
        _suppressTxtImportUi = true;
        try
        {
            SelectComboBoxTag(TxtImportEncodingBox, rules.Encoding.ToString());
            SelectComboBoxTag(TxtImportHeadingRuleBox, rules.HeadingPreset.ToString());
            TxtImportCustomPatternBox.Text = rules.CustomHeadingPattern ?? string.Empty;
            TxtImportCustomPatternBox.IsVisible = rules.HeadingPreset == TxtHeadingRulePreset.CustomRegex;
            TxtImportVolumesBox.IsChecked = rules.RecognizeVolumes;
        }
        finally
        {
            _suppressTxtImportUi = false;
        }

        TxtImportFileTitleText.Text = Path.GetFileName(path);
        TxtImportFilePageText.Text = T(
            "第 {0} / {1} 本",
            _txtImportFileIndex + 1,
            _txtImportFiles.Count);
        TxtImportPreviousFileButton.IsEnabled = _txtImportFileIndex > 0;
        TxtImportNextFileButton.IsEnabled = _txtImportFileIndex + 1 < _txtImportFiles.Count;
        UpdateTxtImportPreviewPaging(null);
        TxtImportPreviewStatusText.Text = T("正在分析章节…");
    }

    private async Task ScheduleTxtImportPreviewAsync(CancellationToken cancellationToken)
    {
        if (_txtImportFiles.Count == 0 || !TxtImportRulesOverlay.IsVisible) return;
        CancelTxtImportPreview();
        if (_txtImportFiles.Count == 0
            || !TryCommitCurrentTxtRules(out var rules, showError: true))
            return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _txtImportPreviewCancellation = cancellation;
        var version = ++_txtImportPreviewVersion;
        TxtImportRefreshPreviewButton.IsEnabled = false;
        TxtImportPreviewStatusText.Text = T("正在分析章节…");
        try
        {
            await Task.Delay(180, cancellation.Token);
            var path = _txtImportFiles[_txtImportFileIndex];
            var preview = await TxtToEpubService.PreviewAsync(
                path,
                rules,
                _txtImportPreviewPageIndex,
                TxtImportPreviewPageSize,
                cancellation.Token);
            if (version != _txtImportPreviewVersion
                || cancellation.IsCancellationRequested
                || !ReferenceEquals(_txtImportPreviewCancellation, cancellation))
                return;

            _txtImportPreviewItems.Clear();
            foreach (var chapter in preview.Chapters)
            {
                _txtImportPreviewItems.Add(new TxtImportPreviewDisplayItem(
                    chapter.Number.ToString(CultureInfo.CurrentCulture),
                    chapter.Title,
                    T(
                        chapter.IsVolume ? "分卷 · {0:N0} 字 · 第 {1} 行" : "章节 · {0:N0} 字 · 第 {1} 行",
                        chapter.CharacterCount,
                        chapter.StartLine)));
            }
            if (_txtImportPreviewItems.Count == 0)
            {
                _txtImportPreviewItems.Add(new TxtImportPreviewDisplayItem(
                    "—",
                    T("没有可预览的目录项"),
                    T("请调整章节规则或逐行修正后重试。")));
            }
            TxtImportPreviewStatusText.Text = T(
                "自动识别编码：{0} · 共 {1} 个目录项 · 第 {2} 页",
                GetTxtEncodingLabel(preview.DetectedEncoding),
                preview.ChapterCount,
                preview.PageIndex + 1);
            UpdateTxtImportPreviewPaging(preview);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version != _txtImportPreviewVersion) return;
            TxtImportPreviewStatusText.Text =
                T("预览失败：{0}", UiText.Localize(exception.Message));
            UpdateTxtImportPreviewPaging(null);
        }
        finally
        {
            if (ReferenceEquals(_txtImportPreviewCancellation, cancellation))
            {
                _txtImportPreviewCancellation = null;
                TxtImportRefreshPreviewButton.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private void UpdateTxtImportPreviewPaging(TxtImportPreview? preview)
    {
        TxtImportPreviewPreviousButton.IsEnabled = preview?.HasPreviousPage == true;
        TxtImportPreviewNextButton.IsEnabled = preview?.HasNextPage == true;
    }

    private bool TryCommitCurrentTxtRules(out TxtImportRules rules, bool showError)
    {
        rules = TxtImportRules.Default;
        if (_txtImportFiles.Count == 0 || _txtImportFileIndex < 0
            || _txtImportFileIndex >= _txtImportFiles.Count)
            return false;

        var encoding = ReadComboBoxTag(TxtImportEncodingBox, TxtTextEncoding.Auto);
        var headingPreset = ReadComboBoxTag(TxtImportHeadingRuleBox, TxtHeadingRulePreset.Automatic);
        var pattern = TxtImportCustomPatternBox.Text?.Trim();
        if (headingPreset == TxtHeadingRulePreset.CustomRegex)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return ReportTxtImportValidationError(showError, "自定义目录规则不能为空。", out rules);
            if (pattern.Length > 256)
                return ReportTxtImportValidationError(showError, "自定义目录规则不能超过 256 个字符。", out rules);
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException exception)
            {
                return ReportTxtImportValidationError(
                    showError,
                    T("自定义目录规则无效：{0}", UiText.Localize(exception.Message)),
                    out rules);
            }
        }

        var path = _txtImportFiles[_txtImportFileIndex];
        var previous = _txtImportRulesByPath.GetValueOrDefault(path) ?? TxtImportRules.Default;
        rules = new TxtImportRules
        {
            Encoding = encoding,
            HeadingPreset = headingPreset,
            CustomHeadingPattern =
                headingPreset == TxtHeadingRulePreset.CustomRegex ? pattern : null,
            RecognizeVolumes = TxtImportVolumesBox.IsChecked == true,
            Corrections = previous.Corrections
        };
        _txtImportRulesByPath[path] = rules;
        return true;
    }

    private bool ReportTxtImportValidationError(
        bool showError,
        string message,
        out TxtImportRules rules)
    {
        rules = TxtImportRules.Default;
        if (showError)
        {
            TxtImportPreviewStatusText.Text = message;
            UpdateTxtImportPreviewPaging(null);
        }
        return false;
    }

    private static TEnum ReadComboBoxTag<TEnum>(ComboBox box, TEnum fallback)
        where TEnum : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string value }
        && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static string ReadComboBoxTag(ComboBox box, string fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: string value }
            ? value
            : fallback;

    private static void SelectComboBoxTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetTxtEncodingLabel(TxtTextEncoding encoding) => encoding switch
    {
        TxtTextEncoding.Utf16LittleEndian => "UTF-16 LE",
        TxtTextEncoding.Utf16BigEndian => "UTF-16 BE",
        TxtTextEncoding.Gb18030 => "GB18030",
        _ => "UTF-8"
    };

    private void TxtImportPreviousFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrentTxtRules(out _, showError: true)
            || _txtImportFileIndex <= 0)
            return;
        CancelTxtImportPreview();
        _txtImportFileIndex--;
        LoadTxtImportFile();
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportNextFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrentTxtRules(out _, showError: true)
            || _txtImportFileIndex + 1 >= _txtImportFiles.Count)
            return;
        CancelTxtImportPreview();
        _txtImportFileIndex++;
        LoadTxtImportFile();
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportResetFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_txtImportFiles.Count == 0) return;
        CancelTxtImportPreview();
        _txtImportRulesByPath[_txtImportFiles[_txtImportFileIndex]] = TxtImportRules.Default;
        LoadTxtImportFile();
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportEncodingBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTxtImportUi) return;
        TriggerTxtImportPreview();
    }

    private void TxtImportHeadingRuleBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTxtImportUi || _txtImportFiles.Count == 0) return;
        var custom = ReadComboBoxTag(
            TxtImportHeadingRuleBox,
            TxtHeadingRulePreset.Automatic) == TxtHeadingRulePreset.CustomRegex;
        TxtImportCustomPatternBox.IsVisible = custom;
        if (custom) TxtImportCustomPatternBox.Focus();
        TriggerTxtImportPreview();
    }

    private void TxtImportCustomPatternBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_suppressTxtImportUi) TriggerTxtImportPreview();
    }

    private void TxtImportVolumesBox_Click(object? sender, RoutedEventArgs e)
    {
        if (!_suppressTxtImportUi) TriggerTxtImportPreview();
    }

    private void TxtImportCorrectionApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(
                TxtImportCorrectionLineBox.Text?.Trim(),
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out var lineNumber)
            || lineNumber < 1)
        {
            TxtImportPreviewStatusText.Text = T("请输入大于 0 的原文行号。");
            TxtImportCorrectionLineBox.Focus();
            return;
        }

        var headingChoice = ReadComboBoxTag(TxtImportCorrectionHeadingBox, "Auto");
        var volumeChoice = ReadComboBoxTag(TxtImportCorrectionVolumeBox, "Auto");
        bool? isHeading = headingChoice switch
        {
            "True" => true,
            "False" => false,
            _ => null
        };
        bool? isVolume = volumeChoice switch
        {
            "True" => true,
            "False" => false,
            _ => null
        };
        var title = TxtImportCorrectionTitleBox.Text?.Trim();
        if (isHeading is null && isVolume is null && string.IsNullOrWhiteSpace(title))
        {
            TxtImportPreviewStatusText.Text = T("请选择章节判定方式或填写修正后的标题。");
            return;
        }

        if (!TryCommitCurrentTxtRules(out var rules, showError: true)) return;
        rules = rules with
        {
            Corrections = rules.Corrections
                .Where(correction => correction.LineNumber != lineNumber)
                .Append(new TxtChapterCorrection(
                    lineNumber,
                    isHeading,
                    string.IsNullOrWhiteSpace(title) ? null : title,
                    isVolume))
                .OrderBy(correction => correction.LineNumber)
                .ToArray()
        };
        _txtImportRulesByPath[_txtImportFiles[_txtImportFileIndex]] = rules;
        TxtImportCorrectionLineBox.Text = string.Empty;
        TxtImportCorrectionTitleBox.Text = string.Empty;
        TxtImportCorrectionHeadingBox.SelectedIndex = 0;
        TxtImportCorrectionVolumeBox.SelectedIndex = 0;
        TxtImportPreviewStatusText.Text = T("已添加第 {0} 行的修正，正在刷新预览…", lineNumber);
        _txtImportPreviewPageIndex = 0;
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportRefreshPreviewButton_Click(object? sender, RoutedEventArgs e) =>
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);

    private void TxtImportPreviewPreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_txtImportPreviewPageIndex <= 0) return;
        _txtImportPreviewPageIndex--;
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportPreviewNextButton_Click(object? sender, RoutedEventArgs e)
    {
        _txtImportPreviewPageIndex++;
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrentTxtRules(out _, showError: true)) return;
        var completion = _txtImportRulesCompletion;
        if (completion is null) return;
        var result = _txtImportFiles.ToDictionary(
            path => path,
            path => _txtImportRulesByPath.GetValueOrDefault(path) ?? TxtImportRules.Default,
            StringComparer.OrdinalIgnoreCase);
        CompleteTxtImportRules(result, completion);
    }

    private void TxtImportResetAllButton_Click(object? sender, RoutedEventArgs e)
    {
        CancelTxtImportPreview();
        foreach (var path in _txtImportFiles)
            _txtImportRulesByPath[path] = TxtImportRules.Default;
        LoadTxtImportFile();
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }

    private void TxtImportCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        var completion = _txtImportRulesCompletion;
        if (completion is not null) CompleteTxtImportRules(null, completion);
    }

    private void TxtImportRulesOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        TxtImportCancelButton_Click(sender, e);
    }

    private void CompleteTxtImportRules(
        IReadOnlyDictionary<string, TxtImportRules>? result,
        TaskCompletionSource<IReadOnlyDictionary<string, TxtImportRules>?> completion)
    {
        if (!ReferenceEquals(_txtImportRulesCompletion, completion)) return;
        _txtImportRulesCompletion = null;
        HideTxtImportRulesOverlay();
        _txtImportFiles = [];
        _txtImportRulesByPath.Clear();
        _txtImportPreviewItems.Clear();
        completion.TrySetResult(result);
    }

    private void HideTxtImportRulesOverlay()
    {
        TxtImportRulesOverlay.IsVisible = false;
        CancelTxtImportPreview();
    }

    private void CancelTxtImportPreview()
    {
        _txtImportPreviewVersion++;
        var cancellation = _txtImportPreviewCancellation;
        _txtImportPreviewCancellation = null;
        cancellation?.Cancel();
        TxtImportRefreshPreviewButton.IsEnabled = true;
    }

    private void TriggerTxtImportPreview()
    {
        _txtImportPreviewPageIndex = 0;
        _ = ScheduleTxtImportPreviewAsync(_lifetimeCancellation.Token);
    }
}
