#if KKINDLE_SWIFT_UI
using System.Text.Json;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile;
using static Kkindle.iOSHead.IOSNativeJson;

namespace Kkindle.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private readonly Dictionary<long, (string Path, TaskCompletionSource<TxtImportRules?> Decision)> _txtImports = [];
    private readonly Dictionary<string, NativeMaterial> _materials = [];

    private async Task<object> DispatchLibraryAsync(string command, JsonElement args, CancellationToken token)
    {
        var library = _shell.Modules.Library.Books;
        if (command.StartsWith("txt.", StringComparison.Ordinal)) return await DispatchTxtAsync(command, args, token);
        if (command.StartsWith("backup.", StringComparison.Ordinal))
        {
            await BackupAsync(command == "backup.restore", token);
            return Data();
        }
        using var operation = BeginOperation();
        await library.InitializeAsync(token);
        await _shell.EnsureReaderDataAsync();
        switch (command)
        {
            case "book.favorite":
                var favoriteBook = await GetBookAsync(args, token);
                favoriteBook.IsFavorite = Flag(args, "favorite");
                await library.UpdateMetadataAsync(favoriteBook, token);
                break;
            case "book.rename":
                await library.RenameBookAsync(Id(args, "bookId"), Text(args, "title").Trim(), token);
                break;
            case "book.save":
                await SaveBookAsync(args, token);
                break;
            case "book.cover":
                return await PickCoverAsync(token);
            case "book.delete":
                var deleteBook = await GetBookAsync(args, token);
                if (await ConfirmAsync("删除这本书", $"《{deleteBook.Title}》将移到回收站。", "移到回收站"))
                    await library.DeleteAsync(deleteBook.Id, token);
                break;
            case "book.kindle":
                await SendToKindleAsync(await GetBookAsync(args, token), token);
                break;
            case "collection.create":
                return await library.CreateCollectionAsync(Text(args, "name"), token);
            case "collection.rename":
                await library.RenameCollectionAsync(Id(args, "collectionId"), Text(args, "name"), token);
                break;
            case "collection.membership":
                if (Flag(args, "member")) await library.AddBookToCollectionAsync(Id(args, "bookId"), Id(args, "collectionId"), token);
                else await library.RemoveBookFromCollectionAsync(Id(args, "bookId"), Id(args, "collectionId"), token);
                break;
            case "collection.clear":
                if (await ConfirmAsync("清空收藏夹", "收藏夹本身会保留，书籍仍在书库中。", "清空"))
                    await library.ClearCollectionAsync(Id(args, "collectionId"), token);
                break;
            case "collection.dissolve":
                if (await ConfirmAsync("解散收藏夹", "书籍会保留在书库中。", "解散"))
                    await library.DissolveCollectionAsync(Id(args, "collectionId"), token);
                break;
            case "collection.merge":
                if (await ConfirmAsync("合并收藏夹", "书籍将加入目标收藏夹，当前收藏夹将被解散。", "合并"))
                    await library.MergeCollectionsAsync(Id(args, "collectionId"), Id(args, "targetId"), token);
                break;
            case "materials.get":
                return await GetMaterialsAsync(token);
            case "materials.export":
                await ExportMaterialsAsync(args, token);
                break;
            case "dashboard.get":
                var period = (ReadingPeriod)Number(args, "period");
                if (!Enum.IsDefined(period)) throw new InvalidOperationException("无效的阅读周期。");
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var range = ReadingPeriodRange.Create(period, today);
                var dashboard = await _shell.Modules.Reader.Data.GetReadingDashboardAsync(100, token, 366, today);
                return Data(("dashboard", dashboard), ("medals", ReadingMedalCatalog.Build(dashboard).ToArray()),
                    ("streak", ReadingMedalCatalog.ComputeStreakDays(dashboard.DailyReading)),
                    ("period", (int)period), ("today", today.ToString("yyyy-MM-dd")),
                    ("buckets", range.BuildBuckets(dashboard.DailyReading, today).ToArray()));
            case "trash.get":
                return (await library.GetTrashItemsAsync(token)).Select(item => Data(
                    ("id", item.Id), ("title", item.DisplayTitle), ("detail", item.KindLabel + " · " + item.SizeLabel),
                    ("deletedAt", item.DeletedAt))).ToArray();
            case "trash.restore":
                await library.RestoreTrashItemAsync(Id(args, "id"), token);
                break;
            case "trash.purge":
                if (await ConfirmAsync("永久删除", "永久删除后无法从回收站恢复。", "永久删除"))
                    await library.PurgeTrashItemAsync(Id(args, "id"), token);
                break;
            default: throw new InvalidOperationException("未知书库操作：" + command);
        }
        return Data();
    }

    private async Task<Book> GetBookAsync(JsonElement args, CancellationToken token) =>
        await _shell.Modules.Library.Books.GetBookAsync(Id(args, "bookId"), token)
        ?? throw new InvalidOperationException("书籍已不存在。");

    private async Task SaveBookAsync(JsonElement args, CancellationToken token)
    {
        // Work on a fresh DTO so a failed database write cannot modify the
        // book currently held by the reader or another screen.
        var book = await GetBookAsync(args, token);
        var fields = args.GetProperty("fields");
        var title = Text(fields, "title").Trim();
        if (title.Length == 0) throw new InvalidOperationException("书名不能为空。");
        if (!BookDetailEditing.TryParseRating(Text(fields, "onlineRating"), out var rating))
            throw new InvalidOperationException("请输入 0 到 10 之间的评分，或留空清除评分。");
        string? Optional(string key) => Text(fields, key).Trim() is { Length: > 0 } text ? text : null;
        book.Title = title;
        book.Authors = Optional("authors") ?? "未知作者";
        book.Series = Optional("series"); book.Publisher = Optional("publisher");
        book.PublishDate = Optional("publishDate"); book.Isbn = Optional("isbn");
        book.PageCount = Optional("pageCount"); book.Binding = Optional("binding");
        book.Tags = Text(fields, "tags").Trim(); book.Category = Text(fields, "category").Trim();
        book.Description = Optional("description");
        if (book.OnlineRating != rating) book.OnlineRatingCount = null;
        book.OnlineRating = rating;
        if (Optional("cover") is { } encoded)
        {
            var cover = Convert.FromBase64String(encoded);
            var path = Path.Combine(_shell.Modules.Paths.Covers, $"{book.Id:N}-manual-{Guid.NewGuid():N}.png");
            using var input = new MemoryStream(cover);
            using var bitmap = new Bitmap(input);
            await using (var output = File.Create(path + ".tmp")) bitmap.Save(output, PngBitmapEncoderOptions.Default);
            File.Move(path + ".tmp", path);
            book.CoverPath = Path.GetRelativePath(_shell.Modules.Paths.Data, path);
        }
        await _shell.Modules.Library.Books.UpdateMetadataAsync(book, token);
    }

    private async Task<object> PickCoverAsync(CancellationToken token)
    {
        var files = await _shell.Services.ImportSource.PickFilesAsync(token);
        try
        {
            if (files.Count == 0) return Data(("cover", null));
            var bytes = await File.ReadAllBytesAsync(files[0], token);
            using var input = new MemoryStream(bytes);
            using var image = new Bitmap(input); // Verify that this really is an image before accepting it.
            using var output = new MemoryStream();
            image.Save(output, PngBitmapEncoderOptions.Default);
            return Data(("cover", Convert.ToBase64String(output.ToArray())));
        }
        finally { _shell.Services.ImportSource.ReleaseFiles(files); }
    }

    private async Task SendToKindleAsync(Book book, CancellationToken token)
    {
        var settings = await _shell.Modules.Integrations.KindleEmailSettings.LoadAsync(token);
        if (!settings.IsConfigured) throw new InvalidOperationException("请先在“我的”中设置发送邮箱。");
        var file = book.Files.FirstOrDefault(item => item.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
            ?? book.Files.FirstOrDefault(item => item.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Kindle 邮箱只接收 EPUB 或 PDF。");
        var path = _shell.Modules.Library.Books.GetAbsoluteFilePath(file);
        if (!File.Exists(path)) throw new InvalidOperationException("书籍文件不在本机，请先打开一次以下载。");
        await _shell.Modules.Integrations.KindleEmail.SendAsync(settings, path, book.Title, cancellationToken: token);
        _shell.ShowToast($"已发送到 {settings.KindleEmailAddress}");
    }

    private async Task<object> GetMaterialsAsync(CancellationToken token)
    {
        var entries = new List<NativeMaterial>();
        foreach (var book in await _shell.Modules.Library.Books.SearchAsync(cancellationToken: token))
        foreach (var file in book.Files)
        {
            foreach (var mark in await _shell.Modules.Reader.Data.GetBookmarksAsync(file.Id, token))
                entries.Add(new NativeMaterial(mark.Id.ToString(), book.Id, book.Title, "bookmark", mark.DisplayQuote,
                    string.Empty, mark.DisplayTitle, mark.CreatedAt,
                    new ReaderProgressRow(book.Id, file.Id, mark.ChapterPath, mark.Fragment, mark.ChapterIndex,
                        mark.ScrollPosition ?? 0, 0, mark.FlowMode, mark.CreatedAt)
                    { ContentPosition = mark.ContentPosition }));
            foreach (var note in await _shell.Modules.Reader.Data.GetAnnotationsAsync(file.Id, token))
            {
                var chapter = note.ChapterPath.StartsWith("pdf:page:", StringComparison.Ordinal)
                    && int.TryParse(note.ChapterPath[9..], out var page) ? page - 1 : -1;
                var location = chapter >= 0 ? $"第 {chapter + 1} 页" : note.ChapterPath;
                // An EPUB chapter path is a file name inside the package. Like the
                // legacy page, the list shows the date and exports keep the path.
                entries.Add(new NativeMaterial(note.Id.ToString(), book.Id, book.Title,
                    string.IsNullOrWhiteSpace(note.Note) ? "highlight" : "note", note.DisplayQuote, note.Note,
                    chapter >= 0 ? location : $"{note.UpdatedAt.LocalDateTime:yyyy/MM/dd}", note.UpdatedAt,
                    new ReaderProgressRow(book.Id, file.Id, note.ChapterPath, null, chapter, 0, 0, 1, note.UpdatedAt)
                    { ContentPosition = new ReaderContentPosition { TextOffset = note.StartOffset } })
                { ExportLocation = location });
            }
        }
        _materials.Clear();
        foreach (var entry in entries) _materials[entry.Id] = entry;
        return entries.OrderByDescending(entry => entry.UpdatedAt).ToArray();
    }

    private async Task ExportMaterialsAsync(JsonElement args, CancellationToken token)
    {
        var entries = args.GetProperty("ids").EnumerateArray()
            .Select(id => _materials.GetValueOrDefault(id.GetString() ?? string.Empty)).OfType<NativeMaterial>().ToArray();
        var records = entries.Select(entry => new ReadingMaterialRecord(ReadingMaterialSource.Local, entry.BookTitle,
            entry.Kind switch { "note" => "笔记", "bookmark" => "书签", _ => "划线" }, entry.ExportLocation,
            entry.Quote, entry.Note, entry.UpdatedAt)).ToArray();
        var markdown = Flag(args, "markdown", true);
        await using var output = new MemoryStream();
        if (markdown) await ReadingMaterialsExport.WriteMarkdownAsync(output, records, token);
        else await ReadingMaterialsExport.WritePlainTextAsync(output, records, token);
        var share = _shell.Services.FileShare ?? throw new InvalidOperationException("此设备不支持分享文件。");
        if (await share.ShareAsync($"Kkindle-阅读资料-{DateTime.Now:yyyyMMdd-HHmmss}.{(markdown ? "md" : "txt")}", output.ToArray()))
            _shell.ShowToast($"已分享 {entries.Length} 条阅读资料");
    }

    private async Task BackupAsync(bool restore, CancellationToken token)
    {
        string? directory = null;
        IReadOnlyList<string> files = [];
        try
        {
            await _shell.EnsureBackupEncryptionAsync(token);
            if (restore)
            {
                files = await _shell.Services.ImportSource.PickFilesAsync(token);
                if (files.Count == 0) return;
                if (files.Count != 1) throw new InvalidOperationException("请选择一个 .kkindle 备份文件。");
                if (!await ConfirmAsync("恢复 Kkindle 备份", "恢复将覆盖当前整个书库，包括书籍、封面和阅读记录。建议先导出备份。恢复成功后需要完全关闭并重新打开应用。", "覆盖并恢复")) return;
                Publish(Data(("type", "dataBusy"), ("busy", true), ("message", "正在恢复备份…")));
                await _shell.RestoreBackupAsync(files[0], token);
            }
            else
            {
                Publish(Data(("type", "dataBusy"), ("busy", true), ("message", "正在导出备份…")));
                directory = Path.Combine(Path.GetTempPath(), "kkindle-native-backup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"Kkindle-备份-{DateTime.Now:yyyyMMdd-HHmmss}{AppBackupService.FileExtension}");
                await _shell.ExportBackupAsync(path, token);
                Publish(Data(("type", "dataBusy"), ("busy", false)));
                if (_shell.Services.FileShare is { } share && await share.ShareFileAsync(path))
                    _shell.ShowToast("备份已分享。");
            }
        }
        finally
        {
            Publish(Data(("type", "dataBusy"), ("busy", false)));
            _shell.Services.ImportSource.ReleaseFiles(files);
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<TxtImportRules?> ChooseTxtRulesAsync(string path, CancellationToken token)
    {
        var id = ++_promptId;
        var decision = new TaskCompletionSource<TxtImportRules?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _txtImports.Add(id, (path, decision));
        Publish(Data(("type", "txtImport"), ("txtImport", Data(("id", id), ("name", Path.GetFileName(path))))));
        try { return await decision.Task.WaitAsync(token); }
        finally { _txtImports.Remove(id); }
    }

    private async Task<object> DispatchTxtAsync(string command, JsonElement args, CancellationToken token)
    {
        var id = args.GetProperty("id").GetInt64();
        if (!_txtImports.TryGetValue(id, out var pending)) return Data();
        if (command == "txt.cancel") { pending.Decision.TrySetResult(null); return Data(); }
        var rules = Read(args.GetProperty("rules"), TxtImportRules.Default, IOSNativeJsonContext.Default.TxtImportRules);
        if (command == "txt.complete") { pending.Decision.TrySetResult(rules); return Data(); }
        var preview = await TxtToEpubService.PreviewAsync(pending.Path, rules, Number(args, "page"), 20, token);
        return Data(("chapterCount", preview.ChapterCount), ("detectedEncoding", preview.DetectedEncoding.ToString()),
            ("chapters", preview.Chapters.ToArray()), ("hasPreviousPage", preview.HasPreviousPage), ("hasNextPage", preview.HasNextPage));
    }
}
#endif
