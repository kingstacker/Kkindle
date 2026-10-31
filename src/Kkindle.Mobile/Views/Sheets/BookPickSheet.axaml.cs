using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>Picks several library books at once, e.g. to fill a collection.</summary>
public partial class BookPickSheet : UserControl, IFullHeightSheet
{
    private const int CoverWidth = 96;

    private readonly IReadOnlyList<PickRow> _rows = [];
    private readonly Func<IReadOnlyList<Book>, Task>? _onConfirm;

    /// <summary>Design-time only.</summary>
    public BookPickSheet() => InitializeComponent();

    public BookPickSheet(IReadOnlyList<Book> books, MobileShell shell, Func<IReadOnlyList<Book>, Task> onConfirm)
    {
        _onConfirm = onConfirm;
        InitializeComponent();
        var grayscale = shell.Preferences.Current.GrayscaleCovers;
        // A row asks for its cover the first time a realized row binds to it,
        // so a long library costs only what is on screen.
        _rows = books.Select(book => new PickRow(book,
            () => shell.Covers.GetAsync(book, CoverWidth, grayscale))).ToList();
        Rows.ItemsSource = _rows;
    }

    public bool FillsHeight => true;

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        var query = FilterBox.Text?.Trim();
        var visible = string.IsNullOrEmpty(query)
            ? _rows
            : _rows.Where(row => row.Book.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Book.Authors.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Rows.ItemsSource = visible;
        NoMatchText.IsVisible = visible.Count == 0;
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PickRow row) return;
        row.IsPicked = !row.IsPicked;
        var count = _rows.Count(item => item.IsPicked);
        ConfirmButton.IsEnabled = count > 0;
        ConfirmButton.Content = count > 0 ? $"加入 {count} 本" : "选择要加入的书";
    }

    private async void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_onConfirm is null) return;
        ConfirmButton.IsEnabled = false;
        try
        {
            await _onConfirm(_rows.Where(row => row.IsPicked).Select(row => row.Book).ToList());
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }
}

public sealed class PickRow(Book book, Func<Task<Bitmap?>> loadCover) : ObservableObject
{
    private Bitmap? _cover;
    private bool _coverRequested;
    private bool _isPicked;

    public Book Book { get; } = book;
    public string Title => Book.Title;
    public string Author => string.IsNullOrWhiteSpace(Book.Authors) ? "未知作者" : Book.Authors;
    public string Initial => string.IsNullOrEmpty(Book.Title) ? "?" : Book.Title[..1];
    public bool HasCover => Cover is not null;

    public Bitmap? Cover
    {
        get
        {
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }
            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        var cover = await loadCover();
        if (SetProperty(ref _cover, cover, nameof(Cover))) OnPropertyChanged(nameof(HasCover));
    }

    public bool IsPicked
    {
        get => _isPicked;
        set => SetProperty(ref _isPicked, value);
    }
}
