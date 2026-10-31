using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Controls;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

/// <summary>One collection (收藏夹): its books, plus adding, removing and folder management.</summary>
public partial class CollectionPage : UserControl
{
    private const int CoverWidth = 280;
    private const double MinTileWidth = 118;
    private const double FinishedPercent = 99.5;

    private readonly BookCollection _collection;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;
    private readonly ObservableCollection<BookTile> _tiles = [];
    private IReadOnlyList<Book> _library = [];

    /// <summary>Design-time only.</summary>
    public CollectionPage()
    {
        _collection = new BookCollection();
        _modules = null!;
        _shell = null!;
        InitializeComponent();
    }

    public CollectionPage(BookCollection collection, ApplicationModules modules, MobileShell shell)
    {
        _collection = collection;
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        BooksGrid.ItemsSource = _tiles;
        BooksGrid.SizeChanged += OnGridSizeChanged;
        // “未收藏” fills itself from books without a folder; it cannot be edited.
        var editable = !MobileCollections.IsUncollected(collection);
        AddButton.IsVisible = editable;
        MoreButton.IsVisible = editable;
        EmptyAddButton.IsVisible = editable;
        HintText.IsVisible = false;
        NameText.Text = MobileCollections.DisplayName(collection);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            await _shell.EnsureReaderDataAsync();
            _library = await _modules.Library.Books.SearchAsync();
            var dashboard = await _modules.Reader.Data.GetReadingDashboardAsync(dayCount: 1);
            var progress = dashboard.Books
                .Where(book => book.IsInLibrary)
                .GroupBy(book => book.BookId)
                .ToDictionary(group => group.Key, group => group.Max(book => book.ProgressPercent));

            var books = _library
                .Where(book => book.CollectionIds.Contains(_collection.Id))
                .OrderByDescending(book => book.UpdatedAt)
                .ToList();
            _tiles.Clear();
            foreach (var book in books)
            {
                var tile = new BookTile(book, progress.GetValueOrDefault(book.Id));
                _tiles.Add(tile);
                _ = LoadCoverAsync(tile);
            }

            NameText.Text = MobileCollections.DisplayName(_collection);
            var reading = _tiles.Count(tile => tile.IsInProgress);
            var finished = _tiles.Count(tile => tile.Progress >= FinishedPercent);
            MetaText.Text = string.Join(" · ", new[]
            {
                books.Count == 0 ? "暂无书籍" : $"{books.Count} 本",
                reading > 0 ? $"{reading} 本在读" : null,
                finished > 0 ? $"读完 {finished} 本" : null
            }.Where(part => part is not null));
            EmptyState.IsVisible = books.Count == 0;
            HintText.IsVisible = books.Count > 0 && !MobileCollections.IsUncollected(_collection);
            Status(null);
            await LoadHeroAsync(books);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status("收藏夹加载失败：" + UiText.Localize(exception.Message));
        }
    }

    private async Task LoadCoverAsync(BookTile tile)
    {
        tile.Cover = await _shell.Covers.GetAsync(tile.Book, CoverWidth, _shell.Preferences.Current.GrayscaleCovers);
    }

    private async Task LoadHeroAsync(IReadOnlyList<Book> books)
    {
        var covers = new List<CoverStackItem>();
        foreach (var book in books.Take(3))
        {
            var image = await _shell.Covers.GetAsync(book, CoverWidth, _shell.Preferences.Current.GrayscaleCovers);
            covers.Add(new CoverStackItem(image, string.IsNullOrEmpty(book.Title) ? "?" : book.Title[..1]));
        }
        HeroCovers.Items = covers;
    }

    private void Status(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void OnGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (BooksGrid.ItemsPanelRoot is UniformGrid grid)
            grid.Columns = Math.Max(3, (int)(e.NewSize.Width / MinTileWidth));
    }

    private static BookTile? TileOf(RoutedEventArgs e)
    {
        var source = e.Source as StyledElement;
        while (source is not null && source.DataContext is not BookTile)
            source = source.Parent as StyledElement;
        return source?.DataContext as BookTile;
    }

    private void OnBookTapped(object? sender, TappedEventArgs e)
    {
        if (TileOf(e) is { } tile) _shell.OpenBook(tile.Book);
    }

    private void OnBookHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || TileOf(e) is not { } tile) return;
        if (MobileCollections.IsUncollected(_collection)) return;
        e.Handled = true;
        _shell.ShowSheet(tile.Title, new ChoiceSheet(
            [new ChoiceItem("查看详情", Tag: "open"), new ChoiceItem("移出收藏夹", Tag: "remove")],
            choice =>
            {
                if (choice.Tag as string == "open")
                {
                    _shell.HideSheet();
                    _shell.OpenBook(tile.Book);
                }
                else _ = RemoveAsync(tile.Book);
            }));
    }

    private async Task RemoveAsync(Book book)
    {
        _shell.HideSheet();
        try
        {
            await _modules.Library.Books.RemoveBookFromCollectionAsync(book.Id, _collection.Id);
            _shell.ShowToast($"已从「{_collection.Name}」移出");
            await ReloadAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell.ShowToast("移出失败：" + UiText.Localize(exception.Message));
        }
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var candidates = _library
            .Where(book => !book.CollectionIds.Contains(_collection.Id))
            .OrderByDescending(book => book.UpdatedAt)
            .ToList();
        if (candidates.Count == 0)
        {
            _shell.ShowToast(_library.Count == 0 ? "书库还是空的" : "书库里的书都已在这个收藏夹");
            return;
        }
        _shell.ShowSheet($"加入「{_collection.Name}」", new BookPickSheet(candidates, _shell, AddAsync));
    }

    private async Task AddAsync(IReadOnlyList<Book> books)
    {
        try
        {
            foreach (var book in books)
                await _modules.Library.Books.AddBookToCollectionAsync(book.Id, _collection.Id);
            _shell.HideSheet();
            _shell.ShowToast($"已加入 {books.Count} 本");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell.ShowToast("加入失败：" + UiText.Localize(exception.Message));
        }
        await ReloadAsync();
    }

    private async void OnMoreClick(object? sender, RoutedEventArgs e) =>
        await MobileCollections.ShowActionsAsync(_shell, _modules, _collection, async change =>
        {
            if (change == CollectionChange.Removed) _shell.GoBack();
            else await ReloadAsync();
        });

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();
}
