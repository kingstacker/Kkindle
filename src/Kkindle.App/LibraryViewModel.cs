using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// ObservableCollection with a single-notification bulk replace. A library
/// refresh used to raise one Remove/Insert/Move per card, and every event made
/// the virtualizing panel tear down its realized containers.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

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
        _coverFileStamp = CoverFileStamp(book);
        Refresh();
    }

    public Book Book { get; private set; }
    public string DataRoot { get; }
    public string Title => _title;
    public string Authors => _authors;
    // A book can contain several EPUB files (for example the original plus a
    // translated or bilingual edition). The old gallery only showed the
    // distinct extension, which made a successfully imported edition look as
    // if it had never been added. Keep the list view's detailed summary
    // separate and surface the file count in the compact gallery label.
    public string FormatLabel => _formatLabel;
    public string FileCountLabel => _fileCountLabel;
    public string TotalSizeLabel => _totalSizeLabel;
    public string FileSummaryLabel => _fileSummaryLabel;
    public string ReadingStatusLabel => _readingStatusLabel;
    public string ReadingStateLabel => _readingStateLabel;
    public string OrganizationLabel => _organizationLabel;
    public string PublicationLabel => _publicationLabel;
    public string IdentifierLabel => _identifierLabel;
    public string DescriptionSummaryLabel => _descriptionSummaryLabel;
    public string UpdatedLabel => _updatedLabel;
    public string DescriptionLabel => _descriptionLabel;
    public Bitmap? CoverImage { get; private set; }

    // Every visible label is derived from the immutable Book snapshot and the
    // current language. Computing them once per Refresh keeps binding reads
    // (including Text + ToolTip.Tip on the same property) allocation-free.
    private string _title = "";
    private string _authors = "";
    private string _formatLabel = "";
    private string _fileCountLabel = "";
    private string _totalSizeLabel = "";
    private string _fileSummaryLabel = "";
    private string _readingStatusLabel = "";
    private string _readingStateLabel = "";
    private string _organizationLabel = "";
    private string _publicationLabel = "";
    private string _identifierLabel = "";
    private string _descriptionSummaryLabel = "";
    private string _updatedLabel = "";
    private string _descriptionLabel = "";

    // Keep selection on the card itself. The legacy WinUI shelf did not use
    // the list control's selection fill; it drew a black outline around the
    // exact 154-DIP card footprint instead.
    private bool _isSelected;
    private bool _isMultiSelected;
    private bool _isHovered;
    private bool _coverLoadAttempted;
    private bool _disposed;
    private int _coverVersion;
    private CancellationTokenSource? _coverCancellation;
    public Task CoverLoadingTask { get; private set; } = Task.CompletedTask;

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
    private bool _isSyncStatusVisible = true;
    private BookSyncStatus _syncStatus = BookSyncStatus.NotSynced;

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
        BookLibraryPresence.Both => UiText.Get("电脑与当前设备都有"),
        BookLibraryPresence.KindleOnly => UiText.Get("仅当前设备有"),
        _ => UiText.Get("仅电脑书库有")
    };

    // The presence badge sits in the card's text footer; when gallery mode
    // hides the footer the badge must go with it so only the cover remains.
    public bool PresenceVisibility => _isLibraryPresenceVisible && _isGalleryTextVisible;

    public BookSyncStatus SyncStatus
    {
        get => _syncStatus;
        private set
        {
            if (!SetProperty(ref _syncStatus, value)) return;
            OnPropertyChanged(nameof(SyncStatusLabel));
            OnPropertyChanged(nameof(SyncNotSyncedVisibility));
            OnPropertyChanged(nameof(SyncSyncedVisibility));
            OnPropertyChanged(nameof(SyncNotDownloadedVisibility));
        }
    }

    public bool SyncStatusVisibility => _isSyncStatusVisible;
    public bool SyncNotSyncedVisibility => SyncStatus == BookSyncStatus.NotSynced;
    // A local copy that is already part of the remote baseline is simply
    // synced from the user's perspective; the separate enum value is kept
    // internally for service/test compatibility.
    public bool SyncSyncedVisibility => SyncStatus is BookSyncStatus.Synced or BookSyncStatus.Downloaded;
    public bool SyncNotDownloadedVisibility => SyncStatus == BookSyncStatus.NotDownloaded;
    public string SyncStatusLabel => SyncStatus switch
    {
        BookSyncStatus.Synced => UiText.Get("已同步"),
        BookSyncStatus.NotDownloaded => UiText.Get("已同步到云端，尚未下载；点击图标下载"),
        BookSyncStatus.Downloaded => UiText.Get("已同步"),
        _ => UiText.Get("未同步；点击图标上传")
    };

    public bool GalleryTextVisibility => _isGalleryTextVisible;

    // Gallery mode trims the card to the cover alone so no blank text block
    // remains under the image.
    public double CardHeight => _isGalleryTextVisible ? 292 : 214;

    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;

    public void SetSyncStatus(BookSyncStatus status) => SyncStatus = status;

    public void SetSyncStatusVisible(bool visible)
    {
        if (_isSyncStatusVisible == visible) return;
        _isSyncStatusVisible = visible;
        OnPropertyChanged(nameof(SyncStatusVisibility));
    }

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

    private string _coverFileStamp = "";
    private string CoverFileStamp(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return "";
        var info = new FileInfo(Path.Combine(DataRoot, book.CoverPath));
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
    }

    public void UpdateBook(Book book)
    {
        // The page query returns the same UpdatedAt for unchanged books, so a
        // refresh no longer stats every cover file just to rediscover that
        // nothing changed.
        var filesChanged = !Book.Files.Select(file => (file.Id, file.Sha256, file.RelativePath))
            .SequenceEqual(book.Files.Select(file => (file.Id, file.Sha256, file.RelativePath)));
        var metadataChanged = Book.UpdatedAt != book.UpdatedAt
            || Book.Title != book.Title
            || Book.CoverPath != book.CoverPath
            || filesChanged;
        var stamp = metadataChanged ? CoverFileStamp(book) : _coverFileStamp;
        var coverChanged = metadataChanged
            && (stamp != _coverFileStamp || Book.Title != book.Title
                || Book.CoverPath != book.CoverPath || filesChanged);
        var wasLoaded = _coverLoadAttempted;
        _coverFileStamp = stamp;
        Book = book;
        Refresh(coverChanged);
        if (coverChanged && wasLoaded) LoadCover();
    }

    public void Refresh(bool unloadCover = true)
    {
        if (unloadCover) UnloadCover();

        var files = Book.Files;
        var title = UiText.Localize(Book.Title);
        var authors = UiText.Localize(Book.Authors);
        var fileCountLabel = files.Count == 0 ? string.Empty : UiText.Get("{0} 个文件", files.Count);
        var totalSizeLabel = files.Count == 0 ? UiText.Get("暂无文件") : FormatSize(files.Sum(file => file.Size));
        var formatLabel = files.Count <= 1
            ? Book.FormatSummary
            : UiText.Get("{0} · {1}", Book.FormatSummary, fileCountLabel);
        var fileSummaryLabel = string.Join(" · ", new[] { Book.FormatSummary, fileCountLabel, totalSizeLabel }
            .Where(value => value.Length > 0));
        var readingStatusLabel = Book.ReadingStatus switch
        {
            LibraryReadingStatus.Reading => UiText.Get("阅读中"),
            LibraryReadingStatus.Finished => UiText.Get("已读"),
            _ => UiText.Get("待读")
        };
        var readingStateLabel = Book.IsFavorite
            ? UiText.Get("{0} · ★ 收藏", readingStatusLabel)
            : readingStatusLabel;
        var organizationLabel = JoinLabels(
            string.IsNullOrWhiteSpace(Book.Category) ? string.Empty : UiText.Get("分类：{0}", Book.Category),
            string.IsNullOrWhiteSpace(Book.Tags) ? string.Empty : UiText.Get("标签：{0}", Book.Tags),
            empty: UiText.Get("暂无分类或标签"));
        var publicationLabel = JoinLabels(
            string.IsNullOrWhiteSpace(Book.Publisher) ? string.Empty : UiText.Get("出版社：{0}", Book.Publisher),
            string.IsNullOrWhiteSpace(Book.PublishDate) ? string.Empty : UiText.Get("出版：{0}", Book.PublishDate),
            string.IsNullOrWhiteSpace(Book.PageCount) ? string.Empty : UiText.Get("页数：{0}", Book.PageCount),
            string.IsNullOrWhiteSpace(Book.Binding) ? string.Empty : UiText.Get("装帧：{0}", Book.Binding),
            empty: UiText.Get("暂无出版信息"));
        var identifierLabel = JoinLabels(
            string.IsNullOrWhiteSpace(Book.Isbn) ? string.Empty : UiText.Get("ISBN：{0}", Book.Isbn),
            Book.DoubanRating is { } rating
                ? UiText.Get("豆瓣：{0:0.0}（{1} 人评价）", rating, Book.DoubanRatingCount ?? 0)
                : string.Empty,
            empty: UiText.Get("暂无 ISBN 或评分"));
        string descriptionLabel;
        string descriptionSummaryLabel;
        if (string.IsNullOrWhiteSpace(Book.Description))
        {
            descriptionLabel = descriptionSummaryLabel = UiText.Get("暂无简介");
        }
        else
        {
            descriptionLabel = Book.Description;
            var description = Regex.Replace(Book.Description, @"\s+", " ").Trim();
            descriptionSummaryLabel = description.Length <= 90 ? description : $"{description[..90]}…";
        }
        var updatedLabel = UiText.Get("更新于 {0:yyyy-MM-dd HH:mm}", Book.UpdatedAt.ToLocalTime());

        SetLabel(ref _title, title, nameof(Title));
        SetLabel(ref _authors, authors, nameof(Authors));
        SetLabel(ref _formatLabel, formatLabel, nameof(FormatLabel));
        SetLabel(ref _fileCountLabel, fileCountLabel, nameof(FileCountLabel));
        SetLabel(ref _totalSizeLabel, totalSizeLabel, nameof(TotalSizeLabel));
        SetLabel(ref _fileSummaryLabel, fileSummaryLabel, nameof(FileSummaryLabel));
        SetLabel(ref _readingStatusLabel, readingStatusLabel, nameof(ReadingStatusLabel));
        SetLabel(ref _readingStateLabel, readingStateLabel, nameof(ReadingStateLabel));
        SetLabel(ref _organizationLabel, organizationLabel, nameof(OrganizationLabel));
        SetLabel(ref _publicationLabel, publicationLabel, nameof(PublicationLabel));
        SetLabel(ref _identifierLabel, identifierLabel, nameof(IdentifierLabel));
        SetLabel(ref _descriptionSummaryLabel, descriptionSummaryLabel, nameof(DescriptionSummaryLabel));
        SetLabel(ref _updatedLabel, updatedLabel, nameof(UpdatedLabel));
        SetLabel(ref _descriptionLabel, descriptionLabel, nameof(DescriptionLabel));
        OnPropertyChanged(nameof(CoverImage));
    }

    private void SetLabel(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private static string JoinLabels(string first, string second, string empty)
    {
        if (first.Length == 0) return second.Length == 0 ? empty : second;
        return second.Length == 0 ? first : string.Concat(first, " · ", second);
    }

    private static string JoinLabels(string first, string second, string third, string fourth, string empty)
    {
        var parts = new List<string>(4);
        if (first.Length > 0) parts.Add(first);
        if (second.Length > 0) parts.Add(second);
        if (third.Length > 0) parts.Add(third);
        if (fourth.Length > 0) parts.Add(fourth);
        return parts.Count == 0 ? empty : string.Join(" · ", parts);
    }

    public void LoadCover()
    {
        if (_disposed || _coverLoadAttempted) return;
        _coverLoadAttempted = true;
        var version = ++_coverVersion;
        var cancellation = new CancellationTokenSource();
        _coverCancellation = cancellation;
        CoverLoadingTask = LoadCoverAsync(Book, version, cancellation);
    }

    private async Task LoadCoverAsync(Book book, int version, CancellationTokenSource cancellation)
    {
        Bitmap? image = null;
        try
        {
            image = await CoverThumbnailCache.LoadAsync(book, DataRoot, cancellation.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || version != _coverVersion) return;
                CoverImage = image;
                image = null;
                OnPropertyChanged(nameof(CoverImage));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (version == _coverVersion) _coverLoadAttempted = false;
        }
        finally
        {
            image?.Dispose();
            if (ReferenceEquals(_coverCancellation, cancellation)) _coverCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Releases the decoded bitmap while keeping the book metadata alive.
    /// Virtualized card roots call this when they leave the visual tree so a
    /// long scroll does not retain every cover visited by the user.
    /// </summary>
    public void UnloadCover()
    {
        ++_coverVersion;
        _coverCancellation?.Cancel();
        if (CoverImage is null && !_coverLoadAttempted) return;
        CoverImage?.Dispose();
        CoverImage = null;
        _coverLoadAttempted = false;
        OnPropertyChanged(nameof(CoverImage));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Refresh recomputes every cached label; the remaining properties are
        // not derived from Book and only need a notification.
        Refresh(unloadCover: false);
        OnPropertyChanged(nameof(PresenceLabel));
        OnPropertyChanged(nameof(SyncStatusLabel));
        OnPropertyChanged(nameof(ConversionProgressMessage));
    }

    public void Dispose()
    {
        _disposed = true;
        UiText.LanguageChanged -= OnLanguageChanged;
        UnloadCover();
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
    private static readonly SemaphoreSlim CoverGate = new(2, 2);
    private string _coverStamp = string.Empty;
    private int _coverVersion;
    private bool _disposed;
    public Task CoverLoadingTask { get; private set; } = Task.CompletedTask;

    public BookCollectionFolderViewModel(
        BookCollection collection,
        int bookCount,
        string dataRoot,
        IReadOnlyList<string?> coverPaths)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Update(collection, bookCount, dataRoot, coverPaths);
    }

    public BookCollection Collection { get; private set; } = null!;
    public void Update(BookCollection collection, int count, string root, IReadOnlyList<string?> paths)
    {
        Collection = collection;
        BookCount = count;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BookCountLabel));
        var stamp = string.Join("|", paths.Select(path =>
        {
            var file = new FileInfo(Path.Combine(root, path ?? "__missing__"));
            return file.Exists ? $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : path;
        }));
        if (stamp == _coverStamp) return;
        _coverStamp = stamp;
        var version = ++_coverVersion;
        CoverLoadingTask = LoadCoversAsync(root, paths.ToArray(), version);
    }

    private async Task LoadCoversAsync(string root, IReadOnlyList<string?> paths, int version)
    {
        await CoverGate.WaitAsync();
        Bitmap?[]? loaded = null;
        try
        {
            if (_disposed || version != _coverVersion) return;
            loaded = await Task.Run(() => Enumerable.Range(0, 3)
                .Select(index => LoadCoverImage(root, index < paths.Count ? paths[index] : null, 96)).ToArray());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || version != _coverVersion) return;
                for (var i = 0; i < 3; i++)
                {
                    _covers[i]?.Dispose();
                    _covers[i] = loaded[i];
                    loaded[i] = null;
                    OnPropertyChanged($"Cover{i + 1}");
                }
            });
        }
        finally
        {
            if (loaded is not null) foreach (var image in loaded) image?.Dispose();
            CoverGate.Release();
        }
    }

    public string Name => string.Equals(
        Collection.Name,
        BookLibraryDefaults.UncollectedCollectionName,
        StringComparison.Ordinal)
        ? UiText.Get("未收藏")
        : UiText.Localize(Collection.Name);
    public int BookCount { get; private set; }
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
        _disposed = true;
        ++_coverVersion;
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
    private int _bookCount;
    private int _filteredBookCount;
    private int _viewRequestId;
    private CancellationTokenSource? _viewCancellation;
    private bool _isBusy;
    private string _statusText = UiText.Get("准备就绪");

    public LibraryViewModel(IBookLibraryService library, string dataRoot)
    {
        _library = library;
        _dataRoot = dataRoot;
        // MainWindow owns the language-change refresh (cards still subscribe
        // individually for their cached labels); refreshing here as well ran
        // two identical database queries and cancelled one of them.
    }

    public BulkObservableCollection<BookCardViewModel> Books { get; } = [];
    public IReadOnlyList<Book> LibraryBooks { get; private set; } = [];
    public IReadOnlyList<string> AvailableAuthors { get; private set; } = [];
    public IReadOnlyList<string> AvailableTags { get; private set; } = [];
    public IReadOnlyList<string> AvailableFormats { get; private set; } = [];
    public IReadOnlyList<string> AvailableCategories { get; private set; } = [];

    public int PageIndex => _pageIndex;
    public int PageCount => _pageCount;
    public int BookCount => _bookCount;
    public int FilteredBookCount => _filteredBookCount;
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

    public event EventHandler? ViewChanged;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            // Microsoft.Data.Sqlite executes its "async" commands synchronously,
            // so run the whole query on the thread pool instead of letting it
            // block the UI thread between awaits.
            _bookCount = await Task.Run(
                () => _library.GetBookCountAsync(cancellationToken),
                cancellationToken);
            OnPropertyChanged(nameof(BookCount));

            var options = await Task.Run(
                () => _library.GetFilterOptionsAsync(cancellationToken),
                cancellationToken);
            AvailableAuthors = options.Authors;
            AvailableTags = options.Tags;
            AvailableFormats = options.Formats;
            AvailableCategories = options.Categories;

            OnPropertyChanged(nameof(AvailableAuthors));
            OnPropertyChanged(nameof(AvailableTags));
            OnPropertyChanged(nameof(AvailableFormats));
            OnPropertyChanged(nameof(AvailableCategories));
            await RefreshViewAsync(resetPage: true, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshView() => _ = RefreshViewSafelyAsync(resetPage: true);

    public void GoToPreviousPage()
    {
        if (!CanGoToPreviousPage) return;
        _pageIndex--;
        _ = RefreshViewSafelyAsync(resetPage: false);
    }

    public void GoToNextPage()
    {
        if (!CanGoToNextPage) return;
        _pageIndex++;
        _ = RefreshViewSafelyAsync(resetPage: false);
    }

    public void Dispose()
    {
        _viewCancellation?.Cancel();
        _viewCancellation?.Dispose();
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
                ? result.SkippedCount == 0
                    ? UiText.Get("已新增 {0} 个文件", result.AddedCount)
                    : UiText.Get("已新增 {0} 个文件，跳过 {1} 个重复或已有文件", result.AddedCount, result.SkippedCount)
                : result.SkippedCount == 0
                    ? UiText.Get("已新增 {0} 个文件，{1} 个失败", result.AddedCount, result.FailureCount)
                    : UiText.Get("已新增 {0} 个文件，跳过 {1} 个重复或已有文件，{2} 个失败", result.AddedCount, result.SkippedCount, result.FailureCount);
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

    public async Task RefreshViewAsync(
        bool resetPage = true,
        CancellationToken cancellationToken = default)
    {
        if (resetPage) _pageIndex = 0;
        var requestId = Interlocked.Increment(ref _viewRequestId);
        _viewCancellation?.Cancel();
        _viewCancellation?.Dispose();
        _viewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestToken = _viewCancellation.Token;
        IsBusy = true;
        try
        {
            var page = await Task.Run(
                () => _library.SearchPageAsync(
                    SearchText,
                    AuthorFilter,
                    TagFilter,
                    FormatFilter,
                    CategoryFilter,
                    CollectionFilterId,
                    ReadingStatusFilter,
                    FavoritesOnly,
                    SortMode,
                    _pageIndex,
                    PageSize,
                    requestToken),
                requestToken);
            requestToken.ThrowIfCancellationRequested();
            if (requestId != _viewRequestId) return;

            var lastPageIndex = Math.Max(0, (int)Math.Ceiling(page.TotalCount / (double)PageSize) - 1);
            if (_pageIndex > lastPageIndex)
            {
                _pageIndex = lastPageIndex;
                page = await Task.Run(
                    () => _library.SearchPageAsync(
                        SearchText,
                        AuthorFilter,
                        TagFilter,
                        FormatFilter,
                        CategoryFilter,
                        CollectionFilterId,
                        ReadingStatusFilter,
                        FavoritesOnly,
                        SortMode,
                        _pageIndex,
                        PageSize,
                        requestToken),
                    requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (requestId != _viewRequestId) return;
            }

            LibraryBooks = page.Books;
            _filteredBookCount = page.TotalCount;
            var pageCount = Math.Max(1, (int)Math.Ceiling(page.TotalCount / (double)PageSize));
            if (_pageIndex >= pageCount) _pageIndex = pageCount - 1;
            if (_pageCount != pageCount)
            {
                _pageCount = pageCount;
                OnPropertyChanged(nameof(PageCount));
            }
            OnPropertyChanged(nameof(LibraryBooks));
            OnPropertyChanged(nameof(FilteredBookCount));
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(PageStatusText));

            var incomingIds = page.Books.Select(book => book.Id).ToHashSet();
            foreach (var id in _bookCards.Keys.Where(id => !incomingIds.Contains(id)).ToArray())
            {
                var old = _bookCards[id];
                old.Dispose();
                _bookCards.Remove(id);
            }

            var orderedCards = new List<BookCardViewModel>(page.Books.Count);
            foreach (var book in page.Books)
            {
                if (!_bookCards.TryGetValue(book.Id, out var card))
                {
                    card = new BookCardViewModel(book, _dataRoot);
                    _bookCards.Add(book.Id, card);
                }
                else
                {
                    card.UpdateBook(book);
                }
                orderedCards.Add(card);
            }
            Books.ReplaceAll(orderedCards);

            var baseStatus = page.TotalCount == 0
                ? _bookCount == 0 ? UiText.Get("书库还是空的")
                    : CollectionFilterId is not null ? UiText.Get("“{0}”收藏夹还是空的", UiText.Localize(CollectionFilterName ?? string.Empty))
                    : UiText.Get("没有符合条件的书籍")
                : HasActiveFilters || !string.IsNullOrWhiteSpace(SearchText)
                    ? CollectionFilterId is not null
                        ? UiText.Get("{0} · {1} 本书", UiText.Localize(CollectionFilterName ?? string.Empty), page.TotalCount)
                        : UiText.Get("找到 {0} 本书", page.TotalCount)
                    : UiText.Get("共 {0} 本书", page.TotalCount);
            StatusText = _pageCount <= 1
                ? baseStatus
                : $"{baseStatus} · {PageStatusText}";

            OnPropertyChanged(nameof(HasActiveFilters));
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (requestId == _viewRequestId) IsBusy = false;
        }
    }

    private async Task RefreshViewSafelyAsync(bool resetPage)
    {
        try
        {
            await RefreshViewAsync(resetPage);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = UiText.Get("刷新书库失败：{0}", UiText.Localize(exception.Message));
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetFilter<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        OnPropertyChanged(nameof(HasActiveFilters));
    }
}
