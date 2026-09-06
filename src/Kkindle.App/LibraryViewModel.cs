using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Presentation data for one local-library book. The domain model remains
/// untouched; this class only turns paths and enum values into UI labels and a
/// loadable Avalonia bitmap.
/// </summary>
public sealed class BookCardViewModel : ObservableObject, IDisposable
{
    public BookCardViewModel(Book book, string dataRoot)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Book = book;
        DataRoot = dataRoot;
        Refresh();
        LoadCover();
    }

    public Book Book { get; }
    public string DataRoot { get; }
    public string Title => UiText.Localize(Book.Title);
    public string Authors => UiText.Localize(Book.Authors);
    public string FormatLabel => Book.FormatSummary;
    public string FileCountLabel => Book.Files.Count == 0 ? string.Empty : UiText.Get("{0} 个文件", Book.Files.Count);
    public string TotalSizeLabel => Book.Files.Count == 0 ? UiText.Get("暂无文件") : FormatSize(Book.Files.Sum(file => file.Size));
    public string FileSummaryLabel => string.Join(" · ", new[] { FormatLabel, FileCountLabel, TotalSizeLabel }
        .Where(value => value.Length > 0));
    public string ReadingStatusLabel => Book.ReadingStatus switch
    {
        LibraryReadingStatus.Reading => UiText.Get("阅读中"),
        LibraryReadingStatus.Finished => UiText.Get("已读"),
        _ => UiText.Get("待读")
    };
    public string ReadingStateLabel => Book.IsFavorite
        ? UiText.Get("{0} · ★ 收藏", ReadingStatusLabel)
        : ReadingStatusLabel;
    public string OrganizationLabel
    {
        get
        {
            var labels = new[]
            {
                string.IsNullOrWhiteSpace(Book.Category) ? string.Empty : UiText.Get("分类：{0}", Book.Category),
                string.IsNullOrWhiteSpace(Book.Tags) ? string.Empty : UiText.Get("标签：{0}", Book.Tags)
            }.Where(value => value.Length > 0).ToArray();
            return labels.Length == 0 ? UiText.Get("暂无分类或标签") : string.Join(" · ", labels);
        }
    }
    public string PublicationLabel
    {
        get
        {
            var labels = new[]
            {
                string.IsNullOrWhiteSpace(Book.Publisher) ? string.Empty : UiText.Get("出版社：{0}", Book.Publisher),
                string.IsNullOrWhiteSpace(Book.PublishDate) ? string.Empty : UiText.Get("出版：{0}", Book.PublishDate),
                string.IsNullOrWhiteSpace(Book.PageCount) ? string.Empty : UiText.Get("页数：{0}", Book.PageCount),
                string.IsNullOrWhiteSpace(Book.Binding) ? string.Empty : UiText.Get("装帧：{0}", Book.Binding)
            }.Where(value => value.Length > 0).ToArray();
            return labels.Length == 0 ? UiText.Get("暂无出版信息") : string.Join(" · ", labels);
        }
    }
    public string IdentifierLabel
    {
        get
        {
            var labels = new[]
            {
                string.IsNullOrWhiteSpace(Book.Isbn) ? string.Empty : UiText.Get("ISBN：{0}", Book.Isbn),
                Book.DoubanRating is { } rating
                    ? UiText.Get("豆瓣：{0:0.0}（{1} 人评价）", rating, Book.DoubanRatingCount ?? 0)
                    : string.Empty
            }.Where(value => value.Length > 0).ToArray();
            return labels.Length == 0 ? UiText.Get("暂无 ISBN 或评分") : string.Join(" · ", labels);
        }
    }
    public string DescriptionSummaryLabel
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(Book.Description)
                ? UiText.Get("暂无简介")
                : Regex.Replace(Book.Description, @"\s+", " ").Trim();
            return description.Length <= 90 ? description : $"{description[..90]}…";
        }
    }
    public string UpdatedLabel => UiText.Get("更新于 {0:yyyy-MM-dd HH:mm}", Book.UpdatedAt.ToLocalTime());
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Book.Description) ? UiText.Get("暂无简介") : Book.Description;
    public Bitmap? CoverImage { get; private set; }

    // Keep selection on the card itself. The legacy WinUI shelf did not use
    // the list control's selection fill; it drew a black outline around the
    // exact 154-DIP card footprint instead.
    private bool _isSelected;
    private bool _isMultiSelected;
    private bool _isHovered;
    private bool _coverLoadAttempted;

    // Any selection (single click or multi-select) turns the card outline
    // black; the check badge below is reserved for genuine multi-selection.
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }

    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => SetProperty(ref _isMultiSelected, value);
    }

    // Hovering the whole card (cover or text) shows the same thin black
    // frame as selection, so the outline lives on the card, not on the cover
    // image alone.
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetProperty(ref _isHovered, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }

    // The black frame around the entire card appears on hover or selection.
    public bool IsFrameVisible => IsSelected || IsHovered;

    // The original gallery always surfaced where a book is available. The
    // portable library starts with the authoritative local copy and changes
    // this value when a future device scan supplies a matching Kindle copy.
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.ComputerOnly;
    private bool _isLibraryPresenceVisible = true;
    private bool _isGalleryTextVisible = true;

    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (!SetProperty(ref _libraryPresence, value)) return;
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
            OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
            OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        }
    }

    // The WinUI reference draws three distinct monochrome glyphs for the
    // library-presence badge (PC only / Kindle only / both). Avalonia keeps
    // the same three-state model so the comparison stays readable instead of
    // using a fixed icon on every card.
    public bool ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both;

    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => UiText.Get("电脑与 Kindle 书库都有"),
        BookLibraryPresence.KindleOnly => UiText.Get("仅 Kindle 书库有"),
        _ => UiText.Get("仅电脑书库有")
    };

    // The presence badge sits in the card's text footer; when gallery mode
    // hides the footer the badge must go with it so only the cover remains.
    public bool PresenceVisibility => _isLibraryPresenceVisible && _isGalleryTextVisible;

    public bool GalleryTextVisibility => _isGalleryTextVisible;

    // Gallery mode trims the card to the cover alone so no blank text block
    // remains under the image.
    public double CardHeight => _isGalleryTextVisible ? 292 : 214;

    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }

    public void SetGalleryTextVisible(bool visible)
    {
        if (_isGalleryTextVisible == visible) return;
        _isGalleryTextVisible = visible;
        OnPropertyChanged(nameof(GalleryTextVisibility));
        OnPropertyChanged(nameof(PresenceVisibility));
        OnPropertyChanged(nameof(CardHeight));
    }

    // Format conversion progress shown on the book card while the conversion
    // popup is minimized to the background. The badge is a tap target that
    // restores the popup, matching the WinUI card behaviour.
    private bool _isConversionProgressVisible;
    private double _conversionProgress;
    private string _conversionProgressLabel = "0%";
    private string _conversionProgressMessage = UiText.Get("正在转换…");

    public bool IsConversionProgressVisible
    {
        get => _isConversionProgressVisible;
        private set
        {
            if (!SetProperty(ref _isConversionProgressVisible, value)) return;
            OnPropertyChanged(nameof(ConversionProgressVisibility));
        }
    }

    public bool ConversionProgressVisibility => IsConversionProgressVisible;

    public double ConversionProgress
    {
        get => _conversionProgress;
        private set => SetProperty(ref _conversionProgress, value);
    }

    public string ConversionProgressLabel
    {
        get => _conversionProgressLabel;
        private set => SetProperty(ref _conversionProgressLabel, value);
    }

    public string ConversionProgressMessage
    {
        get => _conversionProgressMessage;
        private set => SetProperty(ref _conversionProgressMessage, value);
    }

    public void SetConversionProgress(FormatConversionProgress progress, bool showIndicator)
    {
        ConversionProgress = Math.Clamp(progress.Percentage, 0, 100);
        ConversionProgressLabel = $"{progress.RoundedPercentage}%";
        ConversionProgressMessage = UiText.Localize(progress.Message);
        IsConversionProgressVisible = showIndicator;
    }

    public void ClearConversionProgress()
    {
        IsConversionProgressVisible = false;
        ConversionProgress = 0;
        ConversionProgressLabel = "0%";
        ConversionProgressMessage = UiText.Get("正在转换…");
    }

    public void Refresh()
    {
        CoverImage?.Dispose();
        CoverImage = null;
        _coverLoadAttempted = false;

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(TotalSizeLabel));
        OnPropertyChanged(nameof(FileSummaryLabel));
        OnPropertyChanged(nameof(ReadingStatusLabel));
        OnPropertyChanged(nameof(ReadingStateLabel));
        OnPropertyChanged(nameof(OrganizationLabel));
        OnPropertyChanged(nameof(PublicationLabel));
        OnPropertyChanged(nameof(IdentifierLabel));
        OnPropertyChanged(nameof(DescriptionSummaryLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(DescriptionLabel));
        OnPropertyChanged(nameof(CoverImage));
    }

    public void LoadCover()
    {
        if (_coverLoadAttempted) return;
        _coverLoadAttempted = true;
        CoverImage = LoadCoverImage(DataRoot, Book.CoverPath, 320);
        OnPropertyChanged(nameof(CoverImage));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(TotalSizeLabel));
        OnPropertyChanged(nameof(FileSummaryLabel));
        OnPropertyChanged(nameof(ReadingStatusLabel));
        OnPropertyChanged(nameof(ReadingStateLabel));
        OnPropertyChanged(nameof(OrganizationLabel));
        OnPropertyChanged(nameof(PublicationLabel));
        OnPropertyChanged(nameof(IdentifierLabel));
        OnPropertyChanged(nameof(DescriptionSummaryLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(DescriptionLabel));
        OnPropertyChanged(nameof(PresenceLabel));
        OnPropertyChanged(nameof(ConversionProgressMessage));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        CoverImage?.Dispose();
        CoverImage = null;
    }

    private static Bitmap? LoadCoverImage(string dataRoot, string? relativePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(
                stream,
                decodeWidth,
                BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }
}

public sealed class BookCollectionFolderViewModel : ObservableObject, IDisposable
{
    private readonly Bitmap?[] _covers = new Bitmap?[3];

    public BookCollectionFolderViewModel(
        BookCollection collection,
        int bookCount,
        string dataRoot,
        IReadOnlyList<string?> coverPaths)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Collection = collection;
        BookCount = bookCount;
        for (var index = 0; index < _covers.Length; index++)
        {
            var path = index < coverPaths.Count ? coverPaths[index] : null;
            _covers[index] = LoadCoverImage(dataRoot, path, 96);
        }
    }

    public BookCollection Collection { get; }
    public string Name => string.Equals(
        Collection.Name,
        BookLibraryDefaults.UncollectedCollectionName,
        StringComparison.Ordinal)
        ? UiText.Get("未收藏")
        : UiText.Localize(Collection.Name);
    public int BookCount { get; }
    public string BookCountLabel => UiText.Get("{0} 本书", BookCount);
    public Bitmap? Cover1 => _covers[0];
    public Bitmap? Cover2 => _covers[1];
    public Bitmap? Cover3 => _covers[2];

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BookCountLabel));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        foreach (var cover in _covers) cover?.Dispose();
    }

    private static Bitmap? LoadCoverImage(string dataRoot, string? relativePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(
                stream,
                decodeWidth,
                BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class LibraryViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 200;

    private readonly IBookLibraryService _library;
    private readonly string _dataRoot;
    private readonly Dictionary<Guid, BookCardViewModel> _bookCards = [];
    private string _searchText = string.Empty;
    private string? _authorFilter;
    private string? _tagFilter;
    private string? _formatFilter;
    private string? _categoryFilter;
    private Guid? _collectionFilterId;
    private string? _collectionFilterName;
    private LibraryReadingStatus? _readingStatusFilter;
    private bool _favoritesOnly;
    private LibrarySortMode _sortMode;
    private int _pageIndex;
    private int _pageCount = 1;
    private bool _isBusy;
    private string _statusText = UiText.Get("准备就绪");

    public LibraryViewModel(IBookLibraryService library, string dataRoot)
    {
        _library = library;
        _dataRoot = dataRoot;
        UiText.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<BookCardViewModel> Books { get; } = [];
    public IReadOnlyList<Book> LibraryBooks { get; private set; } = [];
    public IReadOnlyList<string> AvailableAuthors { get; private set; } = [];
    public IReadOnlyList<string> AvailableTags { get; private set; } = [];
    public IReadOnlyList<string> AvailableFormats { get; private set; } = [];
    public IReadOnlyList<string> AvailableCategories { get; private set; } = [];

    public int PageIndex => _pageIndex;
    public int PageCount => _pageCount;
    public bool CanGoToPreviousPage => _pageIndex > 0;
    public bool CanGoToNextPage => _pageIndex + 1 < _pageCount;
    public string PageStatusText => _pageCount <= 1
        ? string.Empty
        : UiText.Get("第 {0} / {1} 页", _pageIndex + 1, _pageCount);

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? AuthorFilter
    {
        get => _authorFilter;
        set => SetFilter(ref _authorFilter, value);
    }

    public string? TagFilter
    {
        get => _tagFilter;
        set => SetFilter(ref _tagFilter, value);
    }

    public string? FormatFilter
    {
        get => _formatFilter;
        set => SetFilter(ref _formatFilter, value);
    }

    public string? CategoryFilter
    {
        get => _categoryFilter;
        set => SetFilter(ref _categoryFilter, value);
    }

    public Guid? CollectionFilterId
    {
        get => _collectionFilterId;
        set => SetFilter(ref _collectionFilterId, value);
    }

    public string? CollectionFilterName
    {
        get => _collectionFilterName;
        set => SetProperty(ref _collectionFilterName, value);
    }

    public LibraryReadingStatus? ReadingStatusFilter
    {
        get => _readingStatusFilter;
        set => SetFilter(ref _readingStatusFilter, value);
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set => SetFilter(ref _favoritesOnly, value);
    }

    public LibrarySortMode SortMode
    {
        get => _sortMode;
        set => SetFilter(ref _sortMode, value);
    }

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(AuthorFilter)
        || !string.IsNullOrWhiteSpace(TagFilter)
        || !string.IsNullOrWhiteSpace(FormatFilter)
        || !string.IsNullOrWhiteSpace(CategoryFilter)
        || ReadingStatusFilter is not null
        || FavoritesOnly
        || CollectionFilterId is not null;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var allBooks = await _library.SearchAsync(cancellationToken: cancellationToken);
            LibraryBooks = allBooks;
            _pageIndex = 0;

            AvailableAuthors = allBooks
                .SelectMany(book => SplitValues(book.Authors))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            AvailableTags = allBooks
                .SelectMany(book => SplitValues(book.Tags))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            AvailableFormats = allBooks
                .SelectMany(book => book.Files.Select(file => file.Format.ToUpperInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AvailableCategories = allBooks
                .Select(book => book.Category.Trim())
                .Where(category => category.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            OnPropertyChanged(nameof(AvailableAuthors));
            OnPropertyChanged(nameof(AvailableTags));
            OnPropertyChanged(nameof(AvailableFormats));
            OnPropertyChanged(nameof(AvailableCategories));
            ApplyCurrentView(resetPage: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshView() => ApplyCurrentView(resetPage: true);

    public void GoToPreviousPage()
    {
        if (!CanGoToPreviousPage) return;
        _pageIndex--;
        ApplyCurrentView();
    }

    public void GoToNextPage()
    {
        if (!CanGoToNextPage) return;
        _pageIndex++;
        ApplyCurrentView();
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        foreach (var card in _bookCards.Values)
            card.Dispose();
        _bookCards.Clear();
        Books.Clear();
    }

    public async Task<ImportBatchResult> ImportAsync(
        IEnumerable<string> paths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ImportBookConflict, Task<ImportConflictResolution>>? conflictResolver = null)
    {
        IsBusy = true;
        try
        {
            var result = await _library.ImportAsync(paths, progress, cancellationToken, conflictResolver);
            await RefreshAsync(cancellationToken);
            StatusText = result.FailureCount == 0
                ? UiText.Get("已导入 {0} 项", result.SuccessCount)
                : UiText.Get("已导入 {0} 项，{1} 项失败", result.SuccessCount, result.FailureCount);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteBookAsync(Book book, CancellationToken cancellationToken = default)
    {
        await _library.DeleteAsync(book.Id, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = UiText.Get("已移入回收站：《{0}》", book.Title);
    }

    public async Task DeleteFileAsync(Book book, BookFile file, CancellationToken cancellationToken = default)
    {
        await _library.DeleteFileAsync(book.Id, file.Id, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = UiText.Get("已移入回收站：{0} 文件", file.Format.ToUpperInvariant());
    }

    public string GetAbsoluteFilePath(BookFile file) => _library.GetAbsoluteFilePath(file);

    private void ApplyCurrentView(bool resetPage = false)
    {
        if (resetPage) _pageIndex = 0;
        var filtered = LibraryBooks.Where(MatchesFilters);
        var books = SortMode switch
        {
            LibrarySortMode.TitleAscending => filtered.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            LibrarySortMode.AuthorAscending => filtered.OrderBy(book => book.Authors, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title).ToArray(),
            LibrarySortMode.CreatedDescending => filtered.OrderByDescending(book => book.CreatedAt).ToArray(),
            LibrarySortMode.ProgressDescending => filtered.OrderByDescending(book => book.ReadingStatus).ThenByDescending(book => book.UpdatedAt).ToArray(),
            _ => filtered.OrderByDescending(book => book.UpdatedAt).ToArray()
        };

        var pageCount = Math.Max(1, (int)Math.Ceiling(books.Length / (double)PageSize));
        if (_pageIndex >= pageCount) _pageIndex = pageCount - 1;
        if (_pageCount != pageCount)
        {
            _pageCount = pageCount;
            OnPropertyChanged(nameof(PageCount));
        }
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(PageStatusText));

        // Keep the model list authoritative, but bound the presentation layer
        // to one page. A normal WrapPanel materializes every item; creating
        // 40,000 card visual trees and cover bitmaps is what made large imports
        // exhaust memory even though the database itself was fine.
        var visibleBooks = books
            .Skip(_pageIndex * PageSize)
            .Take(PageSize)
            .ToArray();
        foreach (var card in _bookCards.Values) card.Dispose();
        _bookCards.Clear();
        Books.Clear();
        foreach (var book in visibleBooks)
        {
            var card = new BookCardViewModel(book, _dataRoot);
            _bookCards[book.Id] = card;
            Books.Add(card);
        }

        var baseStatus = books.Length == 0
            ? LibraryBooks.Count == 0 ? UiText.Get("书库还是空的")
                : CollectionFilterId is not null ? UiText.Get("“{0}”收藏夹还是空的", UiText.Localize(CollectionFilterName ?? string.Empty))
                : UiText.Get("没有符合条件的书籍")
            : HasActiveFilters || !string.IsNullOrWhiteSpace(SearchText)
                ? CollectionFilterId is not null
                    ? UiText.Get("{0} · {1} 本书", UiText.Localize(CollectionFilterName ?? string.Empty), books.Length)
                    : UiText.Get("找到 {0} 本书", books.Length)
                : UiText.Get("共 {0} 本书", books.Length);
        StatusText = _pageCount <= 1
            ? baseStatus
            : $"{baseStatus} · {PageStatusText}";

        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private bool MatchesFilters(Book book)
    {
        if (!string.IsNullOrWhiteSpace(SearchText)
            && !book.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !book.Authors.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !book.Tags.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(AuthorFilter)
            && !SplitValues(book.Authors).Any(author => string.Equals(author, AuthorFilter, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(TagFilter)
            && !SplitValues(book.Tags).Any(tag => string.Equals(tag, TagFilter, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(CategoryFilter)
            && !string.Equals(book.Category.Trim(), CategoryFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (ReadingStatusFilter is { } readingStatus && book.ReadingStatus != readingStatus) return false;
        if (FavoritesOnly && !book.IsFavorite) return false;
        if (CollectionFilterId is { } collectionId && !book.CollectionIds.Contains(collectionId)) return false;
        return string.IsNullOrWhiteSpace(FormatFilter)
            || book.Files.Any(file => string.Equals(file.Format, FormatFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitValues(string? value) =>
        (value ?? string.Empty)
        .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);

    private void SetFilter<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        ApplyCurrentView();
}
