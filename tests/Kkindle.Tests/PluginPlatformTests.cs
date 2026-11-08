using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;
using Xunit;

namespace Kkindle.Tests;

/// <summary>
/// The third-party plugin platform: manifest fields added for providers,
/// i18n and compatibility, package signing, the plugin catalog, provider
/// result normalization and the content/AI/reading-write bridge methods.
/// </summary>
public sealed class PluginPlatformTests : IDisposable
{
    private readonly string _root = TestHelpers.CreateTempDirectory();

    public void Dispose() => TestHelpers.TryDelete(_root);

    private const string ProviderManifest = """
        {
          "id": "com.example.platform",
          "name": "平台测试",
          "description": "中文说明",
          "version": "2.1.0",
          "apiVersion": 1,
          "minAppVersion": "1.0.0",
          "homepage": "https://example.com/plugin",
          "permissions": { "library": "read", "content": true, "ai": true, "readingWrite": true, "bookFiles": true },
          "contributes": {
            "dictionaries": [{ "id": "glossary", "title": "术语表", "script": "providers/main.js" }],
            "translators": [{ "id": "pirate", "title": "海盗腔", "script": "providers/main.js" }],
            "background": { "script": "providers/main.js", "intervalMinutes": 60, "onStartup": false }
          },
          "i18n": {
            "en-US": { "name": "Platform test", "description": "English description", "titles": { "glossary": "Glossary" } }
          }
        }
        """;

    // ---- manifest -------------------------------------------------------

    [Fact]
    public void ManifestReadsProvidersBackgroundI18nAndNewPermissions()
    {
        var manifest = PluginManifest.Parse(ProviderManifest);

        Assert.True(manifest.CanReadContent);
        Assert.True(manifest.UsesAi);
        Assert.True(manifest.CanWriteReadingData);
        Assert.True(manifest.CanReadBookFiles);
        Assert.Equal(new Version(1, 0, 0), manifest.MinAppVersion);
        Assert.Equal("https://example.com/plugin", manifest.Homepage);
        Assert.Equal("glossary", Assert.Single(manifest.Dictionaries).Id);
        Assert.Equal("providers/main.js", Assert.Single(manifest.Translators).Script);
        Assert.Equal(60, manifest.Background!.IntervalMinutes);
        Assert.False(manifest.Background.RunOnStartup);
        Assert.True(manifest.HasHeadlessContributions);

        Assert.Equal("Platform test", manifest.GetName("en-US"));
        Assert.Equal("Platform test", manifest.GetName("en-GB"));
        Assert.Equal("平台测试", manifest.GetName("zh-CN"));
        Assert.Equal("English description", manifest.GetDescription("en"));
        Assert.Equal("Glossary", manifest.GetTitle("en-US", "glossary", "术语表"));
        Assert.Equal("海盗腔", manifest.GetTitle("en-US", "pirate", "海盗腔"));
    }

    [Theory]
    [InlineData("\"permissions\": { \"content\": true }", "permissions.library")]
    [InlineData("\"minAppVersion\": \"one\"", "minAppVersion")]
    [InlineData("\"homepage\": \"http://example.com\"", "homepage")]
    [InlineData("\"contributes\": { \"background\": { \"script\": \"a.js\", \"intervalMinutes\": 5 } }", "15")]
    [InlineData("\"contributes\": { \"dictionaries\": [{ \"id\": \"d\", \"title\": \"t\", \"script\": \"a.html\" }] }", ".js")]
    [InlineData("\"i18n\": { \"english\": { \"name\": \"x\" } }", "i18n")]
    public void InvalidPlatformFieldsAreRejected(string field, string expected)
    {
        var json = $$"""
            { "id": "com.example.bad", "name": "坏插件", "version": "1.0.0", "apiVersion": 1, {{field}} }
            """;
        var exception = Assert.Throws<PluginPackageException>(() => PluginManifest.Parse(json));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void PackagesRequireTheirScriptsAndAFutureMinAppVersionIsRefused()
    {
        var missingScript = WritePackage(ProviderManifest, ("README.md", "x"));
        var manager = new PluginManager(new AppPaths(Path.Combine(_root, "app")));
        var exception = Assert.Throws<PluginPackageException>(() => manager.Inspect(missingScript));
        Assert.Contains("providers/main.js", exception.Message);

        var future = WritePackage(
            ProviderManifest.Replace("\"minAppVersion\": \"1.0.0\"", "\"minAppVersion\": \"999.0.0\""),
            ("providers/main.js", "// noop"));
        var tooNew = Assert.Throws<PluginPackageException>(() => manager.Inspect(future));
        Assert.Contains("999.0.0", tooNew.Message);
    }

    // ---- signing --------------------------------------------------------

    [Fact]
    public void SignedPackagesVerifyAndPinTheirSigner()
    {
        var (privateKey, _, keyId) = PluginSignature.CreateKey();
        var package = WritePackage(ProviderManifest, ("providers/main.js", "// v1"));
        var signed = PluginSignature.Sign(package, privateKey, "Example Co.");
        Assert.Equal(PluginTrustLevel.Signed, signed.Trust);
        Assert.Equal(keyId, signed.KeyId);

        var manager = new PluginManager(new AppPaths(Path.Combine(_root, "app")));
        var preview = manager.Inspect(package);
        Assert.Equal(PluginTrustLevel.Signed, preview.Signature.Trust);
        Assert.Equal("Example Co.", preview.Signature.Publisher);
        var installed = manager.Install(package);
        Assert.Equal(PluginTrustLevel.Signed, installed.Trust);
        Assert.Equal(keyId, manager.List().Single().SignerKeyId);

        // An unsigned update of a signed plugin is flagged.
        var unsignedUpdate = WritePackage(ProviderManifest.Replace("2.1.0", "2.2.0"), ("providers/main.js", "// v2"));
        Assert.True(manager.Inspect(unsignedUpdate).SignerChanged);

        // So is an update signed by somebody else.
        var (otherKey, _, _) = PluginSignature.CreateKey();
        var otherUpdate = WritePackage(ProviderManifest.Replace("2.1.0", "2.2.0"), ("providers/main.js", "// v2"));
        PluginSignature.Sign(otherUpdate, otherKey);
        Assert.True(manager.Inspect(otherUpdate).SignerChanged);

        // The original signer can ship updates without a warning.
        var sameUpdate = WritePackage(ProviderManifest.Replace("2.1.0", "2.2.0"), ("providers/main.js", "// v2"));
        PluginSignature.Sign(sameUpdate, privateKey);
        Assert.False(manager.Inspect(sameUpdate).SignerChanged);
    }

    [Fact]
    public void TamperedSignedPackagesAreRejected()
    {
        var (privateKey, _, _) = PluginSignature.CreateKey();
        var package = WritePackage(ProviderManifest, ("providers/main.js", "// original"));
        PluginSignature.Sign(package, privateKey);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            archive.GetEntry("providers/main.js")!.Delete();
            TestHelpers.AddZipEntry(archive, "providers/main.js", "// changed");
        }

        var manager = new PluginManager(new AppPaths(Path.Combine(_root, "app")));
        var exception = Assert.Throws<PluginPackageException>(() => manager.Inspect(package));
        Assert.Contains("签名", exception.Message);

        var added = WritePackage(ProviderManifest, ("providers/main.js", "// original"));
        PluginSignature.Sign(added, privateKey);
        using (var archive = ZipFile.Open(added, ZipArchiveMode.Update))
            TestHelpers.AddZipEntry(archive, "providers/extra.js", "// smuggled");
        Assert.Throws<PluginPackageException>(() => manager.Inspect(added));
    }

    [Fact]
    public void UnsignedPackagesStillInstall()
    {
        var package = WritePackage(ProviderManifest, ("providers/main.js", "// unsigned"));
        var manager = new PluginManager(new AppPaths(Path.Combine(_root, "app")));
        Assert.Equal(PluginTrustLevel.Unsigned, manager.Inspect(package).Signature.Trust);
        Assert.Equal(PluginTrustLevel.Unsigned, manager.Install(package).Trust);
    }

    // ---- catalog --------------------------------------------------------

    [Fact]
    public void CatalogParsesEntriesAndSkipsInvalidOnes()
    {
        var sha = new string('a', 64);
        var entries = PluginCatalogService.Parse($$"""
            {
              "format": 1,
              "plugins": [
                { "id": "com.example.one", "name": "一", "version": "1.2.0", "download": "one.kkplugin", "sha256": "{{sha}}", "size": 10,
                  "author": "A", "minAppVersion": "1.0.0", "i18n": { "en-US": { "name": "One" } } },
                { "id": "com.example.bad-hash", "name": "坏", "version": "1.0.0", "download": "x.kkplugin", "sha256": "123" },
                { "id": "com.example.http", "name": "明文", "version": "1.0.0", "download": "http://evil.example/x.kkplugin", "sha256": "{{sha}}" },
                { "id": "com.example.one", "name": "重复", "version": "9.0.0", "download": "dup.kkplugin", "sha256": "{{sha}}" }
              ]
            }
            """, new Uri("https://kkindle.example/plugins/index.json"));

        var entry = Assert.Single(entries);
        Assert.Equal("com.example.one", entry.Id);
        Assert.Equal(new Uri("https://kkindle.example/plugins/one.kkplugin"), entry.Download);
        Assert.Equal("One", entry.GetName("en-US"));
        Assert.Equal("一", entry.GetName("zh-CN"));
        Assert.True(entry.IsCompatible);

        Assert.Throws<InvalidDataException>(() =>
            PluginCatalogService.Parse("""{ "format": 2, "plugins": [] }""", new Uri("https://kkindle.example/")));
    }

    [Fact]
    public async Task CatalogFindsUpdatesAndVerifiesDownloads()
    {
        var manager = new PluginManager(new AppPaths(Path.Combine(_root, "app")));
        var installed = manager.Install(WritePackage(ProviderManifest, ("providers/main.js", "// v1")));
        var newer = WritePackage(ProviderManifest.Replace("2.1.0", "2.5.0"), ("providers/main.js", "// v2"));
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(newer))).ToLowerInvariant();
        var indexPath = Path.Combine(_root, "index.json");
        await File.WriteAllTextAsync(indexPath, $$"""
            { "format": 1, "plugins": [
              { "id": "{{installed.Id}}", "name": "平台测试", "version": "2.5.0",
                "download": "{{Path.GetFileName(newer)}}", "sha256": "{{hash}}", "size": {{new FileInfo(newer).Length}} },
              { "id": "com.example.future", "name": "未来", "version": "1.0.0", "minAppVersion": "999.0.0",
                "download": "future.kkplugin", "sha256": "{{hash}}" }
            ] }
            """);

        using var catalog = new PluginCatalogService();
        var entries = await catalog.GetEntriesAsync(new Uri(indexPath));
        Assert.Equal(2, entries.Count);
        Assert.False(entries.Single(entry => entry.Id == "com.example.future").IsCompatible);

        var update = Assert.Single(PluginCatalogService.FindUpdates(manager.List(), entries));
        Assert.Equal("2.5.0", update.Available.Version);

        var downloaded = await catalog.DownloadAsync(update.Available);
        try
        {
            Assert.Equal("2.5.0", manager.Install(downloaded).Manifest.Version);
        }
        finally
        {
            File.Delete(downloaded);
        }

        var tampered = update.Available with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.DownloadAsync(tampered));
    }

    // ---- provider results -----------------------------------------------

    [Fact]
    public void DictionaryResultsAreNormalized()
    {
        Assert.Equal("释义", Assert.Single(Assert.Single(
            PluginProviderResults.ParseDictionary(JsonValue.Create("释义"), "书")).Senses).Text);

        var entries = PluginProviderResults.ParseDictionary(JsonNode.Parse("""
            { "entries": [
              { "headword": "book", "phonetic": "/bʊk/", "senses": [
                  "a written work",
                  { "pos": "v.", "definition": "to reserve", "examples": ["book a table", 5] } ] },
              { "word": "tome", "definition": "a large book" },
              { "nothing": true },
              42
            ] }
            """), "book");
        Assert.Equal(2, entries.Count);
        Assert.Equal("/bʊk/", entries[0].Phonetic);
        Assert.Equal("v.", entries[0].Senses[1].PartOfSpeech);
        Assert.Equal(["book a table"], entries[0].Senses[1].Examples);
        Assert.Equal("tome", entries[1].Headword);
        Assert.Empty(PluginProviderResults.ParseDictionary(JsonNode.Parse("[1, 2]"), "x"));
        Assert.Empty(PluginProviderResults.ParseDictionary(null, "x"));

        Assert.Equal("hola", PluginProviderResults.ParseTranslation(JsonValue.Create(" hola ")));
        Assert.Equal("hola", PluginProviderResults.ParseTranslation(JsonNode.Parse("""{ "text": "hola" }""")));
        Assert.Null(PluginProviderResults.ParseTranslation(JsonNode.Parse("""{ "text": 1 }""")));
    }

    // ---- bridge: content, reading writes, AI -----------------------------

    [Fact]
    public async Task BridgeReadsChaptersAndWritesAnnotationsAndBookmarks()
    {
        await using var harness = await ContentHarness.CreateAsync(_root, ProviderManifest);

        var toc = await harness.CallAsync("book.getToc", new() { ["bookId"] = harness.BookId });
        Assert.True(toc.Ok, toc.Error);
        var chapters = toc.Result!.Value.GetProperty("chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Equal("OEBPS/Text/ch2.xhtml", chapters[1].GetProperty("path").GetString());
        Assert.Equal("第二章", toc.Result.Value.GetProperty("toc")[1].GetProperty("title").GetString());

        var text = await harness.CallAsync("book.getChapterText", new()
        {
            ["bookId"] = harness.BookId,
            ["chapterIndex"] = 1,
            ["offset"] = 0,
            ["maxLength"] = 5
        });
        Assert.True(text.Ok, text.Error);
        Assert.True(text.Result!.Value.GetProperty("hasMore").GetBoolean());
        var full = await harness.CallAsync("book.getChapterText", new() { ["bookId"] = harness.BookId, ["chapterIndex"] = 1 });
        var body = full.Result!.Value.GetProperty("text").GetString()!;
        var start = body.IndexOf("needle", StringComparison.Ordinal);
        Assert.True(start >= 0, body);

        var annotation = await harness.CallAsync("reading.addAnnotation", new()
        {
            ["bookId"] = harness.BookId,
            ["chapterIndex"] = 1,
            ["startOffset"] = start,
            ["endOffset"] = start + 6,
            ["note"] = "plugin note",
            ["color"] = "#aa3300",
            ["style"] = "wavy"
        });
        Assert.True(annotation.Ok, annotation.Error);
        var saved = Assert.Single(await harness.Reader.GetAnnotationsAsync(harness.BookFileId));
        Assert.Equal("needle", saved.SelectedText);
        Assert.Equal("OEBPS/Text/ch2.xhtml", saved.ChapterPath);
        Assert.Equal("#AA3300", saved.Color);
        Assert.Equal("wavy", saved.UnderlineStyle);

        var outOfRange = await harness.CallAsync("reading.addAnnotation", new()
        {
            ["bookId"] = harness.BookId,
            ["chapterIndex"] = 1,
            ["startOffset"] = 0,
            ["endOffset"] = body.Length + 10
        });
        Assert.False(outOfRange.Ok);

        var bookmark = await harness.CallAsync("reading.addBookmark", new()
        {
            ["bookId"] = harness.BookId,
            ["chapterIndex"] = 0,
            ["title"] = "开头"
        });
        Assert.True(bookmark.Ok, bookmark.Error);
        Assert.Equal("开头", Assert.Single(await harness.Reader.GetBookmarksAsync(harness.BookFileId)).Title);

        var file = await harness.CallAsync("library.readFile", new() { ["bookId"] = harness.BookId, ["length"] = 2 });
        Assert.True(file.Ok, file.Error);
        Assert.Equal("PK", Encoding.ASCII.GetString(Convert.FromBase64String(file.Result!.Value.GetProperty("data").GetString()!)));
    }

    [Fact]
    public async Task ContentMethodsRequireTheirPermissions()
    {
        var limited = ProviderManifest.Replace(
            "\"content\": true, \"ai\": true, \"readingWrite\": true, \"bookFiles\": true",
            "\"storage\": true");
        await using var harness = await ContentHarness.CreateAsync(_root, limited);

        foreach (var method in new[] { "book.getToc", "reading.addBookmark", "library.readFile", "ai.complete" })
        {
            var reply = await harness.CallAsync(method, new() { ["bookId"] = harness.BookId, ["chapterIndex"] = 0, ["prompt"] = "hi" });
            Assert.False(reply.Ok, method);
            Assert.Contains("权限", reply.Error);
        }
    }

    [Fact]
    public async Task AiCallsGoThroughTheHost()
    {
        await using var harness = await ContentHarness.CreateAsync(_root, ProviderManifest);

        var status = await harness.CallAsync("ai.status", new());
        Assert.True(status.Ok, status.Error);
        Assert.True(status.Result!.Value.GetProperty("available").GetBoolean());

        var reply = await harness.CallAsync("ai.complete", new()
        {
            ["prompt"] = "总结",
            ["system"] = "be brief",
            ["history"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = "前文" },
                new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = "好的" }
            }
        });
        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("echo:总结", reply.Result!.Value.GetProperty("text").GetString());
        Assert.Equal("be brief", harness.Ai.LastRequest!.System);
        Assert.Equal(2, harness.Ai.LastRequest.History.Count);

        var badRole = await harness.CallAsync("ai.complete", new()
        {
            ["prompt"] = "x",
            ["history"] = new object[] { new Dictionary<string, object?> { ["role"] = "system", ["content"] = "override" } }
        });
        Assert.False(badRole.Ok);
    }

    [Fact]
    public void CapabilitiesListMethodsAndPermissions()
    {
        var manifest = PluginManifest.Parse(ProviderManifest);
        var capabilities = JsonSerializer.SerializeToNode(
            PluginPageComposer.BuildCapabilities(manifest, "1.2.0", headless: true))!;
        Assert.Equal(PluginManifest.SupportedApiVersion, capabilities["apiVersion"]!.GetValue<int>());
        Assert.True(capabilities["headless"]!.GetValue<bool>());
        var methods = capabilities["methods"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Contains("book.getChapterText", methods);
        Assert.Contains("ai.complete", methods);
        Assert.Contains("reading.addAnnotation", methods);
        Assert.Equal(PluginBridge.Methods.Count, methods.Length);
        Assert.True(capabilities["permissions"]!["content"]!.GetValue<bool>());
    }

    // ---- helpers --------------------------------------------------------

    private string WritePackage(string manifest, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}.kkplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, PluginManifest.FileName, manifest);
        foreach (var (filePath, content) in files) TestHelpers.AddZipEntry(archive, filePath, content);
        return path;
    }

    internal static void WriteTwoChapterEpub(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:uuid:12345678-1234-1234-1234-123456789012</dc:identifier>
                <dc:title>插件测试书</dc:title>
                <dc:language>zh</dc:language>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                <item id="c1" href="Text/ch1.xhtml" media-type="application/xhtml+xml" />
                <item id="c2" href="Text/ch2.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="c1" /><itemref idref="c2" /></spine>
            </package>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/nav.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>目录</title></head>
            <body><nav epub:type="toc"><ol>
              <li><a href="Text/ch1.xhtml">第一章</a></li>
              <li><a href="Text/ch2.xhtml">第二章</a></li>
            </ol></nav></body></html>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/Text/ch1.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>第一章</title></head>
            <body><h1>第一章</h1><p>开篇的文字。</p></body></html>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/Text/ch2.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>第二章</title></head>
            <body><h1>第二章</h1><p>A haystack with a needle inside it.</p></body></html>
            """);
    }

    private sealed record Reply(bool Ok, JsonElement? Result, string Error);

    private sealed class FakeAiHost : IPluginAiHost
    {
        public PluginAiRequest? LastRequest { get; private set; }

        public Task<PluginAiStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PluginAiStatus(true, "Test", "model", null));

        public Task<string> CompleteAsync(PluginAiRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult("echo:" + request.Prompt);
        }
    }

    private sealed class ContentHarness : IAsyncDisposable
    {
        private PluginBridge _bridge = null!;
        private long _nextId;

        public string BookId { get; private set; } = string.Empty;
        public Guid BookFileId { get; private set; }
        public ReaderDataService Reader { get; private set; } = null!;
        public FakeAiHost Ai { get; } = new();

        public static async Task<ContentHarness> CreateAsync(string root, string manifest)
        {
            var harness = new ContentHarness();
            var paths = new AppPaths(Path.Combine(root, "app-" + Guid.NewGuid().ToString("N")));
            paths.EnsureDirectories();
            var source = Path.Combine(root, $"source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(source, "providers"));
            await File.WriteAllTextAsync(Path.Combine(source, PluginManifest.FileName), manifest);
            await File.WriteAllTextAsync(Path.Combine(source, "providers", "main.js"), "// noop");
            var manager = new PluginManager(paths);
            var plugin = manager.InstallFromDirectory(source);

            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            var epub = Path.Combine(root, $"book-{Guid.NewGuid():N}.epub");
            WriteTwoChapterEpub(epub);
            var import = await library.ImportAsync([epub]);
            var bookId = import.Items.Single(item => item.Succeeded).BookId!.Value;
            var book = (await library.GetBookAsync(bookId))!;
            harness.BookId = bookId.ToString("D");
            harness.BookFileId = book.Files[0].Id;

            harness.Reader = new ReaderDataService(paths);
            await harness.Reader.InitializeAsync();
            var context = new PluginPageContext(plugin.Id, plugin.Manifest.Version, "glossary", [bookId], "zh-CN", "1.2.0");
            harness._bridge = new PluginBridge(
                plugin,
                context,
                library,
                paths,
                new PluginStorage(manager.GetDataDirectory(plugin.Id)),
                new HeadlessUi(),
                readerData: harness.Reader,
                content: new PluginBookContentService(paths, library),
                ai: harness.Ai);
            return harness;
        }

        public async Task<Reply> CallAsync(string method, Dictionary<string, object?> parameters)
        {
            var message = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = ++_nextId,
                ["method"] = method,
                ["params"] = parameters
            });
            var handled = await _bridge.HandleAsync(message, CancellationToken.None);
            Assert.NotNull(handled);
            using var document = JsonDocument.Parse(handled!.Value.Reply);
            var root = document.RootElement;
            return new Reply(
                root.GetProperty("ok").GetBoolean(),
                root.TryGetProperty("result", out var result) ? result.Clone() : null,
                root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()! : string.Empty);
        }

        public ValueTask DisposeAsync()
        {
            _bridge.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HeadlessUi : IPluginUiHost
    {
        public bool NetworkEnabled => false;
        public void Close() { }
        public void Notify(string message) { }
        public void SetTitle(string title) { }
        public void OpenExternal(Uri uri) { }
        public void BooksChanged(IReadOnlyCollection<Guid> bookIds) { }
        public Task<bool> RequestNetworkAccessAsync(string host) => Task.FromResult(false);
        public Task<bool> ConfirmImportAsync(string fileName, long sizeBytes) => Task.FromResult(false);
        public void RaiseEvent(string name, JsonNode payload) { }
        public void Log(string level, string message) { }
        public Task<IReadOnlyList<PluginPickedFile>> PickFilesAsync(bool allowMultiple, IReadOnlyList<string> extensions) =>
            Task.FromResult<IReadOnlyList<PluginPickedFile>>([]);
        public Task<PluginPickedFile?> SaveFileAsync(string suggestedName, byte[] content) => Task.FromResult<PluginPickedFile?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
    }
}
