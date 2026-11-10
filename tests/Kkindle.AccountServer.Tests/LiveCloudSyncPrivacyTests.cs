using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    // Opt-in real deployed process + PostgreSQL + signed local storage, not the
    // SQLite fixture. The file contains credentials for dedicated fictional
    // Plus accounts provisioned through a local demo order and admin fulfilment.
    private sealed class LocalCloudTheoryAttribute : TheoryAttribute
    {
        public LocalCloudTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KKINDLE_LIVE_SYNC_CONFIG")))
                Skip = "本机服务端验证需显式提供 KKINDLE_LIVE_SYNC_CONFIG；普通回归不连接外部服务。";
        }
    }

    [LocalCloudTheory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task LivePostgresCloudSyncRoundTripsOpaquePaddedBooksAndCoversWithRealPlusPermissions(AccountRegion region)
    {
        var configPath = Environment.GetEnvironmentVariable("KKINDLE_LIVE_SYNC_CONFIG")!;
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var configured = config.RootElement.GetProperty("regions").GetProperty(AccountRegions.Slug(region));
        var url = configured.GetProperty("serverUrl").GetString()!;
        var username = configured.GetProperty("username").GetString()!;
        var password = configured.GetProperty("password").GetString()!;
        var expectedId = configured.GetProperty("id").GetGuid();
        var uri = AccountValidation.ServerUri(url);
        Assert.True(uri.IsLoopback, "实时验证只允许本机服务，不能写入生产实例。");
        Assert.StartsWith("privacy_", username);
        var root = Path.Combine(Path.GetTempPath(), "kkindle-live-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var firstBase = new AppPaths(Path.Combine(root, "client-a"));
            var secondBase = new AppPaths(Path.Combine(root, "client-b"));
            var protector = new TestProtector();
            using var firstHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            using var secondHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            using var firstAccount = new AccountService(firstBase, protector, firstHttp, dataRegion: region);
            using var secondAccount = new AccountService(secondBase, protector, secondHttp, dataRegion: region);
            await firstAccount.SelectRegionAsync(region); await secondAccount.SelectRegionAsync(region);
            Assert.Equal(expectedId, (await firstAccount.SignInAsync(url, username, password)).User!.Id);
            Assert.Equal(expectedId, (await secondAccount.SignInAsync(url, username, password)).User!.Id);
            Assert.True((await new DeviceMembershipService(firstAccount).RefreshAsync()).CanUseAccountCloudSync);
            var firstPaths = AccountRegionPaths.ForRegion(firstBase, region);
            var secondPaths = AccountRegionPaths.ForRegion(secondBase, region);
            var firstLibrary = new SqliteBookLibraryService(firstPaths, new BookMetadataService());
            var secondLibrary = new SqliteBookLibraryService(secondPaths, new BookMetadataService());
            var firstReader = new ReaderDataService(firstPaths); var secondReader = new ReaderDataService(secondPaths);
            await firstLibrary.InitializeAsync(); await secondLibrary.InitializeAsync();
            await firstReader.InitializeAsync(); await secondReader.InitializeAsync();
            var book = await ImportLivePrivacyBookAsync(firstLibrary, root);
            var file = Assert.Single(book.Files);
            Assert.NotNull(book.CoverPath);
            var original = await File.ReadAllBytesAsync(Path.Combine(firstPaths.Data, file.RelativePath));
            var cover = await File.ReadAllBytesAsync(Path.Combine(firstPaths.Data, book.CoverPath!));
            var coverHash = Convert.ToHexString(SHA256.HashData(cover)).ToLowerInvariant();
            await firstReader.SaveAnnotationAsync(new ReaderAnnotation
            {
                Id = Guid.NewGuid(), BookId = book.Id, BookFileId = file.Id, ChapterPath = "chapter.xhtml",
                SelectedText = "fictional local quote", Note = "fictional note from A", StartOffset = 0, EndOffset = 5
            });
            await firstReader.AddReadingTimeAsync(book.Id, file.Id, 120, 10, 1, 10);
            var settings = await firstAccount.CreateSyncSettingsAsync(new S3SyncSettings
            {
                Enabled = true, DownloadBookFilesOnSync = false,
                EncryptionKey = "fictional-live-privacy-sync-key", Prefix = "privacy-" + Guid.NewGuid().ToString("N")
            });
            // No test entitlement override: the client and server both enforce Plus.
            var firstSync = new S3SyncService(firstPaths, protector, firstAccount, region);
            var secondSync = new S3SyncService(secondPaths, protector, secondAccount, region);
            var firstDevice = Guid.NewGuid().ToString("N"); var secondDevice = Guid.NewGuid().ToString("N");
            await firstSync.TestConnectionAsync(settings);
            Assert.False((await firstSync.SyncAsync(firstDevice, settings)).IsPartial);
            var pulled = await secondSync.SyncAsync(secondDevice, settings);
            Assert.False(pulled.IsPartial); Assert.Equal(1, pulled.BooksAdded); Assert.Equal(0, pulled.FilesDownloaded);
            var remote = Assert.Single(await secondLibrary.SearchAsync());
            var remoteFile = Assert.Single(remote.Files);
            var target = Path.Combine(secondPaths.Data, remoteFile.RelativePath);
            Assert.False(File.Exists(target));
            Assert.Equal(original, await File.ReadAllBytesAsync(await secondSync.EnsureBookFileDownloadedAsync(secondDevice, settings, remoteFile)));
            Assert.Equal(file.Sha256, await Hashing.Sha256Async(target));
            Assert.Equal(cover, await File.ReadAllBytesAsync(Path.Combine(secondPaths.Data, remote.CoverPath!)));
            await secondReader.AddReadingTimeAsync(book.Id, file.Id, 60, 20, 2, 10);
            var note = Assert.Single(await secondReader.GetAllAnnotationsAsync());
            note.Note = "fictional note from B"; note.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await secondReader.SaveAnnotationAsync(note);
            Assert.False((await secondSync.SyncAsync(secondDevice, settings)).IsPartial);
            Assert.False((await firstSync.SyncAsync(firstDevice, settings)).IsPartial);
            Assert.Equal("fictional note from B", Assert.Single(await firstReader.GetAllAnnotationsAsync()).Note);
            Assert.Equal(180, (await firstReader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
            var token = await ReadStoredTokenAsync(firstBase, protector);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = uri };
            http.DefaultRequestHeaders.Add("X-Kkindle-Region", AccountRegions.Slug(region));
            using var listing = await ObjectRequestAsync(http, HttpMethod.Get, "", token);
            listing.EnsureSuccessStatusCode();
            var objects = (await listing.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountSyncObjectMetadataArray))!;
            var prefix = "regions/" + AccountRegions.Slug(region) + "/" + settings.Prefix + "/";
            var ownObjects = objects.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var blobs = ownObjects.Where(item => item.Key.Contains("/objects/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, blobs.Length); // Book and cover, deduplicated across devices.
            Assert.Equal(2, ownObjects.Count(item => item.Key.EndsWith("/snapshot.bin", StringComparison.Ordinal)));
            var secretFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.EncryptionKey))).ToLowerInvariant();
            Assert.All(ownObjects, item =>
            {
                Assert.DoesNotContain(file.Sha256, item.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(coverHash, item.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secretFingerprint, item.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(settings.EncryptionKey, item.Key, StringComparison.Ordinal);
            });
            foreach (var blob in blobs)
            {
                Assert.Matches(@"/objects/encrypted/[a-f0-9]{64}/[a-f0-9]{64}$", blob.Key);
                using var read = await ObjectRequestAsync(http, HttpMethod.Get, blob.Key, token);
                read.EnsureSuccessStatusCode();
                Assert.True((await read.Content.ReadAsByteArrayAsync()).AsSpan().StartsWith("KKINDLE-SYNC3"u8));
            }
            Assert.DoesNotContain(ownObjects, item => item.Key.Contains(".kkindle-connection-", StringComparison.Ordinal));
            var membership = (await new DeviceMembershipService(firstAccount).RefreshAsync()).Membership;
            Assert.True(membership.StorageBytes > 0); Assert.True(membership.PlusDownloadUsedBytes > 0);
            Assert.True(await secondAccount.SignOutAsync());
            Assert.True(await firstAccount.SignOutAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Book> ImportLivePrivacyBookAsync(SqliteBookLibraryService library, string root)
    {
        var source = Path.Combine(root, "fictional-privacy-book.epub");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            Write("mimetype", "application/epub+zip");
            Write("META-INF/container.xml", """
                <?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Write("content.opf", """
                <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">fictional-privacy-sync</dc:identifier><dc:title>Fictional privacy sync book</dc:title><dc:language>en</dc:language><meta name="cover" content="cover"/></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="cover" href="cover.png" media-type="image/png" properties="cover-image"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """);
            Write("chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Fictional local quote.</p><p>" + new string('x', 1_100_137) + "</p></body></html>");
            using var cover = archive.CreateEntry("cover.png", CompressionLevel.NoCompression).Open();
            cover.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4b8AAAAASUVORK5CYII="));
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        await library.ImportAsync([source]);
        return Assert.Single(await library.SearchAsync());
    }
}
