using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderFormatCacheTests
{
    [Fact]
    public async Task ConvertsAzw3OnlyOnceForSameSourceHash()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var source = Path.Combine(root, "book.azw3");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3"), source);
            var converter = new FakeConverter();
            var cache = new ReaderFormatCacheService(paths, converter);
            var hash = await Hashing.Sha256Async(source);

            var first = await cache.PrepareEpubAsync(source, hash, "azw3");
            var second = await cache.PrepareEpubAsync(source, hash, "azw3");

            Assert.False(first.CacheHit);
            Assert.True(second.CacheHit);
            Assert.Equal(first.EpubPath, second.EpubPath);
            Assert.Equal(0, converter.CallCount);
            Assert.True(new FileInfo(second.EpubPath).Length > 0);
            await File.WriteAllTextAsync(second.EpubPath, "broken cache");
            var repaired = await cache.PrepareEpubAsync(source, hash, "azw3");
            Assert.False(repaired.CacheHit);
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(repaired.EpubPath, await Hashing.Sha256Async(repaired.EpubPath));
            Assert.Equal(6, document.Chapters.Count);
            Assert.Equal(0, converter.CallCount);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private sealed class FakeConverter : IBookFormatConverter
    {
        public int CallCount { get; private set; }

        public async Task ConvertAsync(
            string sourcePath,
            string destinationPath,
            IProgress<FormatConversionProgress>? progress = null,
            CancellationToken cancellationToken = default,
            FormatConversionMetadata? metadata = null)
        {
            CallCount++;
            await TxtToEpubService.ConvertAsync(sourcePath, destinationPath, cancellationToken);
        }
    }
}
