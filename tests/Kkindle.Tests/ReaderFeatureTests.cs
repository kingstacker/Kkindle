using System.IO.Compression;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderFeatureTests
{
    [Fact]
    public void MillionCharacterChunksPreserveOffsetsAndCancelBetweenChunks()
    {
        var text = string.Concat(Enumerable.Repeat("这是长篇书籍性能测试，分块必须保留文字位置。", 50000));
        using var cancellation = new CancellationTokenSource();
        using var chunks = EpubBookContentService.EnumerateChunks(text, 3, "长章", "long.xhtml", cancellation.Token).GetEnumerator();
        var count = 0;
        var previousEnd = 0;
        while (chunks.MoveNext())
        {
            var chunk = chunks.Current;
            Assert.Equal(text[chunk.StartOffset..chunk.EndOffset].Trim(), chunk.Content);
            Assert.InRange(chunk.StartOffset, 0, previousEnd);
            Assert.True(chunk.EndOffset > previousEnd);
            previousEnd = chunk.EndOffset;
            count++;
        }
        Assert.Equal(text.Length, previousEnd);
        Assert.True(count > 1000);
        using var cancelled = EpubBookContentService.EnumerateChunks(text, 0, "", "", cancellation.Token).GetEnumerator();
        Assert.True(cancelled.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancelled.MoveNext());
    }

    [Fact]
    public async Task BuildsCachedChineseBookIndexAndFindsRelevantChunks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "index.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/package.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="one" href="one.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="two.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="one.xhtml">规模与规律</a></li>
                        <li><a href="two.xhtml">城市系统</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/one.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><body>
                      <h1>规模与规律</h1>
                      <p>规模法则描述系统尺度变化时仍然保持的数量关系。</p>
                      <p>它帮助读者比较动物、企业和城市在不同尺度下的共同结构。</p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/two.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><body>
                      <h1>城市系统</h1><p>城市人口和基础设施之间存在可测量的统计关系。</p>
                    </body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            var readerData = new ReaderDataService(paths);
            await readerData.InitializeAsync();
            var hash = new string('d', 64);
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(epub, hash);
            var book = new Book { Id = Guid.NewGuid(), Title = "规模", Authors = "测试作者" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Sha256 = hash, Format = "epub" };
            var indexer = new EpubBookContentService(readerData);

            var firstCount = await indexer.EnsureIndexedAsync(book, file, document);
            var secondCount = await indexer.EnsureIndexedAsync(book, file, document);
            var results = await readerData.SearchBookAsync(book.Id, "这本书如何解释规模法则？");

            Assert.True(firstCount >= 2);
            Assert.Equal(0, secondCount);
            Assert.Contains(results, chunk => chunk.Content.Contains("规模法则", StringComparison.Ordinal));
            Assert.Contains(results, chunk => chunk.ChapterTitle == "规模与规律");
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PersistsUpdatesAndDeletesReaderAnnotations()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var fileId = Guid.NewGuid();
            var annotation = new ReaderAnnotation
            {
                Id = Guid.NewGuid(),
                BookId = Guid.NewGuid(),
                BookFileId = fileId,
                ChapterPath = "text/chapter.xhtml",
                StartOffset = 12,
                EndOffset = 20,
                SelectedText = "规模法则",
                Prefix = "理解",
                Suffix = "的意义",
                Note = "重要概念",
                Color = "#F1C40F",
                UnderlineStyle = "wavy"
            };

            await service.SaveAnnotationAsync(annotation);
            annotation.Note = "更新后的笔记";
            annotation.UpdatedAt = DateTimeOffset.UtcNow;
            await service.SaveAnnotationAsync(annotation);
            var saved = Assert.Single(await service.GetAnnotationsAsync(fileId));

            Assert.Equal("更新后的笔记", saved.Note);
            Assert.Equal(12, saved.StartOffset);
            Assert.Equal("#F1C40F", saved.Color);
            Assert.Equal("wavy", saved.UnderlineStyle);

            await service.DeleteAnnotationAsync(annotation.Id);
            Assert.Empty(await service.GetAnnotationsAsync(fileId));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ResolvesOnlyFootnotesInsideEpubCache()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epubRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(epubRoot);
            var notes = Path.Combine(epubRoot, "notes.xhtml");
            await File.WriteAllTextAsync(notes, """
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <aside id="note-1"><p>这是跨文件脚注内容。</p><a href="chapter.xhtml#back">↩</a></aside>
                </body></html>
                """);
            var outside = Path.Combine(root, "outside.xhtml");
            await File.WriteAllTextAsync(outside, "<html><body><p id=\"bad\">不应读取</p></body></html>");
            var service = new EpubFootnoteResolver();

            var result = await service.ResolveAsync(epubRoot,
            [
                new Uri(notes).AbsoluteUri + "#note-1",
                new Uri(outside).AbsoluteUri + "#bad"
            ]);

            var text = Assert.Single(result).Value;
            Assert.Contains("跨文件脚注内容", text);
            Assert.DoesNotContain("不应读取", text);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ResolvesFootnoteMarkerParagraphInsteadOfOnlyAnchorText()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epubRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(epubRoot);
            var chapter = Path.Combine(epubRoot, "chapter.xhtml");
            await File.WriteAllTextAsync(chapter, """
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <p><a href="chapter.xhtml#note1" id="note1n">[1]</a> full footnote details after the marker.</p>
                </body></html>
                """);

            var service = new EpubFootnoteResolver();
            var result = await service.ResolveAsync(
                epubRoot,
                [new Uri(chapter).AbsoluteUri + "#note1n"]);

            var text = Assert.Single(result).Value;
            Assert.Contains("[1]", text);
            Assert.Contains("full footnote details after the marker", text);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ResolvesFootnoteFromHtmlContainingNamedEntities()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epubRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(epubRoot);
            var notes = Path.Combine(epubRoot, "notes.html");
            await File.WriteAllTextAsync(notes, """
                <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Strict//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd">
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <p><a id="zw11" href="chapter.html#zww11">注11</a>&nbsp;&nbsp;footnote details after an HTML entity.</p>
                </body></html>
                """);

            var service = new EpubFootnoteResolver();
            var result = await service.ResolveAsync(
                epubRoot,
                [new Uri(notes).AbsoluteUri + "#zw11"]);

            var text = Assert.Single(result).Value;
            Assert.Contains("注11", text);
            Assert.Contains("footnote details after an HTML entity", text);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task EncryptsAiApiKeyAtRestForCurrentWindowsUser()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new AiSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            const string secret = "sk-test-secret-value";
            await store.SaveAsync(new AiConnectionSettings
            {
                Provider = "deepseek",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-v4-flash",
                ApiKey = secret
            });

            var json = await File.ReadAllTextAsync(Path.Combine(paths.Data, "ai-settings.json"));
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.Equal(secret, loaded.ApiKey);
            Assert.Equal("deepseek-v4-flash", loaded.Model);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
