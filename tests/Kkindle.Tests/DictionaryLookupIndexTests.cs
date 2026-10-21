using System.Text.Json;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class DictionaryLookupIndexTests
{
    [Fact]
    public async Task IndexSurvivesRestartRebuildsOnSourceChangeAndIsRemovedWithDictionary()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var source = Path.Combine(root, "sample.tsv");
            await File.WriteAllTextAsync(source, "apple\t苹果\nCAFÉ\t咖啡\n");
            var service = new DictionaryService(paths);
            var definition = await service.ImportAsync(source);
            Assert.Equal("咖啡", Assert.Single(await service.LookupAsync("café")).Definition);
            var index = Directory.GetFiles(root, "*.lookup.db", SearchOption.AllDirectories).Single();
            var indexTime = File.GetLastWriteTimeUtc(index);
            service = new DictionaryService(paths);
            Assert.Equal("苹果", Assert.Single(await service.LookupAsync("APPLE")).Definition);
            Assert.Equal(indexTime, File.GetLastWriteTimeUtc(index));
            var json = Path.Combine(paths.Dictionaries, definition.RelativePath);
            await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new Dictionary<string, string> { ["apple"] = "updated definition" }));
            Assert.Equal("updated definition", Assert.Single(await service.LookupAsync("Apple")).Definition);
            Assert.Empty(await service.LookupAsync("café"));
            await service.SetEnabledAsync(definition.Id, false);
            Assert.Empty(await service.LookupAsync("apple"));
            await service.RemoveAsync(definition.Id);
            Assert.False(File.Exists(index));
        }
        finally { Directory.Delete(root, true); }
    }
}
