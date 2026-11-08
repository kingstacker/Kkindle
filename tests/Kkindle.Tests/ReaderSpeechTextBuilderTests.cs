using Kkindle.Core;
using Kkindle.Layout;

namespace Kkindle.Tests;

public sealed class ReaderSpeechTextBuilderTests
{
    [Fact]
    public void SpeechOmitsRubyAndPreservesSourceOffsets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kkindle-speech-{Guid.NewGuid():N}.xhtml");
        try
        {
            File.WriteAllText(path, "<html><body><p><ruby>银<rt>yín</rt></ruby>行测试</p></body></html>");
            var content = new XhtmlChapterLoader(false).Load(path);
            var speech = ReaderSpeechTextBuilder.Load(path, false, CancellationToken.None);
            Assert.NotNull(speech);
            Assert.Contains("银", speech.Text);
            Assert.Contains("行测试", speech.Text);
            Assert.DoesNotContain("yín", speech.Text);
            var target = speech.Text.IndexOf("测试", StringComparison.Ordinal);
            var mapped = speech.MapToSource(target, 2);
            Assert.Equal("测试", content.BodyText.Substring(mapped.Start, mapped.Length));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void CrossPageSpeechRangeCoversEveryVisibleCharacter(TypesetWritingMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kkindle-speech-{Guid.NewGuid():N}.xhtml");
        try
        {
            var body = "前文 &amp;amp; &lt;tag&gt; <ruby>银<rt>yín</rt></ruby>"
                + string.Concat(Enumerable.Repeat("行跨行跨页测试", 28)) + "。";
            File.WriteAllText(path, $"<html><body><p>{body}</p></body></html>");
            var content = new XhtmlChapterLoader(false).Load(path);
            using var engine = new TypesetEngine(new TypesetFontLibrary(FindBundledFont()));
            var layout = engine.Compose(content, new TypesetLayoutOptions
            {
                WritingMode = mode,
                BaseFontSize = 17f,
                LineHeight = 1.8f,
                ViewportWidth = 180f,
                ViewportHeight = 180f,
                InsetHorizontal = 24f,
                InsetVertical = 24f,
            });
            var speech = ReaderSpeechTextBuilder.Build(content, 0, layout);
            Assert.NotNull(speech);
            Assert.Contains("&amp; <tag>", speech.Text);
            Assert.DoesNotContain("yín", speech.Text);
            var segment = Assert.Single(TtsTextSegmenter.SplitPlainTextSentences(speech.Text));
            var range = speech.MapToSource(segment.Start, segment.Length);
            var pagesWithText = new HashSet<int>();
            var bandsOnPages = new Dictionary<int, IReadOnlyList<SkiaSharp.SKRect>>();

            for (var index = segment.Start; index < segment.End; index++)
            {
                if (char.IsWhiteSpace(speech.Text[index])) continue;
                var sourceOffset = speech.MapToSource(index, 1).Start;
                var pageIndex = layout.GetPageIndexOfOffset(sourceOffset);
                pagesWithText.Add(pageIndex);
                var character = layout.GetCharRect(pageIndex, sourceOffset);
                Assert.True(character.HasValue, $"Missing character rectangle at {sourceOffset}");
                if (!bandsOnPages.TryGetValue(pageIndex, out var bands))
                {
                    bands = layout.GetOverlayRects(pageIndex, range.Start, range.Length);
                    bandsOnPages.Add(pageIndex, bands);
                }
                var rect = character.Value;
                Assert.Contains(bands, band => band.Left <= rect.Left + 0.01f
                    && band.Top <= rect.Top + 0.01f
                    && band.Right >= rect.Right - 0.01f
                    && band.Bottom >= rect.Bottom - 0.01f);
            }

            Assert.True(pagesWithText.Count > 1);
            Assert.Contains(bandsOnPages.Values, bands => bands.Count > 1);
        }
        finally { File.Delete(path); }
    }

    private static string FindBundledFont()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "Kkindle.App", "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Bundled font not found.");
    }

    [Fact]
    public void LoadHonorsCancellationBeforeOpeningTheFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ReaderSpeechTextBuilder.Load("missing.xhtml", false, cancellation.Token));
    }
}
