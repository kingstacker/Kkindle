using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>The details a Z-Library page shows, which are written into the book on download.</summary>
public partial class ZLibraryBookSheet : UserControl, IFullHeightSheet
{
    private readonly Action? _download;

    /// <summary>Design-time only.</summary>
    public ZLibraryBookSheet() => InitializeComponent();

    public ZLibraryBookSheet(ZLibraryRow row, Action download)
    {
        _download = download;
        InitializeComponent();
        var book = row.Book;
        TitleText.Text = book.Title;
        AuthorText.Text = book.Author;
        InfoText.Text = book.InfoLabel;
        CoverImage.Source = row.Cover;
        CoverInitial.Text = row.Initial;
        CoverInitial.IsVisible = row.Cover is null;

        var facts = new List<(string Label, string Value)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add((label, value.Trim()));
        }
        Add("出版社", book.Publisher);
        Add("年份", book.Year is > 0 ? book.Year.Value.ToString(CultureInfo.InvariantCulture) : null);
        Add("丛书", book.Series);
        Add("版本", book.Edition);
        Add("卷", book.Volume);
        Add("ISBN", book.Identifier);
        Add("页数", book.Pages is > 0 ? book.Pages.Value.ToString(CultureInfo.InvariantCulture) : null);
        Add("语言", book.Language);
        FactsList.ItemsSource = facts.Select(fact => new { fact.Label, fact.Value }).ToList();

        var description = ZLibraryMetadata.Description(book);
        DescriptionText.Text = description;
        DescriptionText.IsVisible = description.Length > 0;
        if (row.State == "已在书库") ShowStatus("书库中已有这本书");
    }

    public bool FillsHeight => true;

    public void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    public void SetBusy(bool busy) => DownloadButton.IsEnabled = !busy;

    private void OnDownloadClick(object? sender, RoutedEventArgs e) => _download?.Invoke();
}
