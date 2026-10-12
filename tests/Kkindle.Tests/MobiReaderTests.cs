using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class MobiReaderTests
{
    [Fact]
    public async Task LargeLegacyBookIsSplitWithoutLosingTextOrLinkTargets()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var markup = "<html><body><div><a href='#last'>跳到最后</a>" +
                string.Concat(Enumerable.Range(0, 2500).Select(i => $"<p id='p{i}'>第{i}段，正文内容完整保留。你好，世界。😀</p>")) +
                "<h2 id='last'>最后一章</h2><a href='#p0'>返回开头</a></div></body></html>";
            var text = System.Text.Encoding.UTF8.GetBytes(markup);
            var records = new List<byte[]> { new byte[140] };
            for (var p = 0; p < text.Length; p += 4096) records.Add(text[p..Math.Min(text.Length, p + 4096)]);
            var header = records[0];
            void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset, 4), value);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), 1);
            U32(4, (uint)text.Length);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)(records.Count - 1));
            "MOBI"u8.CopyTo(header.AsSpan(16)); U32(20, 116); U32(28, 65001); U32(36, 6);
            U32(84, 132); U32(88, 8); U32(108, uint.MaxValue); "Big book"u8.CopyTo(header.AsSpan(132));
            var table = new byte[78 + records.Count * 8]; "BOOKMOBI"u8.CopyTo(table.AsSpan(60));
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(76, 2), (ushort)records.Count);
            var position = table.Length;
            for (var i = 0; i < records.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(78 + i * 8, 4), (uint)position);
                position += records[i].Length;
            }
            var source = Path.Combine(root, "big.mobi");
            await File.WriteAllBytesAsync(source, table.Concat(records.SelectMany(r => r)).ToArray());
            var output = Path.Combine(root, "big.epub");
            await Azw3ReaderService.PrepareEpubAsync(source, output);
            using var zip = ZipFile.OpenRead(output);
            XDocument Read(ZipArchiveEntry e) { using var stream = e.Open(); return XDocument.Load(stream); }
            var docs = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") && !e.FullName.EndsWith("nav.xhtml")).Select(Read).ToArray();
            Assert.True(docs.Length > 1);
            XNamespace html = "http://www.w3.org/1999/xhtml";
            var paragraphs = docs.SelectMany(d => d.Descendants(html + "p")).ToArray();
            Assert.Equal(2500, paragraphs.Length);
            for (var i = 0; i < paragraphs.Length; i++) Assert.Equal($"第{i}段，正文内容完整保留。你好，世界。😀", paragraphs[i].Value);
            foreach (var link in docs.SelectMany(d => d.Descendants(html + "a")))
            {
                var target = link.Attribute("href")!.Value.Split('#');
                var targetDoc = Read(zip.GetEntry("OEBPS/" + target[0])!);
                Assert.Contains(targetDoc.Descendants(), n => n.Attribute("id")?.Value == target[1]);
            }
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("native-legacy.mobi")]
    [InlineData("native-legacy-uncompressed.mobi")]
    [InlineData("native-hybrid.azw3")]
    public async Task OpensMobiTextImagesAndNavigationWithoutConverterAndRepairsCache(string fixture)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.mobi");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", fixture), source);
            var original = await File.ReadAllBytesAsync(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var cache = new ReaderFormatCacheService(paths, new ForbiddenConverter());
            var hash = await Hashing.Sha256Async(source);
            var first = await cache.PrepareEpubAsync(source, hash, "mobi");
            var second = await cache.PrepareEpubAsync(source, hash, "mobi");
            Assert.False(first.CacheHit);
            Assert.True(second.CacheHit);
            Assert.Equal(first.EpubPath, second.EpubPath);
            using (var zip = ZipFile.OpenRead(first.EpubPath))
            {
                XNamespace html = "http://www.w3.org/1999/xhtml";
                XDocument Read(ZipArchiveEntry entry) { using var stream = entry.Open(); return XDocument.Load(stream); }
                var docs = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") && !e.FullName.EndsWith("nav.xhtml")).Select(Read).ToArray();
                var text = string.Join('\n', docs.Select(d => d.Root!.Value));
                Assert.Contains("你好，世界。😀", text);
                Assert.Contains("第0段，正文内容完整保留。", text);
                Assert.Contains("第199段，正文内容完整保留。", text);
                Assert.Contains("谢谢阅读。", text);
                var images = docs.SelectMany(d => d.Descendants(html + "img")).ToArray();
                Assert.NotEmpty(images);
                foreach (var image in images) Assert.NotNull(zip.GetEntry("OEBPS/" + image.Attribute("src")!.Value));
                var nav = Read(zip.GetEntry("OEBPS/nav.xhtml")!);
                Assert.Contains("第一章", nav.Root!.Value);
                Assert.Contains("第二章", nav.Root.Value);
                if (fixture == "native-hybrid.azw3") Assert.NotEmpty(nav.Descendants(html + "ol").Skip(1));
                else Assert.Equal(5, nav.Descendants(html + "a").Count());
                foreach (var link in docs.SelectMany(d => d.Descendants(html + "a")).Concat(nav.Descendants(html + "a")))
                {
                    var href = link.Attribute("href")?.Value;
                    if (href is null || !href.StartsWith("part-")) continue;
                    var target = href.Split('#');
                    var entry = zip.GetEntry("OEBPS/" + target[0]);
                    Assert.NotNull(entry);
                    if (target.Length > 1) Assert.Contains(Read(entry).Descendants(), n => n.Attribute("id")?.Value == target[1]);
                }
            }
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(first.EpubPath, await Hashing.Sha256Async(first.EpubPath));
            Assert.NotEmpty(document.Chapters);
            await File.WriteAllTextAsync(first.EpubPath, "broken");
            Assert.False((await cache.PrepareEpubAsync(source, hash, "mobi")).CacheHit);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DrmCorruptAndCancelledMobiDoNotPublishCache()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var original = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-legacy.mobi"));
            var bytes = original.ToArray();
            var first = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78, 4));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(first + 12, 2), 2);
            var source = Path.Combine(root, "drm.mobi");
            var output = Path.Combine(root, "book.epub");
            await File.WriteAllBytesAsync(source, bytes);
            var exception = await Assert.ThrowsAsync<NotSupportedException>(() => Azw3ReaderService.PrepareEpubAsync(source, output));
            Assert.Contains("DRM", exception.Message);
            Assert.False(File.Exists(output));
            await File.WriteAllBytesAsync(source, bytes[..90]);
            await Assert.ThrowsAsync<InvalidDataException>(() => Azw3ReaderService.PrepareEpubAsync(source, output));
            Assert.False(File.Exists(output));
            await File.WriteAllBytesAsync(source, original);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Azw3ReaderService.PrepareEpubAsync(source, output, cancellation.Token));
            Assert.False(File.Exists(output));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private sealed class ForbiddenConverter : IBookFormatConverter
    {
        public Task ConvertAsync(string sourcePath, string destinationPath, IProgress<FormatConversionProgress>? progress = null,
            CancellationToken cancellationToken = default, FormatConversionMetadata? metadata = null)
            => throw new InvalidOperationException("Native MOBI reading must never invoke Calibre.");
    }
}
