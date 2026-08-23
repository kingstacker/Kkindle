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

            Assert.Equal(["序言", "第一部"], document.Navigation.Select(item => item.Title));
            Assert.Equal([3, 4], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("intro.xhtml#intro", document.Navigation[0].Target);
            Assert.EndsWith("part.xhtml#part-one", document.Navigation[1].Target);
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
            Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
            Assert.Contains("script-src 'nonce-", html, StringComparison.Ordinal);
            Assert.Contains("src=\".kkindle-reader-bridge.js\"", html, StringComparison.Ordinal);
            Assert.Contains("srcset=\"../images/ok.webp 1x\"", html, StringComparison.Ordinal);
            Assert.Contains("src=\"../images/lazy.jpg\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<![CDATA[", html, StringComparison.Ordinal);
            var bridge = await File.ReadAllTextAsync(Path.Combine(
                Path.GetDirectoryName(document.Chapters[0])!,
                ".kkindle-reader-bridge.js"));
            Assert.Contains("invokeCSharpAction", bridge, StringComparison.Ordinal);
            Assert.Contains("chrome.webview", bridge, StringComparison.Ordinal);
            Assert.Contains("postAvWebViewMessage", bridge, StringComparison.Ordinal);
            Assert.Contains("type: \"scroll\"", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("turnPaginatedPage", bridge, StringComparison.Ordinal);
            Assert.Contains("send({ type: \"page\", direction });", bridge, StringComparison.Ordinal);
            Assert.Contains("send({ type: \"key\", key });", bridge, StringComparison.Ordinal);
            Assert.Contains("paginatedWheelRemainder", bridge, StringComparison.Ordinal);
            Assert.Contains("visibility: visible !important; opacity: 1 !important;", bridge, StringComparison.Ordinal);
            Assert.Contains("contextmenu", bridge, StringComparison.Ordinal);
            Assert.Contains("reportSelection(event)", bridge, StringComparison.Ordinal);
            Assert.Contains("contextMenu: !!contextEvent", bridge, StringComparison.Ordinal);
            Assert.Contains("getSelectionAnchorRect", bridge, StringComparison.Ordinal);
            Assert.Contains("const anchorX = hasAnchor ? rect.left", bridge, StringComparison.Ordinal);
            Assert.Contains("const left = Math.min(Math.max(8, x)", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("const anchorX = contextEvent ? contextEvent.clientX", bridge, StringComparison.Ordinal);
            Assert.Contains("align-self: center", bridge, StringComparison.Ordinal);
            Assert.Contains("bookmarkToggle", bridge, StringComparison.Ordinal);
            Assert.Contains("data-kkindle-footnote-href", bridge, StringComparison.Ordinal);
            Assert.Contains("footnoteHoverElement", bridge, StringComparison.Ordinal);
            Assert.Contains("if (footnoteHoverElement === element) return", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("element.removeAttribute('href')", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("if (footnote) return", bridge, StringComparison.Ordinal);
            Assert.Contains("send({ type: \"link\", href: absoluteHref", bridge, StringComparison.Ordinal);
            Assert.Contains("type: \"footnoteHover\"", bridge, StringComparison.Ordinal);
            Assert.Contains("nativeContinuousScroll", bridge, StringComparison.Ordinal);
            Assert.Contains("if (nativeContinuousScroll) return", bridge, StringComparison.Ordinal);
            Assert.Contains("type: 'continuousEdge'", bridge, StringComparison.Ordinal);
            Assert.Contains("continuousWheelGestureGap", bridge, StringComparison.Ordinal);
            Assert.Contains("if (startsNewGesture)", bridge, StringComparison.Ordinal);
            Assert.Contains("getContinuousScrollMetrics", bridge, StringComparison.Ordinal);
            Assert.Contains("body?.scrollHeight", bridge, StringComparison.Ordinal);
            Assert.Contains("position + viewport >= extent - 4", bridge, StringComparison.Ordinal);
            Assert.Contains("../images/ok.jpg", html, StringComparison.Ordinal);
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
            Assert.Contains("first\u00a0second &amp; third©", html, StringComparison.Ordinal);
            Assert.DoesNotContain("&nbsp;", html, StringComparison.Ordinal);
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
            Assert.DoesNotContain("stale transformed chapter", html, StringComparison.Ordinal);
            var bridge = await File.ReadAllTextAsync(Path.Combine(
                Path.GetDirectoryName(rebuilt.Chapters[0])!,
                ".kkindle-reader-bridge.js"));
            Assert.Contains("data-action=\"highlight-menu\"", bridge, StringComparison.Ordinal);
            Assert.Contains("荧光标记（黑白反色）  ▰", bridge, StringComparison.Ordinal);
            Assert.Contains(".kk-sel-styles.above", bridge, StringComparison.Ordinal);
            Assert.Contains("display: inline-flex; align-items: center; justify-content: center", bridge, StringComparison.Ordinal);
            Assert.Contains("selectionBar.style.display = 'flex'", bridge, StringComparison.Ordinal);
            Assert.Contains("dismissedSelectionText", bridge, StringComparison.Ordinal);
            Assert.Contains("highlightButton.addEventListener('mouseenter', openStyles)", bridge, StringComparison.Ordinal);
            Assert.Contains("highlightButton.addEventListener('mouseleave', scheduleCloseStyles)", bridge, StringComparison.Ordinal);
            Assert.Contains("highlightPanel.addEventListener('mouseenter', clearCloseStylesTimer)", bridge, StringComparison.Ordinal);
            Assert.Contains("if (!pointerIsInHighlightMenu()) closeStyles()", bridge, StringComparison.Ordinal);
            Assert.Contains("}, 160)", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain(".kk-sel-highlight-wrap:not(:hover) .kk-sel-styles", bridge, StringComparison.Ordinal);
            Assert.Contains("document.addEventListener('mousemove'", bridge, StringComparison.Ordinal);
            Assert.Contains("pointerIsInHighlightMenu()", bridge, StringComparison.Ordinal);
            Assert.Contains("document.documentElement.addEventListener('mouseleave', closeStyles)", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("document.addEventListener('mouseleave', closeStyles, true)", bridge, StringComparison.Ordinal);
            Assert.Contains("position: absolute; top: 100%; left: 0", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("styleHoverTimer", bridge, StringComparison.Ordinal);
            Assert.Contains("isSelectionBarTarget", bridge, StringComparison.Ordinal);
            Assert.Contains("if (!hadSelection && !canTurnPage)", bridge, StringComparison.Ordinal);
            Assert.Contains("selectionBar?.style.display === 'flex'", bridge, StringComparison.Ordinal);
            Assert.Contains("pagePointerDown", bridge, StringComparison.Ordinal);
            Assert.Contains("document.addEventListener(\"pointerup\"", bridge, StringComparison.Ordinal);
            Assert.Contains("requestAnimationFrame?.(() =>", bridge, StringComparison.Ordinal);
            Assert.Contains("send({ type: \"pageClick\", side: onLeft ? \"left\" : \"right\" });", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("const direction = onLeft", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("turnPaginatedPage", bridge, StringComparison.Ordinal);
            var pointerSideIndex = bridge.IndexOf(
                "const onLeft = x < width / 3",
                StringComparison.Ordinal);
            var pointerSendIndex = bridge.IndexOf(
                "send({ type: \"pageClick\", side: onLeft ? \"left\" : \"right\" });",
                pointerSideIndex,
                StringComparison.Ordinal);
            Assert.InRange(pointerSendIndex - pointerSideIndex, 1, 360);
            Assert.EndsWith("\n48", markerText, StringComparison.Ordinal);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
