using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>Window-level services a plugin page may ask the host for.</summary>
public interface IPluginUiHost
{
    bool NetworkEnabled { get; }
    void Close();
    void Notify(string message);
    void SetTitle(string title);
    void OpenExternal(Uri uri);
    void BooksChanged(IReadOnlyCollection<Guid> bookIds);

    /// <summary>Asks the user whether the plugin may call <paramref name="host"/>.</summary>
    Task<bool> RequestNetworkAccessAsync(string host);

    /// <summary>Asks the user to confirm importing a downloaded file.</summary>
    Task<bool> ConfirmImportAsync(string fileName, long sizeBytes);

    /// <summary>Pushes a named event to the page (progress reporting).</summary>
    void RaiseEvent(string name, JsonNode payload);

    /// <summary>Receives one page diagnostic line; ignored outside developer mode.</summary>
    void Log(string level, string message);

    /// <summary>Shows the host file picker; returns user-approved local files.</summary>
    Task<IReadOnlyList<PluginPickedFile>> PickFilesAsync(
        bool allowMultiple,
        IReadOnlyList<string> extensions);

    /// <summary>Shows the host save dialog and writes the bytes.</summary>
    Task<PluginPickedFile?> SaveFileAsync(string suggestedName, byte[] content);

    Task CopyTextAsync(string text);

    Task<string?> ReadTextAsync();
}

/// <summary>A file the user approved through a plugin window's picker.</summary>
public sealed record PluginPickedFile(string Name, string? Path);

/// <summary>
/// Executes the <c>window.kkindle</c> calls of one plugin page. Every call is
/// checked against the plugin's manifest permissions, and library access is
/// limited to the books the user opened the page with.
/// </summary>
public sealed class PluginBridge : IDisposable
{
    public const int MaximumResponseBytes = 10 * 1024 * 1024;
    public const int MaximumCoverBytes = 10 * 1024 * 1024;
    public const long MaximumImportBytes = 500L * 1024 * 1024;
    private static readonly HashSet<string> ImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".fb2", ".txt", ".djvu", ".cbz", ".cbr",
        ".zip", ".rtf", ".doc", ".docx", ".html", ".htm", ".md", ".lit", ".pdb", ".kfx", ".tcr"
    };
    private const int MaximumRedirects = 5;
    private const int MaximumTextLength = 20_000;
    private const int MaximumFieldLength = 1_000;
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 Kkindle/1.0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Cookie2", "Content-Length", "Connection", "Keep-Alive", "Transfer-Encoding",
        "TE", "Trailer", "Upgrade", "Expect", "Origin", "Proxy-Authorization", "Proxy-Connection"
    };

    private readonly InstalledPlugin _plugin;
    private readonly PluginPageContext _context;
    private readonly IBookLibraryService _library;
    private readonly AppPaths _paths;
    private readonly PluginStorage _storage;
    private readonly IPluginUiHost _ui;
    private readonly PluginSecrets? _secrets;
    private readonly PluginNetworkGrants _grants;
    private readonly IPluginBookTaskHost? _bookTasks;
    private readonly ReaderDataService? _readerData;
    private readonly HashSet<string> _pickedFiles = new(StringComparer.Ordinal);
    // One prompt per host at a time: a page fetching many images can ask for
    // the same origin concurrently, and each naive request would open its own
    // dialog even after the first one was allowed.
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _networkApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _taskGate = new(1, 1);
    private CancellationTokenSource? _taskCancellation;
    private readonly HashSet<Guid> _allowedBooks;
    private bool _disposed;

    /// <summary>
    /// True while a book task (translation, pinyin or model download) runs.
    /// The window asks before closing so a task is not cancelled by accident.
    /// </summary>
    public bool HasRunningTask => _taskGate.CurrentCount == 0;

    public PluginBridge(
        InstalledPlugin plugin,
        PluginPageContext context,
        IBookLibraryService library,
        AppPaths paths,
        PluginStorage storage,
        IPluginUiHost ui,
        PluginSecrets? secrets = null,
        HttpMessageHandler? handler = null,
        IPluginBookTaskHost? bookTasks = null,
        ReaderDataService? readerData = null)
    {
        _plugin = plugin;
        _context = context;
        _library = library;
        _paths = paths;
        _storage = storage;
        _ui = ui;
        _secrets = secrets;
        _bookTasks = bookTasks;
        _readerData = readerData;
        _grants = new PluginNetworkGrants(Path.Combine(paths.PluginData, plugin.Id));
        _allowedBooks = context.BookIds.ToHashSet();
        // Cookies persist for the lifetime of the page so sites that set a
        // visitor cookie on the first response accept follow-up requests.
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All
        }, disposeHandler: true)
        {
            // Large book downloads can take minutes; the plugin window's
            // lifetime token still cancels the transfer when it closes.
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>
    /// Handles one raw message from the page. Returns the request id and the
    /// reply payload, or null when the message is not a bridge request.
    /// </summary>
    public async Task<(long Id, string Reply)?> HandleAsync(string? message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        long id;
        string method;
        JsonElement parameters;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out id)) return null;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String) return null;
            method = methodElement.GetString()!;
            parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = await DispatchAsync(method, parameters, cancellationToken);
            return (id, new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString(JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var text = exception is OperationCanceledException or TaskCanceledException
                ? "请求超时或已取消。"
                : exception.Message;
            return (id, new JsonObject { ["ok"] = false, ["error"] = text }.ToJsonString(JsonOptions));
        }
    }

    private Task<JsonNode?> DispatchAsync(string method, JsonElement parameters, CancellationToken cancellationToken) => method switch
    {
        "library.getBooks" => GetBooksAsync(parameters, cancellationToken),
        "library.search" => SearchLibraryAsync(parameters, cancellationToken),
        "library.getBook" => GetLibraryBookAsync(parameters, cancellationToken),
        "library.getCollections" => GetCollectionsAsync(cancellationToken),
        "library.createCollection" => CreateCollectionAsync(parameters, cancellationToken),
        "library.addToCollection" => ChangeCollectionMembershipAsync(parameters, add: true, cancellationToken),
        "library.removeFromCollection" => ChangeCollectionMembershipAsync(parameters, add: false, cancellationToken),
        "library.importFiles" => ImportPickedFilesAsync(parameters, cancellationToken),
        "library.updateBook" => UpdateBookAsync(parameters, cancellationToken),
        "library.getCover" => GetCoverAsync(parameters, cancellationToken),
        "library.setCover" => SetCoverAsync(parameters, cancellationToken),
        "library.importDownload" => ImportDownloadAsync(parameters, cancellationToken),
        "library.translateBook" => TranslateBookAsync(parameters, cancellationToken),
        "library.annotatePinyin" => AnnotatePinyinAsync(parameters, cancellationToken),
        "library.cancelTask" => CancelTaskAsync(),
        "pinyin.status" => PinyinStatusAsync(),
        "pinyin.downloadModel" => DownloadPinyinModelAsync(cancellationToken),
        "http.fetch" => FetchAsync(parameters, cancellationToken),
        "network.requestAccess" => RequestNetworkAccessAsync(parameters, cancellationToken),
        "secrets.get" => SecretGetAsync(parameters, cancellationToken),
        "secrets.set" => SecretSetAsync(parameters, cancellationToken),
        "secrets.remove" => SecretRemoveAsync(parameters, cancellationToken),
        "storage.get" => StorageGetAsync(parameters, cancellationToken),
        "storage.set" => StorageSetAsync(parameters, cancellationToken),
        "storage.remove" => StorageRemoveAsync(parameters, cancellationToken),
        "ui.close" => Ui(() => _ui.Close()),
        "ui.notify" => Ui(() => _ui.Notify(Truncate(RequiredString(parameters, "message"), 500))),
        "ui.setTitle" => Ui(() => _ui.SetTitle(Truncate(RequiredString(parameters, "title"), 120))),
        "ui.openExternal" => Ui(() => _ui.OpenExternal(ParseExternalUri(RequiredString(parameters, "url")))),
        "ui.log" => Ui(() => _ui.Log(
            Truncate(OptionalString(parameters, "level") ?? "log", 20),
            Truncate(OptionalString(parameters, "message") ?? string.Empty, 4000))),
        "ui.pickFiles" => PickFilesAsync(parameters, cancellationToken),
        "ui.saveFile" => SaveFileAsync(parameters, cancellationToken),
        "ui.copyText" => CopyTextAsync(parameters, cancellationToken),
        "ui.readText" => ReadTextAsync(cancellationToken),
        "reading.getProgress" => GetReadingProgressAsync(parameters, cancellationToken),
        "reading.getAnnotations" => GetReadingAnnotationsAsync(parameters, cancellationToken),
        "reading.getBookmarks" => GetReadingBookmarksAsync(parameters, cancellationToken),
        "reading.getStats" => GetReadingStatsAsync(parameters, cancellationToken),
        _ => throw new InvalidOperationException($"未知的插件接口“{method}”。")
    };

    // ---- library -------------------------------------------------------

    private async Task<JsonNode?> GetBooksAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Read);
        var ids = new List<Guid>();
        if (parameters.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in idsElement.EnumerateArray())
                ids.Add(ParseAllowedBookId(element.GetString()));
        }
        else
        {
            ids.AddRange(_context.BookIds);
        }

        var books = new JsonArray();
        foreach (var id in ids.Distinct())
        {
            var book = await _library.GetBookAsync(id, cancellationToken);
            if (book is not null) books.Add(ToJson(book));
        }
        return books;
    }

    private async Task<JsonNode?> SearchLibraryAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var pageIndex = OptionalInt(parameters, "pageIndex", 0, 0, 100_000);
        var pageSize = OptionalInt(parameters, "pageSize", 50, 1, 200);
        Guid? collectionId = null;
        if (OptionalString(parameters, "collectionId") is { Length: > 0 } collectionValue)
            collectionId = ParseGuid(collectionValue, "collectionId");
        var favorite = parameters.TryGetProperty("favorite", out var favoriteElement)
            && favoriteElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? favoriteElement.GetBoolean()
                : (bool?)null;
        var page = await _library.SearchPageAsync(
            OptionalString(parameters, "query"),
            EmptyToNull(OptionalString(parameters, "author")),
            EmptyToNull(OptionalString(parameters, "tag")),
            EmptyToNull(OptionalString(parameters, "format")),
            EmptyToNull(OptionalString(parameters, "category")),
            collectionId,
            OptionalReadingStatus(parameters),
            favorite,
            OptionalSortMode(parameters),
            pageIndex,
            pageSize,
            cancellationToken);
        return new JsonObject
        {
            ["books"] = new JsonArray(page.Books.Select(book => (JsonNode)ToJson(book)).ToArray()),
            ["totalCount"] = page.TotalCount,
            ["pageIndex"] = pageIndex,
            ["pageSize"] = pageSize
        };
    }

    private async Task<JsonNode?> GetLibraryBookAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var id = ParseGuid(RequiredString(parameters, "id"), "id");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        return ToJson(book);
    }

    private async Task<JsonNode?> GetCollectionsAsync(CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var summaries = await _library.GetCollectionSummariesAsync(cancellationToken);
        return new JsonArray(summaries.Select(summary => (JsonNode)new JsonObject
        {
            ["id"] = summary.Collection.Id.ToString("D"),
            ["name"] = summary.Collection.Name,
            ["bookCount"] = summary.BookCount
        }).ToArray());
    }

    private async Task<JsonNode?> CreateCollectionAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        RequireLibrary(PluginLibraryAccess.Write);
        var name = RequiredString(parameters, "name").Trim();
        if (name.Length > 60)
            throw new ArgumentException("分组名称不能超过 60 个字符。");
        var collection = await _library.CreateCollectionAsync(name, cancellationToken);
        return new JsonObject
        {
            ["id"] = collection.Id.ToString("D"),
            ["name"] = collection.Name,
            ["bookCount"] = 0
        };
    }

    private async Task<JsonNode?> ChangeCollectionMembershipAsync(
        JsonElement parameters,
        bool add,
        CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        RequireLibrary(PluginLibraryAccess.Write);
        var bookId = ParseGuid(RequiredString(parameters, "bookId"), "bookId");
        var collectionId = ParseGuid(RequiredString(parameters, "collectionId"), "collectionId");
        var collection = (await _library.GetCollectionsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == collectionId)
            ?? throw new ArgumentException("分组不存在或已被删除。");
        if (string.Equals(collection.Name, BookLibraryDefaults.UncollectedCollectionName, StringComparison.Ordinal))
            throw new ArgumentException("不能通过插件修改“未收藏”分组。");

        if (add)
            await _library.AddBookToCollectionAsync(bookId, collectionId, cancellationToken);
        else
            await _library.RemoveBookFromCollectionAsync(bookId, collectionId, cancellationToken);
        _ui.BooksChanged([bookId]);
        return new JsonObject
        {
            ["bookId"] = bookId.ToString("D"),
            ["collectionId"] = collectionId.ToString("D"),
            ["member"] = add
        };
    }

    private async Task<JsonNode?> ImportPickedFilesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!_plugin.Manifest.CanImportLibrary)
            throw new UnauthorizedAccessException("插件没有导入书籍的权限。");
        if (!parameters.TryGetProperty("paths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("缺少参数“paths”。");

        var paths = new List<string>();
        foreach (var element in pathsElement.EnumerateArray())
        {
            var path = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("paths 只能包含文件路径。");
            // Only files the user approved through this window's picker may be
            // imported, so a plugin cannot read arbitrary host paths.
            bool allowed;
            lock (_pickedFiles) allowed = _pickedFiles.Remove(path);
            if (!allowed)
                throw new UnauthorizedAccessException("只能导入插件通过文件选择器选中的文件。");
            paths.Add(path);
        }
        if (paths.Count == 0) return new JsonObject { ["items"] = new JsonArray() };

        var result = await _library.ImportAsync(paths, progress: null, cancellationToken);
        var items = new JsonArray();
        foreach (var item in result.Items)
        {
            items.Add(new JsonObject
            {
                ["sourcePath"] = item.SourcePath,
                ["succeeded"] = item.Succeeded,
                ["message"] = item.Message,
                ["bookId"] = item.BookId?.ToString("D"),
                ["added"] = item.Added
            });
        }
        return new JsonObject
        {
            ["items"] = items,
            ["successCount"] = result.SuccessCount,
            ["addedCount"] = result.AddedCount
        };
    }

    // ---- reading data --------------------------------------------------

    private async Task<JsonNode?> GetReadingProgressAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var files = new JsonArray();
        foreach (var file in book.Files)
        {
            var progress = await reader.GetProgressAsync(file.Id, cancellationToken);
            if (progress is null) continue;
            files.Add(new JsonObject
            {
                ["fileId"] = file.Id.ToString("D"),
                ["format"] = file.Format.ToUpperInvariant(),
                ["chapterPath"] = progress.ChapterPath,
                ["fragment"] = progress.Fragment,
                ["chapterIndex"] = progress.ChapterIndex,
                ["scrollPosition"] = progress.ScrollPosition,
                ["progressPercent"] = progress.ProgressPercent,
                ["flowMode"] = progress.FlowMode,
                ["updatedAt"] = progress.UpdatedAt,
                ["contentPosition"] = progress.ContentPosition is null
                    ? null
                    : JsonSerializer.SerializeToNode(progress.ContentPosition, JsonOptions)
            });
        }
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["title"] = book.Title,
            ["files"] = files
        };
    }

    private async Task<JsonNode?> GetReadingAnnotationsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var limit = OptionalInt(parameters, "limit", 500, 1, 2000);
        var rows = new List<ReaderAnnotation>();
        foreach (var file in book.Files)
            rows.AddRange(await reader.GetAnnotationsAsync(file.Id, cancellationToken, limit));
        var annotations = new JsonArray(rows
            .OrderByDescending(row => row.UpdatedAt)
            .Take(limit)
            .Select(row => (JsonNode)new JsonObject
            {
                ["id"] = row.Id.ToString("D"),
                ["bookId"] = row.BookId.ToString("D"),
                ["fileId"] = row.BookFileId.ToString("D"),
                ["chapterPath"] = row.ChapterPath,
                ["startOffset"] = row.StartOffset,
                ["endOffset"] = row.EndOffset,
                ["selectedText"] = Truncate(row.SelectedText, MaximumTextLength),
                ["note"] = Truncate(row.Note, MaximumTextLength),
                ["color"] = row.Color,
                ["underlineStyle"] = row.UnderlineStyle,
                ["createdAt"] = row.CreatedAt,
                ["updatedAt"] = row.UpdatedAt
            })
            .ToArray());
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["annotations"] = annotations
        };
    }

    private async Task<JsonNode?> GetReadingBookmarksAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var limit = OptionalInt(parameters, "limit", 500, 1, 2000);
        var rows = new List<ReaderBookmark>();
        foreach (var file in book.Files)
            rows.AddRange(await reader.GetBookmarksAsync(file.Id, cancellationToken, limit));
        var bookmarks = new JsonArray(rows
            .OrderByDescending(row => row.CreatedAt)
            .Take(limit)
            .Select(row => (JsonNode)new JsonObject
            {
                ["id"] = row.Id.ToString("D"),
                ["bookId"] = row.BookId.ToString("D"),
                ["fileId"] = row.BookFileId.ToString("D"),
                ["chapterPath"] = row.ChapterPath,
                ["chapterIndex"] = row.ChapterIndex,
                ["title"] = row.Title,
                ["quote"] = Truncate(row.Quote, MaximumTextLength),
                ["createdAt"] = row.CreatedAt
            })
            .ToArray());
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["bookmarks"] = bookmarks
        };
    }

    private async Task<JsonNode?> GetReadingStatsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var files = new JsonArray();
        foreach (var file in book.Files)
        {
            var stats = await reader.GetReadingStatsAsync(file.Id, cancellationToken);
            if (stats is null) continue;
            files.Add(new JsonObject
            {
                ["fileId"] = file.Id.ToString("D"),
                ["format"] = file.Format.ToUpperInvariant(),
                ["cumulativeSeconds"] = stats.CumulativeSeconds,
                ["progressPercent"] = stats.ProgressPercent,
                ["completedChapters"] = stats.CompletedChapters,
                ["totalChapters"] = stats.TotalChapters,
                ["updatedAt"] = stats.UpdatedAt
            });
        }
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["files"] = files
        };
    }

    private async Task<(Book Book, ReaderDataService Reader)> ResolveReadingBookAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var reader = _readerData ?? throw new InvalidOperationException("阅读数据服务不可用。");
        var id = ParseGuid(RequiredString(parameters, "bookId"), "bookId");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        return (book, reader);
    }

    // ---- files and clipboard -------------------------------------------

    private async Task<JsonNode?> PickFilesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var extensions = new List<string>();
        if (parameters.TryGetProperty("extensions", out var extensionsElement)
            && extensionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in extensionsElement.EnumerateArray())
            {
                var value = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(value)) continue;
                if (!value.StartsWith('.')) value = "." + value;
                if (value.Length > 12 || value.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
                    throw new ArgumentException($"文件扩展名“{value}”无效。");
                extensions.Add(value.ToLowerInvariant());
            }
        }
        if (extensions.Count > 20)
            throw new ArgumentException("最多只能指定 20 种文件扩展名。");

        var files = await _ui.PickFilesAsync(OptionalBool(parameters, "multiple", false), extensions);
        var result = new JsonArray();
        foreach (var file in files)
        {
            if (file.Path is { } path) lock (_pickedFiles) _pickedFiles.Add(path);
            result.Add(new JsonObject { ["name"] = file.Name, ["path"] = file.Path });
        }
        return new JsonObject { ["files"] = result };
    }

    private async Task<JsonNode?> SaveFileAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(RequiredString(parameters, "fileName"));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName 无效。");
        var text = OptionalString(parameters, "text");
        var base64 = OptionalString(parameters, "base64");
        byte[] content;
        if (base64 is not null)
        {
            try { content = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new ArgumentException("base64 内容无效。"); }
        }
        else if (text is not null)
        {
            content = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            throw new ArgumentException("需要提供 text 或 base64 内容。");
        }
        if (content.Length > MaximumResponseBytes)
            throw new ArgumentException("保存的文件超过 10 MB。");

        var saved = await _ui.SaveFileAsync(fileName, content);
        return saved is null
            ? null
            : new JsonObject { ["name"] = saved.Name, ["path"] = saved.Path };
    }

    private async Task<JsonNode?> CopyTextAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var text = RequiredString(parameters, "text");
        if (text.Length > 100_000)
            throw new ArgumentException("复制的文本过长。");
        await _ui.CopyTextAsync(text);
        return null;
    }

    private async Task<JsonNode?> ReadTextAsync(CancellationToken cancellationToken)
    {
        var text = await _ui.ReadTextAsync();
        return text is null ? null : new JsonObject { ["text"] = Truncate(text, 100_000) };
    }

    private async Task<JsonNode?> UpdateBookAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Write);
        var id = ParseAllowedBookId(RequiredString(parameters, "id"));
        if (!parameters.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("缺少要更新的字段。");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");

        ApplyPatch(book, patch);
        await _library.UpdateMetadataAsync(book, cancellationToken);
        _ui.BooksChanged([id]);
        var saved = await _library.GetBookAsync(id, cancellationToken) ?? book;
        return ToJson(saved);
    }

    internal static void ApplyPatch(Book book, JsonElement patch)
    {
        // Validate everything before touching the book so a bad field cannot
        // leave a half-applied update behind.
        var changes = new List<Action>();
        foreach (var property in patch.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "title":
                {
                    var text = RequiredText(value, property.Name, MaximumFieldLength);
                    changes.Add(() => book.Title = text);
                    break;
                }
                case "authors":
                {
                    var text = RequiredText(value, property.Name, MaximumFieldLength);
                    changes.Add(() => book.Authors = text);
                    break;
                }
                case "series": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Series = text); break; }
                case "description": { var text = OptionalText(value, property.Name, MaximumTextLength); changes.Add(() => book.Description = text); break; }
                case "publisher": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Publisher = text); break; }
                case "publishDate": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.PublishDate = text); break; }
                case "isbn": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Isbn = text); break; }
                case "pageCount": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.PageCount = text); break; }
                case "binding": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Binding = text); break; }
                case "tags": { var text = OptionalText(value, property.Name, MaximumFieldLength) ?? string.Empty; changes.Add(() => book.Tags = text); break; }
                case "category": { var text = OptionalText(value, property.Name, MaximumFieldLength) ?? string.Empty; changes.Add(() => book.Category = text); break; }
                case "seriesIndex":
                {
                    var number = OptionalNumber(value, property.Name, 0, 100_000);
                    changes.Add(() => book.SeriesIndex = number);
                    break;
                }
                case "rating":
                {
                    var number = OptionalNumber(value, property.Name, 0, 10);
                    changes.Add(() => book.OnlineRating = number);
                    break;
                }
                case "ratingCount":
                {
                    var number = OptionalNumber(value, property.Name, 0, int.MaxValue);
                    changes.Add(() => book.OnlineRatingCount = number is null ? null : (int)number.Value);
                    break;
                }
                default:
                    throw new ArgumentException($"不支持更新字段“{property.Name}”。");
            }
        }
        foreach (var change in changes) change();
    }

    private async Task<JsonNode?> GetCoverAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Read);
        var id = ParseAllowedBookId(RequiredString(parameters, "id"));
        var book = await _library.GetBookAsync(id, cancellationToken);
        if (book?.CoverPath is not { Length: > 0 } coverPath) return null;
        var path = Path.GetFullPath(Path.Combine(_paths.Data, coverPath));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumCoverBytes) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var format = DetectImage(bytes);
        return format is null ? null : JsonValue.Create($"data:{format.Value.MediaType};base64,{Convert.ToBase64String(bytes)}");
    }

    private async Task<JsonNode?> SetCoverAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Write);
        var id = ParseAllowedBookId(RequiredString(parameters, "id"));
        // Accept a bare Base64 payload, a data: URL, or "<media-type>;base64,<data>".
        var data = RequiredString(parameters, "data");
        const string Base64Marker = "base64,";
        var marker = data.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        var payload = marker >= 0 ? data[(marker + Base64Marker.Length)..] : data;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            throw new ArgumentException("封面数据不是有效的 Base64。");
        }
        if (bytes.Length == 0 || bytes.Length > MaximumCoverBytes)
            throw new ArgumentException("封面图片为空或超过 10 MB。");
        var format = DetectImage(bytes) ?? throw new ArgumentException("封面必须是 JPEG、PNG、WebP 或 GIF 图片。");

        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        _paths.EnsureDirectories();
        // A fresh name per write keeps image caches from showing the old cover.
        var path = Path.Combine(_paths.Covers, $"{book.Id:N}-plugin-{Guid.NewGuid():N}{format.Extension}");
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
        book.CoverPath = Path.GetRelativePath(_paths.Data, path);
        await _library.UpdateMetadataAsync(book, cancellationToken);
        _ui.BooksChanged([id]);
        return ToJson(book);
    }

    internal static JsonObject ToJson(Book book) => new()
    {
        ["id"] = book.Id.ToString("D"),
        ["title"] = book.Title,
        ["authors"] = book.Authors,
        ["series"] = book.Series,
        ["seriesIndex"] = book.SeriesIndex,
        ["description"] = book.Description,
        ["publisher"] = book.Publisher,
        ["publishDate"] = book.PublishDate,
        ["isbn"] = book.Isbn,
        ["pageCount"] = book.PageCount,
        ["binding"] = book.Binding,
        ["rating"] = book.OnlineRating,
        ["ratingCount"] = book.OnlineRatingCount,
        ["tags"] = book.Tags,
        ["category"] = book.Category,
        ["hasCover"] = !string.IsNullOrWhiteSpace(book.CoverPath),
        ["favorite"] = book.IsFavorite,
        ["readingStatus"] = book.ReadingStatus.ToString().ToLowerInvariant(),
        ["collectionIds"] = new JsonArray(book.CollectionIds
            .Select(id => (JsonNode)JsonValue.Create(id.ToString("D"))!)
            .ToArray()),
        ["updatedAt"] = book.UpdatedAt,
        ["formats"] = new JsonArray(book.Files
            .Select(file => file.Format.ToUpperInvariant())
            .Distinct()
            .Select(format => (JsonNode)JsonValue.Create(format)!)
            .ToArray())
    };

    // ---- network -------------------------------------------------------

    private async Task<JsonNode?> FetchAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireNetwork();
        if (!_ui.NetworkEnabled)
            throw new InvalidOperationException("网络访问已在应用设置中关闭。");

        var uri = await ParseAllowedUriAsync(RequiredString(parameters, "url"), cancellationToken);
        var method = (OptionalString(parameters, "method") ?? "GET").ToUpperInvariant();
        if (method is not ("GET" or "POST" or "HEAD"))
            throw new ArgumentException($"不支持的请求方法“{method}”。");
        var body = OptionalString(parameters, "body");
        var responseType = OptionalString(parameters, "responseType") ?? "text";
        if (responseType is not ("text" or "base64"))
            throw new ArgumentException("responseType 只能是 text 或 base64。");
        var headers = ParseHeaders(parameters);

        for (var redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            string? contentType = null;
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = value; continue; }
                request.Headers.TryAddWithoutValidation(name, value);
            }
            if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            if (!request.Headers.Contains("Accept-Language")) request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.6");
            if (body is not null && method == "POST")
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "text/plain; charset=utf-8");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                if (redirect >= MaximumRedirects)
                    throw new InvalidOperationException("重定向次数过多。");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                uri = await ParseAllowedUriAsync(next.AbsoluteUri, cancellationToken);
                if (status is 301 or 302 or 303 && method == "POST") { method = "GET"; body = null; }
                continue;
            }

            var bytes = await ReadLimitedAsync(response, cancellationToken);
            var responseHeaders = new JsonObject();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                responseHeaders[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return new JsonObject
            {
                ["status"] = status,
                ["ok"] = response.IsSuccessStatusCode,
                ["url"] = uri.AbsoluteUri,
                ["contentType"] = mediaType,
                ["headers"] = responseHeaders,
                ["body"] = responseType == "base64"
                    ? Convert.ToBase64String(bytes)
                    : Decode(bytes, response.Content.Headers.ContentType?.CharSet)
            };
        }
    }

    private static List<KeyValuePair<string, string>> ParseHeaders(JsonElement parameters)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (parameters.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headerElement.EnumerateObject())
            {
                if (ForbiddenHeaders.Contains(header.Name)
                    || header.Name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)
                    || header.Name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"不允许设置请求头“{header.Name}”。");
                if (header.Value.ValueKind != JsonValueKind.String) continue;
                headers.Add(new(header.Name, header.Value.GetString() ?? string.Empty));
            }
        }
        return headers;
    }

    private async Task<Uri> ParseAllowedUriAsync(string value, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("只能请求 https:// 地址。");
        if (!uri.IsDefaultPort)
            throw new ArgumentException("只能请求默认端口（443）。");
        if (_plugin.Manifest.NetworkOrigins.Any(origin => origin.Allows(uri)))
            return uri;
        if (await _grants.IsApprovedAsync(uri.IdnHost, cancellationToken))
            return uri;
        throw new UnauthorizedAccessException($"插件没有访问 {uri.Host} 的权限。请先在插件页面请求访问授权。");
    }

    private async Task<JsonNode?> RequestNetworkAccessAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireNetwork();
        var text = RequiredString(parameters, "origin").Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort)
            throw new ArgumentException("只能请求访问 https:// 默认端口的网站。");
        var host = uri.IdnHost;
        if (!IsGrantableHost(host))
            throw new ArgumentException("这个地址不能作为插件访问的网站。");
        var approved = _plugin.Manifest.NetworkOrigins.Any(origin => origin.Allows(uri))
            || await _grants.IsApprovedAsync(host, cancellationToken);
        if (!approved)
        {
            if (!await RequestApprovalAsync(host, cancellationToken))
                return new JsonObject { ["granted"] = false, ["host"] = host };
        }
        return new JsonObject { ["granted"] = true, ["host"] = host };
    }

    private Task<bool> RequestApprovalAsync(string host, CancellationToken cancellationToken)
    {
        var pending = _networkApprovals.GetOrAdd(
            host,
            key => new Lazy<Task<bool>>(() => ApproveHostAsync(key, cancellationToken)));
        return pending.Value;
    }

    private async Task<bool> ApproveHostAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _ui.RequestNetworkAccessAsync(host)) return false;
            await _grants.ApproveAsync(host, cancellationToken);
            return true;
        }
        finally
        {
            _networkApprovals.TryRemove(host, out _);
        }
    }

    private static bool IsGrantableHost(string host) =>
        host.Length <= 253
        && Uri.CheckHostName(host) == UriHostNameType.Dns
        && host.Contains('.')
        && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the target of a download redirect. File hosts commonly hand
    /// the transfer to a CDN whose name the plugin cannot know in advance, so
    /// an undeclared https host is offered to the user instead of failing the
    /// download outright; the answer is remembered like a runtime grant.
    /// </summary>
    private async Task<Uri> AuthorizeDownloadRedirectAsync(Uri target, CancellationToken cancellationToken)
    {
        try
        {
            return await ParseAllowedUriAsync(target.AbsoluteUri, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            var host = target.IdnHost;
            if (!IsGrantableHost(host) || !await RequestApprovalAsync(host, cancellationToken))
                throw new UnauthorizedAccessException($"下载被重定向到 {target.Host}，未获允许访问。");
            return target;
        }
    }

    private void RequireNetwork()
    {
        if (!_plugin.Manifest.HasNetworkPermission)
            throw new UnauthorizedAccessException("插件没有声明网络权限。");
    }

    private void RequireImport()
    {
        if (!_plugin.Manifest.CanImportLibrary)
            throw new UnauthorizedAccessException("插件没有导入书籍的权限。");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidOperationException("响应内容超过 10 MB。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("响应内容超过 10 MB。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
            catch (ArgumentException) { }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    // ---- storage -------------------------------------------------------

    private async Task<JsonNode?> StorageGetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireStorage();
        return await _storage.GetAsync(RequiredString(parameters, "key"), cancellationToken);
    }

    private async Task<JsonNode?> StorageSetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireStorage();
        var value = parameters.TryGetProperty("value", out var element) && element.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(element.GetRawText())
            : null;
        await _storage.SetAsync(RequiredString(parameters, "key"), value, cancellationToken);
        return null;
    }

    private async Task<JsonNode?> StorageRemoveAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireStorage();
        await _storage.SetAsync(RequiredString(parameters, "key"), null, cancellationToken);
        return null;
    }

    // ---- book tasks ----------------------------------------------------

    private async Task<JsonNode?> TranslateBookAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireTranslation();
        RequireBookTasks();
        var bookId = ParseAllowedBookId(RequiredString(parameters, "bookId"));
        var provider = (OptionalString(parameters, "provider") ?? "ai").ToLowerInvariant() switch
        {
            "ai" => "ai",
            "bing" or "bing-free" => "bing",
            "google" or "google-free" => "google",
            _ => throw new ArgumentException("不支持的翻译服务。")
        };
        var request = new PluginTranslationRequest(
            provider,
            OptionalString(parameters, "sourceLanguage") ?? "auto",
            RequiredString(parameters, "targetLanguage"),
            OptionalBool(parameters, "translated", true),
            OptionalBool(parameters, "bilingual", false),
            OptionalInt(parameters, "aiRequestsPerMinute", 0, 0, BookTranslationSettings.MaxAiRequestsPerMinute),
            Truncate(OptionalString(parameters, "googleProxyAddress") ?? string.Empty, 500),
            OptionalBool(parameters, "resume", true));
        if (!request.Translated && !request.Bilingual)
            throw new ArgumentException("请至少选择一种翻译结果。");
        return await RunTaskAsync(
            "translation",
            bookId,
            (progress, token) => _bookTasks!.TranslateBookAsync(bookId, request, progress, token),
            cancellationToken);
    }

    private async Task<JsonNode?> AnnotatePinyinAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequirePinyin();
        RequireBookTasks();
        var bookId = ParseAllowedBookId(RequiredString(parameters, "bookId"));
        var engine = (OptionalString(parameters, "engine") ?? "dotnet-g2p").ToLowerInvariant();
        if (engine is not ("dotnet-g2p" or "g2pw"))
            throw new ArgumentException("不支持的注音引擎。");
        var style = (OptionalString(parameters, "outputStyle") ?? "tone-marked").ToLowerInvariant();
        if (style is not ("tone-marked" or "tone-number" or "none"))
            throw new ArgumentException("不支持的注音样式。");
        var request = new PluginPinyinRequest(
            engine,
            style,
            OptionalBool(parameters, "aiReview", true),
            OptionalBool(parameters, "resume", true));
        return await RunTaskAsync(
            "pinyin",
            bookId,
            (progress, token) => _bookTasks!.AnnotatePinyinAsync(bookId, request, progress, token),
            cancellationToken);
    }

    private Task<JsonNode?> CancelTaskAsync()
    {
        var cancelled = _taskCancellation is not null;
        _taskCancellation?.Cancel();
        return Task.FromResult<JsonNode?>(new JsonObject { ["cancelling"] = cancelled });
    }

    private Task<JsonNode?> PinyinStatusAsync()
    {
        RequirePinyin();
        RequireBookTasks();
        var status = _bookTasks!.GetPinyinStatus();
        return Task.FromResult<JsonNode?>(new JsonObject { ["g2pwInstalled"] = status.G2pwInstalled });
    }

    private Task<JsonNode?> DownloadPinyinModelAsync(CancellationToken cancellationToken)
    {
        RequirePinyin();
        RequireBookTasks();
        return RunTaskAsync(
            "pinyin-model",
            null,
            async (progress, token) =>
            {
                await _bookTasks!.DownloadPinyinModelAsync(progress, token);
                return new JsonObject { ["installed"] = true };
            },
            cancellationToken);
    }

    private async Task<JsonNode?> RunTaskAsync(
        string task,
        Guid? bookId,
        Func<IProgress<PluginTaskProgress>, CancellationToken, Task<JsonObject>> run,
        CancellationToken cancellationToken)
    {
        if (!_taskGate.Wait(0))
            throw new InvalidOperationException("已有任务正在进行，请等待完成或先取消。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _taskCancellation = linked;
        try
        {
            var progress = new Progress<PluginTaskProgress>(
                value => ReportTaskProgress(task, bookId, value));
            var result = await run(progress, linked.Token);
            if (bookId is { } id)
                _ui.BooksChanged([id]);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new JsonObject
            {
                ["task"] = task,
                ["bookId"] = bookId?.ToString("D"),
                ["cancelled"] = true
            };
        }
        finally
        {
            _taskCancellation = null;
            _taskGate.Release();
        }
    }

    private void ReportTaskProgress(string task, Guid? bookId, PluginTaskProgress value)
    {
        try
        {
            _ui.RaiseEvent("task.progress", new JsonObject
            {
                ["task"] = task,
                ["bookId"] = bookId?.ToString("D"),
                ["stage"] = value.Stage,
                ["message"] = value.Message,
                ["percent"] = Math.Clamp(value.Percent, 0, 100),
                ["processed"] = value.Processed,
                ["total"] = value.Total,
                ["indeterminate"] = value.Indeterminate
            });
        }
        catch (Exception)
        {
            // Progress is cosmetic; a closing window must not fail the task.
        }
    }

    private void RequireTranslation()
    {
        if (!_plugin.Manifest.AllowsTranslation)
            throw new UnauthorizedAccessException("插件没有翻译书籍的权限。");
    }

    private void RequirePinyin()
    {
        if (!_plugin.Manifest.AllowsPinyin)
            throw new UnauthorizedAccessException("插件没有书籍注音的权限。");
    }

    private void RequireBookTasks()
    {
        if (_bookTasks is null)
            throw new InvalidOperationException("当前版本不支持插件任务。");
    }

    // ---- imports -------------------------------------------------------

    /// <summary>
    /// Downloads a file the plugin points at and imports it into the library.
    /// The user sees the file name and size before any bytes are written, and
    /// a per-request progress event lets the plugin page show the transfer.
    /// </summary>
    private async Task<JsonNode?> ImportDownloadAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireImport();
        RequireNetwork();
        if (!_ui.NetworkEnabled)
            throw new InvalidOperationException("网络访问已在应用设置中关闭。");

        var uri = await ParseAllowedUriAsync(RequiredString(parameters, "url"), cancellationToken);
        var headers = ParseHeaders(parameters);
        var fileName = NormalizeImportFileName(OptionalString(parameters, "fileName"), uri);
        var directory = Path.Combine(_paths.PluginData, _plugin.Id, "downloads");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var (name, value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
                if (!request.Headers.Contains("User-Agent"))
                    request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                if (!request.Headers.Contains("Accept"))
                    request.Headers.TryAddWithoutValidation("Accept", "*/*");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (redirect >= MaximumRedirects)
                        throw new InvalidOperationException("重定向次数过多。");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    uri = await AuthorizeDownloadRedirectAsync(next, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"下载失败（HTTP {status}）。");
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                // Quota and login failures come back as a JSON message or an
                // HTML page; importing either as the requested book is wrong.
                var expectsHtml = Path.GetExtension(fileName).ToLowerInvariant() is ".html" or ".htm";
                if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                    || (!expectsHtml && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("下载被服务拒绝，请检查账号状态或下载额度。");

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                if (totalBytes > MaximumImportBytes)
                    throw new InvalidOperationException($"文件超过 {MaximumImportBytes / (1024 * 1024)} MB，无法导入。");
                if (!await _ui.ConfirmImportAsync(fileName, totalBytes))
                    return new JsonObject { ["cancelled"] = true, ["fileName"] = fileName };

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var destination = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    var lastReport = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read <= 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        copied += read;
                        if (copied > MaximumImportBytes)
                            throw new InvalidOperationException($"文件超过 {MaximumImportBytes / (1024 * 1024)} MB，无法导入。");
                        if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(200))
                        {
                            lastReport = Stopwatch.GetTimestamp();
                            ReportImportProgress(fileName, copied, totalBytes, "download");
                        }
                    }
                    await destination.FlushAsync(cancellationToken);
                    ReportImportProgress(fileName, copied, totalBytes, "download");
                }
                break;
            }

            var result = await _library.ImportAsync(
                [temporaryPath],
                new Progress<TransferProgress>(progress =>
                    ReportImportProgress(fileName, progress.BytesCopied, progress.TotalBytes, "import")),
                cancellationToken);
            var item = result.Items.FirstOrDefault();
            if (item?.BookId is { } importedId)
                _ui.BooksChanged([importedId]);
            return new JsonObject
            {
                ["fileName"] = fileName,
                ["imported"] = item?.Succeeded == true,
                ["added"] = item?.Added == true,
                ["message"] = item?.Message,
                ["bookId"] = item?.BookId?.ToString("D")
            };
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ReportImportProgress(string fileName, long copied, long totalBytes, string stage)
    {
        try
        {
            _ui.RaiseEvent("import.progress", new JsonObject
            {
                ["fileName"] = fileName,
                ["stage"] = stage,
                ["downloadedBytes"] = copied,
                ["totalBytes"] = totalBytes,
                ["percent"] = totalBytes > 0 ? Math.Clamp(copied * 100d / totalBytes, 0, 100) : 0
            });
        }
        catch (Exception)
        {
            // Progress is cosmetic; a closing window must not fail the import.
        }
    }

    private static string NormalizeImportFileName(string? value, Uri uri)
    {
        var name = (value ?? string.Empty).Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character);
        }
        name = builder.ToString().Trim(' ', '.');
        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            name = name[..Math.Max(1, 120 - extension.Length)] + extension;
        }
        if (name.Length == 0)
            throw new ArgumentException("插件没有提供要导入的文件名。");
        if (!ImportExtensions.Contains(Path.GetExtension(name)))
            throw new ArgumentException("只能导入常见电子书文件（EPUB、PDF、MOBI、AZW3、TXT 等）。");
        return name;
    }

    // ---- secrets -------------------------------------------------------

    private PluginSecrets RequireSecrets()
    {
        if (!_plugin.Manifest.UsesSecrets || _secrets is null)
            throw new UnauthorizedAccessException("插件没有保存账号信息的权限。");
        return _secrets;
    }

    private async Task<JsonNode?> SecretGetAsync(JsonElement parameters, CancellationToken cancellationToken) =>
        JsonValue.Create(await RequireSecrets().GetAsync(RequiredString(parameters, "key"), cancellationToken));

    private async Task<JsonNode?> SecretSetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        await RequireSecrets().SetAsync(
            RequiredString(parameters, "key"),
            OptionalString(parameters, "value"),
            cancellationToken);
        return null;
    }

    private async Task<JsonNode?> SecretRemoveAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        await RequireSecrets().SetAsync(RequiredString(parameters, "key"), null, cancellationToken);
        return null;
    }

    // ---- helpers -------------------------------------------------------

    private static Task<JsonNode?> Ui(Action action)
    {
        action();
        return Task.FromResult<JsonNode?>(null);
    }

    private void RequireLibrary(PluginLibraryAccess access)
    {
        if (_plugin.Manifest.LibraryAccess < access)
            throw new UnauthorizedAccessException(access == PluginLibraryAccess.Write
                ? "插件没有修改书库的权限。"
                : "插件没有读取书库的权限。");
    }

    private void RequireLibraryAll()
    {
        if (!_plugin.Manifest.HasLibraryScopeAll)
            throw new UnauthorizedAccessException("插件没有访问整个书库的权限。");
    }

    private static Guid ParseGuid(string value, string name) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new ArgumentException($"参数“{name}”不是有效的书籍或分组 id。");

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static LibraryReadingStatus? OptionalReadingStatus(JsonElement parameters) =>
        OptionalString(parameters, "readingStatus")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "unread" => LibraryReadingStatus.Unread,
            "reading" => LibraryReadingStatus.Reading,
            "finished" => LibraryReadingStatus.Finished,
            var value => throw new ArgumentException($"readingStatus 不支持“{value}”。")
        };

    private static LibrarySortMode OptionalSortMode(JsonElement parameters) =>
        OptionalString(parameters, "sort")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "updated" => LibrarySortMode.UpdatedDescending,
            "title" => LibrarySortMode.TitleAscending,
            "author" => LibrarySortMode.AuthorAscending,
            "created" => LibrarySortMode.CreatedDescending,
            "progress" => LibrarySortMode.ProgressDescending,
            var value => throw new ArgumentException($"sort 不支持“{value}”。")
        };

    private void RequireStorage()
    {
        if (!_plugin.Manifest.UsesStorage)
            throw new UnauthorizedAccessException("插件没有声明存储权限。");
    }

    private Guid ParseAllowedBookId(string? value)
    {
        if (!Guid.TryParse(value, out var id) || !_allowedBooks.Contains(id))
            throw new UnauthorizedAccessException("插件只能访问打开它时选中的书籍。");
        return id;
    }

    private static Uri ParseExternalUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("只能打开 http(s) 链接。");
        return uri;
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        OptionalString(parameters, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少参数“{name}”。");

    private static bool OptionalBool(JsonElement parameters, string name, bool fallback) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int OptionalInt(JsonElement parameters, string name, int fallback, int minimum, int maximum)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return fallback;
        // A fractional or non-numeric value must not silently become the
        // fallback (for the AI rate limit that would mean "unlimited").
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || double.IsNaN(number) || Math.Floor(number) != number)
            throw new ArgumentException($"{name} 必须是整数。");
        return (int)Math.Clamp(number, minimum, maximum);
    }

    private static string? OptionalString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequiredText(JsonElement value, string name, int maximumLength)
    {
        var text = OptionalText(value, name, maximumLength);
        return string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException($"字段“{name}”不能为空。")
            : text;
    }

    private static string? OptionalText(JsonElement value, string name, int maximumLength)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"字段“{name}”必须是文本。");
        var text = value.GetString()!.Trim();
        if (text.Length > maximumLength)
            throw new ArgumentException($"字段“{name}”过长。");
        return text.Length == 0 ? null : text;
    }

    private static double? OptionalNumber(JsonElement value, string name, double minimum, double maximum)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        double number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) number = parsed;
        else throw new ArgumentException($"字段“{name}”必须是数字。");
        if (double.IsNaN(number) || number < minimum || number > maximum)
            throw new ArgumentException($"字段“{name}”超出范围。");
        return number;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    internal static (string MediaType, string Extension)? DetectImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", ".png");
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", ".webp");
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return ("image/gif", ".gif");
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _taskCancellation?.Cancel();
        _http.Dispose();
    }
}
