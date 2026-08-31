using System.IO.Compression;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Kkindle.Tests;

public sealed class ProductivityFeatureTests
{
    [Fact]
    public async Task SettingsRoundTripAndNormalizeInvalidValues()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new AppSettingsStore(paths);
            await store.SaveAsync(new AppSettings
            {
                OnboardingCompleted = true,
                DefaultDeviceModel = "  Kindle Paperwhite  ",
                PreferredOpenFormat = ".MOBI",
                CalibrePath = "  C:\\Calibre  ",
                AutoBackupEnabled = true,
                AutoGenerateEpubAndAzw3OnImport = true,
                AutoBackupRetention = 99,
                AiEnabled = false,
                NetworkEnabled = false,
                AutoUpdateCheckEnabled = false,
                AutoConnectDevice = true,
                CompareKindleLibraryEnabled = false,
                ReaderVerticalDebugBoxesEnabled = true,
                LastAutoUpdateCheckAt = new DateTimeOffset(2026, 8, 23, 9, 30, 0, TimeSpan.FromHours(8)),
                PendingUpdateVersion = "1.2.3",
                PendingUpdateReleaseNotes = "修复阅读器翻页问题",
                PendingUpdatePackagePath = Path.Combine(root, "update.exe"),
                PendingUpdateDownloadedAt = new DateTimeOffset(2026, 8, 23, 9, 35, 0, TimeSpan.FromHours(8)),
                DefaultReaderLayout = new ReaderLayoutSettings(FontScale: 9, LineHeight: -1)
            });

            var restored = await store.LoadAsync();
            Assert.True(restored.OnboardingCompleted);
            Assert.Equal("Kindle Paperwhite", restored.DefaultDeviceModel);
            Assert.Equal("mobi", restored.PreferredOpenFormat);
            Assert.Equal("C:\\Calibre", restored.CalibrePath);
            Assert.Equal(30, restored.AutoBackupRetention);
            Assert.True(restored.AutoGenerateEpubAndAzw3OnImport);
            Assert.False(restored.AiEnabled);
            Assert.False(restored.NetworkEnabled);
            Assert.False(restored.AutoUpdateCheckEnabled);
            Assert.True(restored.AutoConnectDevice);
            Assert.False(restored.CompareKindleLibraryEnabled);
            Assert.True(restored.ReaderVerticalDebugBoxesEnabled);
            Assert.Equal(new DateTimeOffset(2026, 8, 23, 9, 30, 0, TimeSpan.FromHours(8)), restored.LastAutoUpdateCheckAt);
            Assert.Equal("1.2.3", restored.PendingUpdateVersion);
            Assert.Equal("修复阅读器翻页问题", restored.PendingUpdateReleaseNotes);
            Assert.Equal(Path.Combine(root, "update.exe"), restored.PendingUpdatePackagePath);
            Assert.Equal(new DateTimeOffset(2026, 8, 23, 9, 35, 0, TimeSpan.FromHours(8)), restored.PendingUpdateDownloadedAt);
            Assert.Equal(restored, store.LoadSynchronously());

            // Defaults start clean; a fresh install has never checked for updates.
            var freshSettings = new AppSettings();
            Assert.False(freshSettings.OnboardingCompleted);
            Assert.Null(freshSettings.LastAutoUpdateCheckAt);
            Assert.Null(freshSettings.PendingUpdateVersion);
            Assert.Null(freshSettings.PendingUpdatePackagePath);
            Assert.Null(freshSettings.PendingUpdateDownloadedAt);
            Assert.InRange(restored.DefaultReaderLayout.FontScale, 0.75, 2.0);
            Assert.InRange(restored.DefaultReaderLayout.LineHeight, 1.2, 2.8);

            await store.SaveAsync(restored with { AutoConnectDevice = false });
            Assert.False((await store.LoadAsync()).AutoConnectDevice);

            await File.WriteAllTextAsync(paths.Settings, "{ invalid json");
            var defaults = await store.LoadAsync();
            Assert.Equal("epub", defaults.PreferredOpenFormat);
            Assert.False(defaults.AutoGenerateEpubAndAzw3OnImport);
            Assert.True(defaults.CollectionsMutuallyExclusive);
            Assert.True(defaults.AutoConnectDevice);
            Assert.True(defaults.CompareKindleLibraryEnabled);
            Assert.True(defaults.AutoUpdateCheckEnabled);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DictionaryImportsLooksUpAndRemovesEntries()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "dictionary.txt");
            await File.WriteAllTextAsync(source, "# comment\nKindle\t电子阅读器\nbook=书\ninvalid\n");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new DictionaryService(paths);

            var imported = await service.ImportAsync(source, "测试词典");
            Assert.Equal(2, imported.EntryCount);
            var match = Assert.Single(await service.LookupAsync("kindle"));
            Assert.Equal("电子阅读器", match.Definition);
            Assert.Equal("测试词典", match.DictionaryName);

            await service.RemoveAsync(imported.Id);
            Assert.Empty(await service.ListAsync());
            Assert.Empty(await service.LookupAsync("book"));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task KindleDictionaryConvertsImportsAndLooksUpEntries()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "dictionary.azw");
            await File.WriteAllTextAsync(source, "fake Kindle dictionary");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new DictionaryService(paths, new FakeKindleDictionaryConverter());

            var imported = await service.ImportAsync(source, "Kindle 测试词典");

            Assert.Equal(2, imported.EntryCount);
            var kindle = Assert.Single(await service.LookupAsync("kindle"));
            Assert.Contains("电子阅读器", kindle.Definition.Replace(Environment.NewLine, string.Empty));
            Assert.Equal("Kindle 测试词典", kindle.DictionaryName);
            Assert.Equal("书", Assert.Single(await service.LookupAsync("book")).Definition);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PrcDictionaryUsesKindleDictionaryImportPipeline()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "dictionary.prc");
            await File.WriteAllTextAsync(source, "fake PRC Kindle dictionary");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new DictionaryService(paths, new FakeKindleDictionaryConverter());

            var imported = await service.ImportAsync(source, "PRC 测试词典");

            Assert.Equal(2, imported.EntryCount);
            Assert.Equal("PRC 测试词典", imported.Name);
            Assert.Equal("电子阅读器", Assert.Single(await service.LookupAsync("KINDLE")).Definition.Replace(Environment.NewLine, string.Empty));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FontLibraryCopiesListsAndRemovesManagedFont()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "Reading Font.ttf");
            await File.WriteAllBytesAsync(source, [0, 1, 2, 3]);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new FontLibraryService(paths);

            var imported = await service.ImportAsync(source);
            Assert.True(File.Exists(service.GetAbsolutePath(imported)));
            Assert.Equal(imported.Id, Assert.Single(await service.ListAsync()).Id);

            await service.RemoveAsync(imported.Id);
            Assert.Empty(await service.ListAsync());
            Assert.False(File.Exists(service.GetAbsolutePath(imported)));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Path.ChangeExtension(source, ".exe")));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void PdfSearchReturnsPageExcerptAndHonorsLimit()
    {
        IReadOnlyList<PdfPageText> pages =
        [
            new(1, "A short introduction."),
            new(2, "Kindle search then Kindle again."),
            new(3, "No match here.")
        ];

        var result = PdfTextService.Search(pages, "kindle", 1);
        var match = Assert.Single(result);
        Assert.Equal(2, match.PageNumber);
        Assert.Contains("Kindle search", match.Excerpt);
        Assert.Empty(PdfTextService.Search(pages, "   "));
    }

    [Fact]
    public async Task PdfTextServiceExtractsRealPdfPages()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "sample.pdf");
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(PageSize.A4).AddText("First searchable page", 12, new PdfPoint(25, 700), font);
            builder.AddPage(PageSize.A4).AddText("Second page", 12, new PdfPoint(25, 700), font);
            await File.WriteAllBytesAsync(path, builder.Build());

            var pages = await new PdfTextService().ExtractAsync(path);
            Assert.Equal(2, pages.Count);
            Assert.Contains("First searchable page", pages[0].Text);
            Assert.Equal(1, Assert.Single(PdfTextService.Search(pages, "searchable")).PageNumber);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void RootConfigurationPersistsAndFallsBackOnCorruption()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var app = Path.Combine(root, "app");
            var data = Path.Combine(root, "relocated");
            Directory.CreateDirectory(app);
            AppRootConfiguration.Save(app, data);
            Assert.Equal(Path.GetFullPath(data), AppRootConfiguration.ResolveRoot(app));

            File.WriteAllText(Path.Combine(app, "app-root.json"), "broken");
            Assert.Equal(Path.GetFullPath(app), AppRootConfiguration.ResolveRoot(app));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void RootConfigurationSupportsSeparatePlatformFallback()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var configuration = Path.Combine(root, "config");
            var defaultData = Path.Combine(root, "data");
            Assert.Equal(Path.GetFullPath(defaultData), AppRootConfiguration.ResolveRoot(configuration, defaultData));
            var relocated = Path.Combine(root, "relocated");
            AppRootConfiguration.Save(configuration, relocated);
            Assert.Equal(Path.GetFullPath(relocated), AppRootConfiguration.ResolveRoot(configuration, defaultData));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task LibraryPersistsProductivityAndDoubanMetadata()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "sample.pdf");
            await File.WriteAllTextAsync(source, "test");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            await service.ImportAsync([source]);
            var book = Assert.Single(await service.SearchAsync());
            book.Category = "技术";
            book.IsFavorite = true;
            book.ReadingStatus = LibraryReadingStatus.Finished;
            book.Publisher = "商务印书馆";
            book.PublishDate = "2004-1";
            book.Isbn = "9787100040945";
            book.PageCount = "376";
            book.Binding = "平装";
            book.DoubanRating = 8.3;
            book.DoubanRatingCount = 81342;
            await service.UpdateMetadataAsync(book);

            var restored = Assert.Single(await service.SearchAsync());
            Assert.Equal("技术", restored.Category);
            Assert.True(restored.IsFavorite);
            Assert.Equal(LibraryReadingStatus.Finished, restored.ReadingStatus);
            Assert.Equal("商务印书馆", restored.Publisher);
            Assert.Equal("2004-1", restored.PublishDate);
            Assert.Equal("9787100040945", restored.Isbn);
            Assert.Equal("376", restored.PageCount);
            Assert.Equal("平装", restored.Binding);
            Assert.Equal(8.3, restored.DoubanRating);
            Assert.Equal(81342, restored.DoubanRatingCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ExistingLibrarySchemaIsMigratedWithoutLosingBooks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Books (
                        Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Authors TEXT NOT NULL,
                        Series TEXT NULL, SeriesIndex REAL NULL, Description TEXT NULL,
                        Tags TEXT NOT NULL DEFAULT '', CoverPath TEXT NULL,
                        CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                    CREATE TABLE BookFiles (
                        Id TEXT PRIMARY KEY, BookId TEXT NOT NULL, Format TEXT NOT NULL,
                        RelativePath TEXT NOT NULL, Size INTEGER NOT NULL, Sha256 TEXT NOT NULL UNIQUE);
                    INSERT INTO Books (Id, Title, Authors, Tags, CreatedAt, UpdatedAt)
                    VALUES ($id, 'Legacy', 'Author', '', $now, $now);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            var book = Assert.Single(await service.SearchAsync());
            Assert.Equal("Legacy", book.Title);
            Assert.Equal(string.Empty, book.Category);
            Assert.False(book.IsFavorite);
            Assert.Equal(LibraryReadingStatus.Unread, book.ReadingStatus);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DashboardAggregatesReadingAndProductivityData()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new ReaderDataService(new AppPaths(Path.Combine(root, "app")));
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            await service.AddReadingTimeAsync(bookId, fileId, 120, 100, 1, 1);
            await service.SaveBookmarkAsync(new ReaderBookmark
            {
                BookId = bookId, BookFileId = fileId, ChapterPath = "pdf:1", Title = "第 1 页"
            });
            await service.SaveAnnotationAsync(new ReaderAnnotation
            {
                BookId = bookId, BookFileId = fileId, ChapterPath = "pdf:1",
                SelectedText = "note", EndOffset = 4
            });

            var dashboard = await service.GetReadingDashboardAsync();
            Assert.Equal(1, dashboard.BooksStarted);
            Assert.Equal(1, dashboard.BooksFinished);
            Assert.Equal(120, dashboard.TotalSeconds);
            Assert.Equal(100, dashboard.AverageProgress);
            Assert.Equal(1, dashboard.BookmarkCount);
            Assert.Equal(1, dashboard.AnnotationCount);
            Assert.Equal(fileId, Assert.Single(dashboard.RecentBooks).BookFileId);
            Assert.Equal(120, dashboard.DailyReading.Sum(day => day.ActiveSeconds));
            Assert.Equal(14, dashboard.DailyReading.Count);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private sealed class FakeKindleDictionaryConverter : IBookFormatConverter
    {
        public Task ConvertAsync(
            string sourcePath,
            string destinationPath,
            IProgress<FormatConversionProgress>? progress = null,
            CancellationToken cancellationToken = default,
            FormatConversionMetadata? metadata = null)
        {
            using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
            TestHelpers.AddZipEntry(archive, "OEBPS/dictionary.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:idx="https://kindlegen.s3.amazonaws.com/AmazonKindlePublishingGuidelines.pdf">
                  <body>
                    <idx:entry name="default">
                      <idx:orth value="Kindle">Kindle</idx:orth>
                      <p><b>电子</b>阅读器</p>
                    </idx:entry>
                    <dl><dt>book</dt><dd>书</dd></dl>
                  </body>
                </html>
                """);
            return Task.CompletedTask;
        }
    }
}
