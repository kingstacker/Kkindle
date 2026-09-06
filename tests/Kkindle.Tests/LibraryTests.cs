using System.IO.Compression;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class LibraryTests
{
    [Fact]
    public async Task CreatesCollectionsAndPersistsDraggedBookMembership()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "collection.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([source]);
            var book = Assert.Single(await service.SearchAsync());

            var uncollected = (await service.GetCollectionsAsync())
                .Single(collection => collection.Name == BookLibraryDefaults.UncollectedCollectionName);
            var reading = await service.CreateCollectionAsync("待读");
            var favorites = await service.CreateCollectionAsync("喜欢的书");
            await service.AddBookToCollectionAsync(book.Id, reading.Id);
            await service.AddBookToCollectionAsync(book.Id, favorites.Id);

            var restored = Assert.Single(await service.SearchAsync());
            Assert.Equal(3, restored.CollectionIds.Count);
            Assert.Contains(uncollected.Id, restored.CollectionIds);
            Assert.Contains(reading.Id, restored.CollectionIds);
            Assert.Contains(favorites.Id, restored.CollectionIds);
            Assert.Equal(3, (await service.GetCollectionsAsync()).Count);

            await service.RemoveBookFromCollectionAsync(book.Id, reading.Id);
            await service.DeleteCollectionAsync(favorites.Id);

            restored = Assert.Single(await service.SearchAsync());
            Assert.Equal(uncollected.Id, Assert.Single(restored.CollectionIds));
            Assert.Equal(2, (await service.GetCollectionsAsync()).Count);
            Assert.Contains(uncollected.Id, (await service.GetCollectionsAsync()).Select(collection => collection.Id));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DefaultUncollectedCollectionReceivesImportedBooks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var first = Path.Combine(root, "first.epub");
            var second = Path.Combine(root, "second.epub");
            CreateEpub(first, "first", "第一本");
            CreateEpub(second, "second", "第二本");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();

            var uncollected = Assert.Single(await service.GetCollectionsAsync());
            Assert.Equal(BookLibraryDefaults.UncollectedCollectionName, uncollected.Name);

            await service.ImportAsync([first, second]);
            var books = await service.SearchAsync();
            Assert.Equal(2, books.Count);
            Assert.All(books, book => Assert.Contains(uncollected.Id, book.CollectionIds));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RejectsDuplicateCollectionNamesIgnoringCase()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.CreateCollectionAsync("Science");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateCollectionAsync(" science "));

            Assert.Contains("同名收藏夹", exception.Message);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void PlansDefaultReaderFormatsFromImportedFormat()
    {
        Assert.Equal(
            ["azw3"],
            BookFormatConversionPolicy.GetMissingDefaultReaderFormats(
                [new BookFile { Format = "epub" }]));
        Assert.Equal(
            ["epub"],
            BookFormatConversionPolicy.GetMissingDefaultReaderFormats(
                [new BookFile { Format = "azw3" }]));
        Assert.Equal(
            ["epub", "azw3"],
            BookFormatConversionPolicy.GetMissingDefaultReaderFormats(
                [new BookFile { Format = "mobi" }]));
        Assert.Empty(BookFormatConversionPolicy.GetMissingDefaultReaderFormats(
            [new BookFile { Format = "epub" }, new BookFile { Format = "azw3" }]));
    }

    [Fact]
    public async Task ImportsEpubMetadataCoverAndAvoidsDuplicateHash()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "纸上书.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();

            var first = await service.ImportAsync([source]);
            var second = await service.ImportAsync([source]);
            var books = await service.SearchAsync();

            Assert.Equal(1, first.SuccessCount);
            Assert.Equal(1, second.SuccessCount);
            Assert.True(Assert.Single(first.Items).Added);
            Assert.False(Assert.Single(second.Items).Added);
            Assert.Single(books);
            Assert.Equal("测试书", books[0].Title);
            Assert.Equal("测试作者", books[0].Authors);
            Assert.Equal("一本测试用书", books[0].Description);
            Assert.Single(books[0].Files);
            Assert.Equal("纸上书.epub", Path.GetFileName(books[0].Files[0].RelativePath));
            Assert.NotNull(books[0].CoverPath);
            Assert.True(File.Exists(service.GetAbsoluteFilePath(books[0].Files[0])));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SearchMatchesTitleAndTags()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "search.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            var imported = await service.ImportAsync([source]);
            var book = (await service.SearchAsync()).Single();
            book.Tags = "阅读,测试";
            await service.UpdateMetadataAsync(book);

            Assert.Single(await service.SearchAsync("测试"));
            Assert.Empty(await service.SearchAsync("不存在"));
            Assert.Equal(1, imported.SuccessCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task AddsConvertedFormatToExistingBookWithoutCreatingDuplicateBook()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "原书.epub");
            CreateEpub(source);
            var converted = Path.Combine(root, "测试书.pdf");
            await File.WriteAllBytesAsync(converted, [1, 2, 3, 4]);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([source]);
            var book = Assert.Single(await service.SearchAsync());

            var added = await service.AddFileToBookAsync(book.Id, converted);
            var books = await service.SearchAsync();

            Assert.Equal(book.Id, added.BookId);
            Assert.Single(books);
            Assert.Equal(2, books[0].Files.Count);
            Assert.Contains(books[0].Files, file => file.Format == "pdf");
            Assert.All(books[0].Files, file => Assert.True(File.Exists(service.GetAbsoluteFilePath(file))));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DeletesOnlySelectedFormatAndRemovesBookAfterLastFormat()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "原书.epub");
            CreateEpub(source);
            var converted = Path.Combine(root, "原书.pdf");
            await File.WriteAllBytesAsync(converted, [1, 2, 3, 4]);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([source]);
            var book = Assert.Single(await service.SearchAsync());
            var pdf = await service.AddFileToBookAsync(book.Id, converted);
            var pdfPath = service.GetAbsoluteFilePath(pdf);

            await service.DeleteFileAsync(book.Id, pdf.Id);

            var remaining = Assert.Single(await service.SearchAsync());
            Assert.Single(remaining.Files);
            Assert.Equal("epub", remaining.Files[0].Format);
            Assert.False(File.Exists(pdfPath));

            var epub = remaining.Files[0];
            var epubPath = service.GetAbsoluteFilePath(epub);
            await service.DeleteFileAsync(remaining.Id, epub.Id);

            Assert.Empty(await service.SearchAsync());
            Assert.False(File.Exists(epubPath));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BatchImportReportsMetadataFailureAndContinuesWithOtherFiles()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var broken = Path.Combine(root, "损坏.epub");
            var valid = Path.Combine(root, "正常.epub");
            CreateEpub(broken, "broken");
            CreateEpub(valid, "valid");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var metadata = new SelectiveFailureMetadataService("损坏.epub", new BookMetadataService());
            var service = new SqliteBookLibraryService(paths, metadata);
            await service.InitializeAsync();

            var result = await service.ImportAsync([broken, valid]);
            var book = Assert.Single(await service.SearchAsync());

            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Contains(result.Items, item => item.SourcePath == broken && !item.Succeeded);
            Assert.Contains(result.Items, item => item.SourcePath == valid && item.Succeeded);
            Assert.Equal("测试书", book.Title);
            Assert.Empty(Directory.EnumerateFiles(paths.Library, "*.part", SearchOption.AllDirectories));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SameBookWithSameSourceNameKeepsBothFilesUsingNumberedName()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var firstDirectory = Path.Combine(root, "first");
            var secondDirectory = Path.Combine(root, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var first = Path.Combine(firstDirectory, "同名.epub");
            var second = Path.Combine(secondDirectory, "同名.epub");
            CreateEpub(first, "first");
            CreateEpub(second, "second");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();

            var result = await service.ImportAsync([first, second]);
            var book = Assert.Single(await service.SearchAsync());
            var importedNames = book.Files
                .Select(file => Path.GetFileName(file.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(2, book.Files.Count);
            Assert.Contains("同名.epub", importedNames);
            Assert.Contains("同名 (2).epub", importedNames);
            Assert.All(book.Files, file => Assert.True(File.Exists(service.GetAbsoluteFilePath(file))));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ImportConflictResolverCanKeepSameTitleAsSeparateEdition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var first = Path.Combine(root, "first.epub");
            var second = Path.Combine(root, "second.epub");
            CreateEpub(first, "first");
            CreateEpub(second, "second");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([first]);

            var seen = new List<ImportBookConflict>();
            var result = await service.ImportAsync(
                [second],
                conflictResolver: conflict =>
                {
                    seen.Add(conflict);
                    return Task.FromResult(ImportConflictResolution.KeepSeparate);
                });

            Assert.Single(seen);
            Assert.Equal("second.epub", Path.GetFileName(seen[0].SourcePath));
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(2, (await service.SearchAsync()).Count);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DeletedBookAndFormatCanBeRestoredFromTrash()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "原书.epub");
            CreateEpub(source);
            var converted = Path.Combine(root, "原书.pdf");
            await File.WriteAllBytesAsync(converted, [1, 2, 3, 4]);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([source]);
            var book = Assert.Single(await service.SearchAsync());
            var pdf = await service.AddFileToBookAsync(book.Id, converted);
            var pdfPath = service.GetAbsoluteFilePath(pdf);
            var coverPath = Path.Combine(paths.Data, book.CoverPath!);

            await service.DeleteFileAsync(book.Id, pdf.Id);
            var formatTrash = Assert.Single(await service.GetTrashItemsAsync());
            Assert.Equal(LibraryTrashItemKind.File, formatTrash.Kind);
            Assert.False(File.Exists(pdfPath));
            await service.RestoreTrashItemAsync(formatTrash.Id);
            var restoredWithFormat = Assert.Single(await service.SearchAsync());
            Assert.Equal(2, restoredWithFormat.Files.Count);
            Assert.True(File.Exists(service.GetAbsoluteFilePath(
                restoredWithFormat.Files.Single(file => file.Id == pdf.Id))));

            var epubPath = service.GetAbsoluteFilePath(restoredWithFormat.Files.Single(file => file.Format == "epub"));
            await service.DeleteAsync(restoredWithFormat.Id);
            var bookTrash = Assert.Single(await service.GetTrashItemsAsync());
            Assert.Equal(LibraryTrashItemKind.Book, bookTrash.Kind);
            Assert.Empty(await service.SearchAsync());
            Assert.False(File.Exists(epubPath));
            Assert.False(File.Exists(coverPath));

            await service.RestoreTrashItemAsync(bookTrash.Id);
            var restoredBook = Assert.Single(await service.SearchAsync());
            Assert.Equal(book.Id, restoredBook.Id);
            Assert.Equal(2, restoredBook.Files.Count);
            Assert.All(restoredBook.Files, file => Assert.True(File.Exists(service.GetAbsoluteFilePath(file))));
            Assert.True(File.Exists(coverPath));
            Assert.Empty(await service.GetTrashItemsAsync());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PurgingTrashItemRemovesItsManagedContent()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "清理.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([source]);
            var book = Assert.Single(await service.SearchAsync());
            await service.DeleteAsync(book.Id);
            var trash = Assert.Single(await service.GetTrashItemsAsync());

            await service.PurgeTrashItemAsync(trash.Id);

            Assert.Empty(await service.GetTrashItemsAsync());
            Assert.Empty(Directory.EnumerateFiles(paths.Trash, "*", SearchOption.AllDirectories));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CancellationCleansPartialImportAndLeavesLibraryEmpty()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "取消导入.pdf");
            await File.WriteAllBytesAsync(source, new byte[8 * 1024 * 1024]);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            using var cancellation = new CancellationTokenSource();
            var progress = new TestHelpers.InlineProgress<TransferProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ImportAsync([source], progress, cancellation.Token));

            Assert.Empty(await service.SearchAsync());
            Assert.Empty(Directory.EnumerateFiles(paths.Library, "*.part", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(paths.Library, "*", SearchOption.AllDirectories));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void RejectsBookFilePathOutsideManagedDataDirectory()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            var outside = Path.Combine(root, "outside.epub");
            var relativeOutsidePath = Path.GetRelativePath(paths.Data, outside);

            Assert.Throws<InvalidOperationException>(() =>
                service.GetAbsoluteFilePath(new BookFile { RelativePath = relativeOutsidePath }));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FallbackMetadataCleansHashBeforeDownloadSourceMarker()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "纸上作品_0123456789ABCDEF0123456789ABCDEF (Z-Library).pdf");
            await File.WriteAllTextAsync(source, "not a parsed PDF");

            var metadata = await new BookMetadataService().ReadMetadataAsync(source);

            Assert.Equal("纸上作品", metadata.Title);
            Assert.Equal("未知作者", metadata.Authors);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static void CreateEpub(string path, string? uniqueMarker = null, string? title = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{{(title ?? "测试书")}}</dc:title>
                <dc:creator>测试作者</dc:creator>
                <dc:description>一本测试用书</dc:description>
                <meta name="cover" content="cover" />
              </metadata>
              <manifest><item id="cover" href="cover.jpg" media-type="image/jpeg" /></manifest>
            </package>
            """);
        if (uniqueMarker is not null)
            TestHelpers.AddZipEntry(archive, "OEBPS/test-marker.txt", uniqueMarker);
        var cover = archive.CreateEntry("OEBPS/cover.jpg");
        using var stream = cover.Open();
        stream.Write([1, 2, 3, 4]);
    }


    private sealed class SelectiveFailureMetadataService(
        string failingFileName,
        IMetadataService inner) : IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        {
            return string.Equals(Path.GetFileName(path), failingFileName, StringComparison.Ordinal)
                ? Task.FromException<BookMetadata>(new InvalidDataException("图书文件已损坏。"))
                : inner.ReadMetadataAsync(path, cancellationToken);
        }
    }
}
