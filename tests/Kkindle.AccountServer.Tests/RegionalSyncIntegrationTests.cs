using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    [Fact]
    public async Task RegionalServersKeepSameUsernameTokensAndObjectsSeparateEvenWithSameDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-region-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var china = await LocalServer.CreateAsync(sharedDirectory: root);
            await using var international = await LocalServer.CreateAsync(region: AccountRegion.International, sharedDirectory: root);
            var chinese = await RegisterAsync(china.Http, "test_reader");
            var foreign = await RegisterAsync(international.Http, "test_reader");
            Assert.NotEqual(chinese.User.Id, foreign.User.Id);
            Assert.Equal(AccountRegion.China, chinese.Region);
            Assert.Equal(AccountRegion.International, foreign.Region);
            using (var cross = await AuthorizedAsync(international.Http, HttpMethod.Get, "session", chinese.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
            using (var cross = await AuthorizedAsync(china.Http, HttpMethod.Get, "session", foreign.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
            var other = await RegisterAsync(china.Http, "another_reader");
            const string key = "regions/china/kkindle/objects/test.bin";
            using (var write = await ObjectRequestAsync(china.Http, HttpMethod.Put, key, chinese.Token, "fictional-China-data"))
                Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
            using (var same = await ObjectRequestAsync(china.Http, HttpMethod.Get, key, chinese.Token))
                Assert.Equal("fictional-China-data", await same.Content.ReadAsStringAsync());
            using (var cross = await ObjectRequestAsync(international.Http, HttpMethod.Get, key, foreign.Token))
                Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
            using (var someoneElse = await ObjectRequestAsync(china.Http, HttpMethod.Get, key, other.Token))
                Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode);
            using (var cross = await ObjectRequestAsync(international.Http, HttpMethod.Put, key, chinese.Token, "fictional-bad-upload"))
                Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
            using var wrongHeader = new HttpRequestMessage(HttpMethod.Get, "api/accounts/session");
            wrongHeader.Headers.Add("X-Kkindle-Region", "international");
            wrongHeader.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chinese.Token);
            using var rejected = await china.Http.SendAsync(wrongHeader);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.True(File.Exists(Path.Combine(china.RegionDirectory, "accounts.db")));
            Assert.True(File.Exists(Path.Combine(international.RegionDirectory, "accounts.db")));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task TwoLocalDevicesSyncRealBooksAnnotationsAndAdditiveReadingDataWithinOneRegion(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var firstBase = new AppPaths(Path.Combine(server.Directory, "client-a"));
        var secondBase = new AppPaths(Path.Combine(server.Directory, "client-b"));
        var protector = new TestProtector();
        using var firstAccount = new AccountService(firstBase, protector, dataRegion: region);
        using var secondAccount = new AccountService(secondBase, protector, dataRegion: region);
        await firstAccount.SelectRegionAsync(region);
        await secondAccount.SelectRegionAsync(region);
        await firstAccount.SignInAsync(server.Url, "test_reader", TestPassword, register: true);
        await secondAccount.SignInAsync(server.Url, "test_reader", TestPassword);
        var firstPaths = AccountRegionPaths.ForRegion(firstBase, region);
        var secondPaths = AccountRegionPaths.ForRegion(secondBase, region);
        var firstLibrary = new SqliteBookLibraryService(firstPaths, new BookMetadataService());
        var secondLibrary = new SqliteBookLibraryService(secondPaths, new BookMetadataService());
        await firstLibrary.InitializeAsync(); await secondLibrary.InitializeAsync();
        var firstReader = new ReaderDataService(firstPaths); var secondReader = new ReaderDataService(secondPaths);
        await firstReader.InitializeAsync(); await secondReader.InitializeAsync();
        var book = await ImportTestBookAsync(firstLibrary, server.Directory, region.ToString());
        var file = Assert.Single(book.Files);
        await firstReader.AddReadingTimeAsync(book.Id, file.Id, 120, 10, 1, 10);
        await firstReader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(), BookId = book.Id, BookFileId = file.Id, ChapterPath = "chapter.xhtml",
            SelectedText = "fictional regional quote", Note = "fictional regional note", StartOffset = 0, EndOffset = 5
        });
        var firstSync = new S3SyncService(firstPaths, protector, firstAccount, region);
        var secondSync = new S3SyncService(secondPaths, protector, secondAccount, region);
        var settings = await firstAccount.CreateSyncSettingsAsync(new S3SyncSettings
        { Enabled = true, DownloadBookFilesOnSync = true, EncryptionKey = "fictional-regional-sync-test-key" });
        var firstDevice = Guid.NewGuid().ToString("N"); var secondDevice = Guid.NewGuid().ToString("N");
        await firstSync.SaveSettingsAsync(firstDevice, settings);
        var persisted = await firstSync.LoadSettingsAsync();
        Assert.Equal(region, persisted.Settings.AccountRegion);
        Assert.Equal(settings.AccountUserId, persisted.Settings.AccountUserId);
        Assert.Equal(server.Url, persisted.Settings.AccountServerUrl);
        await firstSync.TestConnectionAsync(settings);
        Assert.False((await firstSync.SyncAsync(firstDevice, settings)).IsPartial);
        var pull = await secondSync.SyncAsync(secondDevice, settings);
        Assert.False(pull.IsPartial); Assert.Equal(1, pull.BooksAdded); Assert.Equal(1, pull.FilesDownloaded);
        var downloaded = Assert.Single(await secondLibrary.SearchAsync());
        Assert.Equal(book.Id, downloaded.Id);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(firstPaths.Data, file.RelativePath)),
            await File.ReadAllBytesAsync(Path.Combine(secondPaths.Data, Assert.Single(downloaded.Files).RelativePath)));
        await using (var connection = new SqliteConnection($"Data Source={secondPaths.Database}"))
        {
            await connection.OpenAsync();
            using var query = connection.CreateCommand(); query.CommandText = "SELECT Note FROM ReaderAnnotations;";
            Assert.Equal("fictional regional note", await query.ExecuteScalarAsync());
        }
        Assert.Equal(120, (await secondReader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        await secondReader.AddReadingTimeAsync(book.Id, file.Id, 60, 20, 2, 10);
        await secondSync.SyncAsync(secondDevice, settings);
        await firstSync.SyncAsync(firstDevice, settings);
        Assert.Equal(180, (await firstReader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        await firstSync.SyncAsync(firstDevice, settings);
        Assert.Equal(180, (await firstReader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        var token = await ReadStoredTokenAsync(firstBase, protector);
        using var list = await ObjectRequestAsync(server.Http, HttpMethod.Get, "", token);
        var objects = (await list.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSyncObjectMetadataArray))!;
        Assert.NotEmpty(objects);
        Assert.All(objects, item => Assert.StartsWith("regions/" + AccountRegions.Slug(region) + "/", item.Key));
        var secretFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.EncryptionKey))).ToLowerInvariant();
        Assert.DoesNotContain(objects, item => item.Key.Contains(settings.EncryptionKey, StringComparison.Ordinal)
            || item.Key.Contains(secretFingerprint, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(objects, item => item.Key.Contains(".kkindle-connection-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingRegionsRestoresIndependentConnectionsAndBlocksOldLibrarySyncUntilReopen()
    {
        await using var china = await LocalServer.CreateAsync();
        await using var international = await LocalServer.CreateAsync(region: AccountRegion.International);
        var basePaths = new AppPaths(Path.Combine(china.Directory, "client"));
        var protector = new TestProtector();
        ApplicationModules Open() => new(new ApplicationModuleOptions
        {
            Paths = basePaths, SecretProtector = protector, FormatConverter = new UnavailableBookFormatConverter(), MetadataService = new BookMetadataService()
        });
        using var firstRun = Open();
        var chinaStatus = await firstRun.Accounts.Service.SignInAsync(china.Url, "test_reader", TestPassword, register: true);
        await firstRun.Library.Books.InitializeAsync();
        await firstRun.Reader.Data.InitializeAsync();
        await ImportTestBookAsync((SqliteBookLibraryService)firstRun.Library.Books, china.Directory, "China only");
        var chineseSettings = await firstRun.Accounts.Service.CreateSyncSettingsAsync(new S3SyncSettings
            { EncryptionKey = "fictional-region-switch-sync-key" });
        var switched = await firstRun.Accounts.Service.SelectRegionAsync(AccountRegion.International);
        Assert.False(switched.IsLoggedIn); Assert.True(switched.RequiresDataRestart);
        var foreignStatus = await firstRun.Accounts.Service.SignInAsync(international.Url, "test_reader", TestPassword, register: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstRun.Accounts.Service.CreateSyncSettingsAsync(new S3SyncSettings()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstRun.Sync.Service.TestConnectionAsync(chineseSettings));
        using var secondRun = Open();
        Assert.Equal(AccountRegion.International, secondRun.DataRegion);
        Assert.NotEqual(basePaths.Database, secondRun.Paths.Database);
        Assert.Equal(foreignStatus.User, (await secondRun.Accounts.Service.GetStatusAsync(true)).User);
        Assert.False((await secondRun.Accounts.Service.GetStatusAsync()).RequiresDataRestart);
        await secondRun.Library.Books.InitializeAsync();
        Assert.Empty(await secondRun.Library.Books.SearchAsync());
        var foreignSettings = await secondRun.Accounts.Service.CreateSyncSettingsAsync(new S3SyncSettings
            { EncryptionKey = "fictional-region-switch-sync-key" });
        await secondRun.Sync.Service.TestConnectionAsync(foreignSettings);
        var restored = await secondRun.Accounts.Service.SelectRegionAsync(AccountRegion.China);
        Assert.Equal(chinaStatus.User, restored.User); Assert.Equal(china.Url, restored.ServerUrl);
        Assert.True(restored.RequiresDataRestart);
        using var thirdRun = Open();
        Assert.Equal(basePaths.Database, thirdRun.Paths.Database);
        Assert.Single(await thirdRun.Library.Books.SearchAsync());
        Assert.Equal(chinaStatus.User, (await thirdRun.Accounts.Service.GetStatusAsync(true)).User);
    }

    [Theory]
    [InlineData("C:/outside.bin")]
    [InlineData("folder\\..\\outside.bin")]
    [InlineData("folder/name.")]
    public async Task ObjectApiRejectsPathsThatCouldEscapeOrAliasItsStorage(string key)
    {
        await using var server = await LocalServer.CreateAsync();
        var account = await RegisterAsync(server.Http, "test_reader");
        using var response = await ObjectRequestAsync(server.Http, HttpMethod.Put, key, account.Token, "fictional-test-data");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ObjectRequestAsync(HttpClient http, HttpMethod method, string key, string token, string? body = null)
    {
        using var request = new HttpRequestMessage(method, "api/sync/objects" + (key.Length == 0 ? "" : "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString))));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body);
        return await http.SendAsync(request);
    }

    private static async Task<Book> ImportTestBookAsync(SqliteBookLibraryService library, string root, string title)
    {
        var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".epub");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            Write("mimetype", "application/epub+zip");
            Write("META-INF/container.xml", """
                <?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Write("content.opf", $"""
                <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">fictional-regional-test</dc:identifier><dc:title>{title}</dc:title><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """);
            Write("chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Fictional local sync test.</p></body></html>");
            void Write(string name, string content)
            {
                using var output = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
                output.Write(content);
            }
        }
        await library.ImportAsync([source]);
        return Assert.Single(await library.SearchAsync());
    }
}
