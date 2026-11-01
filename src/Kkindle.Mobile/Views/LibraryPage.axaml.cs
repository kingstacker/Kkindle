using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Controls;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public partial class LibraryPage : UserControl
{
    private const int CoverWidth = 280;
    private const double MinTileWidth = 118;
    private const double FinishedPercent = 99.5;

    private enum BookFilter
    {
        All,
        Reading,
        Favorite,
        Finished
    }

    private readonly ApplicationModules _modules;
    private readonly MobileServices _services;
    private readonly MobileShell _shell;
    private const int FolderCoverWidth = 200;

    private readonly ObservableCollection<BookTile> _tiles = [];
    private readonly ObservableCollection<CollectionCard> _cards = [];
    private IReadOnlyList<Book> _books = [];
    private IReadOnlyList<LibraryCollectionSummary> _collections = [];
    private Dictionary<Guid, double> _progress = [];
    private Book? _continueBook;
    private BookFilter _filter = BookFilter.All;

    /// <summary>Design-time only: keeps the XAML previewer and runtime loader working.</summary>
    public LibraryPage()
    {
        _modules = null!;
        _services = null!;
        _shell = null!;
        InitializeComponent();
    }

    public LibraryPage(ApplicationModules modules, MobileServices services, MobileShell shell)
    {
        _modules = modules;
        _services = services;
        _shell = shell;
        InitializeComponent();
        BooksGrid.SizeChanged += OnGridSizeChanged;
        CollectionsGrid.ItemsSource = _cards;
        ApplyViewMode();
        ApplyMode();
        Loaded += OnLoaded;
    }

    public IReadOnlyList<Book> VisibleBooks => _tiles.Select(tile => tile.Book).ToList();

    private async void OnLoaded(object? sender, RoutedEventArgs e) => await ReloadAsync();

    public async Task ReloadAsync()
    {
        try
        {
            await _modules.Library.Books.InitializeAsync();
            await _shell.EnsureReaderDataAsync();
            _books = await _modules.Library.Books.SearchAsync();
            _collections = await _modules.Library.Books.GetCollectionSummariesAsync();
            var dashboard = await _modules.Reader.Data.GetReadingDashboardAsync(dayCount: 1);
            _progress = dashboard.Books
                .Where(book => book.IsInLibrary)
                .GroupBy(book => book.BookId)
                .ToDictionary(group => group.Key, group => group.Max(book => book.ProgressPercent));
            _continueBook = dashboard.Books
                .Where(item => item.IsInLibrary && item.ProgressPercent < FinishedPercent)
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => _books.FirstOrDefault(book => book.Id == item.BookId))
                .FirstOrDefault(book => book is not null);
            Status(null);
            ApplyFilter();
            BuildCollectionCards();
        }
        catch (Exception exception)
        {
            Status("书库加载失败：" + exception.Message);
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<Book> filtered = _books;
        var query = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(book =>
                book.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || book.Authors.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        filtered = _filter switch
        {
            BookFilter.Reading => filtered.Where(book => ProgressOf(book) is > 0 and < FinishedPercent
                || book.ReadingStatus == LibraryReadingStatus.Reading),
            BookFilter.Favorite => filtered.Where(book => book.IsFavorite),
            BookFilter.Finished => filtered.Where(book => ProgressOf(book) >= FinishedPercent
                || book.ReadingStatus == LibraryReadingStatus.Finished),
            _ => filtered
        };

        filtered = _shell.Preferences.Current.Sort switch
        {
            MobileBookSort.Title => filtered.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            MobileBookSort.Author => filtered.OrderBy(book => book.Authors, StringComparer.CurrentCultureIgnoreCase),
            MobileBookSort.Added => filtered.OrderByDescending(book => book.CreatedAt),
            _ => filtered.OrderByDescending(book => book.UpdatedAt)
        };

        _tiles.Clear();
        foreach (var book in filtered)
        {
            var tile = new BookTile(book, ProgressOf(book));
            _tiles.Add(tile);
            _ = LoadCoverAsync(tile);
        }

        SummaryText.Text = BuildSummary();
        PresentContinueCard(string.IsNullOrEmpty(query) && _filter == BookFilter.All);
        PresentEmptyState(!string.IsNullOrEmpty(query));
    }

    private double ProgressOf(Book book) => _progress.GetValueOrDefault(book.Id);

    private string BuildSummary()
    {
        var reading = _progress.Values.Count(value => value is > 0 and < FinishedPercent);
        return reading > 0 ? $"{_books.Count} 本 · {reading} 本在读" : $"{_books.Count} 本";
    }

    private void BuildCollectionCards()
    {
        // Named folders first, in name order; the automatic “未收藏” goes last
        // and only appears while it holds something.
        var custom = _collections.Where(summary => !MobileCollections.IsUncollected(summary.Collection)).ToList();
        var uncollected = _collections.FirstOrDefault(summary =>
            MobileCollections.IsUncollected(summary.Collection) && summary.BookCount > 0);

        _cards.Clear();
        foreach (var summary in uncollected is null ? custom : custom.Append(uncollected))
        {
            var card = new CollectionCard(summary.Collection, summary.BookCount);
            _cards.Add(card);
            _ = LoadFolderCoversAsync(card);
        }
        _cards.Add(CollectionCard.Create);

        var filed = _books.Count(book => book.CollectionIds.Any(id => custom.Any(summary => summary.Collection.Id == id)));
        CollectionsSummary.Text = custom.Count == 0
            ? "还没有收藏夹，把相关的书归到一起吧"
            : $"{custom.Count} 个收藏夹 · 已归档 {filed} 本";
        CollectionsHint.IsVisible = custom.Count > 0;
    }

    private async Task LoadFolderCoversAsync(CollectionCard card)
    {
        var books = _books
            .Where(book => book.CollectionIds.Contains(card.Collection!.Id))
            .OrderByDescending(book => book.UpdatedAt)
            .Take(3)
            .ToList();
        var covers = new List<CoverStackItem>(books.Count);
        foreach (var book in books)
        {
            var image = await _shell.Covers.GetAsync(book, FolderCoverWidth);
            covers.Add(new CoverStackItem(image, string.IsNullOrEmpty(book.Title) ? "?" : book.Title[..1]));
        }
        card.Covers = covers;
    }

    private void PresentContinueCard(bool allowed)
    {
        ContinueCard.IsVisible = allowed && _continueBook is not null;
        if (!ContinueCard.IsVisible || _continueBook is not { } book) return;
        var progress = ProgressOf(book);
        ContinueTitle.Text = book.Title;
        ContinueProgress.Value = progress;
        ContinuePercent.Text = $"{progress:0}%";
        _ = LoadContinueCoverAsync(book);
    }

    private void PresentEmptyState(bool searching)
    {
        EmptyState.IsVisible = _tiles.Count == 0;
        if (!EmptyState.IsVisible) return;
        var libraryEmpty = _books.Count == 0;
        EmptyTitle.Text = libraryEmpty ? "书库还是空的" : searching ? "没有找到相关书籍" : "这里还没有书";
        EmptyDetail.Text = libraryEmpty ? "点右上角 + 导入 EPUB 或 PDF" : "换个关键词或筛选条件试试";
    }

    private async Task LoadCoverAsync(BookTile tile)
    {
        tile.Cover = await _shell.Covers.GetAsync(
            tile.Book, CoverWidth);
    }

    private async Task LoadContinueCoverAsync(Book book)
    {
        ContinueCover.Source = await _shell.Covers.GetAsync(
            book, CoverWidth);
    }

    private void Status(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void ApplyViewMode()
    {
        var list = _shell.Preferences.Current.ListView;
        BooksGrid.IsVisible = !list;
        BooksRows.IsVisible = list;
        // Only the visible surface owns the items, so hidden tiles cost nothing.
        BooksGrid.ItemsSource = list ? null : _tiles;
        BooksRows.ItemsSource = list ? _tiles : null;
        if (this.TryFindResource(list ? "IconGrid" : "IconList", out var icon) && icon is Geometry geometry)
            ViewIcon.Data = geometry;
    }

    private void OnGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (BooksGrid.ItemsPanelRoot is UniformGrid grid)
            grid.Columns = Math.Max(3, (int)(e.NewSize.Width / MinTileWidth));
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        SearchRow.IsVisible = true;
        SearchBox.Focus();
    }

    private void OnCloseSearchClick(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchRow.IsVisible = false;
    }

    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        _filter = sender switch
        {
            Button { Name: nameof(FilterReading) } => BookFilter.Reading,
            Button { Name: nameof(FilterFavorite) } => BookFilter.Favorite,
            Button { Name: nameof(FilterFinished) } => BookFilter.Finished,
            _ => BookFilter.All
        };
        FilterAll.Classes.Set("on", _filter == BookFilter.All);
        FilterReading.Classes.Set("on", _filter == BookFilter.Reading);
        FilterFavorite.Classes.Set("on", _filter == BookFilter.Favorite);
        FilterFinished.Classes.Set("on", _filter == BookFilter.Finished);
        ApplyFilter();
    }

    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        var current = _shell.Preferences.Current.Sort;
        ChoiceItem Option(string title, MobileBookSort sort) => new(title, Selected: sort == current, Tag: sort);
        _shell.ShowSheet("排序", new ChoiceSheet(
            [
                Option("最近更新", MobileBookSort.Recent),
                Option("书名", MobileBookSort.Title),
                Option("作者", MobileBookSort.Author),
                Option("添加时间", MobileBookSort.Added)
            ],
            choice =>
            {
                _shell.Preferences.Update(value => value with { Sort = (MobileBookSort)choice.Tag! });
                _shell.HideSheet();
                ApplyFilter();
            }));
    }

    private void OnViewClick(object? sender, RoutedEventArgs e)
    {
        _shell.Preferences.Update(value => value with { ListView = !value.ListView });
        ApplyViewMode();
    }

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        var collections = sender == CollectionsTab;
        if (collections == _shell.Preferences.Current.CollectionsView) return;
        _shell.Preferences.Update(value => value with { CollectionsView = collections });
        ApplyMode();
        PageScroller.Offset = default;
    }

    private void ApplyMode()
    {
        var collections = _shell.Preferences.Current.CollectionsView;
        BooksTab.Classes.Set("on", !collections);
        CollectionsTab.Classes.Set("on", collections);
        BooksPanel.IsVisible = !collections;
        CollectionsPanel.IsVisible = collections;
        // Search filters books; in the folder view it would have nothing to act on.
        SearchButton.IsVisible = !collections;
        if (collections && SearchRow.IsVisible) OnCloseSearchClick(null, new RoutedEventArgs());
    }

    private void OnCollectionTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CollectionCard card) return;
        if (card.Collection is { } collection) _shell.OpenCollection(collection);
        else PromptCreateCollection();
    }

    private async void OnCollectionHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started) return;
        if ((sender as Control)?.DataContext is not CollectionCard { Collection: { } collection }) return;
        e.Handled = true;
        await MobileCollections.ShowActionsAsync(_shell, _modules, collection,
            _ => ReloadAsync(), open: () => _shell.OpenCollection(collection));
    }

    private void PromptCreateCollection() =>
        MobileCollections.PromptCreate(_shell, _modules, collection =>
        {
            _shell.OpenCollection(collection);
            return Task.CompletedTask;
        });

    private void OnBookTapped(object? sender, TappedEventArgs e)
    {
        StyledElement? source = e.Source as StyledElement;
        while (source is not null && source.DataContext is not BookTile)
            source = source.Parent as StyledElement;
        if (source?.DataContext is BookTile tile) _shell.OpenBook(tile.Book);
    }

    private void OnContinueClick(object? sender, RoutedEventArgs e)
    {
        if (_continueBook is { } book) _shell.OpenReader(book);
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        // The masthead + adds whatever the current view lists.
        if (_shell.Preferences.Current.CollectionsView) PromptCreateCollection();
        else await ImportAsync();
    }

    public async Task ImportAsync()
    {
        try
        {
            ImportButton.IsEnabled = false;
            var files = await _services.ImportSource.PickFilesAsync();
            if (files.Count == 0) return;
            Status($"正在导入 {files.Count} 个文件…");
            var result = await _modules.Library.Books.ImportAsync(files);
            await ReloadAsync();
            _shell.ShowToast(result.FailureCount > 0
                ? $"已导入 {result.AddedCount} 本，{result.FailureCount} 个文件失败"
                : $"已导入 {result.AddedCount} 本");
        }
        catch (Exception exception)
        {
            Status("导入失败：" + exception.Message);
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }
}

/// <summary>Presentation row for one library book; keeps domain models untouched.</summary>
public sealed class BookTile(Book book, double progress) : ObservableObject
{
    private Bitmap? _cover;

    public Book Book { get; } = book;
    public double Progress { get; } = progress;
    public bool IsInProgress => Progress is > 0 and < 99.5;
    public string ProgressText => Progress > 0 ? $"{Progress:0}%" : "未读";
    public string Title => Book.Title;
    public string Author => string.IsNullOrWhiteSpace(Book.Authors) ? "未知作者" : Book.Authors;
    public string Format => string.IsNullOrWhiteSpace(Book.FormatSummary) ? "未知格式" : Book.FormatSummary;
    public string TitleInitial => string.IsNullOrEmpty(Book.Title) ? "?" : Book.Title[..1];
    public bool HasCover => Cover is not null;

    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (SetProperty(ref _cover, value)) OnPropertyChanged(nameof(HasCover));
        }
    }
}

/// <summary>One folder in the collections view; a card without a collection is the “new folder” cell.</summary>
public sealed class CollectionCard : ObservableObject
{
    public static CollectionCard Create => new(null, 0);

    private IReadOnlyList<CoverStackItem> _covers = [];

    public CollectionCard(BookCollection? collection, int bookCount)
    {
        Collection = collection;
        BookCount = bookCount;
    }

    public BookCollection? Collection { get; }
    public int BookCount { get; }
    public bool IsCreate => Collection is null;
    public string Name => Collection is null ? string.Empty : MobileCollections.DisplayName(Collection);
    public string CountText => BookCount == 0 ? "空" : $"{BookCount} 本";

    public IReadOnlyList<CoverStackItem> Covers
    {
        get => _covers;
        set => SetProperty(ref _covers, value);
    }
}
