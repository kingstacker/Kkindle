using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

/// <summary>Target-format picker for batch conversion.</summary>
internal sealed class BatchConvertWindow : Window
{
    public BatchConvertWindow(int count, Func<string, Task> apply)
    {
        Title = UiText.Get("批量转换");
        Width = 420;
        Height = 240;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppAppearanceResources.GetBrush("PaperBrush");

        var target = new ComboBox
        {
            ItemsSource = new[] { "EPUB", "AZW3" },
            SelectedIndex = 0,
            Height = 38
        };
        var applyButton = new Button
        {
            Content = UiText.Get("转换 {0} 本书", count),
            Padding = new Thickness(14, 6)
        };
        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(14, 6)
        };
        cancelButton.Classes.Add("quiet");
        applyButton.Click += async (_, _) =>
        {
            var format = target.SelectedIndex == 0 ? "epub" : "azw3";
            Close();
            await apply(format);
        };
        cancelButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = UiText.Get("批量转换 {0} 本书", count),
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppAppearanceResources.GetBrush("InkBrush")
                },
                new TextBlock
                {
                    Text = UiText.Get("已有目标格式的书籍会自动跳过。"),
                    FontSize = 11,
                    Foreground = AppAppearanceResources.GetBrush("MutedInkBrush")
                },
                target,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { cancelButton, applyButton }
                }
            }
        };
    }
}
