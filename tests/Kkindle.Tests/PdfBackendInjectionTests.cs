using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class PdfBackendInjectionTests
{
    [Fact]
    public async Task MetadataAndIndexingUseTheInjectedBackendAndDisposeIt()
    {
        var factory = new FakePdfDocumentFactory();
        var metadata = await new BookMetadataService(factory).ReadMetadataAsync("fake.pdf");
        Assert.Equal("Injected title", metadata.Title);
        Assert.Equal("Injected author", metadata.Authors);
        Assert.Equal(new byte[] { 1, 2, 3 }, metadata.CoverBytes);
        var pages = await new PdfTextService(factory).ExtractAsync("fake.pdf");
        Assert.Equal(new[] { "Page 1", "Page 2" }, pages.Select(page => page.Text));
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(2, factory.DisposeCount);
    }

    [Fact]
    public async Task ApplicationLibraryImportUsesTheInjectedMetadataBackend()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-pdf-backend", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        try
        {
            var source = Path.Combine(root, "input.pdf");
            await File.WriteAllTextAsync(source, "fake backend input");
            var factory = new FakePdfDocumentFactory();
            using var modules = new ApplicationModules(new ApplicationModuleOptions
            {
                Paths = paths,
                SecretProtector = new TestProtector(),
                FormatConverter = new UnavailableBookFormatConverter(),
                MetadataService = new BookMetadataService(factory)
            });
            await modules.Library.Books.InitializeAsync();
            var result = await modules.Library.Books.ImportAsync([source]);
            Assert.Equal(1, result.AddedCount);
            Assert.Equal("Injected title", Assert.Single(await modules.Library.Books.SearchAsync()).Title);
            Assert.Equal(1, factory.OpenCount);
            Assert.Equal(1, factory.DisposeCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
