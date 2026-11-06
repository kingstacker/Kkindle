namespace Kkindle.Core;

public interface IBookLibraryService
{
    event EventHandler<LocalDataChangedEventArgs>? DataChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Book>> SearchAsync(string? query = null, CancellationToken cancellationToken = default);
    Task<Book?> GetBookAsync(Guid bookId, CancellationToken cancellationToken = default);
    Task<int> GetBookCountAsync(CancellationToken cancellationToken = default);
    Task<LibraryFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default);
    Task<LibraryPageResult> SearchPageAsync(
        string? query = null,
        string? author = null,
        string? tag = null,
        string? format = null,
        string? category = null,
        Guid? collectionId = null,
        LibraryReadingStatus? readingStatus = null,
        bool? favorite = null,
        LibrarySortMode sortMode = LibrarySortMode.UpdatedDescending,
        int pageIndex = 0,
        int pageSize = 200,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryCollectionSummary>> GetCollectionSummariesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryBookMatch>> GetLibraryMatchRecordsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, string>> GetBookTitlesAsync(
        IReadOnlyCollection<Guid> bookIds,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, LibraryBookDisplayInfo>> GetBookDisplayInfosAsync(
        IReadOnlyCollection<Guid> bookIds,
        CancellationToken cancellationToken = default);
    Task<ImportBatchResult> ImportAsync(
        IEnumerable<string> paths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ImportBookConflict, Task<ImportConflictResolution>>? conflictResolver = null,
        IReadOnlyDictionary<string, TxtImportRules>? txtImportRules = null);
    Task<BookFile> AddFileToBookAsync(Guid bookId, string sourcePath, CancellationToken cancellationToken = default);
    Task RenameBookAsync(Guid bookId, string title, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(Book book, CancellationToken cancellationToken = default);
    Task<string?> EnsurePdfCoverAsync(Guid bookId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task DeleteFileAsync(Guid bookId, Guid bookFileId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default);
    Task RemoveFromLibraryKeepFilesAsync(Guid bookId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryTrashItem>> GetTrashItemsAsync(CancellationToken cancellationToken = default);
    Task RestoreTrashItemAsync(Guid trashItemId, CancellationToken cancellationToken = default);
    Task PurgeTrashItemAsync(Guid trashItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BookCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default);
    Task<BookCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);
    Task RenameCollectionAsync(Guid collectionId, string name, CancellationToken cancellationToken = default);
    Task ClearCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task DissolveCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task MergeCollectionsAsync(
        Guid sourceCollectionId,
        Guid targetCollectionId,
        CancellationToken cancellationToken = default);
    Task AddBookToCollectionAsync(Guid bookId, Guid collectionId, CancellationToken cancellationToken = default);
    Task RemoveBookFromCollectionAsync(Guid bookId, Guid collectionId, CancellationToken cancellationToken = default);
    string GetAbsoluteFilePath(BookFile file);
}

public interface IBookFormatConverter
{
    Task ConvertAsync(
        string sourcePath,
        string destinationPath,
        IProgress<FormatConversionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        FormatConversionMetadata? metadata = null);
}

public interface IPinyinBookService
{
    Task<PinyinBookResumeInfo?> FindResumeAsync(
        string sourcePath,
        string destinationPath,
        PinyinBookOptions options,
        CancellationToken cancellationToken = default);

    Task<PinyinBookResult> GenerateAsync(
        string sourcePath,
        string destinationPath,
        PinyinBookOptions? options = null,
        IProgress<PinyinBookProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PinyinBookResumeMode resumeMode = PinyinBookResumeMode.Restart);
}

public interface IEpubTranslationService : IDisposable
{
    Task<BookTranslationResumeInfo?> FindResumeAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        CancellationToken cancellationToken = default);

    Task<BookTranslationResult> TranslateAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        BookTranslationResumeMode resumeMode = BookTranslationResumeMode.Restart);
}

public interface IMetadataService
{
    Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default);
}

public interface IKindleDeviceService
{
    Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KindleBook>>([]);
    Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task SendBookAsync(KindleDevice device, BookFile bookFile, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, string? coverOverridePath = null);
    Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default);
    Task<string> ExportBookAsync(KindleDevice device, KindleBook book, string destinationDirectory, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(KindleDevice device, KindleResourceKind kind, CancellationToken cancellationToken = default);
    Task SendResourceAsync(KindleDevice device, KindleResourceKind kind, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task ExportResourceAsync(KindleDevice device, KindleDeviceResource resource, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveResourceAsync(KindleDevice device, KindleDeviceResource resource, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue);
    Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default);
    Task DeleteClippingsAsync(KindleDevice device, IReadOnlyCollection<string> clippingIds, CancellationToken cancellationToken = default);
    Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default) => Task.FromResult<KindleAnnotationSnapshot?>(null);
    Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(UiText.Get("当前设备暂不支持回写 Kindle 批注。"));
    Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device, KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations, CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress) =>
        ApplyAnnotationMutationsAsync(device, snapshot, mutations, cancellationToken);
    Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default);
}

public static class BookLibraryDefaults
{
    // System view for books that have no user-created collection membership.
    // Created automatically and maintained by the library service.
    public const string UncollectedCollectionName = "未收藏";
}
