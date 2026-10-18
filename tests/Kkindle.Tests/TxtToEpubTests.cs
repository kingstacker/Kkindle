using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Kkindle.Core;
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

    [Fact]
    public async Task RecognizesNumberedListChapterPrefixesAndPunctuationInTitles()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "novel.txt");
            await File.WriteAllTextAsync(source,
                "第40章 本命灵庙\n正文。\n41.第41章 守岁人\n继续正文。\n第42章 红灯娘娘会\n再继续。\n第0100章姐，我想了\n正文。\n第101章 红灯照夜，相安无事\n结尾。\n第一章提到的人，后来又出现了。");
            var output = Path.Combine(root, "novel.epub");
            await TxtToEpubService.ConvertAsync(source, output);
            using var zip = ZipFile.OpenRead(output);
            XNamespace ns = "http://www.w3.org/1999/xhtml";
            Assert.Equal(new[] { "第40章 本命灵庙", "第41章 守岁人", "第42章 红灯娘娘会", "第0100章姐，我想了", "第101章 红灯照夜，相安无事" },
                ReadXml(zip, "OEBPS/nav.xhtml").Descendants(ns + "a").Select(a => a.Value));
            Assert.Contains("第一章提到的人，后来又出现了。",
                ReadXml(zip, "OEBPS/chapter-00005.xhtml").Descendants(ns + "p").Select(p => p.Value));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PreviewAndConversionUseTheSameSelectedRulesAndCorrections()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "custom.txt");
            await File.WriteAllTextAsync(source,
                "作者：测试作者\n第一幕\n正文一\n普通行\n正文二\n", new UTF8Encoding(false));
            var rules = new TxtImportRules
            {
                Encoding = TxtTextEncoding.Utf8,
                HeadingPreset = TxtHeadingRulePreset.CustomRegex,
                CustomHeadingPattern = @"^第[一二三]幕$",
                RecognizeVolumes = false,
                Corrections =
                [
                    new TxtChapterCorrection(2, IsHeading: true, Title: "卷一：校正标题", IsVolume: true),
                    new TxtChapterCorrection(4, IsHeading: true, Title: "第二幕（手工修正）")
                ]
            };

            var preview = await TxtToEpubService.PreviewAsync(source, rules, pageSize: 10);
            var output = Path.Combine(root, "custom.epub");
            await TxtToEpubService.ConvertWithRulesAsync(source, output, rules);

            Assert.Equal(TxtTextEncoding.Utf8, preview.DetectedEncoding);
            Assert.Equal(new[] { "前言", "卷一：校正标题", "第二幕（手工修正）" }, preview.Chapters.Select(chapter => chapter.Title));
            Assert.Equal(new[] { false, true, false }, preview.Chapters.Select(chapter => chapter.IsVolume));
            using var zip = ZipFile.OpenRead(output);
            XNamespace ns = "http://www.w3.org/1999/xhtml";
            var toc = ReadXml(zip, "OEBPS/nav.xhtml");
            Assert.Equal(preview.Chapters.Select(chapter => chapter.Title), toc.Descendants(ns + "a").Select(anchor => anchor.Value));
            Assert.Equal(2, toc.Descendants(ns + "ol").Count());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task LibraryImportUsesPerFileTxtRules()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "custom-import.txt");
            await File.WriteAllTextAsync(source,
                "作者：测试作者\n第一幕\n正文一\n普通行\n正文二\n",
                new UTF8Encoding(false));
            var rules = new TxtImportRules
            {
                Encoding = TxtTextEncoding.Utf8,
                HeadingPreset = TxtHeadingRulePreset.CustomRegex,
                CustomHeadingPattern = @"^第[一二三]幕$",
                RecognizeVolumes = false,
                Corrections =
                [
                    new TxtChapterCorrection(4, IsHeading: true, Title: "第二幕（导入修正）")
                ]
            };
            var paths = new AppPaths(Path.Combine(root, "app"));
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();

            var result = await library.ImportAsync(
                [source],
                txtImportRules: new Dictionary<string, TxtImportRules>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [source] = rules
                });

            Assert.True(Assert.Single(result.Items).Succeeded);
            var book = Assert.Single(await library.SearchAsync());
            var file = Assert.Single(book.Files);
            var epub = Path.Combine(paths.Data, file.RelativePath);
            using var zip = ZipFile.OpenRead(epub);
            XNamespace ns = "http://www.w3.org/1999/xhtml";
            Assert.Equal(
                new[] { "前言", "第一幕", "第二幕（导入修正）" },
                ReadXml(zip, "OEBPS/nav.xhtml").Descendants(ns + "a").Select(anchor => anchor.Value));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PreviewReturnsOnlyRequestedPageAndReportsTotalChapterCount()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "many-chapters.txt");
            var text = string.Join('\n', Enumerable.Range(1, 251).SelectMany(number =>
                new[] { $"第{number}章 标题{number}", "正文内容" }));
            await File.WriteAllTextAsync(source, text);

            var secondPage = await TxtToEpubService.PreviewAsync(source, pageIndex: 1, pageSize: 100);

            Assert.Equal(251, secondPage.ChapterCount);
            Assert.Equal(100, secondPage.Chapters.Count);
            Assert.Equal(101, secondPage.Chapters[0].Number);
            Assert.Equal("第101章 标题101", secondPage.Chapters[0].Title);
            Assert.True(secondPage.HasPreviousPage);
            Assert.True(secondPage.HasNextPage);
            var finalPage = await TxtToEpubService.PreviewAsync(source, pageIndex: 2, pageSize: 100);
            Assert.Equal(51, finalPage.Chapters.Count);
            Assert.Equal(201, finalPage.Chapters[0].Number);
            Assert.Equal(251, finalPage.Chapters[^1].Number);
            Assert.False(finalPage.HasNextPage);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PreviewHonorsForcedEncodingAndCancellation()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "utf16.txt");
            await File.WriteAllTextAsync(source, "第1章 标题\n正文", new UnicodeEncoding(false, true, true));
            var preview = await TxtToEpubService.PreviewAsync(source, new TxtImportRules { Encoding = TxtTextEncoding.Utf16LittleEndian });
            Assert.Equal(TxtTextEncoding.Utf16LittleEndian, preview.DetectedEncoding);
            Assert.Equal("第1章 标题", Assert.Single(preview.Chapters).Title);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TxtToEpubService.PreviewAsync(source, cancellationToken: canceled.Token));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }
}
