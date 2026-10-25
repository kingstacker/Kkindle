using System.Text.Json;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class KindleScanCacheTests
{
    [Theory]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    public async Task OldSkippedPdfMetadataIsRetriedWithoutDiscardingCompletedScans(int version, int expectedCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.Data);
        var entries = new[]
        {
            Entry("skipped.pdf", "PDF"),
            Entry("read-without-cover.pdf", "pdf", sha256: "completed-hash"),
            Entry("covered.pdf", "pdf", coverPath: "cover.png"),
            Entry("other.epub", "epub")
        };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(paths.Data, "kindle-scan-cache.json"),
                JsonSerializer.Serialize(new { Version = version, Entries = entries }));
            var store = new KindleScanCacheStore(paths);
            var loaded = await store.GetDeviceEntriesAsync("device", CancellationToken.None);

            Assert.Equal(expectedCount, loaded.Count);
            Assert.Equal(version == 4, loaded.ContainsKey("skipped.pdf"));
            Assert.Equal("completed-hash", loaded["read-without-cover.pdf"].Sha256);
            Assert.Equal("cover.png", loaded["covered.pdf"].CoverPath);
            Assert.Contains("other.epub", loaded.Keys);

            await store.ReplaceDeviceEntriesAsync("device", loaded.Values, CancellationToken.None);
            var reloaded = await new KindleScanCacheStore(paths).GetDeviceEntriesAsync("device", CancellationToken.None);
            Assert.Equal(expectedCount, reloaded.Count);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "kindle-scan-cache.json")));
            Assert.Equal(4, document.RootElement.GetProperty("Version").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static KindleScanCacheEntry Entry(string path, string format, string sha256 = "", string? coverPath = null) =>
        new() { DeviceIdentity = "device", RelativePath = path, Format = format, Sha256 = sha256, CoverPath = coverPath };
}
