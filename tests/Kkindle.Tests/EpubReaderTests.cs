using System.IO.Compression;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EpubReaderTests
{
    [Fact]
    public async Task PreparesChaptersInSpineOrder()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "reader.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="two" href="chapter-2.xhtml" media-type="application/xhtml+xml" />
                        <item id="one" href="chapter-1.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter-1.xhtml", "<html><body>第一章</body></html>");
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter-2.xhtml", "<html><body>第二章</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var document = await service.PrepareAsync(epub, new string('a', 64));

            Assert.Equal(2, document.Chapters.Count);
            Assert.EndsWith("chapter-1.xhtml", document.Chapters[0]);
            Assert.EndsWith("chapter-2.xhtml", document.Chapters[1]);
            Assert.Equal(["第 1 章", "第 2 章"], document.Navigation.Select(item => item.Title));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task MergesNestedLinksFromPhysicalTocPages()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "nested-physical-toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="content.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="cover" href="cover.xhtml" media-type="application/xhtml+xml" />
                        <item id="toc" href="toc.xhtml" media-type="application/xhtml+xml" />
                        <item id="volume-one" href="volume-one.xhtml" media-type="application/xhtml+xml" />
                        <item id="volume-one-toc" href="volume-one-toc.xhtml" media-type="application/xhtml+xml" />
                        <item id="one" href="one.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="two.xhtml" media-type="application/xhtml+xml" />
                        <item id="volume-two" href="volume-two.xhtml" media-type="application/xhtml+xml" />
                        <item id="volume-two-toc" href="volume-two-toc.xhtml" media-type="application/xhtml+xml" />
                        <item id="three" href="three.xhtml" media-type="application/xhtml+xml" />
                        <item id="four" href="four.xhtml" media-type="application/xhtml+xml" />
                        <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
                      </manifest>
                      <spine toc="ncx">
                        <itemref idref="cover" />
                        <itemref idref="toc" />
                        <itemref idref="volume-one" />
                        <itemref idref="volume-one-toc" />
                        <itemref idref="one" />
                        <itemref idref="two" />
                        <itemref idref="volume-two" />
                        <itemref idref="volume-two-toc" />
                        <itemref idref="three" />
                        <itemref idref="four" />
                      </spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "toc.ncx", """
                    <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/">
                      <navMap>
                        <navPoint id="toc"><navLabel><text>目录</text></navLabel><content src="toc.xhtml#toc-heading" /></navPoint>
                        <navPoint id="volume-one"><navLabel><text>第一册</text></navLabel><content src="volume-one.xhtml" /></navPoint>
                        <navPoint id="volume-two"><navLabel><text>第二册</text></navLabel><content src="volume-two.xhtml" /></navPoint>
                      </navMap>
                    </ncx>
                    """);
                TestHelpers.AddZipEntry(archive, "cover.xhtml", "<html><body>封面</body></html>");
                TestHelpers.AddZipEntry(archive, "toc.xhtml", """
                    <html><body><p>目录</p>
                      <p><a href="volume-one.xhtml">第一册</a></p>
                      <p><a href="volume-two.xhtml">第二册</a></p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "volume-one.xhtml", "<html><body>第一册</body></html>");
                TestHelpers.AddZipEntry(archive, "volume-one-toc.xhtml", """
                    <html><body><p>目录</p>
                      <p><a href="one.xhtml">第一章</a></p>
                      <p><a href="two.xhtml">第二章</a></p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "one.xhtml", "<html><body>一</body></html>");
                TestHelpers.AddZipEntry(archive, "two.xhtml", "<html><body>二</body></html>");
                TestHelpers.AddZipEntry(archive, "volume-two.xhtml", "<html><body>第二册</body></html>");
                TestHelpers.AddZipEntry(archive, "volume-two-toc.xhtml", """
                    <html><body><p>目录</p>
                      <p><a href="three.xhtml">第三章</a></p>
                      <p><a href="four.xhtml">第四章</a></p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "three.xhtml", "<html><body>三</body></html>");
                TestHelpers.AddZipEntry(archive, "four.xhtml", "<html><body>四</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('b', 64));

            Assert.Equal(
                ["目录", "第一册", "第一章", "第二章", "第二册", "第三章", "第四章"],
                document.Navigation.Select(item => item.Title));
            Assert.Equal([1, 2, 4, 5, 6, 8, 9], document.Navigation.Select(item => item.ChapterIndex));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DoesNotTreatFootnoteLinksAsNavigationEntries()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "footnotes-not-toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package><manifest>
                      <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
                      <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
                    </manifest>
                    <spine toc="ncx"><itemref idref="chapter" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "toc.ncx", """
                    <ncx><navMap>
                      <navPoint><navLabel><text>第一章</text></navLabel><content src="chapter.xhtml" /></navPoint>
                    </navMap></ncx>
                    """);
                TestHelpers.AddZipEntry(archive, "chapter.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><body>
                      <h2 id="sigil_toc_id_1">第一章</h2>
                      <p>正文<sup><a href="#note1n" id="note1">[1]</a></sup>继续。</p>
                      <p>更多正文<sup><a href="#note2n" id="note2">［2］</a></sup>。</p>
                      <div class="fnote">
                        <p><a href="#note1" id="note1n">[1]</a>第一条脚注</p>
                        <p><a href="#note2" id="note2n">［2］</a>第二条脚注</p>
                      </div>
                    </body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('f', 64));

            Assert.Equal(["第一章"], document.Navigation.Select(item => item.Title));
            Assert.DoesNotContain(document.Navigation, item => item.Title is "[1]" or "［2］");
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task KeepsNativeVerticalTextRunsUnmodified()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "vertical-numbers.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package><manifest>
                      <item id="one" href="chapter.xhtml" media-type="application/xhtml+xml" />
                    </manifest><spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
                    <html><body><p>数字1和12以及2–3，还有12345，20°C，COPYRIGHT ISBN A1，ASCII, punctuation!符号#结束。don't</p></body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('7', 64));

            var html = await File.ReadAllTextAsync(document.Chapters[0]);
            Assert.Contains("数字1和12以及2–3，还有12345，20°C，COPYRIGHT ISBN A1，ASCII, punctuation!符号#结束。don't", html, StringComparison.Ordinal);
            Assert.DoesNotContain("data-kkindle-vertical-run", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-cell-inner", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-tcy-inner", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-digit", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-number", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-latin", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-punctuation", html, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SanitizationKeepsCjkTextAroundWrappedVerticalRuns()
    {
        // A paragraph that mixes CJK prose with digits, Latin abbreviations
        // and ASCII punctuation must survive extraction as one uninterrupted
        // native shaping run with every character present.
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "mixed-paragraph.epub");
            const string paragraph =
                "Linux validation paragraph 001 includes COPYRIGHT, ISBN, A1, 单数字7，双位数12，"
                + "三位数200，search-token-linux and AI context linux.竖排长数字12345678901234567890不能断开。";
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/package.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
                      <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                        <dc:identifier id="book-id">urn:uuid:mixed</dc:identifier>
                        <dc:title>Mixed</dc:title>
                        <dc:language>zh-CN</dc:language>
                      </metadata>
                      <manifest><item id="one" href="chapter.xhtml" media-type="application/xhtml+xml" /></manifest>
                      <spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/chapter.xhtml",
                    "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>t</title></head><body><p>"
                    + paragraph + "</p></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('d', 64));

            var html = await File.ReadAllTextAsync(document.Chapters[0]);
            var bodyStart = html.IndexOf("<body", StringComparison.Ordinal);
            var body = html[bodyStart..];
            var plain = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", string.Empty);
            Assert.Contains("单数字7", plain, StringComparison.Ordinal);
            Assert.Contains("双位数12", plain, StringComparison.Ordinal);
            Assert.Contains("三位数200", plain, StringComparison.Ordinal);
            foreach (var expected in new[] { "COPYRIGHT", "ISBN", "A1", "竖排长数字", "不能断开。" })
                Assert.Contains(expected, plain, StringComparison.Ordinal);
            Assert.DoesNotContain("data-kkindle-vertical-run", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-latin", html, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReadsEpub3NavigationAndFragmentTargets()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "toc.epub");
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
                        <item id="one" href="text/one.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="text/two.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="text/one.xhtml">开始阅读</a></li>
                        <li><a href="text/two.xhtml#part-2">第二部分</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/text/one.xhtml", "<html><body>一</body></html>");
                TestHelpers.AddZipEntry(archive, "EPUB/text/two.xhtml", "<html><body><h1 id=\"part-2\">二</h1></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('c', 64));

            Assert.Equal(["开始阅读", "第二部分"], document.Navigation.Select(item => item.Title));
            Assert.Equal([0, 1], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("#part-2", document.Navigation[1].Target);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PrefersEpub2GuideTocWhenNcxTargetsAreWrong()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "broken-ncx.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package><manifest>
                      <item id="cover" href="cover.xhtml" media-type="application/xhtml+xml" />
                      <item id="title" href="title.xhtml" media-type="application/xhtml+xml" />
                      <item id="toc-page" href="toc.xhtml" media-type="application/xhtml+xml" />
                      <item id="intro" href="intro.xhtml" media-type="application/xhtml+xml" />
                      <item id="part" href="part.xhtml" media-type="application/xhtml+xml" />
                      <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
                    </manifest>
                    <spine toc="ncx">
                      <itemref idref="cover" /><itemref idref="title" /><itemref idref="toc-page" />
                      <itemref idref="intro" /><itemref idref="part" />
                    </spine>
                    <guide><reference type="toc" title="Contents" href="toc.xhtml#contents" /></guide>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "toc.ncx", """
                    <ncx><navMap>
                      <navPoint><navLabel><text>书名页</text></navLabel><content src="toc.xhtml" /></navPoint>
                      <navPoint><navLabel><text>目录</text></navLabel><content src="toc.xhtml" /></navPoint>
                      <navPoint><navLabel><text>第一部</text></navLabel><content src="intro.xhtml#wrong" /></navPoint>
                    </navMap></ncx>
                    """);
                TestHelpers.AddZipEntry(archive, "cover.xhtml", "<html><head><title>Cover</title></head><body /></html>");
                TestHelpers.AddZipEntry(archive, "title.xhtml", "<html><head><title>书名页</title></head><body>书名</body></html>");
                TestHelpers.AddZipEntry(archive, "toc.xhtml", """
                    <html><head><title>Table of Contents</title></head><body>
                      <h1 id="contents">目录</h1>
                      <a href="intro.xhtml#intro">序言</a>
                      <a href="part.xhtml#part-one">第一部</a>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "intro.xhtml", "<html><head><title>序言</title></head><body><h1 id=\"intro\">序言</h1></body></html>");
                TestHelpers.AddZipEntry(archive, "part.xhtml", "<html><head><title>第一部</title></head><body><h1 id=\"part-one\">第一部</h1></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('3', 64));

            Assert.Equal(["目录", "序言", "第一部"], document.Navigation.Select(item => item.Title));
            Assert.Equal([2, 3, 4], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("toc.xhtml", document.Navigation[0].Target);
            Assert.EndsWith("intro.xhtml#intro", document.Navigation[1].Target);
            Assert.EndsWith("part.xhtml#part-one", document.Navigation[2].Target);
            Assert.Equal(["封面", "书名页", "目录", "序言", "第一部"], document.ChapterTitles);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReplacesDuplicateChapterTitlesWithBodyDerivedOnes()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "front-matter.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="content.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="cover" href="cover.xhtml" media-type="application/xhtml+xml" />
                        <item id="bio" href="bio.xhtml" media-type="application/xhtml+xml" />
                        <item id="dedication" href="dedication.xhtml" media-type="application/xhtml+xml" />
                        <item id="preface" href="preface.xhtml" media-type="application/xhtml+xml" />
                        <item id="note" href="note.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine>
                        <itemref idref="cover" /><itemref idref="bio" /><itemref idref="dedication" />
                        <itemref idref="preface" /><itemref idref="note" />
                      </spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "cover.xhtml", "<html><head><title>Cover</title></head><body /></html>");
                // Calibre conversions repeat the book-level <title> on every
                // split file; only the first body line tells them apart.
                TestHelpers.AddZipEntry(archive, "bio.xhtml", """
                    <html><head><title>冰与火之歌</title></head><body>
                      <p><span class="bold">作者介绍</span></p>
                      <p>乔治 R·R·马丁，1948年出生于美国。</p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "dedication.xhtml", """
                    <html><head><title>冰与火之歌</title></head><body>
                      <p> </p>
                      <p>本书献给马林达</p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "preface.xhtml", """
                    <html><head><title>冰与火之歌</title></head><body>
                      <p>2011年注定是冰与火之歌的大年。在这一年HBO将这部小说改编为电视剧集并大获成功。</p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "note.xhtml", """
                    <html><head><title>自序</title></head><body><p>随便写写</p></body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('9', 64));

            Assert.Equal(
                ["封面", "作者介绍", "本书献给马林达", "2011年注定是冰与火之歌的大年。在这一…", "自序"],
                document.ChapterTitles);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PrefersRoleMarkedTocOverEarlierLandmarksNavigation()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "role-toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/package.opf", """
                    <package><manifest>
                      <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="NAV" />
                      <item id="one" href="one.xhtml" media-type="application/xhtml+xml" />
                      <item id="two" href="two.xhtml" media-type="application/xhtml+xml" />
                    </manifest><spine><itemref idref="one" /><itemref idref="two" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html><body>
                      <nav epub:type="landmarks" xmlns:epub="http://www.idpf.org/2007/ops">
                        <a href="one.xhtml">正文入口</a>
                      </nav>
                      <nav role="doc-toc">
                        <a href="one.xhtml">第一章</a>
                        <a href="two.xhtml?edition=2#part">第二章</a>
                        <a href="two.xhtml?edition=3#part">重复第二章</a>
                      </nav>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/one.xhtml", "<html><body>一</body></html>");
                TestHelpers.AddZipEntry(archive, "EPUB/two.xhtml", "<html><body><h1 id=\"part\">二</h1></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('1', 64));

            Assert.Equal(["第一章", "第二章"], document.Navigation.Select(item => item.Title));
            Assert.DoesNotContain('?', document.Navigation[1].Target);
            Assert.EndsWith("two.xhtml#part", document.Navigation[1].Target);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task InfersUnmarkedTocFromMostValidSpineLinks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "inferred-toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/package.opf", """
                    <package><manifest>
                      <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                      <item id="one" href="one.xhtml" media-type="application/xhtml+xml" />
                      <item id="two" href="two.xhtml" media-type="application/xhtml+xml" />
                    </manifest><spine><itemref idref="one" /><itemref idref="two" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html><body>
                      <nav><a href="one.xhtml">快捷入口</a></nav>
                      <nav><a href="one.xhtml">第一章</a><a href="two.xhtml">第二章</a></nav>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/one.xhtml", "<html><body>一</body></html>");
                TestHelpers.AddZipEntry(archive, "EPUB/two.xhtml", "<html><body>二</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('2', 64));

            Assert.Equal(["第一章", "第二章"], document.Navigation.Select(item => item.Title));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SanitizesHtmlScriptsEventsExternalResourcesAndAddsReaderBridge()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "unsafe-content.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package><manifest>
                      <item id="one" href="chapter.xhtml" media-type="application/xhtml+xml" />
                      <item id="css" href="styles/book.css" media-type="text/css" />
                    </manifest><spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
                    <!DOCTYPE html>
                    <html xmlns="http://www.w3.org/1999/xhtml">
                      <head>
                        <script>window.pwned = true;</script>
                        <style>.local { background-image: url("../images/ok.jpg"); } .remote { background-image: url("https://example.com/x.png"); }</style>
                      </head>
                      <body onload="window.pwned = true">
                        <img src="https://example.com/remote.jpg" />
                        <img class="local" src="../images/ok.jpg" />
                        <a class="footnote" href="#note-1"><img src="https://example.com/note.png" alt="这是完整脚注解释" width="24" height="24" /></a>
                        <picture>
                          <source type="image/webp" srcset="../images/ok.webp 1x, https://example.com/ok.webp 2x" />
                          <img class="lazy" data-src="../images/lazy.jpg" data-srcset="../images/lazy.jpg 1x, https://example.com/lazy@2x.jpg 2x" alt="lazy image" />
                        </picture>
                        <p id="note-1">Footnote text</p>
                        <a href="javascript:alert(1)">unsafe link</a>
                        <a href="chapter.xhtml#part">safe link</a>
                      </body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/styles/book.css", """
                    .local { background: url("../images/ok.jpg"); }
                    .remote { background: url("data:image/png;base64,AAAA"); }
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/images/ok.jpg", "image");
                TestHelpers.AddZipEntry(archive, "OEBPS/images/ok.webp", "image");
                TestHelpers.AddZipEntry(archive, "OEBPS/images/lazy.jpg", "image");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('f', 64));

            var html = await File.ReadAllTextAsync(document.Chapters[0]);
            Assert.DoesNotContain("<script>window.pwned", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onload=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://example.com", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            // The self-drawn engine renders the sanitized XHTML directly:
            Assert.DoesNotContain("Content-Security-Policy", html, StringComparison.Ordinal);
            Assert.Contains("srcset=\"../images/ok.webp 1x\"", html, StringComparison.Ordinal);
            Assert.Contains("src=\"../images/lazy.jpg\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<![CDATA[", html, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(
                Path.GetDirectoryName(document.Chapters[0])!,
                Path.GetDirectoryName(document.Chapters[0])!,
                ".kkindle-reader-bridge.js")));
            Assert.Contains("class=\"kkindle-footnote-marker\">注</sup>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("这是完整脚注解释", html, StringComparison.Ordinal);
            Assert.DoesNotContain("width=\"24\"", html, StringComparison.Ordinal);

            var cssPath = Path.Combine(document.RootPath, "OEBPS", "styles", "book.css");
            var css = await File.ReadAllTextAsync(cssPath);
            Assert.Contains("../images/ok.jpg", css, StringComparison.Ordinal);
            Assert.DoesNotContain("data:image", css, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void ExtractsLocalImagePathsFromLazyAndSrcSetReferences()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var chapter = Path.Combine(root, "chapter.xhtml");
            var imageRoot = Path.Combine(root, "images");
            Directory.CreateDirectory(imageRoot);
            File.WriteAllText(Path.Combine(imageRoot, "cover.webp"), "image");
            File.WriteAllText(Path.Combine(imageRoot, "cover.jpg"), "image");
            File.WriteAllText(chapter, """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body>
                    <picture>
                      <source srcset="images/cover.webp 1x, https://example.com/cover.webp 2x" />
                      <img data-src="images/cover.jpg" alt="cover" />
                    </picture>
                  </body>
                </html>
                """);

            var imagePaths = EpubReaderImageReferenceNormalizer.ExtractLocalImagePaths(chapter);

            Assert.Contains(Path.Combine(root, "images", "cover.webp"), imagePaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine(root, "images", "cover.jpg"), imagePaths, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(imagePaths, path => path.Contains("example.com", StringComparison.OrdinalIgnoreCase));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PreparesXhtmlThatUsesStandardHtmlNamedEntities()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "html-entities.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package><manifest>
                      <item id="one" href="chapter.xhtml" media-type="application/xhtml+xml" />
                    </manifest><spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
                    <!DOCTYPE html>
                    <html xmlns="http://www.w3.org/1999/xhtml">
                      <head><title>Entity test</title></head>
                      <body><p>first&nbsp;second &amp; third&copy;</p></body>
                    </html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('7', 64));

            var html = await File.ReadAllTextAsync(document.Chapters[0]);
            Assert.Contains("first\u00a0second &amp; third", html, StringComparison.Ordinal);
            Assert.Contains("©", html, StringComparison.Ordinal);
            Assert.DoesNotContain("&nbsp;", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-latin", html, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReadsNestedEpub3SubchaptersAsSeparateNavigationItems()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "nested-toc.epub");
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
                        <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="chapter" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="chapter.xhtml">Chapter</a><ol>
                          <li><a href="chapter.xhtml#part-1">Part 1</a></li>
                          <li><a href="chapter.xhtml#part-2">Part 2</a></li>
                        </ol></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(
                    archive,
                    "EPUB/chapter.xhtml",
                    "<html><body><h1>Chapter</h1><h2 id=\"part-1\">Part 1</h2><h2 id=\"part-2\">Part 2</h2></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('e', 64));

            Assert.Equal(["Chapter", "Part 1", "Part 2"], document.Navigation.Select(item => item.Title));
            Assert.Equal([0, 0, 0], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("chapter.xhtml", document.Navigation[0].Target);
            Assert.EndsWith("chapter.xhtml#part-1", document.Navigation[1].Target);
            Assert.EndsWith("chapter.xhtml#part-2", document.Navigation[2].Target);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RejectsArchivePathOutsideReaderCache()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "unsafe.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
                TestHelpers.AddZipEntry(archive, "../outside.txt", "unsafe");

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.PrepareAsync(epub, new string('b', 64)));
            Assert.False(File.Exists(Path.Combine(paths.ReaderCache, "outside.txt")));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReusesCompletedExtractionForSameContentHash()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "cached.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package><manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml" /></manifest>
                    <spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "one.xhtml", "<html><body>original</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var hash = new string('d', 64);
            var first = await service.PrepareAsync(epub, hash);
            await File.WriteAllTextAsync(first.Chapters[0], "<html><body>cached</body></html>");

            var second = await service.PrepareAsync(epub, hash);

            Assert.Equal("<html><body>cached</body></html>", await File.ReadAllTextAsync(second.Chapters[0]));
            Assert.True(File.Exists(Path.Combine(second.RootPath, ".kkindle-extracted")));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RebuildsStaleExtractionWhenReaderBridgeVersionChanges()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "stale-cache.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package><manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml" /></manifest>
                    <spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "one.xhtml", "<html><body>original chapter</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var hash = new string('9', 64);
            var first = await service.PrepareAsync(epub, hash);
            var marker = Path.Combine(first.RootPath, ".kkindle-extracted");
            await File.WriteAllTextAsync(first.Chapters[0], "<html><body>stale transformed chapter</body></html>");
            await File.WriteAllTextAsync(marker, $"{hash}\n0");

            var rebuilt = await service.PrepareAsync(epub, hash);
            var html = await File.ReadAllTextAsync(rebuilt.Chapters[0]);
            var markerText = await File.ReadAllTextAsync(marker);

            Assert.Contains("original chapter", html, StringComparison.Ordinal);
            Assert.DoesNotContain("kkindle-vertical-latin", html, StringComparison.Ordinal);
            Assert.DoesNotContain("stale transformed chapter", html, StringComparison.Ordinal);
            Assert.EndsWith("\n69", markerText, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
