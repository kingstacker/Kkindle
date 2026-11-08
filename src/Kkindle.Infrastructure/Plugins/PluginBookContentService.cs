using System.Collections.Concurrent;
using Kkindle.Core;
#if NET10_0_OR_GREATER
using Kkindle.Layout;
#endif

namespace Kkindle.Infrastructure.Plugins;

/// <summary>One readable file of a book, as listed by <c>book.getToc</c>.</summary>
public sealed record PluginBookChapter(int Index, string Title, string Path, int Length);

/// <summary>Navigation entry of a book's table of contents.</summary>
public sealed record PluginBookTocItem(string Title, int ChapterIndex, int Level, string? Fragment);

/// <summary>The reading-order chapters and table of contents of one book file.</summary>
public sealed record PluginBookOutline(
    Guid BookId,
    Guid FileId,
    string Format,
    IReadOnlyList<PluginBookChapter> Chapters,
    IReadOnlyList<PluginBookTocItem> Toc);

/// <summary>A slice of one chapter's text.</summary>
public sealed record PluginChapterText(
    int ChapterIndex,
    string Title,
    string Path,
    string Text,
    int Offset,
    int Length,
    bool HasMore);

/// <summary>Raw bytes of a book file, read in chunks.</summary>
public sealed record PluginFileChunk(
    Guid FileId,
    string Name,
    string Format,
    long Size,
    long Offset,
    byte[] Data,
    bool HasMore);

/// <summary>Book content services lent to plugins with the <c>content</c> or <c>bookFiles</c> permission.</summary>
public interface IPluginContentHost
{
    Task<PluginBookOutline> GetOutlineAsync(Book book, Guid? fileId, CancellationToken cancellationToken);

    Task<PluginChapterText> GetChapterTextAsync(
        Book book,
        Guid? fileId,
        int chapterIndex,
        int offset,
        int maxLength,
        CancellationToken cancellationToken);

    Task<PluginFileChunk> ReadFileAsync(
        Book book,
        Guid? fileId,
        long offset,
        int length,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads EPUB, AZW3 and MOBI text exactly as the native reader sees it:
/// the same prepared reader cache and <see cref="XhtmlChapterLoader"/>
/// stream, so offsets line up with annotations and reader positions.
/// </summary>
public sealed class PluginBookContentService : IPluginContentHost
{
    private static readonly string[] TextFormats = ["epub", "azw3", "mobi", "azw", "prc"];
    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly Func<BookFile, CancellationToken, Task<string?>>? _resolveFile;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<PreparedBook>>> _prepared = new();
    private readonly ConcurrentDictionary<(Guid FileId, int Chapter), string> _chapterText = new();

    public PluginBookContentService(
        AppPaths paths,
        IBookLibraryService library,
        Func<BookFile, CancellationToken, Task<string?>>? resolveFile = null)
    {
        _paths = paths;
        _library = library;
        _resolveFile = resolveFile;
    }

    public async Task<PluginBookOutline> GetOutlineAsync(Book book, Guid? fileId, CancellationToken cancellationToken)
    {
        var (file, prepared) = await PrepareAsync(book, fileId, cancellationToken);
        var chapters = new List<PluginBookChapter>();
        for (var index = 0; index < prepared.Document.Chapters.Count; index++)
        {
            chapters.Add(new PluginBookChapter(
                index,
                prepared.Document.ChapterTitles.ElementAtOrDefault(index) ?? string.Empty,
                RelativeChapterPath(prepared.Document, index),
                (await LoadChapterAsync(file.Id, prepared.Document, index, cancellationToken)).Length));
        }
        var toc = prepared.Document.Navigation
            .Select(item => new PluginBookTocItem(
                item.Title,
                item.ChapterIndex,
                item.Level,
                item.Target.Contains('#') ? Uri.UnescapeDataString(item.Target[(item.Target.IndexOf('#') + 1)..]) : null))
            .ToArray();
        return new PluginBookOutline(book.Id, file.Id, file.Format.ToLowerInvariant(), chapters, toc);
    }

    public async Task<PluginChapterText> GetChapterTextAsync(
        Book book,
        Guid? fileId,
        int chapterIndex,
        int offset,
        int maxLength,
        CancellationToken cancellationToken)
    {
        var (file, prepared) = await PrepareAsync(book, fileId, cancellationToken);
        if (chapterIndex < 0 || chapterIndex >= prepared.Document.Chapters.Count)
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "章节序号超出范围。");
        var text = await LoadChapterAsync(file.Id, prepared.Document, chapterIndex, cancellationToken);
        offset = Math.Clamp(offset, 0, text.Length);
        var length = Math.Min(Math.Max(0, maxLength), text.Length - offset);
        return new PluginChapterText(
            chapterIndex,
            prepared.Document.ChapterTitles.ElementAtOrDefault(chapterIndex) ?? string.Empty,
            RelativeChapterPath(prepared.Document, chapterIndex),
            text.Substring(offset, length),
            offset,
            text.Length,
            offset + length < text.Length);
    }

    public async Task<PluginFileChunk> ReadFileAsync(
        Book book,
        Guid? fileId,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        var file = fileId is { } id
            ? book.Files.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidOperationException("书籍文件不存在。")
            : book.Files.FirstOrDefault()
                ?? throw new InvalidOperationException("这本书没有文件。");
        var path = await ResolvePathAsync(file, cancellationToken);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        offset = Math.Clamp(offset, 0, stream.Length);
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(length, stream.Length - offset)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total < buffer.Length) Array.Resize(ref buffer, total);
        return new PluginFileChunk(
            file.Id,
            Path.GetFileName(file.RelativePath),
            file.Format.ToLowerInvariant(),
            stream.Length,
            offset,
            buffer,
            offset + total < stream.Length);
    }

    private async Task<string> LoadChapterAsync(
        Guid fileId,
        EpubReaderDocument document,
        int index,
        CancellationToken cancellationToken)
    {
        if (_chapterText.TryGetValue((fileId, index), out var cached)) return cached;
#if NET10_0_OR_GREATER
        var text = await Task.Run(
            () => new XhtmlChapterLoader().Load(document.Chapters[index], cancellationToken).BodyText,
            cancellationToken);
#else
        await Task.CompletedTask;
        string text = document.Chapters.Count >= 0
            ? throw new NotSupportedException("当前运行环境不支持读取书籍正文。")
            : string.Empty;
#endif
        // Keep the cache bounded; a long book read chapter by chapter
        // would otherwise pin its whole text in memory.
        if (_chapterText.Count > 400) _chapterText.Clear();
        _chapterText[(fileId, index)] = text;
        return text;
    }

    private static string RelativeChapterPath(EpubReaderDocument document, int index) =>
        Path.GetRelativePath(document.RootPath, document.Chapters[index]).Replace('\\', '/');

    private async Task<(BookFile File, PreparedBook Prepared)> PrepareAsync(
        Book book,
        Guid? fileId,
        CancellationToken cancellationToken)
    {
        var file = fileId is { } id
            ? book.Files.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidOperationException("书籍文件不存在。")
            : book.Files
                .Where(candidate => TextFormats.Contains(candidate.Format.ToLowerInvariant()))
                .OrderBy(candidate => Array.IndexOf(TextFormats, candidate.Format.ToLowerInvariant()))
                .FirstOrDefault()
                ?? throw new InvalidOperationException("这本书没有可读取正文的 EPUB、AZW3 或 MOBI 文件。");
        if (!TextFormats.Contains(file.Format.ToLowerInvariant()))
            throw new InvalidOperationException($"暂不支持读取 {file.Format.ToUpperInvariant()} 文件的正文。");

        var lazy = _prepared.GetOrAdd(file.Id, _ => new Lazy<Task<PreparedBook>>(
            () => PrepareCoreAsync(file, CancellationToken.None)));
        try
        {
            return (file, await lazy.Value.WaitAsync(cancellationToken));
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _prepared.TryRemove(new KeyValuePair<Guid, Lazy<Task<PreparedBook>>>(file.Id, lazy));
            throw;
        }
    }

    private async Task<PreparedBook> PrepareCoreAsync(BookFile file, CancellationToken cancellationToken)
    {
        var source = await ResolvePathAsync(file, cancellationToken);
        if (file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
        {
            var hash = string.IsNullOrWhiteSpace(file.Sha256)
                ? await Hashing.Sha256Async(source, cancellationToken)
                : file.Sha256;
            return new PreparedBook(await new EpubReaderPreparationService(_paths).PrepareAsync(source, hash, cancellationToken));
        }

        var temporary = Path.Combine(Path.GetTempPath(), "Kkindle", "plugin-content", Guid.NewGuid().ToString("N") + ".epub");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            await Azw3ReaderService.PrepareEpubAsync(source, temporary, cancellationToken);
            var hash = await Hashing.Sha256Async(temporary, cancellationToken);
            return new PreparedBook(await new EpubReaderPreparationService(_paths).PrepareAsync(temporary, hash, cancellationToken));
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<string> ResolvePathAsync(BookFile file, CancellationToken cancellationToken)
    {
        var path = _library.GetAbsoluteFilePath(file);
        if (File.Exists(path)) return path;
        if (_resolveFile is not null && await _resolveFile(file, cancellationToken) is { } resolved && File.Exists(resolved))
            return resolved;
        throw new FileNotFoundException("书籍文件不在本机，请先在书库中下载。");
    }

    private sealed record PreparedBook(EpubReaderDocument Document);
}

/// <summary>AI completion lent to plugins with the <c>ai</c> permission.</summary>
public interface IPluginAiHost
{
    /// <summary>Whether AI is enabled and configured; plugins can check before offering AI features.</summary>
    Task<PluginAiStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<string> CompleteAsync(PluginAiRequest request, CancellationToken cancellationToken);
}

public sealed record PluginAiStatus(bool Available, string? Provider, string? Model, string? Reason);

public sealed record PluginAiRequest(
    string? System,
    string Prompt,
    IReadOnlyList<(string Role, string Content)> History);

/// <summary>Reader navigation lent to plugin pages.</summary>
public interface IPluginReaderHost
{
    /// <summary>
    /// Opens the book in the reader, optionally at a chapter and character
    /// offset. The plugin window closes first so the reader is visible.
    /// </summary>
    Task OpenAsync(Guid bookId, Guid? fileId, string? chapterPath, int? offset, CancellationToken cancellationToken);
}
