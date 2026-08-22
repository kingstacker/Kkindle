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
            await File.WriteAllTextAsync(source, "azw3");
            var converter = new FakeConverter();
            var cache = new ReaderFormatCacheService(paths, converter);
            var hash = new string('a', 64);

            var first = await cache.PrepareEpubAsync(source, hash, "azw3");
            var second = await cache.PrepareEpubAsync(source, hash, "azw3");

            Assert.False(first.CacheHit);
            Assert.True(second.CacheHit);
            Assert.Equal(first.EpubPath, second.EpubPath);
            Assert.Equal(1, converter.CallCount);
            Assert.True(new FileInfo(second.EpubPath).Length > 0);
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
            await File.WriteAllTextAsync(destinationPath, "epub", cancellationToken);
        }
    }
}
