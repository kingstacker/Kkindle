using System.ComponentModel;
using Kkindle.Core;
using Kkindle.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kkindle.McpServer;

/// <summary>
/// MCP tools backed by Kkindle's existing SQLite library, reader data,
/// conversion, email, and mounted-device services.
/// </summary>
[McpServerToolType]
public sealed class KkindleMcpTools
{
    private const int DefaultLibraryLimit = 100;
    private const int DefaultSearchLimit = 50;
    private const int DefaultRecentLimit = 20;
    private const int MaximumResultLimit = 200;
    private const int MaximumQueryLength = 200;
    private const int MaximumDeviceIdLength = 200;
    private const int MaximumPathLength = 4_096;

    private readonly IBookLibraryService _library;
    private readonly ReaderDataService _readerData;
    private readonly IKindleDeviceService _devices;
    private readonly IKindleDeviceService? _ejectDevices;
    private readonly IBookFormatConverter? _formatConverter;
    private readonly KindleEmailSettingsStore? _emailSettingsStore;
    private readonly KindleEmailSender? _emailSender;

    public KkindleMcpTools(
        IBookLibraryService library,
        ReaderDataService readerData,
        IKindleDeviceService devices)
        : this(library, readerData, devices, null, null, null, null)
    {
    }

    public KkindleMcpTools(
        IBookLibraryService library,
        ReaderDataService readerData,
        IKindleDeviceService devices,
        IBookFormatConverter? formatConverter,
        KindleEmailSettingsStore? emailSettingsStore,
        KindleEmailSender? emailSender,
        IKindleDeviceService? ejectDevices = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _readerData = readerData ?? throw new ArgumentNullException(nameof(readerData));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _ejectDevices = ejectDevices;
        _formatConverter = formatConverter;
        _emailSettingsStore = emailSettingsStore;
        _emailSender = emailSender;
    }

    /// <summary>
    /// Lists books in the local Kkindle library, including author, formats,
    /// library reading status, and the latest persisted reading progress.
    /// </summary>
    /// <param name="limit">Maximum number of books to return, from 1 to 200.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>The first page of the library and its total book count.</returns>
    [McpServerTool(
        Name = "list_library",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List books in the local Kkindle library with title, author, reading progress, and format.")]
    public Task<BookListResult> ListLibraryAsync(
        [Description("Maximum number of books to return (1-200). Defaults to 100.")] int limit = DefaultLibraryLimit,
        CancellationToken cancellationToken = default) =>
        ListBooksAsync(query: null, ValidateLimit(limit, nameof(limit)), cancellationToken);

    /// <summary>
    /// Searches the local library by the existing library service's title,
    /// author, tag, and series matching rules.
    /// </summary>
    /// <param name="query">Non-empty keyword or phrase, up to 200 characters.</param>
    /// <param name="limit">Maximum number of matching books to return, from 1 to 200.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Matching books and the total number of matches.</returns>
    [McpServerTool(
        Name = "search_books",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Search Kkindle's local library by book title, author, tag, or series.")]
    public Task<BookListResult> SearchBooksAsync(
        [Description("Search keyword or phrase; must not be empty and must be at most 200 characters.")] string query,
        [Description("Maximum number of matching books to return (1-200). Defaults to 50.")] int limit = DefaultSearchLimit,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = ValidateQuery(query);
        return ListBooksAsync(
            normalizedQuery,
            ValidateLimit(limit, nameof(limit)),
            cancellationToken);
    }

    /// <summary>
    /// Gets full metadata and all stored file records for one local library
    /// book, together with its latest persisted reading progress.
    /// </summary>
    /// <param name="bookId">The book's GUID from the library.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Detailed metadata for the requested book.</returns>
    [McpServerTool(
        Name = "get_book_metadata",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get detailed metadata, formats, files, and reading progress for one Kkindle library book.")]
    public async Task<BookMetadataResult> GetBookMetadataAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new McpException($"Book '{id}' was not found in the Kkindle library.");
        var progress = await ReadProgressAsync(book, cancellationToken);
        return ToBookMetadata(book, progress);
    }

    /// <summary>
    /// Detects currently connected reader devices using Kkindle's existing
    /// mounted-storage device service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Connected device summaries and their count.</returns>
    [McpServerTool(
        Name = "list_devices",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List currently connected Kindle or other supported reader devices.")]
    public async Task<DeviceListResult> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        var summaries = devices.Select(ToDeviceInfo).ToArray();
        return new DeviceListResult(summaries.Length, summaries);
    }

    /// <summary>
    /// Returns the current connection and storage status for all detected
    /// devices, or for one device when an identity, root path, or name is
    /// supplied.
    /// </summary>
    /// <param name="deviceId">Optional device identity, root path, or exact name.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Current connected state and device status records.</returns>
    [McpServerTool(
        Name = "device_status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get current connection and storage status for connected reader devices.")]
    public async Task<DeviceStatusResult> DeviceStatusAsync(
        [Description("Optional device identity, root path, or exact device name; omit for all devices.")] string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = ValidateOptionalDeviceId(deviceId);
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        if (filter is not null)
        {
            devices = devices
                .Where(device => DeviceMatches(device, filter))
                .ToArray();
            if (devices.Count == 0)
                throw new McpException($"No connected device matched '{filter}'.");
        }

        var statuses = devices.Select(ToDeviceInfo).ToArray();
        return new DeviceStatusResult(
            statuses.Length > 0,
            statuses.Length,
            filter,
            statuses);
    }

    /// <summary>
    /// Returns the persisted reader position and reading statistics for every
    /// format belonging to one local book.
    /// </summary>
    [McpServerTool(
        Name = "get_reading_progress",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get reading percentage, chapter/scroll position, reading statistics, and update time for a book.")]
    public async Task<ReadingProgressResult> GetReadingProgressAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var files = new List<ReadingProgressFile>(book.Files.Count);

        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = await _readerData.GetProgressAsync(file.Id, cancellationToken);
            var stats = await _readerData.GetReadingStatsAsync(file.Id, cancellationToken);
            var progressIsLatest = progress is not null
                && (stats is null || progress.UpdatedAt >= stats.UpdatedAt);
            var updatedAt = progress is null
                ? stats?.UpdatedAt
                : stats is null
                    ? progress.UpdatedAt
                    : progress.UpdatedAt >= stats.UpdatedAt ? progress.UpdatedAt : stats.UpdatedAt;
            var source = progress is null
                ? stats is null ? null : "ReaderReadingStats"
                : stats is null
                    ? "ReaderProgress"
                    : progressIsLatest ? "ReaderProgress" : "ReaderReadingStats";

            files.Add(new ReadingProgressFile(
                file.Id,
                file.Format,
                progress is null && stats is null
                    ? null
                    : ClampProgress(progressIsLatest ? progress!.ProgressPercent : stats!.ProgressPercent),
                updatedAt,
                progress?.ChapterPath,
                progress?.Fragment,
                progress?.ChapterIndex,
                progress?.ScrollPosition,
                progress?.FlowMode,
                progress?.ContentPosition,
                stats?.CumulativeSeconds,
                stats?.CompletedChapters,
                stats?.TotalChapters,
                source));
        }

        var ordered = files
            .OrderByDescending(file => file.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(file => file.BookFileId)
            .ToArray();
        return new ReadingProgressResult(
            book.Id,
            book.Title,
            ordered.FirstOrDefault(),
            ordered);
    }

    /// <summary>
    /// Returns the reader service's recent-book dashboard entries, including
    /// cumulative reading time and the latest progress snapshot.
    /// </summary>
    [McpServerTool(
        Name = "list_recent",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List recently read books with progress, cumulative reading time, and update time.")]
    public async Task<RecentBooksResult> ListRecentAsync(
        [Description("Maximum number of recent books to return (1-100). Defaults to 20.")] int limit = DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        var dashboard = await _readerData.GetReadingDashboardAsync(
            ValidateRecentLimit(limit),
            cancellationToken);
        var recent = new List<RecentBookInfo>(dashboard.RecentBooks.Count);
        foreach (var item in dashboard.RecentBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var book = item.IsInLibrary
                ? await _library.GetBookAsync(item.BookId, cancellationToken)
                : null;
            recent.Add(new RecentBookInfo(
                item.BookId,
                item.BookFileId,
                string.IsNullOrWhiteSpace(item.Title) ? book?.Title ?? string.Empty : item.Title,
                book?.Authors,
                item.ProgressPercent,
                item.CumulativeSeconds,
                item.UpdatedAt,
                item.IsInLibrary,
                book?.Files
                    .Select(file => file.Format.Trim().ToUpperInvariant())
                    .Where(format => format.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? []));
        }

        return new RecentBooksResult(
            new ReadingDashboardSummary(
                dashboard.BooksStarted,
                dashboard.BooksFinished,
                dashboard.TotalSeconds,
                ClampProgress(dashboard.AverageProgress),
                dashboard.BookmarkCount,
                dashboard.AnnotationCount),
            recent);
    }

    /// <summary>Lists the normalized tag values already exposed by the library filters.</summary>
    [McpServerTool(
        Name = "list_tags",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List all tags currently used by books in the local Kkindle library.")]
    public async Task<TagListResult> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        var filters = await _library.GetFilterOptionsAsync(cancellationToken);
        return new TagListResult(filters.Tags);
    }

    /// <summary>Lists the library's real collection records and book counts.</summary>
    [McpServerTool(
        Name = "list_collections",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List local Kkindle collections with their IDs, creation times, and book counts.")]
    public async Task<CollectionListResult> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await _library.GetCollectionSummariesAsync(cancellationToken);
        return new CollectionListResult(
            summaries.Select(summary => new CollectionInfo(
                summary.Collection.Id,
                summary.Collection.Name,
                summary.Collection.CreatedAt,
                summary.BookCount)).ToArray());
    }

    /// <summary>Creates a new library collection (收藏夹).</summary>
    [McpServerTool(
        Name = "create_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Create a new Kkindle library collection (收藏夹).")]
    public async Task<CollectionOperationResult> CreateCollectionAsync(
        [Description("Name of the collection to create; must not be blank and at most 200 characters.")] string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateCollectionName(name);
        var collection = await RunLibraryMutationAsync(
            ct => _library.CreateCollectionAsync(normalizedName, ct),
            "Create collection",
            cancellationToken);
        return new CollectionOperationResult(
            collection.Id,
            collection.Name,
            true,
            [$"Created collection '{collection.Name}'."]);
    }

    /// <summary>Renames an existing library collection (收藏夹).</summary>
    [McpServerTool(
        Name = "rename_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Rename a Kkindle library collection (收藏夹).")]
    public async Task<CollectionOperationResult> RenameCollectionAsync(
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        [Description("New collection name; must not be blank and at most 200 characters.")] string name,
        CancellationToken cancellationToken = default)
    {
        var (_, currentName) = await RequireCollectionAsync(collectionId, cancellationToken);
        var normalizedName = ValidateCollectionName(name);
        await RunLibraryMutationAsync(
            ct => _library.RenameCollectionAsync(collectionId, normalizedName, ct),
            "Rename collection",
            cancellationToken);
        return new CollectionOperationResult(
            collectionId,
            normalizedName,
            true,
            [$"Renamed collection '{currentName}' to '{normalizedName}'."]);
    }

    /// <summary>Deletes a library collection (收藏夹). The books inside are not deleted.</summary>
    [McpServerTool(
        Name = "delete_collection",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Delete a Kkindle library collection (收藏夹); the books inside are kept and become uncollected.")]
    public async Task<CollectionOperationResult> DeleteCollectionAsync(
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var (_, name) = await RequireCollectionAsync(collectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.DeleteCollectionAsync(collectionId, ct),
            "Delete collection",
            cancellationToken);
        return new CollectionOperationResult(
            collectionId,
            name,
            true,
            [$"Deleted collection '{name}'."]);
    }

    /// <summary>Empties a library collection without deleting the books themselves.</summary>
    [McpServerTool(
        Name = "clear_collection",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Remove every book from a Kkindle collection; the collection itself and the books are kept.")]
    public async Task<CollectionOperationResult> ClearCollectionAsync(
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var (_, name) = await RequireCollectionAsync(collectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.ClearCollectionAsync(collectionId, ct),
            "Clear collection",
            cancellationToken);
        return new CollectionOperationResult(
            collectionId,
            name,
            true,
            [$"Cleared collection '{name}'."]);
    }

    /// <summary>Moves every book of the source collection into the target one, then deletes the source.</summary>
    [McpServerTool(
        Name = "merge_collections",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Merge source collection into target collection (books move to the target) and delete the source.")]
    public async Task<CollectionOperationResult> MergeCollectionsAsync(
        [Description("Source collection GUID whose books are moved into the target; it is deleted afterwards.")] Guid sourceCollectionId,
        [Description("Target collection GUID that receives the books.")] Guid targetCollectionId,
        CancellationToken cancellationToken = default)
    {
        if (sourceCollectionId == targetCollectionId)
            throw new McpException("sourceCollectionId and targetCollectionId must be different.");
        var (_, sourceName) = await RequireCollectionAsync(sourceCollectionId, cancellationToken);
        var (_, targetName) = await RequireCollectionAsync(targetCollectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.MergeCollectionsAsync(sourceCollectionId, targetCollectionId, ct),
            "Merge collections",
            cancellationToken);
        return new CollectionOperationResult(
            targetCollectionId,
            targetName,
            true,
            [$"Merged collection '{sourceName}' into '{targetName}'."]);
    }

    /// <summary>Adds one library book to a collection (收藏夹).</summary>
    [McpServerTool(
        Name = "add_book_to_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Add one library book to a Kkindle collection (收藏夹).")]
    public async Task<CollectionBookOperationResult> AddBookToCollectionAsync(
        [Description("Book GUID returned by list_library or search_books.")] Guid bookId,
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var book = await GetBookOrThrowAsync(bookId, cancellationToken);
        var (_, collectionName) = await RequireCollectionAsync(collectionId, cancellationToken);
        if (collectionName.Equals(BookLibraryDefaults.UncollectedCollectionName, StringComparison.OrdinalIgnoreCase))
            throw new McpException("‘未收藏’是系统视图，不能直接添加。请移除这本书的所有自定义收藏夹成员关系。");
        await RunLibraryMutationAsync(
            ct => _library.AddBookToCollectionAsync(bookId, collectionId, ct),
            "Add book to collection",
            cancellationToken);
        return new CollectionBookOperationResult(
            bookId,
            collectionId,
            collectionName,
            true,
            [$"Added '{book.Title}' to collection '{collectionName}'."]);
    }

    /// <summary>Removes one library book from a collection (收藏夹).</summary>
    [McpServerTool(
        Name = "remove_book_from_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Remove one library book from a Kkindle collection (收藏夹); the book itself is kept.")]
    public async Task<CollectionBookOperationResult> RemoveBookFromCollectionAsync(
        [Description("Book GUID returned by list_library or search_books.")] Guid bookId,
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var book = await GetBookOrThrowAsync(bookId, cancellationToken);
        var (_, collectionName) = await RequireCollectionAsync(collectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.RemoveBookFromCollectionAsync(bookId, collectionId, ct),
            "Remove book from collection",
            cancellationToken);
        return new CollectionBookOperationResult(
            bookId,
            collectionId,
            collectionName,
            true,
            [$"Removed '{book.Title}' from collection '{collectionName}'."]);
    }

    /// <summary>Searches the indexed text content of one book and returns matching passages.</summary>
    [McpServerTool(
        Name = "search_book_content",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Search inside one book's indexed text and return matching passages with chapter context; empty results usually mean the reader index for that book has not been built yet.")]
    public async Task<BookContentSearchResult> SearchBookContentAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        [Description("Search keyword or phrase, up to 200 characters.")] string query,
        [Description("Maximum number of matching passages to return (1-100). Defaults to 6.")] int limit = 6,
        [Description("When true, only whole-phrase matches are returned.")] bool exactPhraseOnly = false,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var normalizedQuery = ValidateQuery(query);
        var clampedLimit = ValidateSearchContentLimit(limit);
        var chunks = await _readerData.SearchBookAsync(
            id,
            normalizedQuery,
            clampedLimit,
            cancellationToken,
            exactPhraseOnly);
        var formatsByFile = book.Files
            .GroupBy(file => file.Id)
            .ToDictionary(group => group.Key, group => group.First().Format);
        var matches = chunks
            .Select(chunk => new BookContentMatch(
                chunk.BookFileId,
                formatsByFile.TryGetValue(chunk.BookFileId, out var format) ? format : string.Empty,
                chunk.ChapterIndex,
                chunk.ChapterTitle,
                chunk.ChapterPath,
                chunk.StartOffset,
                chunk.EndOffset,
                chunk.Content,
                chunk.Rank))
            .ToArray();
        return new BookContentSearchResult(id, book.Title, normalizedQuery, matches.Length, matches);
    }

    /// <summary>Lists every highlight/annotation (划线/批注) saved for one book.</summary>
    [McpServerTool(
        Name = "list_book_annotations",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List all highlights and annotations (划线/批注) for one book across its formats, newest first.")]
    public async Task<BookAnnotationsResult> ListBookAnnotationsAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        [Description("Maximum annotations per book format to return (1-1000). Defaults to 200.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var clampedLimit = ValidateAnnotationLimit(limit);
        var annotations = new List<BookAnnotationInfo>();
        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await _readerData.GetAnnotationsAsync(file.Id, cancellationToken, clampedLimit);
            foreach (var annotation in items)
            {
                annotations.Add(new BookAnnotationInfo(
                    annotation.Id,
                    annotation.BookFileId,
                    file.Format,
                    annotation.ChapterPath,
                    annotation.Fragment,
                    annotation.StartOffset,
                    annotation.EndOffset,
                    annotation.SelectedText,
                    annotation.Prefix,
                    annotation.Suffix,
                    annotation.Color,
                    annotation.UnderlineStyle,
                    annotation.Note,
                    annotation.CreatedAt,
                    annotation.UpdatedAt));
            }
        }
        var ordered = annotations
            .OrderByDescending(annotation => annotation.UpdatedAt)
            .ThenBy(annotation => annotation.Id)
            .ToArray();
        return new BookAnnotationsResult(id, book.Title, ordered.Length, ordered);
    }

    /// <summary>Lists every bookmark (书签) saved for one book.</summary>
    [McpServerTool(
        Name = "list_bookmarks",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List all bookmarks (书签) for one book across its formats.")]
    public async Task<BookBookmarksResult> ListBookmarksAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        [Description("Maximum bookmarks per book format to return (1-1000). Defaults to 200.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var clampedLimit = ValidateAnnotationLimit(limit);
        var bookmarks = new List<BookBookmarkInfo>();
        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await _readerData.GetBookmarksAsync(file.Id, cancellationToken, clampedLimit);
            foreach (var bookmark in items)
            {
                bookmarks.Add(new BookBookmarkInfo(
                    bookmark.Id,
                    bookmark.BookFileId,
                    file.Format,
                    bookmark.ChapterPath,
                    bookmark.Fragment,
                    bookmark.ChapterIndex,
                    bookmark.ScrollPosition,
                    bookmark.FlowMode,
                    bookmark.Title,
                    bookmark.Quote,
                    bookmark.CreatedAt,
                    bookmark.ContentPosition));
            }
        }
        var ordered = bookmarks
            .OrderBy(bookmark => bookmark.ChapterIndex)
            .ThenBy(bookmark => bookmark.CreatedAt)
            .ToArray();
        return new BookBookmarksResult(id, book.Title, ordered.Length, ordered);
    }

    /// <summary>Returns aggregate reading statistics and recent reading activity.</summary>
    [McpServerTool(
        Name = "get_reading_dashboard",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get reading statistics (books started/finished, total reading time, average progress, bookmark and annotation counts) and recent books plus daily reading time.")]
    public async Task<ReadingDashboardResult> GetReadingDashboardAsync(
        [Description("Maximum number of recent books to include (1-50). Defaults to 12.")] int recentLimit = 12,
        CancellationToken cancellationToken = default)
    {
        var limit = recentLimit is < 1 or > 50
            ? throw new McpException($"recentLimit must be between 1 and 50; received {recentLimit}.")
            : recentLimit;
        var dashboard = await _readerData.GetReadingDashboardAsync(limit, cancellationToken);
        var recent = new List<DashboardRecentBookInfo>(dashboard.RecentBooks.Count);
        foreach (var item in dashboard.RecentBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? authors = null;
            if (item.IsInLibrary)
            {
                var book = await _library.GetBookAsync(item.BookId, cancellationToken);
                authors = book?.Authors;
            }
            recent.Add(new DashboardRecentBookInfo(
                item.BookId,
                item.BookFileId,
                string.IsNullOrWhiteSpace(item.Title) ? authors ?? string.Empty : item.Title,
                ClampProgress(item.ProgressPercent),
                item.CumulativeSeconds,
                item.UpdatedAt,
                item.IsInLibrary,
                authors));
        }
        return new ReadingDashboardResult(
            dashboard.BooksStarted,
            dashboard.BooksFinished,
            dashboard.TotalSeconds,
            ClampProgress(dashboard.AverageProgress),
            dashboard.BookmarkCount,
            dashboard.AnnotationCount,
            recent,
            dashboard.DailyReading
                .Select(day => new DashboardDayInfo(day.Date, day.ActiveSeconds))
                .ToArray());
    }
    /// <summary>Returns absolute paths for all stored files of one book.</summary>
    [McpServerTool(
        Name = "get_book_file",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get absolute local file paths and formats for every stored file of one library book; file contents are not returned.")]
    public async Task<BookFilePathResult> GetBookFileAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        return new BookFilePathResult(
            book.Id,
            book.Title,
            book.Files.Select(file => new BookFilePathInfo(
                file.Id,
                file.Format,
                Path.GetFullPath(_library.GetAbsoluteFilePath(file)),
                file.Size,
                File.Exists(_library.GetAbsoluteFilePath(file)))).ToArray());
    }

    /// <summary>
    /// Lists books currently present on one or all detected mounted reader
    /// devices. The scan is read-only but may read device metadata.
    /// </summary>
    [McpServerTool(
        Name = "list_device_library",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List books on connected Kindle or supported mounted reader devices; optionally filter by device ID, root path, or name.")]
    public async Task<DeviceLibraryResult> ListDeviceLibraryAsync(
        [Description("Optional device identity, root path, or exact device name; omit for all devices.")] string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = ValidateOptionalDeviceId(deviceId);
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        if (filter is not null)
        {
            devices = devices.Where(device => DeviceMatches(device, filter)).ToArray();
            if (devices.Count == 0)
                throw new McpException($"No connected device matched '{filter}'.");
        }

        var result = new List<DeviceLibraryInfo>(devices.Count);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var books = await _devices.ScanBooksAsync(device, cancellationToken);
            result.Add(new DeviceLibraryInfo(
                device.Identity,
                device.Name,
                device.RootPath,
                books.Select(book => new DeviceBookInfo(
                    book.RelativePath,
                    book.Title,
                    book.Authors,
                    book.Format,
                    book.Size,
                    book.Sha256,
                    book.ModifiedAt,
                    book.IsManagedByKkindle)).ToArray()));
        }

        return new DeviceLibraryResult(result);
    }

    /// <summary>
    /// Safely ejects a connected mounted device. dryRun defaults to true so a
    /// client must explicitly request the physical operation.
    /// </summary>
    [McpServerTool(
        Name = "eject_device",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Safely eject a connected reader device; this has a real device-side effect when dryRun is false, and dryRun defaults to true.")]
    public async Task<EjectDeviceResult> EjectDeviceAsync(
        [Description("Optional device identity, root path, or exact device name; required when more than one device is connected.")] string? deviceId = null,
        [Description("When true, only report the eject action. Defaults to true.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var device = await ResolveSingleDeviceAsync(deviceId, cancellationToken);
        var actions = new[]
        {
            $"Flush and safely eject '{device.Name}' ({device.RootPath})."
        };
        if (dryRun)
            return new EjectDeviceResult(device.Identity, device.Name, dryRun, false, actions);

        try
        {
            await (_ejectDevices ?? _devices).EjectAsync(device, cancellationToken);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new McpException($"The configured device service has no safe-eject implementation: {exception.Message}");
        }

        return new EjectDeviceResult(device.Identity, device.Name, false, true, actions);
    }

    /// <summary>
    /// Sends one local book file through USB or the configured Send to Kindle
    /// email account. dryRun is mandatory in the schema and defaults to true.
    /// The existing web workflow is UI-bound and therefore only supports a
    /// dry-run from this standalone server.
    /// </summary>
    [McpServerTool(
        Name = "send_to_kindle",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Send a book to Kindle over usb, email, or web. This can have a real external/device effect; dryRun is required and defaults to true. web actual execution is unavailable because the existing KindleWebSendWorkflow requires the desktop UI page.")]
    public async Task<SendToKindleResult> SendToKindleAsync(
        [Description("Book GUID returned by the library; provide this or filePath, but not both.")] string? bookId = null,
        [Description("Absolute or relative local file path; provide this or bookId, but not both.")] string? filePath = null,
        [Description("Transfer channel: usb, email, or web. Defaults to usb.")] string channel = "usb",
        [Description("When true, only return the planned action list. Defaults to true; false performs the send for usb/email.")] bool dryRun = true,
        [Description("Optional connected-device identity, root path, or exact name for the usb channel.")] string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var source = await ResolveSendSourceAsync(bookId, filePath, normalizedChannel, cancellationToken);
        var actions = new List<string>();
        string? deviceIdentity = null;
        string? deviceName = null;
        string? recipient = null;
        string? sender = null;

        switch (normalizedChannel)
        {
            case "usb":
            {
                var device = await ResolveSingleDeviceAsync(deviceId, cancellationToken);
                deviceIdentity = device.Identity;
                deviceName = device.Name;
                actions.Add($"Send '{source.Path}' as {source.Format.ToUpperInvariant()} to USB device '{device.Name}'.");
                if (!dryRun)
                {
                    if (DeviceTransferPolicy.RequiresKindleConversion(device.Profile, source.BookFile))
                    {
                        var temporaryDirectory = Path.Combine(
                            Path.GetTempPath(),
                            "Kkindle",
                            "mcp-kindle-send",
                            Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporaryDirectory);
                        try
                        {
                            var convertedPath = Path.Combine(
                                temporaryDirectory,
                                KindleTransferPolicy.CreateSafeFileName(source.Title, ".azw3"));
                            var converter = _formatConverter
                                ?? throw new McpException("Book format conversion is not registered in this MCP server.");
                            await converter.ConvertAsync(
                                source.Path,
                                convertedPath,
                                cancellationToken: cancellationToken,
                                metadata: new FormatConversionMetadata(source.Title, string.Empty));
                            var convertedInfo = new FileInfo(convertedPath);
                            var convertedFile = new BookFile
                            {
                                Id = source.BookFile.Id,
                                BookId = source.BookFile.BookId,
                                Format = "azw3",
                                RelativePath = Path.GetFileName(convertedPath),
                                Size = convertedInfo.Length,
                                Sha256 = await Hashing.Sha256Async(convertedPath, cancellationToken)
                            };
                            await _devices.SendBookAsync(
                                device,
                                convertedFile,
                                convertedPath,
                                cancellationToken: cancellationToken);
                        }
                        finally
                        {
                            try { Directory.Delete(temporaryDirectory, recursive: true); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    else
                    {
                        await _devices.SendBookAsync(
                            device,
                            source.BookFile,
                            source.Path,
                            cancellationToken: cancellationToken);
                    }
                }
                break;
            }
            case "email":
            {
                var settings = await RequireEmailSettingsAsync(cancellationToken);
                recipient = settings.KindleEmailAddress;
                sender = settings.SenderEmailAddress;
                actions.Add($"Send '{source.Path}' as an email attachment from '{sender}' to '{recipient}'.");
                if (!dryRun)
                    await RequireEmailSender().SendAsync(
                        settings,
                        source.Path,
                        $"Kkindle：{source.Title}",
                        cancellationToken);
                break;
            }
            case "web":
                actions.Add($"Stage '{source.Path}' in the existing Send to Kindle web workflow.");
                if (!dryRun)
                    throw new McpException(
                        "The existing KindleWebSendWorkflow requires the desktop UI's IKindleWebPage and cannot run in the standalone MCP server.");
                break;
        }

        return new SendToKindleResult(
            normalizedChannel,
            dryRun,
            !dryRun,
            source.BookId,
            source.Title,
            source.Path,
            source.Format,
            deviceIdentity,
            deviceName,
            recipient,
            sender,
            actions);
    }

    /// <summary>
    /// Converts a local file or a stored book format using the existing
    /// BookFormatConversionPolicy/IBookFormatConverter path. Existing output
    /// files and existing target formats are never overwritten.
    /// </summary>
    [McpServerTool(
        Name = "convert_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Convert a book to targetFormat using the existing Calibre-backed converter. The operation writes a file, never overwrites an existing output, and returns an error on conflicts.")]
    public async Task<ConvertBookResult> ConvertBookAsync(
        [Description("Target output format, for example epub, azw3, pdf, or mobi.")] string targetFormat,
        [Description("Absolute or relative source file path; provide this or bookId, but not both.")] string? filePath = null,
        [Description("Book GUID returned by the library; provide this or filePath, but not both.")] string? bookId = null,
        [Description("Destination directory. For a bookId without this value, the converted file is added to that book in the library; for a filePath it defaults to the source directory.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target))
            throw new McpException("targetFormat must be one of epub, azw3, pdf, or mobi.");

        var source = await ResolveConversionSourceAsync(filePath, bookId, target, cancellationToken);
        if (!BookFormatConversionPolicy.IsCalibreInputFormat(source.Format))
            throw new McpException($"The source format '{source.Format}' is not supported by the existing conversion policy.");

        if (source.Book is not null
            && source.Book.Files.Any(file => string.Equals(
                BookFormatConversionPolicy.Normalize(file.Format),
                target,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpException($"Book '{source.Book.Id}' already has a {target.ToUpperInvariant()} file; no file was overwritten.");
        }

        var title = source.Book?.Title ?? Path.GetFileNameWithoutExtension(source.Path);
        var authors = source.Book?.Authors ?? string.Empty;
        var outputDirectoryPath = string.IsNullOrWhiteSpace(outputDirectory)
            ? source.Book is null
                ? Path.GetDirectoryName(source.Path) ?? throw new McpException("The source file has no destination directory.")
                : null
            : ValidatePath(outputDirectory, nameof(outputDirectory));
        var metadata = source.Book is null
            ? null
            : new FormatConversionMetadata(source.Book.Title, source.Book.Authors);

        if (source.Book is not null && outputDirectoryPath is null)
        {
            var libraryOutputPath = Path.Combine(
                Path.GetDirectoryName(source.Path)
                    ?? throw new McpException("The library source has no parent directory."),
                KindleTransferPolicy.CreateSafeFileName(title, "." + target));
            if (File.Exists(libraryOutputPath))
                throw new McpException($"The library conversion output already exists and was not overwritten: {libraryOutputPath}");

            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "KkindleMcpConversions",
                Guid.NewGuid().ToString("N"));
            var temporaryOutput = Path.Combine(
                temporaryDirectory,
                KindleTransferPolicy.CreateSafeFileName(title, "." + target));
            try
            {
                Directory.CreateDirectory(temporaryDirectory);
                await RequireFormatConverter().ConvertAsync(
                    source.Path,
                    temporaryOutput,
                    cancellationToken: cancellationToken,
                    metadata: metadata);
                var addedFile = await _library.AddFileToBookAsync(
                    source.Book.Id,
                    temporaryOutput,
                    cancellationToken);
                var absolutePath = Path.GetFullPath(_library.GetAbsoluteFilePath(addedFile));
                return new ConvertBookResult(
                    source.Book.Id,
                    source.Path,
                    absolutePath,
                    target,
                    true,
                    false);
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        var destinationDirectory = outputDirectoryPath
            ?? throw new McpException("A destination directory is required for this conversion.");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(
            destinationDirectory,
            KindleTransferPolicy.CreateSafeFileName(title, "." + target));
        if (File.Exists(destination))
            throw new McpException($"The conversion output already exists and was not overwritten: {destination}");
        if (string.Equals(Path.GetFullPath(source.Path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new McpException("The conversion output cannot be the source file.");

        await RequireFormatConverter().ConvertAsync(
            source.Path,
            destination,
            cancellationToken: cancellationToken,
            metadata: metadata);
        return new ConvertBookResult(
            source.Book?.Id,
            source.Path,
            Path.GetFullPath(destination),
            target,
            false,
            false);
    }

    /// <summary>
    /// Imports one file through the existing library import API. The conflict
    /// resolver deliberately rejects same-content and same-metadata conflicts
    /// so import never overwrites or silently merges an existing book.
    /// </summary>
    [McpServerTool(
        Name = "import_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Import one local book file into the Kkindle library. Existing files are never overwritten; duplicate or same-book conflicts return an error message.")]
    public async Task<ImportBookResult> ImportBookAsync(
        [Description("Absolute or relative local book file path to import.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ValidateExistingFilePath(filePath, nameof(filePath));
        var result = await _library.ImportAsync(
            [sourcePath],
            cancellationToken: cancellationToken,
            conflictResolver: conflict => throw new InvalidOperationException(
                $"A book with the same title and author already exists: '{conflict.ExistingBook.Title}'. Import was not performed."));
        var item = result.Items.SingleOrDefault()
            ?? throw new McpException("The library import returned no result.");

        if (!item.Succeeded || !item.Added || item.BookId is not { } bookId)
        {
            throw new McpException(item.Message ?? "The book already exists or could not be imported; no file was overwritten.");
        }

        var book = item.Book ?? await _library.GetBookAsync(bookId, cancellationToken);
        if (book is null)
            throw new McpException($"The book was imported but could not be read back from the library: {bookId}.");

        var importedFormat = BookFormatConversionPolicy.Normalize(Path.GetExtension(sourcePath));
        var importedFiles = book.Files
            .Where(file => string.Equals(
                BookFormatConversionPolicy.Normalize(file.Format),
                importedFormat,
                StringComparison.OrdinalIgnoreCase))
            .Select(file => new BookFilePathInfo(
                file.Id,
                file.Format,
                Path.GetFullPath(_library.GetAbsoluteFilePath(file)),
                file.Size,
                File.Exists(_library.GetAbsoluteFilePath(file))))
            .ToArray();
        return new ImportBookResult(
            book.Id,
            book.Title,
            importedFormat,
            importedFiles,
            item.Message ?? "Imported");
    }

    /// <summary>
    /// Removes one library book by moving it to the library trash. dryRun
    /// defaults to true so a client must explicitly request the deletion.
    /// </summary>
    [McpServerTool(
        Name = "delete_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Move a Kkindle library book and its files to the trash (recoverable in the desktop app). dryRun defaults to true; set dryRun=false to actually delete.")]
    public async Task<DeleteBookResult> DeleteBookAsync(
        [Description("Book GUID returned by list_library or search_books.")] Guid bookId,
        [Description("When true, only report the planned action. Defaults to true; false performs the deletion.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var book = await GetBookOrThrowAsync(bookId, cancellationToken);
        var actions = new[] { $"Move '{book.Title}' and its library files to trash." };
        if (dryRun)
            return new DeleteBookResult(book.Id, book.Title, dryRun, false, actions);

        await _library.DeleteAsync(bookId, cancellationToken);
        return new DeleteBookResult(book.Id, book.Title, false, true, actions);
    }

    /// <summary>
    /// Removes one book file from a connected device by its device-relative
    /// path (as reported by list_device_library). dryRun defaults to true so
    /// a client must explicitly request the deletion.
    /// </summary>
    [McpServerTool(
        Name = "delete_device_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Delete one book from a connected Kindle or supported reader device. dryRun defaults to true; set dryRun=false to actually delete.")]
    public async Task<DeleteDeviceBookResult> DeleteDeviceBookAsync(
        [Description("Device-relative path of the book file, exactly as reported by list_device_library.")] string relativePath,
        [Description("Optional device identity, root path, or exact device name; required when more than one device is connected.")] string? deviceId = null,
        [Description("When true, only report the planned action. Defaults to true; false performs the deletion.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new McpException("relativePath must not be empty.");

        var device = await ResolveSingleDeviceAsync(deviceId, cancellationToken);
        var books = await _devices.ScanBooksAsync(device, cancellationToken);
        var book = books.FirstOrDefault(candidate => string.Equals(
                candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(
                $"No book with relative path '{relativePath}' was found on device '{device.Name}' ({device.RootPath}).");

        var actions = new[] { $"Delete '{book.Title}' ({book.RelativePath}) from '{device.Name}'." };
        if (dryRun)
            return new DeleteDeviceBookResult(device.Identity, device.Name, book.RelativePath, book.Title, dryRun, false, actions);

        await _devices.RemoveBookAsync(device, book, cancellationToken);
        return new DeleteDeviceBookResult(device.Identity, device.Name, book.RelativePath, book.Title, false, true, actions);
    }
private async Task<Book> GetBookOrThrowAsync(Guid bookId, CancellationToken cancellationToken)
    {
        return await _library.GetBookAsync(bookId, cancellationToken)
            ?? throw new McpException($"Book '{bookId}' was not found in the Kkindle library.");
    }

    private async Task<KindleDevice> ResolveSingleDeviceAsync(
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var filter = ValidateOptionalDeviceId(deviceId);
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        if (filter is not null)
        {
            devices = devices.Where(device => DeviceMatches(device, filter)).ToArray();
            if (devices.Count == 0)
                throw new McpException($"No connected device matched '{filter}'.");
        }

        if (devices.Count == 0)
            throw new McpException("No connected reader device was detected.");
        if (devices.Count > 1)
            throw new McpException("More than one connected device was detected; provide deviceId.");
        return devices[0];
    }

    private async Task<ResolvedSendSource> ResolveSendSourceAsync(
        string? bookId,
        string? filePath,
        string channel,
        CancellationToken cancellationToken)
    {
        var hasBookId = !string.IsNullOrWhiteSpace(bookId);
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);
        if (hasBookId == hasFilePath)
            throw new McpException("Provide exactly one of bookId or filePath.");

        if (hasBookId)
        {
            var id = ParseBookId(bookId!);
            var book = await GetBookOrThrowAsync(id, cancellationToken);
            var candidates = channel switch
            {
                "email" => KindleEmailSelectionPolicy.GetCandidates(book.Files),
                "web" => KindleWebFilePolicy.GetCandidates(book.Files),
                _ => KindleTransferPolicy.GetCandidates(book.Files)
            };
            var file = candidates.FirstOrDefault()
                ?? throw new McpException(
                    $"Book '{book.Title}' has no file supported by the {channel} channel.");
            var path = ValidateExistingFilePath(
                _library.GetAbsoluteFilePath(file),
                "bookId");
            ValidateSendFormatAndSize(path, channel);
            return new ResolvedSendSource(book.Id, book.Title, path, file.Format, file);
        }

        var sourcePath = ValidateExistingFilePath(filePath!, nameof(filePath));
        var format = BookFormatConversionPolicy.Normalize(Path.GetExtension(sourcePath));
        var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
        var sourceIdentity = new Guid(Convert.FromHexString(sourceHash[..32]));
        var sourceFile = new BookFile
        {
            Id = sourceIdentity,
            BookId = Guid.Empty,
            Format = format,
            RelativePath = sourcePath,
            Size = new FileInfo(sourcePath).Length,
            Sha256 = sourceHash
        };
        if (channel == "usb" && KindleTransferPolicy.GetCandidates([sourceFile]).Count == 0)
            throw new McpException($"The file format '{format}' is not supported by USB Kindle transfer.");
        ValidateSendFormatAndSize(sourcePath, channel);
        return new ResolvedSendSource(
            null,
            Path.GetFileNameWithoutExtension(sourcePath),
            sourcePath,
            format,
            sourceFile);
    }

    private static void ValidateSendFormatAndSize(string path, string channel)
    {
        var fileInfo = new FileInfo(path);
        if (channel == "email")
        {
            if (!KindleEmailSelectionPolicy.IsSupportedFormat(fileInfo.Extension))
                throw new McpException("The email channel only supports EPUB or PDF files.");
            if (!KindleEmailSelectionPolicy.IsWithinAttachmentLimit(fileInfo.Length))
                throw new McpException("The email attachment exceeds the existing 50 MB Send to Kindle limit.");
        }
        else if (channel == "web")
        {
            try
            {
                KindleWebFilePolicy.Inspect(path);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                throw new McpException(exception.Message);
            }
        }
    }

    private async Task<KindleEmailSettings> RequireEmailSettingsAsync(CancellationToken cancellationToken)
    {
        if (_emailSettingsStore is null)
            throw new McpException("Kindle email settings are not registered in this MCP server.");
        var settings = await _emailSettingsStore.LoadAsync(cancellationToken);
        var validationError = settings.Validate();
        if (validationError is not null)
            throw new McpException(validationError);
        return settings;
    }

    private KindleEmailSender RequireEmailSender() =>
        _emailSender ?? throw new McpException("Kindle email sending is not registered in this MCP server.");

    private IBookFormatConverter RequireFormatConverter() =>
        _formatConverter ?? throw new McpException("Book format conversion is not registered in this MCP server.");

    private async Task<ResolvedConversionSource> ResolveConversionSourceAsync(
        string? filePath,
        string? bookId,
        string targetFormat,
        CancellationToken cancellationToken)
    {
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);
        var hasBookId = !string.IsNullOrWhiteSpace(bookId);
        if (hasFilePath == hasBookId)
            throw new McpException("Provide exactly one of filePath or bookId.");

        if (hasBookId)
        {
            var id = ParseBookId(bookId!);
            var book = await GetBookOrThrowAsync(id, cancellationToken);
            var sourceFile = BookFormatConversionPolicy
                .GetSourceCandidates(book.Files, targetFormat)
                .FirstOrDefault()
                ?? throw new McpException(
                    $"Book '{book.Title}' has no supported source format for {targetFormat.ToUpperInvariant()} conversion.");
            var sourcePath = ValidateExistingFilePath(
                _library.GetAbsoluteFilePath(sourceFile),
                "bookId");
            return new ResolvedConversionSource(book, sourcePath, sourceFile.Format);
        }

        var externalPath = ValidateExistingFilePath(filePath!, nameof(filePath));
        return new ResolvedConversionSource(
            null,
            externalPath,
            BookFormatConversionPolicy.Normalize(Path.GetExtension(externalPath)));
    }

    private static string NormalizeChannel(string? channel)
    {
        var normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("usb" or "email" or "web"))
            throw new McpException("channel must be one of usb, email, or web.");
        return normalized;
    }

    private static int ValidateRecentLimit(int value)
    {
        if (value is < 1 or > 100)
            throw new McpException($"limit must be between 1 and 100; received {value}.");
        return value;
    }

    private static string ValidatePath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpException($"{parameterName} must not be blank.");
        var normalized = path.Trim();
        if (normalized.Length > MaximumPathLength)
            throw new McpException($"{parameterName} is too long.");
        try
        {
            return Path.GetFullPath(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException($"{parameterName} is not a valid path: {exception.Message}");
        }
    }

    private static string ValidateExistingFilePath(string? path, string parameterName)
    {
        var normalized = ValidatePath(path, parameterName);
        if (!File.Exists(normalized))
            throw new McpException($"File '{normalized}' does not exist.");
        return normalized;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<BookListResult> ListBooksAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        var page = await _library.SearchPageAsync(
            query: query,
            sortMode: LibrarySortMode.UpdatedDescending,
            pageIndex: 0,
            pageSize: limit,
            cancellationToken: cancellationToken);

        var books = new List<BookSummary>(page.Books.Count);
        foreach (var book in page.Books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            books.Add(await ToBookSummaryAsync(book, cancellationToken));
        }

        return new BookListResult(page.TotalCount, books);
    }

    private async Task<BookSummary> ToBookSummaryAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var progress = await ReadProgressAsync(book, cancellationToken);
        return new BookSummary(
            book.Id,
            book.Title,
            book.Authors,
            progress?.ProgressPercent,
            progress?.UpdatedAt,
            book.ReadingStatus.ToString(),
            book.Files
                .Select(file => file.Format.Trim().ToUpperInvariant())
                .Where(format => format.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static BookMetadataResult ToBookMetadata(Book book, BookProgressInfo? progress) =>
        new(
            book.Id,
            book.Title,
            book.Authors,
            book.Series,
            book.SeriesIndex,
            book.Description,
            book.Publisher,
            book.PublishDate,
            book.Isbn,
            book.PageCount,
            book.Binding,
            book.DoubanRating,
            book.DoubanRatingCount,
            book.Tags,
            book.Category,
            book.IsFavorite,
            book.ReadingStatus.ToString(),
            book.CoverPath,
            book.CreatedAt,
            book.UpdatedAt,
            progress,
            book.Files
                .Select(file => new BookFileInfo(
                    file.Id,
                    file.Format,
                    file.RelativePath,
                    file.Size,
                    file.Sha256))
                .ToArray());

    private async Task<BookProgressInfo?> ReadProgressAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ProgressCandidate>(book.Files.Count * 2);
        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = await _readerData.GetProgressAsync(file.Id, cancellationToken);
            if (progress is not null)
            {
                candidates.Add(new ProgressCandidate(
                    file,
                    ClampProgress(progress.ProgressPercent),
                    progress.UpdatedAt,
                    "ReaderProgress"));
            }

            var stats = await _readerData.GetReadingStatsAsync(file.Id, cancellationToken);
            if (stats is not null)
            {
                candidates.Add(new ProgressCandidate(
                    file,
                    ClampProgress(stats.ProgressPercent),
                    stats.UpdatedAt,
                    "ReaderReadingStats"));
            }
        }

        var latest = candidates
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenBy(candidate => candidate.File.Id)
            .FirstOrDefault();
        return latest is null
            ? null
            : new BookProgressInfo(
                latest.ProgressPercent,
                latest.UpdatedAt,
                latest.File.Id,
                latest.File.Format,
                latest.Source);
    }

    private static DeviceInfo ToDeviceInfo(KindleDevice device)
    {
        var totalBytes = Math.Max(0, device.TotalBytes);
        var freeBytes = Math.Clamp(device.FreeBytes, 0, totalBytes);
        double? freePercentage = totalBytes == 0 ? null : freeBytes * 100d / totalBytes;
        return new DeviceInfo(
            device.Identity,
            device.Name,
            device.Profile.Family.ToString(),
            device.Profile.DisplayName,
            device.RootPath,
            device.Transport.ToString(),
            device.IsReady,
            totalBytes,
            freeBytes,
            freePercentage,
            device.CanReadNotes);
    }

    private static bool DeviceMatches(KindleDevice device, string filter) =>
        device.Identity.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || device.RootPath.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || device.Name.Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static int ValidateLimit(int value, string parameterName)
    {
        if (value is < 1 or > MaximumResultLimit)
            throw new McpException($"{parameterName} must be between 1 and {MaximumResultLimit}; received {value}.");
        return value;
    }

    private static string ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("query must not be empty.");
        var normalized = query.Trim();
        if (normalized.Length > MaximumQueryLength)
            throw new McpException($"query must be at most {MaximumQueryLength} characters.");
        return normalized;
    }

    private static Guid ParseBookId(string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId) || !Guid.TryParse(bookId.Trim(), out var id))
            throw new McpException("bookId must be a valid GUID.");
        return id;
    }

    private static string? ValidateOptionalDeviceId(string? deviceId)
    {
        if (deviceId is null) return null;
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new McpException("deviceId must not be blank when provided.");
        var normalized = deviceId.Trim();
        if (normalized.Length > MaximumDeviceIdLength)
            throw new McpException($"deviceId must be at most {MaximumDeviceIdLength} characters.");
        return normalized;
    }

    private static double ClampProgress(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    private async Task<(Guid Id, string Name)> RequireCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var collections = await _library.GetCollectionsAsync(cancellationToken);
        var collection = collections.FirstOrDefault(candidate => candidate.Id == collectionId)
            ?? throw new McpException($"Collection '{collectionId}' was not found in the Kkindle library.");
        return (collection.Id, collection.Name);
    }

    private static async Task RunLibraryMutationAsync(
        Func<CancellationToken, Task> action,
        string actionName,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new McpException($"{actionName} failed: {exception.Message}");
        }
    }

    private static async Task<T> RunLibraryMutationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string actionName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new McpException($"{actionName} failed: {exception.Message}");
        }
    }

    private static string ValidateCollectionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpException("name must not be blank.");
        var normalized = name.Trim();
        if (normalized.Length > 200)
            throw new McpException("name must be at most 200 characters.");
        return normalized;
    }

    private static int ValidateSearchContentLimit(int value)
    {
        if (value is < 1 or > 100)
            throw new McpException($"limit must be between 1 and 100; received {value}.");
        return value;
    }

    private static int ValidateAnnotationLimit(int value)
    {
        if (value is < 1 or > 1000)
            throw new McpException($"limit must be between 1 and 1000; received {value}.");
        return value;
    }
    private sealed record ResolvedSendSource(
        Guid? BookId,
        string Title,
        string Path,
        string Format,
        BookFile BookFile);

    private sealed record ResolvedConversionSource(
        Book? Book,
        string Path,
        string Format);

    private sealed record ProgressCandidate(
        BookFile File,
        double ProgressPercent,
        DateTimeOffset UpdatedAt,
        string Source);
}

/// <summary>Paginated library output returned by list_library and search_books.</summary>
public sealed record BookListResult(int TotalCount, IReadOnlyList<BookSummary> Books);

/// <summary>Compact book data used in library and search results.</summary>
public sealed record BookSummary(
    Guid Id,
    string Title,
    string Authors,
    double? ProgressPercent,
    DateTimeOffset? ProgressUpdatedAt,
    string ReadingStatus,
    IReadOnlyList<string> Formats);

/// <summary>Full local book metadata and its stored files.</summary>
public sealed record BookMetadataResult(
    Guid Id,
    string Title,
    string Authors,
    string? Series,
    double? SeriesIndex,
    string? Description,
    string? Publisher,
    string? PublishDate,
    string? Isbn,
    string? PageCount,
    string? Binding,
    double? DoubanRating,
    int? DoubanRatingCount,
    string Tags,
    string Category,
    bool IsFavorite,
    string ReadingStatus,
    string? CoverPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    BookProgressInfo? Progress,
    IReadOnlyList<BookFileInfo> Files);

/// <summary>Latest persisted progress snapshot for one book file.</summary>
public sealed record BookProgressInfo(
    double ProgressPercent,
    DateTimeOffset UpdatedAt,
    Guid BookFileId,
    string Format,
    string Source);

/// <summary>Stored local file metadata belonging to a library book.</summary>
public sealed record BookFileInfo(
    Guid Id,
    string Format,
    string RelativePath,
    long Size,
    string Sha256);

/// <summary>Connected reader-device list output.</summary>
public sealed record DeviceListResult(int DeviceCount, IReadOnlyList<DeviceInfo> Devices);

/// <summary>Current device connection and storage status output.</summary>
public sealed record DeviceStatusResult(
    bool IsConnected,
    int DeviceCount,
    string? RequestedDeviceId,
    IReadOnlyList<DeviceInfo> Devices);

/// <summary>Read-only view of a connected reader device.</summary>
public sealed record DeviceInfo(
    string Id,
    string Name,
    string Family,
    string Model,
    string RootPath,
    string Transport,
    bool IsReady,
    long TotalBytes,
    long FreeBytes,
    double? FreePercentage,
    bool CanReadNotes);

/// <summary>Detailed persisted progress for one book and each of its files.</summary>
public sealed record ReadingProgressResult(
    Guid BookId,
    string Title,
    ReadingProgressFile? Latest,
    IReadOnlyList<ReadingProgressFile> Files);

public sealed record ReadingProgressFile(
    Guid BookFileId,
    string Format,
    double? ProgressPercent,
    DateTimeOffset? UpdatedAt,
    string? ChapterPath,
    string? Fragment,
    int? ChapterIndex,
    int? ScrollPosition,
    int? FlowMode,
    ReaderContentPosition? ContentPosition,
    long? CumulativeSeconds,
    int? CompletedChapters,
    int? TotalChapters,
    string? Source);

/// <summary>Recent reading entries and the dashboard statistics used to build them.</summary>
public sealed record RecentBooksResult(
    ReadingDashboardSummary Summary,
    IReadOnlyList<RecentBookInfo> Books);

public sealed record ReadingDashboardSummary(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount);

public sealed record RecentBookInfo(
    Guid BookId,
    Guid BookFileId,
    string Title,
    string? Authors,
    double ProgressPercent,
    long CumulativeSeconds,
    DateTimeOffset UpdatedAt,
    bool IsInLibrary,
    IReadOnlyList<string> Formats);

public sealed record TagListResult(IReadOnlyList<string> Tags);

public sealed record CollectionListResult(IReadOnlyList<CollectionInfo> Collections);

public sealed record CollectionInfo(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int BookCount);

/// <summary>Absolute local paths for a book's stored files.</summary>
public sealed record BookFilePathResult(
    Guid BookId,
    string Title,
    IReadOnlyList<BookFilePathInfo> Files);

public sealed record BookFilePathInfo(
    Guid Id,
    string Format,
    string AbsolutePath,
    long Size,
    bool Exists);

public sealed record DeviceLibraryResult(IReadOnlyList<DeviceLibraryInfo> Devices);

public sealed record DeviceLibraryInfo(
    string DeviceId,
    string DeviceName,
    string RootPath,
    IReadOnlyList<DeviceBookInfo> Books);

public sealed record DeviceBookInfo(
    string RelativePath,
    string Title,
    string Authors,
    string Format,
    long Size,
    string Sha256,
    DateTimeOffset? ModifiedAt,
    bool IsManagedByKkindle);

public sealed record EjectDeviceResult(
    string DeviceId,
    string DeviceName,
    bool DryRun,
    bool Ejected,
    IReadOnlyList<string> Actions);

public sealed record SendToKindleResult(
    string Channel,
    bool DryRun,
    bool Sent,
    Guid? BookId,
    string Title,
    string SourcePath,
    string Format,
    string? DeviceId,
    string? DeviceName,
    string? Recipient,
    string? Sender,
    IReadOnlyList<string> Actions);

public sealed record ConvertBookResult(
    Guid? BookId,
    string SourcePath,
    string OutputPath,
    string TargetFormat,
    bool AddedToLibrary,
    bool OverwroteExistingFile);

public sealed record ImportBookResult(
    Guid BookId,
    string Title,
    string Format,
    IReadOnlyList<BookFilePathInfo> Files,
    string Message);

public sealed record DeleteBookResult(
    Guid BookId,
    string Title,
    bool DryRun,
    bool Deleted,
    IReadOnlyList<string> Actions);

public sealed record DeleteDeviceBookResult(
    string DeviceId,
    string DeviceName,
    string RelativePath,
    string Title,
    bool DryRun,
    bool Deleted,
    IReadOnlyList<string> Actions);

/// <summary>Outcome of a collection create/rename/delete/clear/merge operation.</summary>
public sealed record CollectionOperationResult(
    Guid CollectionId,
    string CollectionName,
    bool Done,
    IReadOnlyList<string> Actions);

/// <summary>Outcome of adding or removing a book in a collection.</summary>
public sealed record CollectionBookOperationResult(
    Guid BookId,
    Guid CollectionId,
    string CollectionName,
    bool Done,
    IReadOnlyList<string> Actions);

/// <summary>Matching passages found inside one book.</summary>
public sealed record BookContentSearchResult(
    Guid BookId,
    string Title,
    string Query,
    int TotalCount,
    IReadOnlyList<BookContentMatch> Matches);

/// <summary>One matching passage inside a book.</summary>
public sealed record BookContentMatch(
    Guid BookFileId,
    string Format,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterPath,
    int StartOffset,
    int EndOffset,
    string Content,
    double Rank);

/// <summary>Highlights and annotations belonging to one book.</summary>
public sealed record BookAnnotationsResult(
    Guid BookId,
    string Title,
    int TotalCount,
    IReadOnlyList<BookAnnotationInfo> Annotations);

/// <summary>One highlight/annotation (划线/批注).</summary>
public sealed record BookAnnotationInfo(
    Guid Id,
    Guid BookFileId,
    string Format,
    string ChapterPath,
    string? Fragment,
    int StartOffset,
    int EndOffset,
    string SelectedText,
    string Prefix,
    string Suffix,
    string Color,
    string UnderlineStyle,
    string Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Bookmarks belonging to one book.</summary>
public sealed record BookBookmarksResult(
    Guid BookId,
    string Title,
    int TotalCount,
    IReadOnlyList<BookBookmarkInfo> Bookmarks);

/// <summary>One bookmark (书签).</summary>
public sealed record BookBookmarkInfo(
    Guid Id,
    Guid BookFileId,
    string Format,
    string ChapterPath,
    string? Fragment,
    int ChapterIndex,
    int? ScrollPosition,
    int FlowMode,
    string Title,
    string Quote,
    DateTimeOffset CreatedAt,
    ReaderContentPosition? ContentPosition);

/// <summary>Aggregate reading statistics and recent activity.</summary>
public sealed record ReadingDashboardResult(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount,
    IReadOnlyList<DashboardRecentBookInfo> RecentBooks,
    IReadOnlyList<DashboardDayInfo> DailyReading);

/// <summary>One recently read book entry within the dashboard.</summary>
public sealed record DashboardRecentBookInfo(
    Guid BookId,
    Guid BookFileId,
    string Title,
    double ProgressPercent,
    long CumulativeSeconds,
    DateTimeOffset UpdatedAt,
    bool IsInLibrary,
    string? Authors);

/// <summary>Reading time aggregated for one day.</summary>
public sealed record DashboardDayInfo(DateOnly Date, long ActiveSeconds);
