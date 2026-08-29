using Kkindle.Layout;
using SkiaSharp;
using Xunit;

namespace Kkindle.Tests;

/// <summary>
/// Engine-level tests for the self-drawn typesetting engine. The bundled
/// KingHwaOldSong font is used so shaping exercises the real CJK vertical
/// forms; tests locate it from the App assets directory.
/// </summary>
public sealed class KkindleLayoutEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fontPath;

    public KkindleLayoutEngineTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("kkindle-layout-tests").FullName;
        _fontPath = FindBundledFont();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindBundledFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src", "Kkindle.App", "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Bundled KingHwaOldSong font not found; the layout determinism suite requires the repo font assets.");
    }

    private TypesetEngine CreateEngine()
    {
        var fonts = new TypesetFontLibrary(_fontPath);
        return new TypesetEngine(fonts);
    }

    private string WriteChapter(string body)
    {
        var path = Path.Combine(_tempDir, $"chapter-{Guid.NewGuid():N}.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>test</title></head>
              <body>{body}</body>
            </html>
            """);
        return path;
    }

    private static TypesetLayoutOptions Options(
        TypesetWritingMode mode,
        float width = 400f,
        float height = 600f) => new()
    {
        WritingMode = mode,
        BaseFontSize = 17f,
        LineHeight = 1.8f,
        ViewportWidth = width,
        ViewportHeight = height,
        InsetHorizontal = 24f,
        InsetVertical = 24f,
    };

    [Fact]
    public void Horizontal_PaginationCoversTextWithoutOverflow()
    {
        var longParagraph = string.Concat(Enumerable.Range(0, 12).Select(i =>
            $"这是一段足够长的中文正文第{i}句，用来验证分页器会把整章内容切成多页而不会丢失任何文字，也不会把字形画出页面边界之外。"));
        var path = WriteChapter(
            $"<p>{longParagraph}</p>"
            + "<p>The second paragraph mixes English words with 中文标点，用来检验混排断行。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        Assert.Contains("分页器", content.BodyText);
        Assert.True(content.Blocks.Count >= 2);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        Assert.True(layout.Pages.Count >= 2, $"expected multiple pages, got {layout.Pages.Count}");
        Assert.Equal(content.BodyText.Length, layout.BodyTextLength);

        var covered = 0;
        foreach (var page in layout.Pages)
        {
            if (page.TextStartOffset < 0)
            {
                continue;
            }

            Assert.True(page.TextEndOffset >= page.TextStartOffset);
            covered += page.TextEndOffset - page.TextStartOffset;
            foreach (var run in page.Runs)
            {
                Assert.True(run.Glyphs.Length > 0);
                for (var i = 0; i < run.X.Length; i++)
                {
                    var x = run.OriginX + run.X[i];
                    Assert.InRange(x, 0f, page.Width);
                }
            }
        }

        // Every non-whitespace character must land on some page.
        var nonWhitespace = content.BodyText.Count(c => !char.IsWhiteSpace(c));
        Assert.True(covered >= nonWhitespace, $"covered {covered} < {nonWhitespace}");
    }

    [Fact]
    public void Horizontal_NoLineStartsWithClosingPunctuation()
    {
        var path = WriteChapter("<p>书籍阅读器需要处理禁则，例如标点“。」”这样的组合，以及多个连续的）」」绝不能出现在行首，除非悬挂在边距里。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb, width: 260f));

        foreach (var page in layout.Pages)
        {
            var lines = page.Runs
                .GroupBy(r => MathF.Round(r.OriginY, 1))
                .Select(g => g.OrderBy(r => r.OriginX).First())
                .ToList();
            foreach (var line in lines)
            {
                if (line.TextStart < 0 || line.TextLength == 0)
                {
                    continue;
                }

                var first = content.BodyText[line.TextStart];
                Assert.False(
                    TypesetText.IsProhibitedAtLineStart(first) && line.OriginX <= 24f + 1f,
                    $"line starts with prohibited mark '{first}'");
            }
        }
    }

    [Fact]
    public void Vertical_DigitPolicyMatchesLegacyRules()
    {
        var path = WriteChapter("<p>第12章：到2026年，第7卷已经写完3卷，readme 与 AT&amp;T 保持原样。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);
        Assert.Contains("AT", content.BodyText);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        var combinedRuns = new List<PlacedRun>();
        var sidewaysRuns = new List<PlacedRun>();
        foreach (var page in layout.Pages)
        {
            foreach (var run in page.Runs)
            {
                if (run.TextLength == 2 && content.BodyText.Substring(run.TextStart, run.TextLength) == "12")
                {
                    combinedRuns.Add(run);
                }

                if (run.Sideways)
                {
                    sidewaysRuns.Add(run);
                }
            }
        }

        Assert.True(combinedRuns.Count >= 1, "two-digit year/chapter number must form one tate-chu-yoko cell");
        Assert.True(sidewaysRuns.Count >= 1, "latin phrases and long digit runs must stay atomic sideways runs");
        var sidewaysText = string.Concat(sidewaysRuns.Select(r =>
            content.BodyText.Substring(r.TextStart, r.TextLength)));
        Assert.Contains("2026", sidewaysText);
        Assert.Contains("readme", sidewaysText);
        Assert.Contains("AT&T", sidewaysText);
    }

    [Fact]
    public void Vertical_OffsetStreamStaysMonotonicAcrossPages()
    {
        var body = string.Concat(Enumerable.Range(0, 40).Select(i => $"<p>第{i}段：竖排分页必须保持字符偏移随页序单调递增，这是进度恢复与跨端同步的前提。</p>"));
        var path = WriteChapter(body);
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        Assert.True(layout.Pages.Count > 1);
        var previous = -1;
        foreach (var page in layout.Pages)
        {
            if (page.TextStartOffset < 0)
            {
                continue;
            }

            Assert.True(page.TextStartOffset >= previous, "page text offsets must be monotonic");
            previous = page.TextStartOffset;
            foreach (var run in page.Runs)
            {
                Assert.InRange(run.OriginX, 0f, page.Width);
                Assert.InRange(run.OriginY, 0f, page.Height + 40f); // hanging marks may enter the margin
            }
        }
    }

    [Fact]
    public void Compose_IsDeterministicAcrossRuns()
    {
        var path = WriteChapter("<p>确定性测试：同一本书在 Windows、Linux 与 macOS 上必须得到逐字节相同的分页结果，否则进度同步就没有意义。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        string first;
        using (var engine = CreateEngine())
        {
            first = PageModelJson.Serialize(engine.Compose(content, Options(TypesetWritingMode.VerticalRl)));
        }

        string second;
        using (var engine = CreateEngine())
        {
            second = PageModelJson.Serialize(engine.Compose(content, Options(TypesetWritingMode.VerticalRl)));
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public void Interaction_OffsetPageMappingAndHitTestRoundTrip()
    {
        var path = WriteChapter("<p>命中测试与偏移映射是选择、批注与搜索高亮的基础。</p><p>第二段文本提供更多偏移量供断言使用。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        var firstCharOffset = content.BodyText.IndexOf("命中", StringComparison.Ordinal);
        Assert.True(firstCharOffset >= 0);
        var pageIndex = layout.GetPageIndexOfOffset(firstCharOffset);
        Assert.True(pageIndex >= 0);

        var rect = layout.GetCharRect(pageIndex, firstCharOffset);
        Assert.NotNull(rect);
        Assert.True(rect.Value.Width > 0);

        var hit = layout.HitTest(pageIndex, new SKPoint(rect.Value.Left + 2f, (rect.Value.Top + rect.Value.Bottom) / 2f));
        Assert.True(hit >= firstCharOffset && hit <= firstCharOffset + 2, $"hit {hit} not near {firstCharOffset}");

        var overlays = layout.GetOverlayRects(pageIndex, firstCharOffset, 2);
        Assert.NotEmpty(overlays);
    }

    [Fact]
    public void Loader_FootnoteDefinitionsStayInOffsetStreamButNotRendered()
    {
        var path = WriteChapter(
            "<p>正文引用了一个注<sup>1</sup>。</p>"
            + "<aside id=\"footnote-1\"><p>这是脚注的定义内容。</p></aside>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        Assert.Contains("脚注的定义内容", content.BodyText);
        Assert.DoesNotContain(content.Blocks, b => b.Items.Any(i => i.TextStart >= 0 && content.BodyText[i.TextStart..(i.TextStart + i.Text.Length)].Contains("脚注的定义")));
    }

    [Fact]
    public void Loader_MicroCssResolvesPublisherEmphasis()
    {
        var cssPath = Path.Combine(_tempDir, "style.css");
        File.WriteAllText(cssPath, ".em { font-style: italic; } .ttl { text-align: center; font-weight: bold; }");
        var path = Path.Combine(_tempDir, $"styled-{Guid.NewGuid():N}.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><link rel="stylesheet" type="text/css" href="style.css"/></head>
              <body><p class="ttl">标题</p><p>一段<span class="em">斜体强调</span>正文。</p></body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);

        var title = content.Blocks[0];
        Assert.Equal(BlockKind.Heading, title.Kind);
        Assert.True(title.Center);

        var body = content.Blocks[1];
        var emphasized = body.Items.Single(i => i.Text == "斜体强调");
        Assert.True(emphasized.Style.Italic);
    }

    [Fact]
    public void Paint_RendersInkOnTheSurface()
    {
        var path = WriteChapter("<p>渲染冒烟测试。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        using var surface = SKSurface.Create(new SKImageInfo(400, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Assert.NotNull(surface);
        using (var fonts = new TypesetFontLibrary(_fontPath))
        {
            var painter = new TypesetPainter(fonts);
            painter.Paint(surface.Canvas, layout.Pages[0]);
            surface.Canvas.Flush();
        }

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        Assert.NotNull(bitmap);

        var darkPixels = 0;
        for (var y = 0; y < 600; y += 2)
        {
            for (var x = 0; x < 400; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128)
                {
                    darkPixels++;
                }
            }
        }

        Assert.True(darkPixels > 10, $"expected glyph ink on the page, found {darkPixels} dark samples");
    }
}
