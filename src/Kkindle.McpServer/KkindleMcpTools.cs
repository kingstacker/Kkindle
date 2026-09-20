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
                    await _devices.SendBookAsync(device, source.BookFile, source.Path, cancellationToken: cancellationToken);
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
        var sourceFile = new BookFile
        {
            Id = Guid.Empty,
            BookId = Guid.Empty,
            Format = format,
            RelativePath = sourcePath,
            Size = new FileInfo(sourcePath).Length
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
