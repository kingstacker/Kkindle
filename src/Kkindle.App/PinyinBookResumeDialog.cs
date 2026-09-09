using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

internal sealed record PinyinBookResumeChoice(PinyinBookResumeMode Mode);

/// <summary>
/// Lets the user decide whether a previous pinyin cache should be reused.
/// The AI model itself comes from the current AI service settings, so a model
/// changed there is used for every unfinished or failed segment on resume.
/// </summary>
internal sealed class PinyinBookResumeDialog : Window
{
    private readonly TaskCompletionSource<PinyinBookResumeChoice> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public PinyinBookResumeDialog(
        string bookTitle,
        PinyinBookResumeInfo resumeInfo,
        PinyinBookOptions currentOptions)
    {
        Title = "继续书籍注音";
        Width = 580;
        Height = 390;
        MinWidth = 520;
        MinHeight = 350;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFFDFC"));

        var titleText = new TextBlock
        {
            Text = $"《{bookTitle}》发现注音缓存",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var summaryText = new TextBlock
        {
            Text = BuildSummary(resumeInfo),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#55554F")),
            TextWrapping = TextWrapping.Wrap
        };
        var hintText = new TextBlock
        {
            Text = "选择“从上次继续”会保留已完成段，只重新处理失败或未完成段；选择“重新生成”会清空缓存并从第一段开始。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#777770")),
            TextWrapping = TextWrapping.Wrap
        };

        var modeText = new TextBlock
        {
            Text = currentOptions.EnableAiReview
                ? "本次处理方式：本地注音 → AI 疑难复核"
                : "本次处理方式：仅本地注音（不调用 AI）",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(PinyinBookResumeMode.Restart, canceled: true);

        var restartButton = new Button
        {
            Content = "重新生成",
            Padding = new Thickness(14, 7)
        };
        restartButton.Classes.Add("quiet");
        restartButton.Click += (_, _) => Complete(PinyinBookResumeMode.Restart);

        var resumeButton = new Button
        {
            Content = "从上次继续",
            Padding = new Thickness(14, 7)
        };
        resumeButton.Click += (_, _) => Complete(PinyinBookResumeMode.Resume);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, restartButton, resumeButton }
        };

        var cacheHintBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F7F7F4")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E2E2DC")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = "已完成段会从本地缓存恢复；如果更换了 AI 模型，新的模型只会复核未完成或失败段。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#66665F")),
                TextWrapping = TextWrapping.Wrap
            }
        };

        Content = new Border
        {
            Padding = new Thickness(24),
            Background = Background,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
                RowSpacing = 14,
                Children =
                {
                    titleText,
                    summaryText,
                    hintText,
                    modeText,
                    cacheHintBorder,
                    buttons
                }
            }
        };
        Grid.SetRow(summaryText, 1);
        Grid.SetRow(hintText, 2);
        Grid.SetRow(modeText, 3);
        Grid.SetRow(cacheHintBorder, 4);
        Grid.SetRow(buttons, 5);

        Closed += (_, _) => Complete(PinyinBookResumeMode.Restart, canceled: true);
    }

    public Task<PinyinBookResumeChoice> ShowAsync(Window owner)
    {
        Show(owner);
        return _completion.Task;
    }

    private void Complete(PinyinBookResumeMode mode, bool canceled = false)
    {
        if (_completed) return;
        _completed = true;
        _completion.TrySetResult(new PinyinBookResumeChoice(
            canceled ? PinyinBookResumeMode.Restart : mode));
        Close();
    }

    private static string BuildSummary(PinyinBookResumeInfo info)
    {
        var updated = info.UpdatedAt == DateTimeOffset.MinValue
            ? "时间未知"
            : info.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var mode = info.Options.EnableAiReview
            ? "本地注音 + AI 疑难复核"
            : "仅本地注音";
        return $"上次使用：{mode}\n"
            + $"已完成 {info.CompletedSegments:N0} / {info.TotalSegments:N0} 段，"
            + $"未完成 {info.IncompleteSegments:N0} 段，失败 {info.FailedSegments:N0} 段\n"
            + $"缓存更新时间：{updated}";
    }
}
