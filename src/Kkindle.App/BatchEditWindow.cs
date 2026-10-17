using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

internal sealed record BatchEditOptions(
    string? Tags,
    string? Category,
    LibraryReadingStatus? ReadingStatus,
    bool? Favorite);

/// <summary>Batch metadata editor for the library multi-selection bar.</summary>
internal sealed class BatchEditWindow : Window
{
    private readonly TextBox _tags = new();
    private readonly CheckBox _clearTags = new() { Content = UiText.Get("清空标签") };
    private readonly TextBox _category = new();
    private readonly CheckBox _clearCategory = new() { Content = UiText.Get("清空分类") };
    private readonly ComboBox _readingStatus = new();
    private readonly ComboBox _favorite = new();

    public BatchEditWindow(int count, Func<BatchEditOptions, Task> apply)
    {
        Title = UiText.Get("批量编辑");
        Width = 480;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppAppearanceResources.GetBrush("PaperBrush");

        _tags.Watermark = UiText.Get("留空则不修改");
        _category.Watermark = UiText.Get("留空则不修改");
        _readingStatus.ItemsSource = new[]
        {
            UiText.Get("不修改"), UiText.Get("待读"), UiText.Get("阅读中"), UiText.Get("已读")
        };
        _readingStatus.SelectedIndex = 0;
        _favorite.ItemsSource = new[]
        {
            UiText.Get("不修改"), UiText.Get("收藏"), UiText.Get("取消收藏")
        };
        _favorite.SelectedIndex = 0;

        var applyButton = new Button
        {
            Content = UiText.Get("应用到 {0} 本书", count),
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
            var options = BuildOptions();
            await apply(options);
            Close();
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
                    Text = UiText.Get("批量编辑 {0} 本书", count),
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppAppearanceResources.GetBrush("InkBrush")
                },
                Label(UiText.Get("标签")),
                _tags,
                _clearTags,
                Label(UiText.Get("分类")),
                _category,
                _clearCategory,
                Label(UiText.Get("阅读状态")),
                _readingStatus,
                Label(UiText.Get("收藏")),
                _favorite,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { cancelButton, applyButton }
                }
            }
        };
    }

    private BatchEditOptions BuildOptions()
    {
        var tags = _clearTags.IsChecked == true
            ? string.Empty
            : string.IsNullOrWhiteSpace(_tags.Text) ? null : _tags.Text.Trim();
        var category = _clearCategory.IsChecked == true
            ? string.Empty
            : string.IsNullOrWhiteSpace(_category.Text) ? null : _category.Text.Trim();
        LibraryReadingStatus? status = _readingStatus.SelectedIndex switch
        {
            1 => LibraryReadingStatus.Unread,
            2 => LibraryReadingStatus.Reading,
            3 => LibraryReadingStatus.Finished,
            _ => null
        };
        bool? favorite = _favorite.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => null
        };
        return new BatchEditOptions(tags, category, status, favorite);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = AppAppearanceResources.GetBrush("MutedInkBrush")
    };
}
