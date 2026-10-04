using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class TxtToEpubTests
{
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("GB18030")]
    public async Task ImportsTextAsReadableEpubWithNestedChaptersAndDeduplicates(string encodingName)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var source = Path.Combine(root, "测试小说.TXT");
            await File.WriteAllTextAsync(source,
                "开场说明 & <保留>\r\n\r\n第一卷 起点\r\n第 1 章 初见\r\n正文：你好。\r\n第一章提到的人，后来又出现了。\r\n第二章 重逢\r\n再次相见。\r\nChapter 3 End\r\nThe end.\r\n后记\r\n谢谢阅读。", Encoding.GetEncoding(encodingName));
            var original = await File.ReadAllBytesAsync(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            var result = await library.ImportAsync([source]);
            Assert.True(Assert.Single(result.Items).Succeeded);
            var book = Assert.Single(await library.SearchAsync());
            Assert.Equal("测试小说", book.Title);
            Assert.False(string.IsNullOrWhiteSpace(book.CoverPath));
            Assert.True(File.Exists(Path.Combine(paths.Data, book.CoverPath!)));
            var file = Assert.Single(book.Files);
            Assert.Equal("epub", file.Format);
            var epub = Path.Combine(paths.Data, file.RelativePath);
            Assert.Equal(new FileInfo(epub).Length, file.Size);
            Assert.Equal(await Hashing.Sha256Async(epub), file.Sha256);
            using (var zip = ZipFile.OpenRead(epub))
            {
                Assert.Equal("mimetype", zip.Entries[0].FullName);
                var nav = ReadXml(zip, "OEBPS/nav.xhtml");
                XNamespace ns = "http://www.w3.org/1999/xhtml";
                Assert.Equal(new[] { "前言", "第一卷 起点", "第 1 章 初见", "第二章 重逢", "Chapter 3 End", "后记" }, nav.Descendants(ns + "a").Select(e => e.Value));
                Assert.Equal(2, nav.Descendants(ns + "ol").Count());
                var first = ReadXml(zip, "OEBPS/chapter-00001.xhtml");
                Assert.Contains("开场说明 & <保留>", first.Descendants(ns + "p").Select(e => e.Value));
                Assert.Contains("第一章提到的人，后来又出现了。", ReadXml(zip, "OEBPS/chapter-00003.xhtml").Descendants(ns + "p").Select(e => e.Value));
            }
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(epub, file.Sha256);
            Assert.Equal(6, document.Chapters.Count);
            Assert.NotEmpty(document.Navigation);
            await library.ImportAsync([source]);
            Assert.Single((await library.SearchAsync()).Single().Files);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SplitsLongUnstructuredTextWithoutDroppingParagraphs()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "long.txt");
            var lines = Enumerable.Range(0, 1000).Select(i => $"段落{i:D4} " + new string('文', 100)).ToArray();
            await File.WriteAllTextAsync(source, string.Join('\n', lines));
            var epub = Path.Combine(root, "long.epub");
            await TxtToEpubService.ConvertAsync(source, epub);
            using var zip = ZipFile.OpenRead(epub);
            XNamespace ns = "http://www.w3.org/1999/xhtml";
            var chapters = zip.Entries.Where(e => e.FullName.StartsWith("OEBPS/chapter-")).ToArray();
            Assert.True(chapters.Length > 1);
            var preserved = chapters.SelectMany(e => ReadXml(zip, e.FullName).Descendants(ns + "p").Select(p => p.Value));
            Assert.Equal(lines, preserved);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task EmptyTextFailsWithoutCreatingLibraryBook()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "empty.txt");
            await File.WriteAllTextAsync(source, "\n  \n");
            var library = new SqliteBookLibraryService(new AppPaths(Path.Combine(root, "app")), new BookMetadataService());
            await library.InitializeAsync();
            Assert.False(Assert.Single((await library.ImportAsync([source])).Items).Succeeded);
            Assert.Empty(await library.SearchAsync());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("第0001章救？还是不救？")]
    [InlineData("第0001章救?还是不救?")]
    [InlineData("第0001章救！必须救！")]
    [InlineData("Chapter 1 Save? Or Not!")]
    public async Task RecognizesFirstChapterWithQuestionOrExclamationMarks(string heading)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "novel.txt");
            await File.WriteAllTextAsync(source, $"官道红颜 作者：西楼月\n这是简介。\n\n{heading}\n今天是周末。\n第一章提到的人，后来又出现了。\n\n第0002章坏了领导的好事\n第二章正文。");
            var output = Path.Combine(root, "novel.epub");
            await TxtToEpubService.ConvertAsync(source, output);
            using var zip = ZipFile.OpenRead(output);
            XNamespace ns = "http://www.w3.org/1999/xhtml";
            Assert.Equal(new[] { "前言", heading, "第0002章坏了领导的好事" }, ReadXml(zip, "OEBPS/nav.xhtml").Descendants(ns + "a").Select(a => a.Value));
            Assert.DoesNotContain(heading, ReadXml(zip, "OEBPS/chapter-00001.xhtml").Root!.Value);
            var first = ReadXml(zip, "OEBPS/chapter-00002.xhtml");
            Assert.Equal(heading, first.Descendants(ns + "h1").Single().Value);
            Assert.Contains("今天是周末。", first.Root!.Value);
            Assert.Contains("第一章提到的人，后来又出现了。", first.Root.Value);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }
}
