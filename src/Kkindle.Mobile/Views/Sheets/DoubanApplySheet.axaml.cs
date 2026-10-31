using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views.Sheets;

public sealed record DoubanApplyChoices(
    bool Title,
    bool Authors,
    bool Series,
    bool Publication,
    bool Description,
    bool Cover);

/// <summary>
/// Picks which Douban fields to take. Title and author are off by default, as on
/// desktop: they are how the reader filed the book.
/// </summary>
public partial class DoubanApplySheet : UserControl
{
    private readonly Action<DoubanApplyChoices>? _apply;

    /// <summary>Design-time only.</summary>
    public DoubanApplySheet() => InitializeComponent();

    public DoubanApplySheet(DoubanBookMetadata metadata, Action<DoubanApplyChoices> apply)
    {
        _apply = apply;
        InitializeComponent();
        SummaryText.Text = metadata.Rating is { } rating
            ? $"《{metadata.Title}》 豆瓣 {rating:0.0}（{metadata.RatingCount} 人评价）"
            : $"《{metadata.Title}》";
        TitleCheck.Content = $"书名：{metadata.Title}";
        AuthorsCheck.Content = $"作者：{metadata.Authors}";
        SeriesCheck.Content = $"丛书：{metadata.Series}";
        SeriesCheck.IsVisible = !string.IsNullOrWhiteSpace(metadata.Series);
        SeriesCheck.IsChecked = SeriesCheck.IsVisible;
        DescriptionCheck.IsVisible = !string.IsNullOrWhiteSpace(metadata.Description);
        CoverCheck.IsVisible = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => _apply?.Invoke(new DoubanApplyChoices(
        TitleCheck.IsChecked == true,
        AuthorsCheck.IsChecked == true,
        SeriesCheck.IsChecked == true,
        PublicationCheck.IsChecked == true,
        DescriptionCheck.IsChecked == true && DescriptionCheck.IsVisible,
        CoverCheck.IsChecked == true && CoverCheck.IsVisible));
}
