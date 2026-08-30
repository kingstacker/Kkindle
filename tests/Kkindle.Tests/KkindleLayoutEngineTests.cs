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

    private string WritePng(string name = "image.png")
    {
        var path = Path.Combine(_tempDir, name);
        using var surface = SKSurface.Create(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.CornflowerBlue);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
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
    public void Horizontal_JustifiedLinesStayInsideTheContentBox()
    {
        var paragraph = string.Concat(Enumerable.Range(0, 18).Select(_ =>
            "两端对齐会把行内字距均匀铺满内容宽度，这一行必须和其他行一样停在右侧边界之前。"));
        var path = WriteChapter($"<p>{paragraph}</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 360f, height: 520f);
        var layout = engine.Compose(content, options);
        var rightEdge = options.ViewportWidth - options.InsetHorizontal;

        foreach (var page in layout.Pages)
        {
            foreach (var run in page.Runs)
            {
                Assert.True(
                    run.OriginX + run.FlowAdvance <= rightEdge + 0.5f,
                    $"run ends at {run.OriginX + run.FlowAdvance}, content edge is {rightEdge}");
            }
        }
    }

    [Fact]
    public void Horizontal_BreaksAnUnbreakableTokenThatExceedsTheContentBox()
    {
        var token = new string('W', 400);
        var path = WriteChapter($"<p>{token}</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 300f, height: 520f);
        var layout = engine.Compose(content, options);
        var rightEdge = options.ViewportWidth - options.InsetHorizontal;

        Assert.True(layout.Pages.Count > 1, $"expected split token to occupy multiple pages, got {layout.Pages.Count}");
        foreach (var page in layout.Pages)
        {
            foreach (var run in page.Runs)
            {
                Assert.True(
                    run.OriginX + run.FlowAdvance <= rightEdge + 0.5f,
                    $"unbreakable token ends at {run.OriginX + run.FlowAdvance}, content edge is {rightEdge}");
            }
        }
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
    public void Vertical_PunctuationUsesMixedOrientationAndCentersNarrowDigits()
    {
        var path = WriteChapter("<p>甲；乙;丙~丁—戊（2）己（12）庚(2)辛。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl);
        var layout = engine.Compose(content, options);

        PlacedRun RunFor(char character, int occurrence = 0)
        {
            var offset = content.BodyText
                .Select((value, index) => (value, index))
                .Where(pair => pair.value == character)
                .Skip(occurrence)
                .Select(pair => pair.index)
                .First();
            return layout.Pages
                .SelectMany(page => page.Runs)
                .Single(run => run.TextStart == offset && run.TextLength == 1);
        }

        // ASCII marks are UAX #50 R characters and must be rotated in mixed
        // vertical text. They must not be allowed to remain horizontal just
        // because the selected font lacks a vertical alternate.
        Assert.True(RunFor(';').Sideways);
        Assert.True(RunFor('~').Sideways);
        Assert.True(RunFor('—').Sideways);
        Assert.True(RunFor('(').Sideways);

        // Fullwidth/CJK punctuation is Tr/Tu and uses the font's explicit
        // vertical presentation glyph instead of the ASCII fallback.
        Assert.False(RunFor('；').Sideways);
        Assert.False(RunFor('（').Sideways);
        Assert.False(RunFor('）').Sideways);

        // The narrow Arabic digit is centered by its actual shaped advance,
        // not by an assumed one-em glyph box. It therefore shares the exact
        // cross-flow center with the surrounding fullwidth parentheses.
        var opening = RunFor('（');
        var digit = RunFor('2');
        Assert.NotEmpty(opening.X);
        Assert.NotEmpty(digit.X);
        var digitShape = engine.Fonts is { } fonts
            ? new GlyphShaper(fonts).Shape("2", 0, 1, fonts.MainFontPath, options.BaseFontSize)
            : throw new InvalidOperationException("engine font library is unavailable");
        var digitCenter = digit.OriginX + digit.X[0] + digitShape.Advances[0] / 2f;
        var openingRect = layout.GetCharRect(
            layout.GetPageIndexOfOffset(content.BodyText.IndexOf('（')),
            content.BodyText.IndexOf('（'));
        Assert.NotNull(openingRect);
        Assert.InRange(
            Math.Abs(digitCenter - openingRect!.Value.MidX),
            0f,
            0.05f);

        Assert.True(TypesetText.ShouldRotateInVertical(';'));
        Assert.True(TypesetText.ShouldRotateInVertical('~'));
        Assert.True(TypesetText.ShouldRotateInVertical('—'));
        Assert.False(TypesetText.ShouldRotateInVertical('；'));
        Assert.False(TypesetText.ShouldRotateInVertical('（'));
        Assert.True(TypesetText.ShouldRotateInVertical('－'));
        Assert.False(TypesetText.ShouldRotateInVertical('○'));
    }

    [Fact]
    public void Vertical_WhiteCircleUsedAsNumericZeroIsCenteredInItsCell()
    {
        var path = WriteChapter("<p>前四○三年</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var zero = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == "○");

        Assert.False(zero.Sideways);
        using var font = new SKFont(engine.Fonts.GetTypeface(_fontPath), zero.FontSize * zero.Scale);
        _ = font.GetGlyphWidths(zero.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        var actualCenterX = zero.OriginX
            + zero.X[0]
            + (bounds[0].Left + bounds[0].Right) / 2f;
        var expectedCenterX = zero.OriginX + zero.CellWidth / 2f;
        Assert.InRange(Math.Abs(actualCenterX - expectedCenterX), 0f, 0.75f);
    }

    [Fact]
    public void Vertical_ChineseInterpunctIsCenteredInItsCell()
    {
        var path = WriteChapter("<p>杰弗里·韦斯特</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var dot = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == "·");

        Assert.False(dot.Sideways);
        using var font = new SKFont(engine.Fonts.GetTypeface(_fontPath), dot.FontSize * dot.Scale);
        _ = font.GetGlyphWidths(dot.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        var actualCenterX = dot.OriginX
            + dot.X[0]
            + (bounds[0].Left + bounds[0].Right) / 2f;
        var actualCenterY = dot.OriginY
            + dot.Y[0]
            + (bounds[0].Top + bounds[0].Bottom) / 2f;
        var expectedCenterX = dot.OriginX + dot.CellWidth / 2f;
        var cellPitch = options.BaseFontSize + options.LetterSpacingEm * options.BaseFontSize;
        var expectedCenterY = options.InsetVertical + (dot.TextStart + 0.5f) * cellPitch;

        Assert.InRange(Math.Abs(actualCenterX - expectedCenterX), 0f, 0.75f);
        Assert.InRange(Math.Abs(actualCenterY - expectedCenterY), 0f, 0.75f);
    }

    [Fact]
    public void Vertical_KinsokuBacktracksWholeClosingCluster()
    {
        var path = WriteChapter("<p>甲乙丙」。丁戊己庚辛壬癸。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);

        foreach (var page in layout.Pages)
        {
            var columns = page.Runs
                .Where(run => run.TextLength > 0)
                .GroupBy(run => MathF.Round(run.OriginX, 1));
            foreach (var column in columns)
            {
                var ordered = column.OrderBy(run => run.OriginY).ToList();
                var first = ordered[0];
                var firstCharacter = content.BodyText[first.TextStart];
                Assert.False(
                    TypesetText.IsProhibitedAtLineStart(firstCharacter),
                    $"column starts with prohibited mark '{firstCharacter}'");

                var last = ordered[^1];
                var lastCharacter = content.BodyText[last.TextStart + last.TextLength - 1];
                var nextOffset = last.TextStart + last.TextLength;
                if (nextOffset < content.BodyText.Length)
                {
                    Assert.False(
                        TypesetText.IsProhibitedAtLineEnd(lastCharacter),
                        $"column ends with opening mark '{lastCharacter}'");
                }
            }
        }
    }

    [Fact]
    public void Vertical_NumericAffixesStayInTheSameColumn()
    {
        var path = WriteChapter("<p>甲2026年乙第12章丙。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);
        var numberOffset = content.BodyText.IndexOf("2026", StringComparison.Ordinal);
        var yearOffset = content.BodyText.IndexOf('年');
        var chapterOffset = content.BodyText.IndexOf("第12", StringComparison.Ordinal);
        var chapterNumberOffset = chapterOffset + 1;

        var numberPage = layout.GetPageIndexOfOffset(numberOffset);
        var yearPage = layout.GetPageIndexOfOffset(yearOffset);
        var chapterPage = layout.GetPageIndexOfOffset(chapterOffset);
        var chapterNumberPage = layout.GetPageIndexOfOffset(chapterNumberOffset);

        Assert.Equal(numberPage, yearPage);
        Assert.Equal(chapterPage, chapterNumberPage);

        var numberRun = layout.Pages[numberPage].Runs.Single(run =>
            run.TextStart == numberOffset && run.TextLength == 4);
        var yearRun = layout.Pages[yearPage].Runs.Single(run =>
            run.TextStart == yearOffset && run.TextLength == 1);
        Assert.InRange(
            Math.Abs(numberRun.OriginX - yearRun.OriginX),
            0f,
            options.BodyLineHeight * 0.75f);
    }

    [Fact]
    public void Vertical_ThousandsGroupingSpaceStaysInsideOneNumericRun()
    {
        var path = WriteChapter("<p>公司资产达到5 000亿美元。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(
            content,
            Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f));
        var numberOffset = content.BodyText.IndexOf("5 000", StringComparison.Ordinal);
        var numberRun = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => run.TextStart == numberOffset);

        Assert.True(numberRun.Sideways);
        Assert.Equal(5, numberRun.TextLength);
        Assert.DoesNotContain(
            layout.Pages.SelectMany(page => page.Runs),
            run => run.TextStart == numberOffset + 1 && run.TextLength == 1);
    }

    [Fact]
    public void Vertical_OpticallyCentersIdeographicZeroInItsCell()
    {
        var path = WriteChapter("<p>前四〇三年</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var zero = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == "〇");

        using var font = new SKFont(engine.Fonts.GetTypeface(_fontPath), zero.FontSize * zero.Scale);
        _ = font.GetGlyphWidths(zero.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        var actualCenterY = zero.OriginY
            + zero.Y[0]
            + (bounds[0].Top + bounds[0].Bottom) / 2f;
        var cellPitch = options.BaseFontSize + options.LetterSpacingEm * options.BaseFontSize;
        var expectedCenterY = options.InsetVertical
            + (zero.TextStart + 0.5f) * cellPitch;

        Assert.InRange(Math.Abs(actualCenterY - expectedCenterY), 0f, 0.55f);
    }

    [Fact]
    public void Vertical_OversizedSidewaysRunSplitsAtTextElementBoundaries()
    {
        var token = new string('A', 260);
        var path = WriteChapter($"<p>{token}</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);
        var runs = layout.Pages
            .SelectMany(page => page.Runs)
            .Where(run => run.Sideways && run.TextLength > 0)
            .OrderBy(run => run.TextStart)
            .ToList();

        Assert.True(runs.Count > 1, "the oversized sideways run should be split");
        Assert.All(runs, run => Assert.InRange(run.FlowAdvance, 0f, options.ContentHeight + 0.5f));
        Assert.Equal(token.Length, runs.Sum(run => run.TextLength));
        for (var index = 1; index < runs.Count; index++)
        {
            Assert.Equal(
                runs[index - 1].TextStart + runs[index - 1].TextLength,
                runs[index].TextStart);
        }
    }

    [Fact]
    public void Vertical_HonorsPublisherCombineAndOrientationStyles()
    {
        var path = WriteChapter(
            "<p style=\"text-combine-upright: digits 4\">1234</p>"
            + "<p style=\"text-orientation: upright\">AB</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));
        var combined = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => run.TextLength == 4);
        var uprightLetters = layout.Pages
            .SelectMany(page => page.Runs)
            .Where(run => run.TextLength == 1
                && content.BodyText[run.TextStart] is 'A' or 'B')
            .ToList();

        Assert.False(combined.Sideways);
        Assert.True(combined.Scale < 1f);
        Assert.Equal(2, uprightLetters.Count);
        Assert.All(uprightLetters, run => Assert.False(run.Sideways));
    }

    [Fact]
    public void KinsokuStringChecksUseTheCorrectEndOfTheTextElement()
    {
        Assert.True(TypesetText.IsProhibitedAtLineEnd("字（"));
        Assert.False(TypesetText.IsProhibitedAtLineEnd("（字"));
        Assert.True(TypesetText.ShouldKeepTogether("2026", "年"));
        Assert.True(TypesetText.ShouldKeepTogether("第", "12"));
    }

    [Fact]
    public void Vertical_CommonPunctuationMatchesUax50OrientationClasses()
    {
        const string rotated = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"
            + "‐‑‒–—―…•";
        const string upright = "§©®±×÷‖†‡‰‱※‼⁇⁈⁉⁂⁑∞∴∵";
        const string transformed = "‘’“”〈〉〈〉《》「」『』【】〔〕〖〗〘〙〚〛"
            + "〝〞〟〜〰゠ー﹙﹚﹛﹜﹝﹞（）：；［］＿｛｝｜～｟｠￣";

        foreach (var character in rotated)
        {
            Assert.True(
                TypesetText.ShouldRotateInVertical(character),
                $"{character} should be a sideways R character");
        }

        foreach (var character in upright)
        {
            Assert.False(
                TypesetText.ShouldRotateInVertical(character),
                $"{character} should remain upright");
        }

        foreach (var character in transformed)
        {
            Assert.True(TypesetText.IsVerticalTransformed(character), $"{character} should use vert");
            Assert.False(TypesetText.ShouldRotateInVertical(character));
        }

        Assert.False(TypesetText.ShouldRotateInVertical('·'));
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
        Assert.Equal(-1, layout.HitTest(pageIndex, new SKPoint(0, 0)));
    }

    [Fact]
    public void Paint_HorizontalTextProducesInkInsideThePage()
    {
        var path = WriteChapter("<p>横排自绘文字必须落在基线附近，而不是被重复减去绝对基线后画到页面外。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        using var surface = SKSurface.Create(new SKImageInfo(400, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var fonts = new TypesetFontLibrary(_fontPath);
        new TypesetPainter(fonts).Paint(surface.Canvas, layout.Pages[0]);
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        var darkPixels = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128)
                    darkPixels++;
            }
        }

        Assert.True(darkPixels > 10, $"expected horizontal glyph ink, found {darkPixels} dark samples");
        var first = content.BodyText.IndexOf('横');
        var page = layout.GetPageIndexOfOffset(first);
        var rect = layout.GetCharRect(page, first);
        Assert.True(rect is { Top: >= 0, Bottom: <= 600 });
    }

    [Fact]
    public void Vertical_FirstImageDoesNotCreateAnEmptyPage()
    {
        var imagePath = WritePng();
        var path = WriteChapter("<img id=\"cover\" src=\"image.png\"/><p>图片之后的正文。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        Assert.True(layout.Pages.Count >= 2);
        Assert.Contains(layout.Pages[0].Images, image => image.Path == imagePath);
        Assert.NotEmpty(layout.Pages[1].Runs);
        Assert.Equal(0, layout.GetPageIndexOfFragment("cover"));
    }

    [Fact]
    public void Loader_ResolvesImageQueryAndInlineFootnoteHotZone()
    {
        var imagePath = WritePng();
        var path = Path.Combine(_tempDir, "linked.xhtml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body>
                <p id="intro"><span id="inline-anchor">正文<a href="#fn1" epub:type="noteref">注</a></span></p>
                <img src="image.png?cache=1#cover"/>
                <aside id="fn1" epub:type="footnote"><p>脚注定义。</p></aside>
              </body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);
        var imageBlock = Assert.Single(content.Blocks, block => block.Kind == BlockKind.Image);
        Assert.Equal(imagePath, imageBlock.Items[0].ImagePath);
        Assert.Contains("inline-anchor", content.Blocks[0].FragmentIds);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        Assert.True(layout.GetPageIndexOfFragment("inline-anchor") >= 0);
        var hotZone = Assert.Single(layout.Pages.SelectMany(pageItem => pageItem.HotZones));
        Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
        Assert.Contains("#fn1", hotZone.Href, StringComparison.Ordinal);
        Assert.Equal(hotZone, layout.GetHotZoneAt(layout.GetPageIndexOfFragment("intro"),
            new SKPoint((hotZone.Rect.Left + hotZone.Rect.Right) / 2f, (hotZone.Rect.Top + hotZone.Rect.Bottom) / 2f)));
    }

    [Fact]
    public void Loader_KeepsInlineImageFragmentOnTheImageBlock()
    {
        var imagePath = WritePng();
        var path = WriteChapter(
            $"<p>图片前文字<img id=\"inline-image\" src=\"{Path.GetFileName(imagePath)}\"/>图片后文字。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var imageBlock = Assert.Single(content.Blocks, block => block.Kind == BlockKind.Image);

        Assert.Contains("inline-image", imageBlock.FragmentIds);
        Assert.DoesNotContain("inline-image", content.Blocks
            .Where(block => block.Kind != BlockKind.Image)
            .SelectMany(block => block.FragmentIds));
    }

    [Fact]
    public void FormulaImagesStayInlineAndFitBothWritingModes()
    {
        var imagePath = WritePng("w2.png");
        var path = WriteChapter(
            "<p>公式前<img alt=\"w2\" src=\"w2.png\" style=\"height:1.5em;\"/>公式后。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        var paragraph = Assert.Single(content.Blocks);
        Assert.Equal(BlockKind.Paragraph, paragraph.Kind);
        var formula = Assert.Single(paragraph.Items, item => item.Kind == InlineKind.Image);
        Assert.Equal(imagePath, formula.ImagePath);
        Assert.Equal(1.5f, formula.ImageHeightEm);
        Assert.DoesNotContain(content.Blocks, block => block.Kind == BlockKind.Image);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var options = Options(mode);
            var layout = engine.Compose(content, options);
            var placed = Assert.Single(layout.Pages.SelectMany(pageItem => pageItem.Images));

            Assert.True(placed.Rect.Left >= 0f, $"formula left edge escaped in {mode}");
            Assert.True(placed.Rect.Top >= 0f, $"formula top edge escaped in {mode}");
            Assert.True(placed.Rect.Right <= options.ViewportWidth + 0.01f, $"formula right edge escaped in {mode}");
            Assert.True(placed.Rect.Bottom <= options.ViewportHeight + 0.01f, $"formula bottom edge escaped in {mode}");
            Assert.True(placed.Rect.Width < 60f, $"formula was not scaled as an inline object in {mode}");
            Assert.Equal(mode == TypesetWritingMode.VerticalRl ? 90f : 0f, placed.RotationDegrees);
        }
    }

    [Fact]
    public void DecorativeQuoteImagesHonorCssWidthAndAlignment()
    {
        var leftImagePath = WritePng("yinhao-left.png");
        var rightImagePath = WritePng("yinhao-right.png");
        var cssPath = Path.Combine(_tempDir, "quotes.css");
        File.WriteAllText(cssPath, """
            .center { text-align: left; text-indent: 0em; }
            img.yinhao_l { width: 12%; }
            .yinhao_r { text-align: right; }
            .yinhao_r img { width: 12%; }
            """);
        var path = Path.Combine(_tempDir, "dedication.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><link rel="stylesheet" type="text/css" href="quotes.css"/></head>
              <body>
                <div class="juzhong3">
                  <p class="center"><img class="yinhao_l" src="{Path.GetFileName(leftImagePath)}"/><b>致</b></p>
                  <p>杰奎琳（Jacqueline）、乔舒亚（Joshua）。</p>
                  <div class="yinhao_r"><img src="{Path.GetFileName(rightImagePath)}"/></div>
                </div>
              </body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);
        var first = Assert.Single(content.Blocks, block => block.Items.Any(item => item.ImagePath == leftImagePath));
        Assert.Equal(BlockKind.Paragraph, first.Kind);
        var left = Assert.Single(first.Items, item => item.ImagePath == leftImagePath);
        Assert.Equal(0.12f, left.ImageWidthFactor);
        Assert.True(left.DecorativeQuote);

        var rightBlock = Assert.Single(content.Blocks, block => block.Items.Any(item => item.ImagePath == rightImagePath));
        Assert.Equal(BlockKind.Image, rightBlock.Kind);
        Assert.True(rightBlock.AlignRight);
        Assert.Equal(0.12f, rightBlock.Items[0].ImageWidthFactor);
        Assert.True(rightBlock.Items[0].DecorativeQuote);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb);
        var layout = engine.Compose(content, options);
        var images = layout.Pages.SelectMany(page => page.Images).ToList();
        Assert.Equal(2, images.Count);
        foreach (var image in images)
        {
            Assert.InRange(image.Rect.Width, 0f, options.BaseFontSize * 5f + 0.01f);
        }

        var rightPlaced = Assert.Single(images, image => image.Path == rightImagePath);
        Assert.Equal(options.ViewportWidth - options.InsetHorizontal, rightPlaced.Rect.Right, 2);
    }

    [Fact]
    public void FragmentInLongParagraphResolvesToItsActualPage()
    {
        var prefix = string.Concat(Enumerable.Range(0, 60).Select(_ => "前置文字用于把锚点推到下一页。"));
        var path = WriteChapter($"<p>{prefix}<a id=\"later\" href=\"#later\">后置锚点文字。</a></p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var offset = content.BodyText.IndexOf("后置锚点", StringComparison.Ordinal);
            var expectedPage = layout.GetPageIndexOfOffset(offset);

            Assert.True(expectedPage > 0, $"test anchor should cross a page in {mode}");
            Assert.Equal(expectedPage, layout.GetPageIndexOfFragment("later"));
        }
    }

    [Fact]
    public void FragmentHeadingStartsAtTheTopOfItsPage()
    {
        var prefix = string.Concat(Enumerable.Range(0, 35).Select(_ => "前置正文用于让目录标题落在已有内容之后。"));
        var path = WriteChapter($"<p>{prefix}</p><h2 id=\"target-heading\">目标章节</h2><p>章节正文。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        var headingOffset = content.BodyText.IndexOf("目标章节", StringComparison.Ordinal);
        Assert.True(headingOffset >= 0);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var options = Options(mode);
            var layout = engine.Compose(content, options);
            var pageIndex = layout.GetPageIndexOfFragment("target-heading");
            Assert.True(pageIndex > 0, $"heading should follow a filled page in {mode}");
            Assert.Equal(pageIndex, layout.GetPageIndexOfOffset(headingOffset));

            var rect = layout.GetCharRect(pageIndex, headingOffset);
            Assert.True(rect.HasValue, $"heading glyph is missing in {mode}");
            Assert.InRange(
                rect.Value.Top,
                0f,
                options.InsetVertical + options.BaseFontSize * 1.5f);
        }
    }

    [Fact]
    public void Loader_PreservesCollapsedWhitespaceBetweenInlineElements()
    {
        var path = WriteChapter("<p><span>甲</span> <span>乙</span></p>");
        var content = new XhtmlChapterLoader().Load(path);
        var block = Assert.Single(content.Blocks);
        Assert.Contains(block.Items, item => item.Text == " ");

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var firstOffset = content.BodyText.IndexOf('甲');
            var secondOffset = content.BodyText.IndexOf('乙');
            var firstPage = layout.GetPageIndexOfOffset(firstOffset);
            var secondPage = layout.GetPageIndexOfOffset(secondOffset);
            Assert.Equal(firstPage, secondPage);

            var firstRect = layout.GetCharRect(firstPage, firstOffset);
            var secondRect = layout.GetCharRect(secondPage, secondOffset);
            Assert.True(firstRect.HasValue && secondRect.HasValue);
            if (mode == TypesetWritingMode.HorizontalTb)
                Assert.True(secondRect.Value.Left > firstRect.Value.Left);
            else
                Assert.True(secondRect.Value.Top > firstRect.Value.Top);
        }
    }

    [Fact]
    public void Loader_RendersFnoteDefinitionsAndKeepsTheirBacklinkInteractive()
    {
        var path = WriteChapter(
            "<p>正文<sup><a href=\"#note1n\" id=\"note1\">[1]</a></sup>之后。</p>"
            + "<div class=\"fnote\"><p><a href=\"#note1\" id=\"note1n\">[1]</a>这是脚注定义。</p></div>");
        var content = new XhtmlChapterLoader().Load(path);

        Assert.Contains("这是脚注定义", content.BodyText);
        var definition = Assert.Single(content.Blocks, block =>
            block.Items.Any(item => item.Text.Contains("脚注定义", StringComparison.Ordinal)));
        Assert.Contains("note1n", definition.FragmentIds);
        var definitionBacklink = Assert.Single(definition.Items, item => item.Text == "[1]");
        Assert.Equal(InlineKind.Text, definitionBacklink.Kind);
        Assert.True(definitionBacklink.Style.NoWrap);
        Assert.False(definitionBacklink.Style.Superscript);
        Assert.Contains("#note1", definitionBacklink.LinkHref, StringComparison.Ordinal);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var hotZone = Assert.Single(
                layout.Pages.SelectMany(page => page.HotZones),
                zone => zone.Kind == HotZoneKind.FootnoteMarker
                    && zone.Href.Contains("#note1n", StringComparison.Ordinal));
            Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
            Assert.True(layout.GetPageIndexOfFragment("note1n") >= 0);
            Assert.Equal(
                layout.GetPageIndexOfOffset(content.BodyText.IndexOf("这是脚注定义", StringComparison.Ordinal)),
                layout.GetPageIndexOfFragment("note1n"));
        }
    }

    [Fact]
    public void FootnoteReferenceIsCompactAtomicSuperscriptInBothWritingModes()
    {
        var path = WriteChapter(
            "<p>正文<sup><a href=\"#note3n\" id=\"note3\">[3]</a></sup>之后。</p>"
            + "<div class=\"fnote\" id=\"note3n\"><p>这是脚注定义。</p></div>");
        var content = new XhtmlChapterLoader().Load(path);
        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items), item =>
            item.Kind == InlineKind.FootnoteMarker);

        Assert.Equal("[3]", marker.Text);
        Assert.True(marker.Style.Superscript);
        Assert.True(marker.TextStart >= 0);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var options = Options(mode);
            var layout = engine.Compose(content, options);
            var markerRuns = layout.Pages
                .SelectMany(page => page.Runs)
                .Where(run => run.TextStart == marker.TextStart && run.TextLength == marker.Text.Length)
                .ToList();

            // The complete marker is one shaped run, so both brackets share
            // the same superscript baseline instead of becoming three cells.
            var markerRun = Assert.Single(markerRuns);
            Assert.True(markerRun.Style.Superscript);
            Assert.InRange(markerRun.FontSize, 0f, options.BaseFontSize * 0.70f + 0.01f);
            Assert.Equal(3, markerRun.TextLength);
            if (mode == TypesetWritingMode.VerticalRl)
            {
                Assert.False(markerRun.Sideways);
                Assert.Equal(3, markerRun.Glyphs.Length);
            }

            var hotZone = Assert.Single(
                layout.Pages.SelectMany(page => page.HotZones),
                zone => zone.Kind == HotZoneKind.FootnoteMarker
                    && zone.Href.Contains("#note3n", StringComparison.Ordinal));
            Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
            Assert.Contains("#note3n", hotZone.Href, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Loader_TurnsAltTextFootnoteImagesIntoInlineMarkers()
    {
        var path = WriteChapter(
            "<p>正文前<img class=\"qqreader-footnote\" alt=\"这是图片脚注内容。\" src=\"missing-note.png\"/>正文后。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        Assert.DoesNotContain(content.Blocks, block => block.Kind == BlockKind.Image);
        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items), item =>
            item.Kind == InlineKind.FootnoteMarker);
        Assert.Equal("这是图片脚注内容。", marker.FootnoteText);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var hotZone = Assert.Single(layout.Pages.SelectMany(page => page.HotZones));
            Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
            Assert.Equal(marker.FootnoteText, hotZone.FootnoteText);
        }
    }

    [Fact]
    public void Vertical_CharRectAndHitTestUseThePaintedCell()
    {
        var path = WriteChapter("<p>竖排文字的命中区域必须和实际字格保持一致。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        var offset = content.BodyText.IndexOf('竖');
        var pageIndex = layout.GetPageIndexOfOffset(offset);
        var rect = layout.GetCharRect(pageIndex, offset);

        Assert.True(rect is { Width: > 0, Height: > 0 });
        Assert.InRange(rect!.Value.Left, 0, layout.Pages[pageIndex].Width);
        Assert.InRange(rect.Value.Top, 0, layout.Pages[pageIndex].Height);
        var hit = layout.HitTest(
            pageIndex,
            new SKPoint(
                (rect.Value.Left + rect.Value.Right) / 2f,
                (rect.Value.Top + rect.Value.Bottom) / 2f));
        Assert.InRange(hit, offset, offset + 1);
    }

    [Fact]
    public void Vertical_CollapsesParagraphWhitespaceBeforeTheFirstCell()
    {
        var path = WriteChapter("<p>\n        甲乙\n        丙丁\n      </p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl);
        var layout = engine.Compose(content, options);

        var offset = content.BodyText.IndexOf('甲');
        var pageIndex = layout.GetPageIndexOfOffset(offset);
        var rect = layout.GetCharRect(pageIndex, offset);

        Assert.True(rect is { Width: > 0, Height: > 0 });
        // Vertical paragraphs are flush with the content top even though the
        // loader's horizontal paragraph-indent preference remains enabled.
        Assert.InRange(
            rect!.Value.Top,
            options.InsetVertical - 2f,
            options.InsetVertical + options.BaseFontSize);
    }

    [Fact]
    public void Vertical_FullColumnsKeepEveryGlyphInsideTheBottomContentEdge()
    {
        // Five cells fit this content height. The full stop is the sixth cell:
        // it must continue in the next column instead of hanging below the
        // content edge as the legacy WebKit rule allowed.
        var path = WriteChapter("<p>甲乙丙丁戊。己庚辛壬癸。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);
        var contentBottom = options.ViewportHeight - options.InsetVertical;

        for (var offset = 0; offset < content.BodyText.Length; offset++)
        {
            var pageIndex = layout.GetPageIndexOfOffset(offset);
            if (layout.GetCharRect(pageIndex, offset) is not { } rect)
            {
                continue;
            }

            Assert.True(
                rect.Bottom <= contentBottom + 0.01f,
                $"character {offset} ends at {rect.Bottom}, content edge is {contentBottom}");
        }
    }

    [Fact]
    public void Loader_FootnoteDefinitionsStayInOffsetStreamAndAreRendered()
    {
        var path = WriteChapter(
            "<p>正文引用了一个注<sup>1</sup>。</p>"
            + "<aside id=\"footnote-1\"><p>这是脚注的定义内容。</p></aside>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        Assert.Contains("脚注的定义内容", content.BodyText);
        var definition = Assert.Single(content.Blocks, b => b.Items.Any(i =>
            i.Text.Contains("脚注的定义", StringComparison.Ordinal)));
        Assert.Contains("footnote-1", definition.FragmentIds);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        Assert.True(layout.GetPageIndexOfFragment("footnote-1") >= 0);
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

    [Fact]
    public void Paint_RendersEveryAnnotationUnderlineStyleInBothWritingModes()
    {
        var path = WriteChapter("<p>横排和竖排都要能显示选中文字的划线。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        using var fonts = new TypesetFontLibrary(_fontPath);

        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var annotationStart = content.BodyText.IndexOf('横');
            var pageIndex = layout.GetPageIndexOfOffset(annotationStart);
            Assert.True(pageIndex >= 0);
            var page = layout.Pages[pageIndex];
            var bands = layout.GetOverlayRects(pageIndex, annotationStart, 5);
            Assert.NotEmpty(bands);

            foreach (var style in new[] { "solid", "double", "dashed", "dotted", "wavy" })
            {
                using var surface = SKSurface.Create(new SKImageInfo(400, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
                var painter = new TypesetPainter(fonts);
                painter.Paint(
                    surface.Canvas,
                    page,
                    annotationOverlays: new[]
                    {
                        new TypesetAnnotationOverlay
                        {
                            Bands = bands,
                            Style = style,
                            Color = new SKColor(220, 20, 20),
                        },
                    });
                surface.Canvas.Flush();

                using var snapshot = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(snapshot);
                var coloredPixels = 0;
                for (var y = 0; y < 600; y++)
                {
                    for (var x = 0; x < 400; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.Red > 150 && pixel.Green < 100 && pixel.Blue < 100)
                        {
                            coloredPixels++;
                        }
                    }
                }

                Assert.True(
                    coloredPixels > 0,
                    $"expected {style} annotation ink in {mode}, found no colored pixels");
            }
        }
    }
}
