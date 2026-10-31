using Kkindle.Infrastructure;
using Kkindle.TestFixtures;

namespace Kkindle.Tests;

public sealed class ImportFileStagingTests
{
    [Fact]
    public async Task ProviderStreamImportsIntoLibraryAndStagingIsRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-provider-import", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        try
        {
            var fixture = PdfFixture.Write(paths.ReaderCache);
            var bytes = await File.ReadAllBytesAsync(fixture);
            var source = new ImportFileSource("../selected.pdf", null,
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
            var stagingRoot = Path.Combine(paths.ReaderCache, "import-staging");
            string staged;
            string imported;
            using (var staging = await ImportFileStaging.PrepareAsync([source], stagingRoot))
            {
                staged = Assert.Single(staging.Paths);
                Assert.Equal("selected.pdf", Path.GetFileName(staged));
                Assert.StartsWith(Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar, staged);
                var library = new SqliteBookLibraryService(paths, new BookMetadataService());
                await library.InitializeAsync();
                var result = await library.ImportAsync(staging.Paths);
                Assert.Equal(1, result.AddedCount);
                var book = Assert.Single(await library.SearchAsync());
                imported = library.GetAbsoluteFilePath(Assert.Single(book.Files));
            }
            Assert.False(File.Exists(staged));
            Assert.True(File.Exists(imported));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(imported));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFilesAreNotCopiedOrDeletedAndDuplicateNamesStaySeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-provider-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "local.epub");
        await File.WriteAllTextAsync(local, "local");
        try
        {
            var source = new ImportFileSource("same.epub", null, _ => Task.FromResult<Stream>(new MemoryStream([1, 2])));
            var reserved = new ImportFileSource("CON.pdf", null, _ => Task.FromResult<Stream>(new MemoryStream([3])));
            using (var staging = await ImportFileStaging.PrepareAsync(
                [new("local.epub", local, _ => throw new InvalidOperationException()), source, source, reserved], Path.Combine(root, "stage")))
            {
                Assert.Equal(local, staging.Paths[0]);
                Assert.NotEqual(staging.Paths[1], staging.Paths[2]);
                Assert.Equal("same.epub", Path.GetFileName(staging.Paths[1]));
                Assert.Equal("same.epub", Path.GetFileName(staging.Paths[2]));
                Assert.Equal("_CON.pdf", Path.GetFileName(staging.Paths[3]));
            }
            Assert.Equal("local", await File.ReadAllTextAsync(local));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedProviderReadCleansPartialStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-provider-import", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new ImportFileSource("first.epub", null, _ => Task.FromResult<Stream>(new MemoryStream([1])));
            var broken = new ImportFileSource("broken.epub", null, _ => throw new IOException("provider error"));
            await Assert.ThrowsAsync<IOException>(() => ImportFileStaging.PrepareAsync([first, broken], root));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
