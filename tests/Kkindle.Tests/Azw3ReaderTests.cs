using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class Azw3ReaderTests
{
    [Theory]
    [InlineData("native-compressed.azw3")]
    [InlineData("native-uncompressed.azw3")]
    [InlineData("native-hybrid.azw3")]
    public async Task ReadsRealKf8TextNavigationImagesAndLinksWithoutCalibre(string fixture)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Fixture(fixture);
            var original = await File.ReadAllBytesAsync(source);
            var output = Path.Combine(root, "native.epub");
            await Azw3ReaderService.PrepareEpubAsync(source, output);
            using (var zip = ZipFile.OpenRead(output))
            {
                XNamespace html = "http://www.w3.org/1999/xhtml";
                var docs = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") && !e.FullName.EndsWith("nav.xhtml")).Select(ReadXml).ToArray();
                Assert.Equal(6, docs.Length);
                var text = string.Join('\n', docs.Select(d => d.Root!.Value));
                Assert.Contains("前言说明 & <保留>", text);
                Assert.Contains("你好，世界。😀", text);
                Assert.Contains("第0段，正文内容完整保留。", text);
                Assert.Contains("第199段，正文内容完整保留。", text);
                Assert.Contains("谢谢阅读。", text);
                foreach (var image in docs.SelectMany(d => d.Descendants(html + "img")))
                    Assert.NotNull(zip.GetEntry("OEBPS/" + image.Attribute("src")!.Value));
                Assert.NotEmpty(docs.SelectMany(d => d.Descendants(html + "img")));
                foreach (var link in docs.SelectMany(d => d.Descendants(html + "a")))
                {
                    var href = link.Attribute("href")?.Value;
                    if (href is null || !href.StartsWith("part-")) continue;
                    var target = href.Split('#');
                    var chapter = zip.GetEntry("OEBPS/" + target[0]);
                    Assert.NotNull(chapter);
                    if (target.Length > 1)
                        Assert.Contains(ReadXml(chapter!).Descendants(), e => e.Attribute("id")?.Value == target[1] || e.Attribute("name")?.Value == target[1]);
                }
                var back = Assert.Single(docs.SelectMany(d => d.Descendants(html + "a")), a => a.Value == "返回锚点");
                var backTarget = back.Attribute("href")!.Value.Split('#');
                Assert.Equal(2, backTarget.Length);
                Assert.Contains(ReadXml(zip.GetEntry("OEBPS/" + backTarget[0])!).Descendants(), e => e.Attribute("id")?.Value == backTarget[1] && e.Value == "定位锚点");
                Assert.DoesNotContain("kindle:", string.Join('\n', docs.Select(d => d.ToString())));
                Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".css"));
            }
            var paths = new AppPaths(Path.Combine(root, "app")); paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(output, await Hashing.Sha256Async(output));
            Assert.Equal(6, document.Chapters.Count);
            Assert.Contains(document.Navigation, n => n.Title == "第一章 初见" && n.Level == 1);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RejectsDrmAndTruncatedFilesWithoutPublishingOutput()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var bytes = await File.ReadAllBytesAsync(Fixture("native-compressed.azw3"));
            var first = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78, 4));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(first + 12, 2), 2);
            var source = Path.Combine(root, "drm.azw3"); var output = Path.Combine(root, "output.epub");
            await File.WriteAllBytesAsync(source, bytes);
            var exception = await Assert.ThrowsAsync<NotSupportedException>(() => Azw3ReaderService.PrepareEpubAsync(source, output));
            Assert.Contains("DRM", exception.Message);
            Assert.False(File.Exists(output));
            await File.WriteAllBytesAsync(source, bytes[..90]);
            await Assert.ThrowsAsync<InvalidDataException>(() => Azw3ReaderService.PrepareEpubAsync(source, output));
            Assert.False(File.Exists(output));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CancelledReadDoesNotPublishCache()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var output = Path.Combine(root, "output.epub");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Azw3ReaderService.PrepareEpubAsync(Fixture("native-compressed.azw3"), output, cancellation.Token));
            Assert.False(File.Exists(output));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void PalmDocHandlesOverlappingBackReferencesAndRejectsInvalidOffsets()
    {
        Assert.Equal("aaaa", Encoding.ASCII.GetString(Azw3Binary.PalmDoc([97, 128, 8])));
        Assert.Throws<InvalidDataException>(() => Azw3Binary.PalmDoc([128, 8]));
    }

    [Fact]
    public void HuffDicExpandsCompressedPhrasesAndRejectsRecursiveCycles()
    {
        var huff = new byte[1304];
        Encoding.ASCII.GetBytes("HUFF").CopyTo(huff, 0);
        void Word(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        Word(huff, 4, 24); Word(huff, 8, 24); Word(huff, 12, 1048);
        for (var i = 0; i < 256; i++) Word(huff, 24 + i * 4, (255u << 8) | 128u | 8u);
        var cdic = new byte[27];
        Encoding.ASCII.GetBytes("CDIC").CopyTo(cdic, 0);
        Word(cdic, 4, 16); Word(cdic, 8, 2); Word(cdic, 12, 1);
        BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(18), 7);
        BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(20), 0x8001);
        cdic[22] = (byte)'A';
        BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(23), 2);
        cdic[25] = 255; cdic[26] = 255;
        var decoder = new Azw3Binary.HuffDic(i => i == 0 ? huff : cdic, 0, 2, CancellationToken.None);
        Assert.Equal("AAA", Encoding.ASCII.GetString(decoder.Decode([254, 255])));
        cdic[25] = 254;
        var cycle = new Azw3Binary.HuffDic(i => i == 0 ? huff : cdic, 0, 2, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => cycle.Decode([254]));
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", name);
    private static XDocument ReadXml(ZipArchiveEntry entry) { using var stream = entry.Open(); return XDocument.Load(stream); }
}
