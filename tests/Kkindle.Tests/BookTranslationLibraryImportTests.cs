using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class BookTranslationLibraryImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrPartialImportKeepsOldTranslationsAndAnnotations(bool partial)
    {
        using var book = new TranslationTestBook("<p>Original.</p>");
        var oldTranslation = book.Create("old-译文.epub", "<p>旧译文。</p>");
        var oldBilingual = book.Create("old-双语.epub", "<p>Original. 旧译文。</p>");
        var newTranslation = book.Create("new-译文.epub", "<p>新译文。</p>");
        var newBilingual = book.Create("new-双语.epub", "<p>Original. 新译文。</p>");
        var metadata = new ControlledMetadata();
        var library = new SqliteBookLibraryService(book.Paths, metadata);
        await library.InitializeAsync();
        await library.ImportAsync([book.Source, oldTranslation, oldBilingual]);
        var stored = Assert.Single(await library.SearchAsync());
        var oldFiles = stored.Files.Where(file => file.RelativePath.Contains("old-", StringComparison.Ordinal)).ToArray();
        var reader = new ReaderDataService(book.Paths);
        await reader.InitializeAsync();
        foreach (var file in oldFiles) await reader.SaveAnnotationAsync(Note(file));
        metadata.Fail = path => !partial || path == newBilingual;

        var result = await BookTranslationLibraryImport.ImportAsync(library,
            new Dictionary<string, IReadOnlyList<BookFile>>
            {
                [newTranslation] = oldFiles.Where(file => file.RelativePath.Contains("译文", StringComparison.Ordinal)).ToArray(),
                [newBilingual] = oldFiles.Where(file => file.RelativePath.Contains("双语", StringComparison.Ordinal)).ToArray()
            });

        Assert.Equal(partial ? 1 : 2, result.FailureCount);
        Assert.Equal(partial ? 1 : 0, result.SuccessCount);
        var remaining = (await library.GetBookAsync(stored.Id))!.Files;
        foreach (var file in oldFiles)
        {
            Assert.Contains(remaining, item => item.Id == file.Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(file)));
        }
        Assert.Equal(oldFiles.Select(file => file.Id).Order(), (await reader.GetAllAnnotationsAsync()).Select(note => note.BookFileId).Order());
        Assert.Empty(await library.GetTrashItemsAsync());
    }

    [Fact]
    public async Task CancellationBeforeImportCompletesKeepsOldFileAndNotes()
    {
        using var book = new TranslationTestBook("<p>Original.</p>");
        var oldPath = book.Create("old-译文.epub", "<p>旧译文。</p>");
        var newPath = book.Create("new-译文.epub", "<p>新译文。</p>");
        var metadata = new ControlledMetadata();
        var library = new SqliteBookLibraryService(book.Paths, metadata);
        await library.InitializeAsync();
        await library.ImportAsync([book.Source, oldPath]);
        var stored = Assert.Single(await library.SearchAsync());
        var oldFile = stored.Files.Single(file => file.RelativePath.Contains("old-", StringComparison.Ordinal));
        var reader = new ReaderDataService(book.Paths);
        await reader.InitializeAsync();
        await reader.SaveAnnotationAsync(Note(oldFile));
        using var cancellation = new CancellationTokenSource();
        metadata.BeforeRead = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BookTranslationLibraryImport.ImportAsync(library,
            new Dictionary<string, IReadOnlyList<BookFile>> { [newPath] = [oldFile] }, cancellation.Token));
        Assert.Contains((await library.GetBookAsync(stored.Id))!.Files, file => file.Id == oldFile.Id);
        Assert.Equal(oldFile.Id, Assert.Single(await reader.GetAllAnnotationsAsync()).BookFileId);
        Assert.Empty(await library.GetTrashItemsAsync());
    }

    [Fact]
    public async Task DuplicateImportDoesNotDeleteTheExistingFileOrItsNotes()
    {
        using var book = new TranslationTestBook("<p>Original.</p>");
        var oldPath = book.Create("old-译文.epub", "<p>旧译文。</p>");
        var library = new SqliteBookLibraryService(book.Paths, new ControlledMetadata());
        await library.InitializeAsync();
        await library.ImportAsync([book.Source, oldPath]);
        var stored = Assert.Single(await library.SearchAsync());
        var oldFile = stored.Files.Single(file => file.RelativePath.Contains("old-", StringComparison.Ordinal));
        var reader = new ReaderDataService(book.Paths);
        await reader.InitializeAsync();
        await reader.SaveAnnotationAsync(Note(oldFile));
        var result = await BookTranslationLibraryImport.ImportAsync(library,
            new Dictionary<string, IReadOnlyList<BookFile>> { [oldPath] = [oldFile] });
        Assert.False(Assert.Single(result.Items).Added);
        Assert.Contains((await library.GetBookAsync(stored.Id))!.Files, file => file.Id == oldFile.Id);
        Assert.Equal(oldFile.Id, Assert.Single(await reader.GetAllAnnotationsAsync()).BookFileId);
        Assert.Empty(await library.GetTrashItemsAsync());
    }

    [Fact]
    public async Task SuccessfulImportRetiresOldVersionAfterNewFileExists()
    {
        using var book = new TranslationTestBook("<p>Original.</p>");
        var oldPath = book.Create("old-译文.epub", "<p>旧译文。</p>");
        var newPath = book.Create("new-译文.epub", "<p>新译文。</p>");
        var library = new SqliteBookLibraryService(book.Paths, new ControlledMetadata());
        await library.InitializeAsync();
        await library.ImportAsync([book.Source, oldPath]);
        var stored = Assert.Single(await library.SearchAsync());
        var oldFile = stored.Files.Single(file => file.RelativePath.Contains("old-", StringComparison.Ordinal));
        var result = await BookTranslationLibraryImport.ImportAsync(library,
            new Dictionary<string, IReadOnlyList<BookFile>> { [newPath] = [oldFile] });
        Assert.True(Assert.Single(result.Items).Added);
        var remaining = (await library.GetBookAsync(stored.Id))!.Files;
        Assert.DoesNotContain(remaining, file => file.Id == oldFile.Id);
        var newFile = Assert.Single(remaining, file => file.RelativePath.Contains("new-", StringComparison.Ordinal));
        Assert.Equal(await Hashing.Sha256Async(newPath), newFile.Sha256);
        Assert.True(File.Exists(library.GetAbsoluteFilePath(newFile)));
        Assert.Single(await library.GetTrashItemsAsync());
    }

    private static ReaderAnnotation Note(BookFile file) => new()
    {
        BookId = file.BookId, BookFileId = file.Id, ChapterPath = "OEBPS/chapter.xhtml",
        SelectedText = "旧译文", EndOffset = 3, Note = "已有笔记"
    };

    private sealed class ControlledMetadata : IMetadataService
    {
        public Func<string, bool>? Fail { get; set; }
        public Action? BeforeRead { get; set; }
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail?.Invoke(path) == true) throw new InvalidDataException("Simulated import failure");
            return Task.FromResult(new BookMetadata { Title = "Translation regression", Authors = "Test author" });
        }
    }
}
