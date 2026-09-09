using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

internal sealed record BookTranslationResumeChoice(
    BookTranslationResumeMode Mode,
    BookTranslationProvider Provider);

/// <summary>
/// Lets the user decide whether a previous translation cache should be used.
/// The provider selector deliberately applies only to the next run: completed
/// segments can be reused while failed/incomplete segments go through the
/// newly selected provider.
/// </summary>
internal sealed class BookTranslationResumeDialog : Window
{
    private readonly ComboBox _providerBox;
    private readonly TaskCompletionSource<BookTranslationResumeChoice> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public BookTranslationResumeDialog(
        string bookTitle,
        BookTranslationResumeInfo resumeInfo,
        BookTranslationSettings currentSettings)
    {
        Title = "继续书籍翻译";
        Width = 580;
        Height = 390;
        MinWidth = 520;
        MinHeight = 350;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFFDFC"));

        var titleText = new TextBlock
        {
            Text = $"《{bookTitle}》发现翻译缓存",
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
            Text = "选择“从上次继续”会保留已完成段，只重新处理失败或未完成段；选择“重新翻译”会清空缓存并从第一段开始。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#777770")),
            TextWrapping = TextWrapping.Wrap
        };

        var choices = new[]
        {
            new ProviderChoice(BookTranslationProvider.Ai, "AI 翻译"),
            new ProviderChoice(BookTranslationProvider.BingFree, "Bing 免费翻译"),
            new ProviderChoice(BookTranslationProvider.GoogleFree, "Google 免费翻译")
        };
        _providerBox = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(item => item.Provider == currentSettings.Provider)
                ?? choices[0],
            MinWidth = 220,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var providerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "本次未完成段使用的翻译模型",
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                },
                _providerBox
            }
        };
        Grid.SetColumn(_providerBox, 1);

        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(BookTranslationResumeMode.Restart, canceled: true);

        var restartButton = new Button
        {
            Content = "重新翻译",
            Padding = new Thickness(14, 7)
        };
        restartButton.Classes.Add("quiet");
        restartButton.Click += (_, _) => Complete(BookTranslationResumeMode.Restart);

        var resumeButton = new Button
        {
            Content = "从上次继续",
            Padding = new Thickness(14, 7)
        };
        resumeButton.Click += (_, _) => Complete(BookTranslationResumeMode.Resume);

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
                Text = "已完成段会从本地缓存恢复；更换模型后，新的模型只会处理未完成或失败段。",
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
                    providerRow,
                    cacheHintBorder,
                    buttons
                }
            }
        };
        Grid.SetRow(summaryText, 1);
        Grid.SetRow(hintText, 2);
        Grid.SetRow(providerRow, 3);
        Grid.SetRow(cacheHintBorder, 4);
        Grid.SetRow(buttons, 5);

        Closed += (_, _) => Complete(BookTranslationResumeMode.Restart, canceled: true);
    }

    public Task<BookTranslationResumeChoice> ShowAsync(Window owner)
    {
        Show(owner);
        return _completion.Task;
    }

    private void Complete(BookTranslationResumeMode mode, bool canceled = false)
    {
        if (_completed) return;
        _completed = true;
        var provider = _providerBox.SelectedItem is ProviderChoice choice
            ? choice.Provider
            : BookTranslationProvider.Ai;
        _completion.TrySetResult(new BookTranslationResumeChoice(
            canceled ? BookTranslationResumeMode.Restart : mode,
            provider));
        Close();
    }

    private static string BuildSummary(BookTranslationResumeInfo info)
    {
        var updated = info.UpdatedAt == DateTimeOffset.MinValue
            ? "时间未知"
            : info.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return $"上次使用：{GetProviderDisplayName(info.Settings.Provider)}\n"
            + $"已完成 {info.CompletedSegments:N0} / {info.TotalSegments:N0} 段，"
            + $"未完成 {info.IncompleteSegments:N0} 段，失败 {info.FailedSegments:N0} 段\n"
            + $"缓存更新时间：{updated}";
    }

    private static string GetProviderDisplayName(BookTranslationProvider provider) => provider switch
    {
        BookTranslationProvider.BingFree => "Bing 免费翻译",
        BookTranslationProvider.GoogleFree => "Google 免费翻译",
        _ => "AI 翻译"
    };

    private sealed record ProviderChoice(BookTranslationProvider Provider, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
