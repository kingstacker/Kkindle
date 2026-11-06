using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;
using Xunit;

namespace Kkindle.Tests;

public sealed class PluginTests
{
    private const string ValidManifest = """
        {
          "id": "com.example.test",
          "name": "测试插件",
          "version": "1.2.0",
          "apiVersion": 1,
          "permissions": {
            "network": ["https://book.example.com", "https://*.images.example.com"],
            "library": "write",
            "storage": true
          },
          "contributes": {
            "bookActions": [
              { "id": "match", "title": "匹配", "page": "ui/app.html", "multiple": true }
            ],
            "settings": { "page": "ui/settings.html" }
          }
        }
        """;

    // ---- manifest ------------------------------------------------------

    [Fact]
    public void ManifestReadsPermissionsAndActions()
    {
        var manifest = PluginManifest.Parse(ValidManifest);

        Assert.Equal("com.example.test", manifest.Id);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal(PluginLibraryAccess.Write, manifest.LibraryAccess);
        Assert.True(manifest.UsesStorage);
        Assert.Equal(2, manifest.NetworkOrigins.Count);
        var action = Assert.Single(manifest.BookActions);
        Assert.Equal("ui/app.html", action.Page);
        Assert.True(action.AllowsMultipleBooks);
        Assert.Equal("ui/settings.html", manifest.SettingsPage!.Page);
        Assert.Contains("ui/settings.html", manifest.PagePaths);
    }

    [Fact]
    public void ManifestReadsCommandsReaderActionsAndLibraryAll()
    {
        var manifest = PluginManifest.Parse("""
            {
              "id": "com.example.extensions",
              "name": "扩展",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": { "library": "read", "libraryAll": true },
              "contributes": {
                "commands": [
                  { "id": "sync", "title": "同步", "page": "ui/command.html", "width": 800 }
                ],
                "readerActions": [
                  { "id": "lookup", "title": "查询", "page": "ui/reader.html" }
                ],
                "settings": { "page": "ui/settings.html" }
              }
            }
            """);

        Assert.True(manifest.HasLibraryScopeAll);
        var command = Assert.Single(manifest.Commands);
        Assert.Equal("同步", command.Title);
        Assert.Equal(800, command.Width);
        Assert.Equal(680, command.Height);
        var readerAction = Assert.Single(manifest.ReaderActions);
        Assert.Equal("ui/reader.html", readerAction.Page);
        Assert.Contains("ui/command.html", manifest.PagePaths);
        Assert.Contains("ui/reader.html", manifest.PagePaths);
    }

    [Fact]
    public void PageContextCarriesTheReaderSelection()
    {
        using var scope = new TempPluginScope();
        var plugin = scope.Install(("ui/app.html", "<html><body></body></html>"));
        var bookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var html = PluginPageComposer.Compose(
            plugin,
            "ui/app.html",
            new PluginPageContext(
                plugin.Id,
                plugin.Manifest.Version,
                "lookup",
                [bookId],
                "zh-CN",
                "1.2.0",
                new PluginSelectionContext(
                    "选中的文字", 3, 8, "前文", "后文", bookId, fileId, 1, "chapter.xhtml", null)),
            PluginTheme.Light);

        Assert.Contains("\"selection\"", html);
        Assert.Contains("chapter.xhtml", html);
        Assert.Contains("\"startOffset\":3", html);
    }

    [Fact]
    public void ManifestReadsAppPageAndExtraPermissions()
    {
        var manifest = PluginManifest.Parse("""
            {
              "id": "com.example.tools",
              "name": "工具",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": {
                "network": ["https://example.com"],
                "import": true,
                "secrets": true,
                "storage": true
              },
              "contributes": {
                "app": { "title": "在线工具", "page": "ui/library.html", "width": 900, "height": 640 },
                "settings": { "page": "ui/settings.html" }
              }
            }
            """);

        Assert.True(manifest.HasNetworkPermission);
        Assert.True(manifest.CanImportLibrary);
        Assert.True(manifest.UsesSecrets);
        Assert.Equal(PluginLibraryAccess.None, manifest.LibraryAccess);
        Assert.NotNull(manifest.AppPage);
        Assert.Equal("在线工具", manifest.AppPage!.Title);
        Assert.Equal("ui/library.html", manifest.AppPage.Page);
        Assert.Equal(900, manifest.AppPage.Width);
        Assert.Contains("ui/library.html", manifest.PagePaths);
    }

    [Theory]
    [InlineData("""{ "id": "Com.Example", "name": "x", "version": "1.0.0", "apiVersion": 1 }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "abc", "apiVersion": 1 }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0" }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 2 }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 1, "permissions": { "network": ["http://example.com"] } }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 1, "contributes": { "bookActions": [ { "id": "a", "title": "t", "page": "app.html" } ] } }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 1, "contributes": { "bookActions": [ { "id": "a", "title": "t", "page": "../evil.html" } ] }, "permissions": { "library": "write" } }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 1, "contributes": { "app": { "title": "t", "page": "ui/app.js" } } }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 1, "contributes": { "commands": [ { "id": "a", "title": "t", "page": "ui/app.js" } ] } }""")]
    [InlineData("""{ "id": "com.example", "name": "x", "version": "1.0.0", "apiVersion": 1, "contributes": { "readerActions": [ { "id": "a b", "title": "t", "page": "ui/app.html" } ] } }""")]
    public void InvalidManifestsAreRejected(string json)
    {
        Assert.Throws<PluginPackageException>(() => PluginManifest.Parse(json));
    }

    [Fact]
    public void NetworkOriginMatchesHostAndSubdomains()
    {
        var host = PluginNetworkOrigin.Parse("https://example.com");
        var wildcard = PluginNetworkOrigin.Parse("https://*.example.com");

        Assert.True(host.Allows(new Uri("https://example.com/path?a=1")));
        Assert.False(host.Allows(new Uri("https://api.example.com/")));
        Assert.False(host.Allows(new Uri("http://example.com/")));
        Assert.False(host.Allows(new Uri("https://example.com:8443/")));
        Assert.True(wildcard.Allows(new Uri("https://img1.example.com/x")));
        Assert.True(wildcard.Allows(new Uri("https://example.com/x")));
        Assert.False(wildcard.Allows(new Uri("https://example.com.evil.test/x")));
    }

    // ---- package -------------------------------------------------------

    [Fact]
    public void PackageWithoutReferencedPageIsRejected()
    {
        using var scope = new TempPluginScope();
        var package = scope.WritePackage(ValidManifest, ("ui/app.html", "<p>hi</p>"));

        var exception = Assert.Throws<PluginPackageException>(() => PluginPackage.ReadManifest(package));
        Assert.Contains("ui/settings.html", exception.Message);
    }

    [Fact]
    public void PackageWithoutExtendedContributionPageIsRejected()
    {
        using var scope = new TempPluginScope();
        const string manifest = """
            {
              "id": "com.example.extensions",
              "name": "扩展",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": { "library": "read" },
              "contributes": {
                "commands": [ { "id": "open", "title": "打开", "page": "ui/command.html" } ],
                "readerActions": [ { "id": "lookup", "title": "查询", "page": "ui/reader.html" } ],
                "settings": { "page": "ui/settings.html" }
              }
            }
            """;
        var package = scope.WritePackage(manifest,
            ("ui/command.html", "<p>hi</p>"), ("ui/settings.html", "<p>hi</p>"));

        var exception = Assert.Throws<PluginPackageException>(() => PluginPackage.ReadManifest(package));
        Assert.Contains("ui/reader.html", exception.Message);
    }

    [Fact]
    public void PackageRejectsPathsOutsidePluginDirectory()
    {
        using var scope = new TempPluginScope();
        var package = scope.WritePackage(ValidManifest,
            ("ui/app.html", "<p>hi</p>"), ("ui/settings.html", "<p>hi</p>"), ("../escape.txt", "x"));

        Assert.Throws<PluginPackageException>(() => PluginPackage.ReadManifest(package));
    }

    [Fact]
    public void PackageWithMissingManifestIsRejected()
    {
        using var scope = new TempPluginScope();
        var package = scope.WritePackage(null, ("app.html", "<p>hi</p>"));

        Assert.Throws<PluginPackageException>(() => PluginPackage.ReadManifest(package));
    }

    // ---- manager -------------------------------------------------------

    [Fact]
    public void InstallEnableAndUninstallManagePluginAndItsData()
    {
        using var scope = new TempPluginScope();
        var paths = new AppPaths(Path.Combine(scope.Root, "app"));
        var manager = new PluginManager(paths);
        var package = scope.WritePackage(ValidManifest,
            ("ui/app.html", "<p>hi</p>"), ("ui/settings.html", "<p>hi</p>"));

        var installed = manager.Install(package);
        Assert.Equal("com.example.test", installed.Id);
        Assert.True(installed.Enabled);
        Assert.True(File.Exists(Path.Combine(installed.Directory, "ui", "app.html")));
        Assert.Single(manager.List());

        manager.SetEnabled("com.example.test", false);
        Assert.False(Assert.Single(manager.List()).Enabled);
        // A disabled plugin is still installed; the shell filters it out.
        Assert.NotNull(manager.Find("com.example.test"));

        var data = manager.GetDataDirectory("com.example.test");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "storage.json"), "{}");
        manager.Uninstall("com.example.test");
        Assert.Empty(manager.List());
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public void InstallingANewVersionReplacesTheOldOne()
    {
        using var scope = new TempPluginScope();
        var paths = new AppPaths(Path.Combine(scope.Root, "app"));
        var manager = new PluginManager(paths);
        var first = scope.WritePackage(
            ValidManifest, [("ui/app.html", "v1"), ("ui/settings.html", "s1")], "first");
        var second = scope.WritePackage(
            ValidManifest.Replace("\"1.2.0\"", "\"1.3.0\""),
            [("ui/app.html", "v2"), ("ui/settings.html", "s2")], "second");

        manager.Install(first);
        var preview = manager.Inspect(second);
        Assert.True(preview.IsUpdate);
        Assert.False(preview.IsDowngrade);

        var installed = manager.Install(second);
        Assert.Equal("1.3.0", installed.Manifest.Version);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(installed.Directory, "ui", "app.html")));
    }

    [Fact]
    public void InstallsAndInspectsAPluginFromADevelopmentFolder()
    {
        using var scope = new TempPluginScope();
        var manager = new PluginManager(new AppPaths(Path.Combine(scope.Root, "app")));
        var folder = Path.Combine(scope.Root, "dev-plugin");
        Directory.CreateDirectory(Path.Combine(folder, "ui"));
        Directory.CreateDirectory(Path.Combine(folder, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), ValidManifest);
        File.WriteAllText(Path.Combine(folder, "ui", "app.html"), "<p>dev</p>");
        File.WriteAllText(Path.Combine(folder, "ui", "settings.html"), "<p>settings</p>");
        File.WriteAllText(Path.Combine(folder, "node_modules", "left-pad", "index.js"), "ignored");

        var preview = manager.InspectDirectory(folder);
        Assert.Equal("com.example.test", preview.Manifest.Id);
        Assert.False(preview.IsUpdate);

        var installed = manager.InstallFromDirectory(folder);
        Assert.Equal("com.example.test", installed.Id);
        Assert.Equal("<p>dev</p>", File.ReadAllText(Path.Combine(installed.Directory, "ui", "app.html")));
        Assert.False(Directory.Exists(Path.Combine(installed.Directory, "node_modules")));
    }

    [Fact]
    public void DevelopmentFolderRequiresAManifest()
    {
        using var scope = new TempPluginScope();
        var manager = new PluginManager(new AppPaths(Path.Combine(scope.Root, "app")));
        Assert.Throws<PluginPackageException>(
            () => manager.InspectDirectory(Path.Combine(scope.Root, "missing")));

        var empty = Path.Combine(scope.Root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<PluginPackageException>(() => manager.InspectDirectory(empty));
        Assert.Throws<PluginPackageException>(() => manager.InstallFromDirectory(empty));
    }

    [Fact]
    public async Task BridgeWritesACoverFromBase64()
    {
        const string pixel =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        using var scope = new TempPluginScope();
        Book book = null!;
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.Write, seed: async library =>
        {
            book = await AddBookAsync(library, scope.Root, "封面测试", "作者");
            return [book.Id];
        });

        // Plugins usually hand back a "<media type>;base64,..." string.
        var reply = await harness.CallAsync(Request(1, "library.setCover", new Dictionary<string, object?>
        {
            ["id"] = book.Id.ToString("D"),
            ["data"] = "image/png;base64," + pixel
        }));
        Assert.True(reply.Ok, reply.Error);

        var saved = await harness.Library.GetBookAsync(book.Id);
        Assert.NotNull(saved!.CoverPath);
        var cover = Path.Combine(harness.Paths.Data, saved.CoverPath!);
        Assert.True(File.Exists(cover));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(cover).Take(4).ToArray());

        var read = await harness.CallAsync(Request(2, "library.getCover", new Dictionary<string, object?>
        {
            ["id"] = book.Id.ToString("D")
        }));
        Assert.True(read.Ok, read.Error);
        Assert.StartsWith("data:image/png;base64,", read.Result!.Value.GetString());
    }

    // ---- bundled plugins and example ------------------------------------

    [Fact]
    public void BookDetailsPluginIsValid()
    {
        var root = FindRepositoryRoot();
        if (root is null) return; // packaged builds may ship without the repository.

        var folder = Path.Combine(root, "plugins", "book-details-match");
        // The plugin source is maintained outside this repository now.
        if (!Directory.Exists(folder)) return;
        var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
        Assert.Equal("com.kkindle.book-details-match", manifest.Id);
        var action = Assert.Single(manifest.BookActions);
        foreach (var page in manifest.PagePaths)
            Assert.True(File.Exists(Path.Combine(folder, page.Replace('/', Path.DirectorySeparatorChar))), page);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
        Assert.True(manifest.HasNetworkPermission);
        // The search address is entered by the user; the package ships none.
        Assert.Empty(manifest.NetworkOrigins);
        Assert.Equal(PluginLibraryAccess.Write, manifest.LibraryAccess);

        // Extraction must stay site-neutral: the page is read through
        // structured data and visible labels, not one site's URL shape.
        var script = File.ReadAllText(Path.Combine(folder, "ui", "app.js"));
        Assert.DoesNotContain("subject_search", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/subject/", script, StringComparison.Ordinal);
        Assert.Contains("parseSearchResults", script, StringComparison.Ordinal);
        Assert.Contains("findJsonLdBook", script, StringComparison.Ordinal);
        Assert.Contains("labeledValue", script, StringComparison.Ordinal);
        Assert.Contains("extractCoverUrl", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineLibraryPluginIsValid()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;

        var folder = Path.Combine(root, "plugins", "online-library");
        // The plugin source is maintained outside this repository now.
        if (!Directory.Exists(folder)) return;
        var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
        Assert.Equal("com.kkindle.online-library", manifest.Id);
        Assert.NotNull(manifest.AppPage);
        Assert.True(manifest.CanImportLibrary);
        Assert.True(manifest.UsesSecrets);
        Assert.True(manifest.HasNetworkPermission);
        // The server address is entered by the user; the package ships none.
        Assert.Empty(manifest.NetworkOrigins);
        Assert.Equal(PluginLibraryAccess.None, manifest.LibraryAccess);
        foreach (var page in manifest.PagePaths)
            Assert.True(File.Exists(Path.Combine(folder, page.Replace('/', Path.DirectorySeparatorChar))), page);
        Assert.True(File.Exists(Path.Combine(folder, manifest.Icon!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void BundledPluginsAskForTheFirstOpenDisclaimer()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;

        // The matching plugin asks once on its settings page; the matching
        // window itself opens without it. The online library has no separate
        // settings page, so its app page asks.
        foreach (var (name, page) in new[] {
                     ("book-details-match", "settings"),
                     ("online-library", "app") })
        {
            var folder = Path.Combine(root, "plugins", name);
            if (!Directory.Exists(folder)) continue;
            var html = File.ReadAllText(Path.Combine(folder, "ui", page + ".html"));
            var script = File.ReadAllText(Path.Combine(folder, "ui", page + ".js"));
            Assert.Contains("id=\"disclaimer\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"disclaimerAccept\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"disclaimerDecline\"", html, StringComparison.Ordinal);
            Assert.Contains("ensureDisclaimer", script, StringComparison.Ordinal);
            Assert.Contains("disclaimerAccepted", script, StringComparison.Ordinal);
            Assert.Contains("kk.ui.close()", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlineLibraryKeepsSettingsInsideTheAppPage()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;

        var folder = Path.Combine(root, "plugins", "online-library");
        // The plugin source is maintained outside this repository now.
        if (!Directory.Exists(folder)) return;

        using var scope = new TempPluginScope();
        var package = Path.Combine(scope.Root, "online-library.kkplugin");
        System.IO.Compression.ZipFile.CreateFromDirectory(folder, package);
        var plugin = new PluginManager(new AppPaths(Path.Combine(scope.Root, "app"))).Install(package);

        // The standalone settings page is gone; its fields belong to the page
        // opened from the plugin row's “打开” button.
        Assert.Null(plugin.Manifest.SettingsPage);
        Assert.NotNull(plugin.Manifest.AppPage);
        var appPage = plugin.Manifest.AppPage!;
        var html = PluginPageComposer.Compose(
            plugin,
            appPage.Page,
            new PluginPageContext(plugin.Id, plugin.Manifest.Version, null, [], "zh-CN", "1.2.0"),
            PluginTheme.Light);

        Assert.Contains("id=\"settingsButton\"", html);
        Assert.Contains("id=\"settingsPanel\"", html);
        Assert.Contains("id=\"serverUrl\"", html);
        Assert.Contains("id=\"settingsEmail\"", html);
        Assert.Contains("id=\"settingsPassword\"", html);
        Assert.Contains("id=\"saveSettings\"", html);
        // The account fields are the settings fields; there is no second
        // sign-in strip, no duplicate format picker, and the window's own
        // close control is enough.
        Assert.DoesNotContain("id=\"loginPanel\"", html);
        Assert.DoesNotContain("id=\"settingsFormat\"", html);
        Assert.DoesNotContain("id=\"close\"", html);
        // The search-bar format picker is page-drawn, not a native <select>.
        Assert.Contains("id=\"formatMenu\"", html);
        Assert.DoesNotContain("<select", html);
        // Result rows carry a cover and open this detail sheet on click.
        Assert.Contains("id=\"detail\"", html);
        Assert.Contains("id=\"detailCover\"", html);
        Assert.Contains("id=\"detailFacts\"", html);
        Assert.Contains("id=\"detailDownload\"", html);
    }

    [Fact]
    public void BookToolsPluginIsValid()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;

        var folder = Path.Combine(root, "plugins", "book-translation");
        var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
        Assert.Equal("com.kkindle.book-translation", manifest.Id);
        Assert.True(manifest.AllowsTranslation);
        Assert.True(manifest.AllowsPinyin);
        Assert.Equal(PluginLibraryAccess.Read, manifest.LibraryAccess);
        Assert.Equal(2, manifest.BookActions.Count);
        Assert.All(manifest.BookActions, action => Assert.False(action.AllowsMultipleBooks));
        Assert.NotNull(manifest.SettingsPage);
        foreach (var page in manifest.PagePaths)
            Assert.True(File.Exists(Path.Combine(folder, page.Replace('/', Path.DirectorySeparatorChar))), page);
        Assert.True(File.Exists(Path.Combine(folder, manifest.Icon!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void OnlineLibraryPluginInstalls()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;

        var folder = Path.Combine(root, "plugins", "online-library");
        // The plugin source is maintained outside this repository now.
        if (!Directory.Exists(folder)) return;
        using var scope = new TempPluginScope();
        var package = Path.Combine(scope.Root, "online-library.kkplugin");
        System.IO.Compression.ZipFile.CreateFromDirectory(folder, package);
        var manager = new PluginManager(new AppPaths(Path.Combine(scope.Root, "app")));

        var installed = manager.Install(package);

        Assert.Equal("com.kkindle.online-library", installed.Id);
        Assert.True(installed.Enabled);
        Assert.NotNull(installed.Manifest.AppPage);
        Assert.True(installed.Manifest.CanImportLibrary);
        Assert.True(installed.Manifest.UsesSecrets);
    }

    [Fact]
    public void BookInfoExampleIsValid()
    {
        var root = FindRepositoryRoot();
        if (root is null) return;

        var folder = Path.Combine(root, "examples", "book-info");
        var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
        Assert.Equal("com.example.book-info", manifest.Id);
        var action = Assert.Single(manifest.BookActions);
        Assert.True(action.AllowsMultipleBooks);
        Assert.Equal(PluginLibraryAccess.Read, manifest.LibraryAccess);
        Assert.Null(manifest.AppPage);
        foreach (var page in manifest.PagePaths)
            Assert.True(File.Exists(Path.Combine(folder, page.Replace('/', Path.DirectorySeparatorChar))), page);
        Assert.True(File.Exists(Path.Combine(folder, manifest.Icon!.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "plugins")))
                return directory.FullName;
        }
        return null;
    }

    // ---- page composer -------------------------------------------------

    [Fact]
    public void PageCompositionInlinesAssetsAndInjectsBridge()
    {
        using var scope = new TempPluginScope();
        var plugin = scope.Install(
            ("ui/app.html", "<!DOCTYPE html><html><head><link rel=\"stylesheet\" href=\"app.css\"></head><body><h1>x</h1><img src=\"logo.png\"><script src=\"app.js\"></script></body></html>"),
            ("ui/app.css", "body{color:red}"),
            ("ui/app.js", "console.log('</script>')"),
            ("ui/logo.png", ""));

        var html = PluginPageComposer.Compose(
            plugin,
            "ui/app.html",
            new PluginPageContext(plugin.Id, plugin.Manifest.Version, "match", [Guid.NewGuid()], "zh-CN", "1.2.0"),
            PluginTheme.Light);

        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("connect-src 'none'", html);
        Assert.Contains("body{color:red}", html);
        Assert.Contains("console.log('<\\/script>')", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("defineProperty(window, 'kkindle'", html);
        Assert.Contains("importDownload", html);
        Assert.Contains("translateBook", html);
        Assert.Contains("annotatePinyin", html);
        Assert.Contains("cancelTask", html);
        Assert.Contains("downloadModel", html);
        Assert.Contains("requestAccess", html);
        Assert.Contains("secrets", html);
        Assert.Contains("events", html);
        Assert.Contains("library.search", html);
        Assert.Contains("library.getCollections", html);
        Assert.Contains("reading.getProgress", html);
        Assert.Contains("pickFiles", html);
        Assert.Contains("copyText", html);
        Assert.Contains("reportDiagnostic", html);
        Assert.Contains("unhandledrejection", html);
        Assert.Contains("'ui.log'", html);
        Assert.DoesNotContain("src=\"app.js\"", html);
        Assert.DoesNotContain("href=\"app.css\"", html);
    }

    [Fact]
    public void PageAssetsCannotEscapeThePluginDirectory()
    {
        using var scope = new TempPluginScope();
        var plugin = scope.Install(("ui/app.html", "<html><body><script src=\"../../secret.js\"></script></body></html>"));

        Assert.Throws<PluginPackageException>(() => PluginPageComposer.Compose(
            plugin,
            "ui/app.html",
            new PluginPageContext(plugin.Id, "1.0.0", null, [], "zh-CN", "1.2.0"),
            PluginTheme.Light));
    }

    // ---- bridge --------------------------------------------------------

    [Fact]
    public async Task BridgeRejectsUnknownMethodsAndMissingPermissions()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.None);

        var unknown = await harness.CallAsync("""{"id":1,"method":"nope","params":{}}""");
        Assert.False(unknown.Ok);
        Assert.Contains("未知", unknown.Error);

        var library = await harness.CallAsync("""{"id":2,"method":"library.getBooks","params":{}}""");
        Assert.False(library.Ok);
        Assert.Contains("没有读取书库的权限", library.Error);

        var log = await harness.CallAsync(Request(3, "ui.log",
            new Dictionary<string, object?> { ["level"] = "warn", ["message"] = "页面提示" }));
        Assert.True(log.Ok);
        Assert.Contains(("warn", "页面提示"), harness.Ui.Logs);

        Assert.Null(await harness.Bridge.HandleAsync("not json", CancellationToken.None));
    }

    [Fact]
    public async Task WholeLibrarySearchPagesAndRequiresLibraryAll()
    {
        using var scope = new TempPluginScope();
        Guid alphaId = Guid.Empty;
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.Write,
            manifestJson: BridgeManifest(PluginLibraryAccess.Write, libraryAll: true),
            seed: async library =>
            {
                var alpha = await AddBookAsync(library, scope.Root, "Alpha Guide", "Ann");
                alphaId = alpha.Id;
                await AddBookAsync(library, scope.Root, "Beta Notes", "Bob");
                return [];
            });

        var reply = await harness.CallAsync(Request(10, "library.search", new Dictionary<string, object?>
        {
            ["query"] = "Alpha",
            ["pageSize"] = 1
        }));
        Assert.True(reply.Ok);
        var result = reply.Result!.Value;
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, result.GetProperty("pageSize").GetInt32());
        var books = result.GetProperty("books");
        Assert.Equal(1, books.GetArrayLength());
        Assert.Equal(alphaId.ToString("D"), books[0].GetProperty("id").GetString());
        Assert.True(books[0].GetProperty("favorite").ValueKind is JsonValueKind.False or JsonValueKind.True);
        Assert.False(string.IsNullOrWhiteSpace(books[0].GetProperty("readingStatus").GetString()));

        using var otherScope = new TempPluginScope();
        await using var denied = await BridgeHarness.CreateAsync(otherScope, PluginLibraryAccess.Read);
        var deniedReply = await denied.CallAsync(Request(11, "library.search", new Dictionary<string, object?>()));
        Assert.False(deniedReply.Ok);
        Assert.Contains("整个书库", deniedReply.Error);
    }

    [Fact]
    public async Task CollectionsCanBeCreatedAndAssignedThroughLibraryAll()
    {
        using var scope = new TempPluginScope();
        Guid bookId = Guid.Empty;
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.Write,
            manifestJson: BridgeManifest(PluginLibraryAccess.Write, libraryAll: true),
            seed: async library =>
            {
                bookId = (await AddBookAsync(library, scope.Root, "Collection Book", "Ann")).Id;
                return [];
            });

        var created = await harness.CallAsync(Request(20, "library.createCollection",
            new Dictionary<string, object?> { ["name"] = "开发分组" }));
        Assert.True(created.Ok, created.Error);
        var collectionId = created.Result!.Value.GetProperty("id").GetString()!;

        var added = await harness.CallAsync(Request(21, "library.addToCollection", new Dictionary<string, object?>
        {
            ["bookId"] = bookId.ToString("D"),
            ["collectionId"] = collectionId
        }));
        Assert.True(added.Ok, added.Error);

        var listed = await harness.CallAsync(Request(22, "library.search", new Dictionary<string, object?>
        {
            ["collectionId"] = collectionId
        }));
        Assert.True(listed.Ok);
        Assert.Equal(1, listed.Result!.Value.GetProperty("totalCount").GetInt32());

        var removed = await harness.CallAsync(Request(23, "library.removeFromCollection", new Dictionary<string, object?>
        {
            ["bookId"] = bookId.ToString("D"),
            ["collectionId"] = collectionId
        }));
        Assert.True(removed.Ok);

        var listedAgain = await harness.CallAsync(Request(24, "library.search", new Dictionary<string, object?>
        {
            ["collectionId"] = collectionId
        }));
        Assert.Equal(0, listedAgain.Result!.Value.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ReadingDataIsExposedReadOnly()
    {
        using var scope = new TempPluginScope();
        Book? book = null;
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.Write,
            manifestJson: BridgeManifest(PluginLibraryAccess.Write, libraryAll: true),
            withReader: true,
            seed: async library =>
            {
                book = await AddBookAsync(library, scope.Root, "Reading Book", "Ann");
                return [];
            });
        Assert.NotNull(book);
        Assert.NotNull(harness.Reader);
        var fileId = book!.Files[0].Id;
        await harness.Reader!.SaveProgressAsync(new ReaderProgressRow(
            book.Id, fileId, "chapter-2.xhtml", null, 2, 10, 42.5, 0, DateTimeOffset.UtcNow));
        await harness.Reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            BookId = book.Id,
            BookFileId = fileId,
            ChapterPath = "chapter-2.xhtml",
            SelectedText = "highlighted text",
            Note = "my note"
        });
        await harness.Reader.SaveBookmarkAsync(new ReaderBookmark
        {
            BookId = book.Id,
            BookFileId = fileId,
            ChapterPath = "chapter-2.xhtml",
            ChapterIndex = 2,
            Title = "Bookmark",
            Quote = "quoted"
        });

        var progress = await harness.CallAsync(Request(30, "reading.getProgress",
            new Dictionary<string, object?> { ["bookId"] = book.Id.ToString("D") }));
        Assert.True(progress.Ok);
        var progressFile = progress.Result!.Value.GetProperty("files")[0];
        Assert.Equal(42.5, progressFile.GetProperty("progressPercent").GetDouble());

        var annotations = await harness.CallAsync(Request(31, "reading.getAnnotations",
            new Dictionary<string, object?> { ["bookId"] = book.Id.ToString("D") }));
        Assert.True(annotations.Ok);
        var annotation = annotations.Result!.Value.GetProperty("annotations")[0];
        Assert.Equal("highlighted text", annotation.GetProperty("selectedText").GetString());
        Assert.Equal("my note", annotation.GetProperty("note").GetString());

        var bookmarks = await harness.CallAsync(Request(32, "reading.getBookmarks",
            new Dictionary<string, object?> { ["bookId"] = book.Id.ToString("D") }));
        Assert.True(bookmarks.Ok);
        Assert.Equal("Bookmark", bookmarks.Result!.Value.GetProperty("bookmarks")[0].GetProperty("title").GetString());

        var deniedScope = new TempPluginScope();
        await using var denied = await BridgeHarness.CreateAsync(deniedScope, PluginLibraryAccess.Read);
        var deniedReply = await denied.CallAsync(Request(33, "reading.getProgress",
            new Dictionary<string, object?> { ["bookId"] = book.Id.ToString("D") }));
        Assert.False(deniedReply.Ok);
        Assert.Contains("整个书库", deniedReply.Error);
    }

    [Fact]
    public async Task PickedFilesCanBeImportedOnlyOnce()
    {
        using var scope = new TempPluginScope();
        var manifest = """
            {
              "id": "com.example.importer",
              "name": "导入工具",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": { "import": true, "libraryAll": true },
              "contributes": { "settings": { "page": "ui/settings.html" } }
            }
            """;
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.None,
            manifestJson: manifest);
        var source = Path.Combine(scope.Root, "picked-book.txt");
        await File.WriteAllTextAsync(source, "picked book content");
        harness.Ui.PickFilesResult = [new PluginPickedFile("picked-book.txt", source)];

        var picked = await harness.CallAsync(Request(40, "ui.pickFiles", new Dictionary<string, object?>
        {
            ["extensions"] = new[] { ".txt" }
        }));
        Assert.True(picked.Ok);
        Assert.Equal(source, picked.Result!.Value.GetProperty("files")[0].GetProperty("path").GetString());

        var imported = await harness.CallAsync(Request(41, "library.importFiles", new Dictionary<string, object?>
        {
            ["paths"] = new[] { source }
        }));
        Assert.True(imported.Ok);
        Assert.Equal(1, imported.Result!.Value.GetProperty("successCount").GetInt32());

        var repeated = await harness.CallAsync(Request(42, "library.importFiles", new Dictionary<string, object?>
        {
            ["paths"] = new[] { source }
        }));
        Assert.False(repeated.Ok);
        Assert.Contains("文件选择器", repeated.Error);
    }

    [Fact]
    public async Task ClipboardAndSaveFileRoundTripThroughTheHost()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.None);

        var copied = await harness.CallAsync(Request(50, "ui.copyText",
            new Dictionary<string, object?> { ["text"] = "hello clipboard" }));
        Assert.True(copied.Ok);
        Assert.Equal("hello clipboard", harness.Ui.ClipboardText);

        var read = await harness.CallAsync(Request(51, "ui.readText", new Dictionary<string, object?>()));
        Assert.True(read.Ok);
        Assert.Equal("hello clipboard", read.Result!.Value.GetProperty("text").GetString());

        harness.Ui.SaveFileResult = new PluginPickedFile("export.txt", "/tmp/export.txt");
        var saved = await harness.CallAsync(Request(52, "ui.saveFile", new Dictionary<string, object?>
        {
            ["fileName"] = "export.txt",
            ["text"] = "exported"
        }));
        Assert.True(saved.Ok);
        Assert.Equal("/tmp/export.txt", saved.Result!.Value.GetProperty("path").GetString());
        Assert.Equal("exported", Encoding.UTF8.GetString(Assert.Single(harness.Ui.SavedFiles)));
    }

    [Fact]
    public async Task BridgeUpdatesOnlyAllowedFieldsOfOpenedBooks()
    {
        using var scope = new TempPluginScope();
        Book book = null!;
        Book otherBook = null!;
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.Write, seed: async library =>
        {
            book = await AddBookAsync(library, scope.Root, "三体", "刘慈欣");
            return [book.Id];
        });

        var reply = await harness.CallAsync(Request(1, "library.updateBook", new Dictionary<string, object?>
        {
            ["id"] = book.Id.ToString("D"),
            ["patch"] = new Dictionary<string, object?>
            {
                ["publisher"] = "重庆出版社",
                ["pageCount"] = "302",
                ["rating"] = 9.3,
                ["ratingCount"] = 123456
            }
        }));
        Assert.True(reply.Ok, reply.Error);
        var saved = await harness.Library.GetBookAsync(book.Id);
        Assert.Equal("重庆出版社", saved!.Publisher);
        Assert.Equal("302", saved.PageCount);
        Assert.Equal(9.3, saved.OnlineRating);
        Assert.Equal(123456, saved.OnlineRatingCount);

        var unknownField = await harness.CallAsync(Request(2, "library.updateBook", new Dictionary<string, object?>
        {
            ["id"] = book.Id.ToString("D"),
            ["patch"] = new Dictionary<string, object?> { ["coverPath"] = "/etc/passwd" }
        }));
        Assert.False(unknownField.Ok);

        var outOfRange = await harness.CallAsync(Request(3, "library.updateBook", new Dictionary<string, object?>
        {
            ["id"] = book.Id.ToString("D"),
            ["patch"] = new Dictionary<string, object?> { ["rating"] = 11 }
        }));
        Assert.False(outOfRange.Ok);

        otherBook = await AddBookAsync(harness.Library, scope.Root, "另一本书", "作者");
        var notOpened = await harness.CallAsync(Request(4, "library.updateBook", new Dictionary<string, object?>
        {
            ["id"] = otherBook.Id.ToString("D"),
            ["patch"] = new Dictionary<string, object?> { ["title"] = "x" }
        }));
        Assert.False(notOpened.Ok);
        Assert.Contains("只能访问", notOpened.Error);
    }

    [Fact]
    public async Task BridgeHttpIsLimitedToDeclaredOrigins()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/redirect")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://evil.example/secret");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>ok</html>", Encoding.UTF8, "text/html")
            };
        });
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.None, handler);

        var allowed = await harness.CallAsync("""{"id":1,"method":"http.fetch","params":{"url":"https://book.example.com/subject_search?search_text=x"}}""");
        Assert.True(allowed.Ok);
        Assert.Equal(200, allowed.Result!.Value.GetProperty("status").GetInt32());
        Assert.Equal("<html>ok</html>", allowed.Result!.Value.GetProperty("body").GetString());

        var blocked = await harness.CallAsync("""{"id":2,"method":"http.fetch","params":{"url":"https://example.com/"}}""");
        Assert.False(blocked.Ok);
        Assert.Contains("没有访问", blocked.Error);

        var insecure = await harness.CallAsync("""{"id":3,"method":"http.fetch","params":{"url":"http://book.example.com/"}}""");
        Assert.False(insecure.Ok);

        // A redirect must not carry the request past the declared origins.
        var redirect = await harness.CallAsync("""{"id":4,"method":"http.fetch","params":{"url":"https://book.example.com/redirect"}}""");
        Assert.False(redirect.Ok);
        Assert.Contains("没有访问", redirect.Error);
    }

    [Fact]
    public async Task BridgeNetworkPermissionFollowsTheApplicationSetting()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.None, networkEnabled: false);

        var reply = await harness.CallAsync("""{"id":1,"method":"http.fetch","params":{"url":"https://book.example.com/"}}""");
        Assert.False(reply.Ok);
        Assert.Contains("网络访问", reply.Error);
    }

    [Fact]
    public async Task BridgeStoragePersistsJsonValues()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.None);

        Assert.True((await harness.CallAsync("""{"id":1,"method":"storage.set","params":{"key":"settings","value":{"interval":1500}}}""")).Ok);
        var read = await harness.CallAsync("""{"id":2,"method":"storage.get","params":{"key":"settings"}}""");
        Assert.Equal(1500, read.Result!.Value.GetProperty("interval").GetInt32());

        Assert.True((await harness.CallAsync("""{"id":3,"method":"storage.remove","params":{"key":"settings"}}""")).Ok);
        var removed = await harness.CallAsync("""{"id":4,"method":"storage.get","params":{"key":"settings"}}""");
        Assert.True(removed.Ok);
        Assert.Equal(JsonValueKind.Null, removed.Result!.Value.ValueKind);
    }

    // ---- helpers -------------------------------------------------------

    private sealed class TempPluginScope : IDisposable
    {

        public TempPluginScope()
        {
            Root = TestHelpers.CreateTempDirectory();
        }

        public string Root { get; }

        public string WritePackage(string? manifest, params (string Path, string Content)[] files) =>
            WritePackage(manifest, files, "package");

        public string WritePackage(string? manifest, (string Path, string Content)[] files, string name)
        {
            var path = Path.Combine(Root, $"{name}-{++_packagesCount}.kkplugin");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            if (manifest is not null) Add(archive, PluginManifest.FileName, manifest);
            foreach (var (filePath, content) in files) Add(archive, filePath, content);
            return path;
        }

        private int _packagesCount;

        public InstalledPlugin Install(params (string Path, string Content)[] files)
        {
            var paths = new AppPaths(Path.Combine(Root, "app"));
            var manager = new PluginManager(paths);
            // The shared manifest declares a settings page; give every fixture one.
            var entries = files
                .Concat(new[] { ("ui/settings.html", "<html></html>") })
                .GroupBy(file => file.Item1)
                .Select(group => group.First())
                .ToArray();
            var package = WritePackage(ValidManifest, entries, "installed");
            return manager.Install(package);
        }

        private static void Add(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path.Replace('\\', '/'));
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
        }

        public void Dispose() => TestHelpers.TryDelete(Root);
    }


    /// <summary>A plugin manifest for bridge tests. Read/write plugins also declare a book action.</summary>
    private static string BridgeManifest(PluginLibraryAccess access, bool libraryAll = false)
    {
        var library = access switch
        {
            PluginLibraryAccess.Read => "\"library\": \"read\",",
            PluginLibraryAccess.Write => "\"library\": \"write\",",
            _ => string.Empty
        };
        if (libraryAll) library += "\"libraryAll\": true,";
        var actions = access == PluginLibraryAccess.None
            ? "\"settings\": { \"page\": \"ui/settings.html\" }"
            : "\"bookActions\": [ { \"id\": \"match\", \"title\": \"t\", \"page\": \"ui/app.html\" } ],"
              + " \"settings\": { \"page\": \"ui/settings.html\" }";
        return $$"""
            {
              "id": "com.example.test",
              "name": "测试插件",
              "version": "1.2.0",
              "apiVersion": 1,
              "permissions": {
                "network": ["https://book.example.com", "https://*.images.example.com"],
                {{library}}
                "storage": true
              },
              "contributes": { {{actions}} }
            }
            """;
    }

    private static string Request(long id, string method, Dictionary<string, object?> parameters) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        });

    private static async Task<Book> AddBookAsync(IBookLibraryService library, string root, string title, string authors)
    {
        var source = Path.Combine(root, $"{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(source, $"{title} — {authors}");
        var result = await library.ImportAsync([source]);
        var id = result.Items.First(item => item.Succeeded).BookId!.Value;
        var book = await library.GetBookAsync(id);
        book!.Title = title;
        book.Authors = authors;
        await library.UpdateMetadataAsync(book);
        return book;
    }

    private sealed class BridgeHarness : IAsyncDisposable
    {
        private BridgeHarness(
            PluginBridge bridge,
            IBookLibraryService library,
            AppPaths paths,
            SilentUiHost ui,
            PluginPageContext context,
            ReaderDataService? reader)
        {
            Bridge = bridge;
            Library = library;
            Paths = paths;
            Ui = ui;
            Context = context;
            Reader = reader;
        }

        public PluginBridge Bridge { get; }
        public IBookLibraryService Library { get; }
        public AppPaths Paths { get; }
        public SilentUiHost Ui { get; }
        public PluginPageContext Context { get; }
        public ReaderDataService? Reader { get; }

        public static async Task<BridgeHarness> CreateAsync(
            TempPluginScope scope,
            PluginLibraryAccess access,
            HttpMessageHandler? handler = null,
            bool networkEnabled = true,
            Func<IBookLibraryService, Task<IReadOnlyList<Guid>>>? seed = null,
            string? manifestJson = null,
            Func<string, bool>? approveHost = null,
            bool approveImport = true,
            IPluginBookTaskHost? bookTasks = null,
            TaskCompletionSource<bool>? networkApproval = null,
            bool withReader = false)
        {
            var manifest = manifestJson ?? BridgeManifest(access);
            var appPaths = new AppPaths(Path.Combine(scope.Root, "app-" + Guid.NewGuid().ToString("N")));
            var manager = new PluginManager(appPaths);
            var plugin = manager.Install(scope.WritePackage(
                manifest,
                [("ui/app.html", "<html></html>"), ("ui/settings.html", "<html></html>")],
                "bridge-" + Guid.NewGuid().ToString("N")[..8]));

            var library = new SqliteBookLibraryService(appPaths, new BookMetadataService());
            await library.InitializeAsync();
            var bookIds = seed is null ? [] : await seed(library);
            var context = new PluginPageContext(plugin.Id, plugin.Manifest.Version, "match", bookIds, "zh-CN", "1.2.0");
            var ui = new SilentUiHost
            {
                NetworkEnabled = networkEnabled,
                ApproveHost = approveHost ?? (_ => true),
                ApproveImport = approveImport,
                NetworkApprovalGate = networkApproval
            };
            ReaderDataService? reader = null;
            if (withReader)
            {
                reader = new ReaderDataService(appPaths);
                await reader.InitializeAsync();
            }
            var bridge = new PluginBridge(
                plugin,
                context,
                library,
                appPaths,
                new PluginStorage(manager.GetDataDirectory(plugin.Id)),
                ui,
                new PluginSecrets(manager.GetDataDirectory(plugin.Id), new TestHelpers.PlaintextSecretProtector()),
                handler,
                bookTasks,
                reader);
            return new BridgeHarness(bridge, library, appPaths, ui, context, reader);
        }

        public async Task<Reply> CallAsync(string message)
        {
            var handled = await Bridge.HandleAsync(message, CancellationToken.None);
            Assert.NotNull(handled);
            using var document = JsonDocument.Parse(handled!.Value.Reply);
            var root = document.RootElement;
            return new Reply(
                root.GetProperty("ok").GetBoolean(),
                root.TryGetProperty("result", out var result) ? result.Clone() : null,
                root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? string.Empty : string.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void ManifestParsesBookTaskPermissions()
    {
        var manifest = PluginManifest.Parse("""
            {
              "id": "com.example.tasks",
              "name": "书籍工具",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": {
                "library": "read",
                "translation": true,
                "pinyin": true
              }
            }
            """);
        Assert.True(manifest.AllowsTranslation);
        Assert.True(manifest.AllowsPinyin);
    }

    [Fact]
    public void ManifestRequiresLibraryAccessForBookTasks()
    {
        var exception = Assert.Throws<PluginPackageException>(() => PluginManifest.Parse("""
            {
              "id": "com.example.tasks",
              "name": "书籍工具",
              "version": "1.0.0",
              "apiVersion": 1,
              "permissions": { "translation": true, "pinyin": true }
            }
            """));
        Assert.Contains("library", exception.Message);
    }

    // ---- book tasks ----------------------------------------------------

    private const string TaskManifest = """
        {
          "id": "com.example.tasks",
          "name": "书籍工具",
          "version": "1.0.0",
          "apiVersion": 1,
          "permissions": {
            "library": "read",
            "translation": true,
            "pinyin": true,
            "storage": true
          },
          "contributes": {
            "bookActions": [
              { "id": "translate", "title": "翻译", "page": "ui/app.html" },
              { "id": "pinyin", "title": "注音", "page": "ui/app.html" }
            ],
            "settings": { "page": "ui/settings.html" }
          }
        }
        """;

    [Fact]
    public async Task BridgeTranslatesBookThroughTheHostAndReportsProgress()
    {
        using var scope = new TempPluginScope();
        var host = new FakeBookTaskHost();
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.Read,
            seed: library => SeedBookAsync(library, scope.Root, "测试书", "作者"),
            manifestJson: TaskManifest,
            bookTasks: host);

        var bookId = Assert.Single(harness.Context.BookIds);
        var reply = await harness.CallAsync(Request(1, "library.translateBook", new()
        {
            ["bookId"] = bookId.ToString("D"),
            ["provider"] = "ai",
            ["sourceLanguage"] = "en",
            ["targetLanguage"] = "zh-CN",
            ["translated"] = true,
            ["bilingual"] = false,
            ["resume"] = true
        }));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(1, reply.Result!.Value.GetProperty("outputCount").GetInt32());
        var request = Assert.IsType<PluginTranslationRequest>(host.LastTranslation);
        Assert.Equal("ai", request.Provider);
        Assert.Equal("en", request.SourceLanguage);
        Assert.Equal("zh-CN", request.TargetLanguage);
        Assert.True(request.Translated);
        Assert.False(request.Bilingual);

        await Until(() => harness.Ui.Events.Any(item =>
            item.Name == "task.progress"
            && item.Payload?["task"]?.GetValue<string>() == "translation"));
        var progress = harness.Ui.Events.First(item =>
            item.Name == "task.progress"
            && item.Payload?["task"]?.GetValue<string>() == "translation");
        Assert.Equal(50, progress.Payload!["percent"]!.GetValue<double>());
        Assert.Equal(1, progress.Payload["processed"]!.GetValue<int>());
        Assert.Equal(2, progress.Payload["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task BridgeRejectsBookTasksWithoutPermission()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.Read,
            seed: library => SeedBookAsync(library, scope.Root, "测试书", "作者"),
            bookTasks: new FakeBookTaskHost());

        var bookId = Assert.Single(harness.Context.BookIds);
        var reply = await harness.CallAsync(Request(1, "library.translateBook", new()
        {
            ["bookId"] = bookId.ToString("D"),
            ["targetLanguage"] = "zh-CN"
        }));
        Assert.False(reply.Ok);
        Assert.Contains("权限", reply.Error);

        var pinyin = await harness.CallAsync(Request(2, "library.annotatePinyin", new()
        {
            ["bookId"] = bookId.ToString("D")
        }));
        Assert.False(pinyin.Ok);
        Assert.Contains("权限", pinyin.Error);
    }

    [Fact]
    public async Task BridgePinyinStatusAndCancelReachTheHost()
    {
        using var scope = new TempPluginScope();
        var host = new FakeBookTaskHost { WaitForCancellation = true };
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.Read,
            seed: library => SeedBookAsync(library, scope.Root, "测试书", "作者"),
            manifestJson: TaskManifest,
            bookTasks: host);

        var status = await harness.CallAsync(Request(1, "pinyin.status", new()));
        Assert.True(status.Ok, status.Error);
        Assert.True(status.Result!.Value.GetProperty("g2pwInstalled").GetBoolean());

        var bookId = Assert.Single(harness.Context.BookIds);
        var running = harness.Bridge.HandleAsync(
            Request(2, "library.annotatePinyin", new()
            {
                ["bookId"] = bookId.ToString("D"),
                ["engine"] = "dotnet-g2p"
            }),
            CancellationToken.None);
        await Until(() => host.Started);
        // The window uses this to ask before closing and cancelling the job.
        Assert.True(harness.Bridge.HasRunningTask);
        var cancel = await harness.CallAsync(Request(3, "library.cancelTask", new()));
        Assert.True(cancel.Ok);
        Assert.True(cancel.Result!.Value.GetProperty("cancelling").GetBoolean());

        var handled = await running;
        Assert.False(harness.Bridge.HasRunningTask);
        using var document = JsonDocument.Parse(handled!.Value.Reply);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(document.RootElement.GetProperty("result").GetProperty("cancelled").GetBoolean());
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    private static async Task<IReadOnlyList<Guid>> SeedBookAsync(
        IBookLibraryService library,
        string root,
        string title,
        string authors)
    {
        var book = await AddBookAsync(library, root, title, authors);
        return [book.Id];
    }

    // ---- import, secrets, runtime hosts --------------------------------

    private const string FeatureManifest = """
        {
          "id": "com.example.features",
          "name": "功能插件",
          "version": "1.0.0",
          "apiVersion": 1,
          "permissions": {
            "network": ["https://book.example.com"],
            "import": true,
            "secrets": true,
            "storage": true
          },
          "contributes": {
            "app": { "title": "工具", "page": "ui/app.html" },
            "settings": { "page": "ui/settings.html" }
          }
        }
        """;

    [Fact]
    public async Task BridgeSecretsRoundTripAndStayEncrypted()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(
            scope, PluginLibraryAccess.None, manifestJson: FeatureManifest);

        var set = await harness.CallAsync(Request(1, "secrets.set", new()
        {
            ["key"] = "password",
            ["value"] = "s3cret-value"
        }));
        Assert.True(set.Ok, set.Error);

        var get = await harness.CallAsync(Request(2, "secrets.get", new() { ["key"] = "password" }));
        Assert.True(get.Ok);
        Assert.Equal("s3cret-value", get.Result!.Value.GetString());

        var file = Path.Combine(harness.Paths.PluginData, "com.example.features", "secrets.json");
        Assert.True(File.Exists(file));
        Assert.DoesNotContain("s3cret-value", await File.ReadAllTextAsync(file));

        var remove = await harness.CallAsync(Request(3, "secrets.remove", new() { ["key"] = "password" }));
        Assert.True(remove.Ok);
        var missing = await harness.CallAsync(Request(4, "secrets.get", new() { ["key"] = "password" }));
        Assert.Equal(JsonValueKind.Null, missing.Result!.Value.ValueKind);
    }

    [Fact]
    public async Task SecretsRequireThePermission()
    {
        using var scope = new TempPluginScope();
        await using var harness = await BridgeHarness.CreateAsync(scope, PluginLibraryAccess.None);

        var reply = await harness.CallAsync(Request(1, "secrets.set", new()
        {
            ["key"] = "password",
            ["value"] = "x"
        }));
        Assert.False(reply.Ok);
        Assert.Contains("没有保存账号信息", reply.Error);
    }

    [Fact]
    public async Task RuntimeHostApprovalExtendsNetworkAccess()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.None,
            handler,
            manifestJson: FeatureManifest,
            approveHost: host => host == "mirror.example.net");

        var denied = await harness.CallAsync(Request(1, "http.fetch", new()
        {
            ["url"] = "https://mirror.example.net/api"
        }));
        Assert.False(denied.Ok);
        Assert.Contains("没有访问", denied.Error);

        var granted = await harness.CallAsync(Request(2, "network.requestAccess", new()
        {
            ["origin"] = "https://mirror.example.net"
        }));
        Assert.True(granted.Ok, granted.Error);
        Assert.True(granted.Result!.Value.GetProperty("granted").GetBoolean());
        Assert.Contains("mirror.example.net", harness.Ui.RequestedHosts);

        var allowed = await harness.CallAsync(Request(3, "http.fetch", new()
        {
            ["url"] = "https://mirror.example.net/api"
        }));
        Assert.True(allowed.Ok, allowed.Error);

        var rejected = await harness.CallAsync(Request(4, "network.requestAccess", new()
        {
            ["origin"] = "https://other.example.net"
        }));
        Assert.True(rejected.Ok);
        Assert.False(rejected.Result!.Value.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentNetworkRequestsShareOneApprovalDialog()
    {
        using var scope = new TempPluginScope();
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.None,
            manifestJson: FeatureManifest,
            networkApproval: approval);

        var calls = Enumerable.Range(1, 6).Select(index => harness.CallAsync(
            Request(index, "network.requestAccess", new()
            {
                ["origin"] = "https://mirror.example.net"
            }))).ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (PromptCount() == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        // Give the remaining calls time to reach the same host.
        await Task.Delay(100);
        Assert.Equal(1, PromptCount());

        approval.SetResult(true);
        foreach (var call in calls)
        {
            var reply = await call;
            Assert.True(reply.Ok, reply.Error);
            Assert.True(reply.Result!.Value.GetProperty("granted").GetBoolean());
        }
        Assert.Equal(1, PromptCount());

        int PromptCount()
        {
            lock (harness.Ui.RequestedHosts) return harness.Ui.RequestedHosts.Count;
        }
    }

    [Fact]
    public async Task ImportDownloadAddsTheFileToTheLibrary()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("/book.txt", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("imported book body")
            };
        });
        await using var harness = await BridgeHarness.CreateAsync(
            scope, PluginLibraryAccess.None, handler, manifestJson: FeatureManifest);

        var reply = await harness.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/book.txt",
            ["fileName"] = "book.txt"
        }));
        Assert.True(reply.Ok, reply.Error);
        Assert.True(reply.Result!.Value.GetProperty("imported").GetBoolean());
        Assert.True(reply.Result.Value.GetProperty("added").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, reply.Result.Value.GetProperty("bookId").ValueKind);
        Assert.Contains(harness.Ui.Events, entry => entry.Name == "import.progress");

        var books = await harness.Library.SearchAsync();
        Assert.Single(books);
    }

    [Fact]
    public async Task ImportDownloadAsksBeforeFollowingARedirectToAnUndeclaredHost()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "book.example.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://cdn.example.net/file/book.txt");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("cdn book body") };
        });
        await using var harness = await BridgeHarness.CreateAsync(
            scope, PluginLibraryAccess.None, handler, manifestJson: FeatureManifest);

        var reply = await harness.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/dl/1",
            ["fileName"] = "book.txt"
        }));
        Assert.True(reply.Ok, reply.Error);
        Assert.True(reply.Result!.Value.GetProperty("imported").GetBoolean());
        Assert.Contains("cdn.example.net", harness.Ui.RequestedHosts);

        using var deniedScope = new TempPluginScope();
        await using var denied = await BridgeHarness.CreateAsync(
            deniedScope, PluginLibraryAccess.None, handler, manifestJson: FeatureManifest, approveHost: _ => false);
        var refused = await denied.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/dl/1",
            ["fileName"] = "book.txt"
        }));
        Assert.False(refused.Ok);
        Assert.Contains("cdn.example.net", refused.Error);
        Assert.Empty(await denied.Library.SearchAsync());
    }

    [Fact]
    public async Task ImportDownloadRejectsAnHtmlPageInPlaceOfTheBook()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>daily limit reached</html>", System.Text.Encoding.UTF8, "text/html")
        });
        await using var harness = await BridgeHarness.CreateAsync(
            scope, PluginLibraryAccess.None, handler, manifestJson: FeatureManifest);

        var reply = await harness.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/book.epub",
            ["fileName"] = "book.epub"
        }));
        Assert.False(reply.Ok);
        Assert.Contains("拒绝", reply.Error);
        Assert.Empty(await harness.Library.SearchAsync());
    }

    [Fact]
    public async Task ImportDownloadCancelsWhenTheUserDeclines()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body")
        });
        await using var harness = await BridgeHarness.CreateAsync(
            scope,
            PluginLibraryAccess.None,
            handler,
            manifestJson: FeatureManifest,
            approveImport: false);

        var reply = await harness.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/book.txt",
            ["fileName"] = "book.txt"
        }));
        Assert.True(reply.Ok, reply.Error);
        Assert.True(reply.Result!.Value.GetProperty("cancelled").GetBoolean());
        Assert.Empty(await harness.Library.SearchAsync());
    }

    [Fact]
    public async Task ImportRequiresThePermissionAndAnAllowedExtension()
    {
        using var scope = new TempPluginScope();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("x")
        });
        await using var harness = await BridgeHarness.CreateAsync(
            scope, PluginLibraryAccess.None, handler);

        var forbidden = await harness.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/book.txt",
            ["fileName"] = "book.txt"
        }));
        Assert.False(forbidden.Ok);
        Assert.Contains("没有导入书籍的权限", forbidden.Error);

        using var scope2 = new TempPluginScope();
        await using var harness2 = await BridgeHarness.CreateAsync(
            scope2, PluginLibraryAccess.None, handler, manifestJson: FeatureManifest);
        var badExtension = await harness2.CallAsync(Request(1, "library.importDownload", new()
        {
            ["url"] = "https://book.example.com/book.exe",
            ["fileName"] = "book.exe"
        }));
        Assert.False(badExtension.Ok);
        Assert.Contains("常见电子书", badExtension.Error);
    }

    private sealed record Reply(bool Ok, JsonElement? Result, string Error);

    private sealed class FakeBookTaskHost : IPluginBookTaskHost
    {
        public bool WaitForCancellation { get; init; }
        public bool Started { get; private set; }
        public PluginTranslationRequest? LastTranslation { get; private set; }

        public async Task<JsonObject> TranslateBookAsync(
            Guid bookId,
            PluginTranslationRequest request,
            IProgress<PluginTaskProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastTranslation = request;
            progress?.Report(new PluginTaskProgress("translate", "处理中", 50, 1, 2));
            started();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new JsonObject { ["outputCount"] = 1, ["added"] = 1, ["skipped"] = 0, ["failed"] = 0 };
        }

        public async Task<JsonObject> AnnotatePinyinAsync(
            Guid bookId,
            PluginPinyinRequest request,
            IProgress<PluginTaskProgress>? progress,
            CancellationToken cancellationToken)
        {
            started();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new JsonObject { ["scannedPages"] = 1, ["characters"] = 10 };
        }

        public PluginPinyinStatus GetPinyinStatus() => new(true);

        public Task DownloadPinyinModelAsync(
            IProgress<PluginTaskProgress>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        private void started() => Started = true;
    }

    private sealed class SilentUiHost : IPluginUiHost
    {
        public bool NetworkEnabled { get; init; } = true;
        public void Close() { }
        public void Notify(string message) { }
        public void SetTitle(string title) { }
        public void OpenExternal(Uri uri) { }
        public Func<string, bool> ApproveHost { get; init; } = _ => true;
        public bool ApproveImport { get; init; } = true;
        public TaskCompletionSource<bool>? NetworkApprovalGate { get; init; }
        public List<string> RequestedHosts { get; } = [];
        public List<(string Name, JsonNode? Payload)> Events { get; } = [];
        public List<(string Level, string Message)> Logs { get; } = [];

        public void BooksChanged(IReadOnlyCollection<Guid> bookIds) { }

        public Task<bool> RequestNetworkAccessAsync(string host)
        {
            lock (RequestedHosts) RequestedHosts.Add(host);
            return NetworkApprovalGate is null
                ? Task.FromResult(ApproveHost(host))
                : NetworkApprovalGate.Task;
        }

        public Task<bool> ConfirmImportAsync(string fileName, long sizeBytes) =>
            Task.FromResult(ApproveImport);

        public void RaiseEvent(string name, JsonNode payload)
        {
            lock (Events) Events.Add((name, payload));
        }

        public void Log(string level, string message)
        {
            lock (Logs) Logs.Add((level, message));
        }

        public IReadOnlyList<PluginPickedFile> PickFilesResult { get; set; } = [];
        public PluginPickedFile? SaveFileResult { get; set; }
        public string? ClipboardText { get; set; }
        public List<byte[]> SavedFiles { get; } = [];

        public Task<IReadOnlyList<PluginPickedFile>> PickFilesAsync(
            bool allowMultiple,
            IReadOnlyList<string> extensions) => Task.FromResult(PickFilesResult);

        public Task<PluginPickedFile?> SaveFileAsync(string suggestedName, byte[] content)
        {
            SavedFiles.Add(content);
            return Task.FromResult(SaveFileResult);
        }

        public Task CopyTextAsync(string text)
        {
            ClipboardText = text;
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(ClipboardText);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
