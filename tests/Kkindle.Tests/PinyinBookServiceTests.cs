using System.IO.Compression;
using System.Net;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class PinyinBookServiceTests
{
    [Fact]
    public async Task GeneratesRubyEpubWhilePreservingAssetsAndSkippingExistingRuby()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "儿童读物.epub");
            var destinationPath = Path.Combine(root, "儿童读物-拼音版.epub");
            CreateEpub(sourcePath);
            var progress = new List<PinyinBookProgress>();

            var service = new PinyinBookService();
            var result = await service.GenerateAsync(
                sourcePath,
                destinationPath,
                progress: new TestHelpers.InlineProgress<PinyinBookProgress>(progress.Add));

            Assert.Equal(sourcePath, result.SourcePath);
            Assert.Equal(destinationPath, result.OutputPath);
            Assert.Equal(1, result.ScannedPageCount);
            Assert.Equal(1, result.AnnotatedPageCount);
            Assert.Equal(8, result.AnnotatedCharacterCount);
            Assert.Equal(100, progress[^1].Percentage);

            var markup = ReadArchiveEntry(destinationPath, "OEBPS/chapter.xhtml");
            Assert.Contains("<ruby>银<rt>yín</rt></ruby>", markup, StringComparison.Ordinal);
            Assert.Contains("<ruby>行<rt>háng</rt></ruby>", markup, StringComparison.Ordinal);
            Assert.Contains("<ruby>行<rt>xíng</rt></ruby>", markup, StringComparison.Ordinal);
            Assert.Contains("<ruby>为<rt>wéi</rt></ruby>", markup, StringComparison.Ordinal);
            Assert.Contains("<ruby>国<rt>guó</rt></ruby>", markup, StringComparison.Ordinal);
            Assert.Contains("<ruby>中<rt>zhōng</rt></ruby>", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<ruby>不<rt>", markup, StringComparison.Ordinal);
            Assert.Equal("image-bytes", ReadArchiveEntry(destinationPath, "OEBPS/image.txt"));

            using var archive = ZipFile.OpenRead(destinationPath);
            Assert.Equal("mimetype", archive.Entries[0].FullName);
            Assert.Equal("application/epub+zip", ReadArchiveEntry(destinationPath, "mimetype"));
            Assert.NotEqual(await File.ReadAllBytesAsync(sourcePath), await File.ReadAllBytesAsync(destinationPath));

        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData(PinyinBookOutputStyle.ToneNumber, "yin2")]
    [InlineData(PinyinBookOutputStyle.NoTone, "yin")]
    public async Task SupportsAlternativePinyinStyles(PinyinBookOutputStyle style, string expectedPinyin)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "style.epub");
            var destinationPath = Path.Combine(root, "style-output.epub");
            CreateEpub(sourcePath);

            await new PinyinBookService().GenerateAsync(
                sourcePath,
                destinationPath,
                new PinyinBookOptions { OutputStyle = style });

            var markup = ReadArchiveEntry(destinationPath, "OEBPS/chapter.xhtml");
            Assert.Contains($"<ruby>银<rt>{expectedPinyin}</rt></ruby>", markup, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsOverwritingTheSourceBook()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "same.epub");
            CreateEpub(sourcePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PinyinBookService().GenerateAsync(sourcePath, sourcePath));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void KindleSendPoliciesPreferTheGeneratedPinyinVersion()
    {
        var original = new BookFile
        {
            Format = "epub",
            RelativePath = "library/book/original.epub"
        };
        var pinyin = new BookFile
        {
            Format = "epub",
            RelativePath = "library/book/book_pinyin.epub"
        };
        var legacyPinyin = new BookFile
        {
            Format = "epub",
            RelativePath = "library/book/book+pinyin.epub"
        };
        var legacyPinyinWithoutSeparator = new BookFile
        {
            Format = "epub",
            RelativePath = "library/book/bookpinyin.epub"
        };
        var legacyChinesePinyin = new BookFile
        {
            Format = "epub",
            RelativePath = "library/book/book-拼音版.epub"
        };

        Assert.Same(pinyin, KindleTransferPolicy.SelectPreferred([original, pinyin]));
        Assert.Same(pinyin, KindleEmailSelectionPolicy.SelectPreferred([original, pinyin]));
        Assert.True(PinyinBookPolicy.IsGeneratedPinyinVersion(legacyPinyin));
        Assert.True(PinyinBookPolicy.IsGeneratedPinyinVersion(legacyPinyinWithoutSeparator));
        Assert.True(PinyinBookPolicy.IsGeneratedPinyinVersion(legacyChinesePinyin));
    }

    [Theory]
    [InlineData("书名", "书名_pinyin")]
    [InlineData("书名_pinyin", "书名_pinyin")]
    [InlineData("书名+pinyin", "书名_pinyin")]
    [InlineData("书名+PINYIN", "书名_pinyin")]
    [InlineData("书名pinyin", "书名_pinyin")]
    [InlineData("pinyin", "pinyin_pinyin")]
    [InlineData("书名-拼音版", "书名_pinyin")]
    public void GeneratedPinyinTitleAlwaysUsesTheRequiredSuffix(string title, string expected)
    {
        Assert.Equal(expected, PinyinBookPolicy.CreateGeneratedTitle(title));
    }

    [Fact]
    public void LegacyPinyinFileWithTrailingWhitespaceStillGetsTheRequiredSeparator()
    {
        var file = new BookFile
        {
            Format = "epub",
            RelativePath = "library/book/规模pinyin .epub"
        };

        Assert.True(PinyinBookPolicy.IsGeneratedPinyinVersion(file));
        Assert.Equal("规模_pinyin", PinyinBookPolicy.CreateGeneratedTitle(
            Path.GetFileNameWithoutExtension(file.RelativePath).Trim()));
    }

    [Fact]
    public async Task ConvertsNonEpubInputBeforeAnnotatingWhenAConverterIsProvided()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "book.azw3");
            var destinationPath = Path.Combine(root, "book-拼音版.epub");
            await File.WriteAllTextAsync(sourcePath, "source");
            var converter = new RecordingConverter(path => CreateEpub(path));

            var result = await new PinyinBookService(converter).GenerateAsync(
                sourcePath,
                destinationPath);

            Assert.True(converter.Called);
            Assert.True(result.AnnotatedCharacterCount > 0);
            Assert.Contains("<ruby>银<rt>yín</rt></ruby>", ReadArchiveEntry(destinationPath, "OEBPS/chapter.xhtml"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task SendsOnlyPolyphoneFallbacksToAiAndAppliesAnAllowedChoice()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "review.epub");
            var destinationPath = Path.Combine(root, "review-output.epub");
            CreateEpub(sourcePath, "行。 ");

            using var aiClient = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"[{\\\"id\\\":1,\\\"pinyin\\\":\\\"háng\\\"}]\"}}]}\n\n"
                            + "data: [DONE]\n\n",
                        Encoding.UTF8,
                        "text/event-stream")
                };
            }));
            var settings = new AiConnectionSettings
            {
                Provider = "custom",
                BaseUrl = "https://api.example.com/v1",
                Model = "pinyin-review",
                ApiKey = "sk-test"
            };

            var result = await new PinyinBookService(null, aiClient, settings).GenerateAsync(
                sourcePath,
                destinationPath,
                new PinyinBookOptions { EnableAiReview = true });

            Assert.Equal(1, result.ReviewCandidateCount);
            Assert.Equal(1, result.AiReviewedCandidateCount);
            Assert.Null(result.AiReviewError);
            Assert.Contains(
                "<ruby>行<rt>háng</rt></ruby>",
                ReadArchiveEntry(destinationPath, "OEBPS/chapter.xhtml"),
                StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task LocalOnlyModeSkipsAiReviewWithoutCallingTheAiClient()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "local-only.epub");
            var destinationPath = Path.Combine(root, "local-only-output.epub");
            CreateEpub(sourcePath, "行。 ");
            var requestCount = 0;

            using var aiClient = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("unexpected")
                };
            }));
            var settings = new AiConnectionSettings
            {
                Provider = "custom",
                BaseUrl = "https://api.example.com/v1",
                Model = "pinyin-review",
                ApiKey = "sk-test"
            };

            var result = await new PinyinBookService(null, aiClient, settings).GenerateAsync(
                sourcePath,
                destinationPath,
                new PinyinBookOptions { EnableAiReview = false });

            Assert.True(result.ReviewCandidateCount > 0);
            Assert.Equal(0, result.AiReviewedCandidateCount);
            Assert.Null(result.AiReviewError);
            Assert.Equal(0, requestCount);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task KeepsPinyinResumeCacheWhenCanceledAndResumesCompletedSegments()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "resume.epub");
            var destinationPath = Path.Combine(root, "resume-output.epub");
            CreateMultiSegmentEpub(sourcePath);
            var options = new PinyinBookOptions { EnableAiReview = false };
            using var cancellation = new CancellationTokenSource();
            var canceledAfterFirstSegment = false;

            await Assert.ThrowsAsync<OperationCanceledException>(() => new PinyinBookService().GenerateAsync(
                sourcePath,
                destinationPath,
                options,
                new TestHelpers.InlineProgress<PinyinBookProgress>(value =>
                {
                    if (!canceledAfterFirstSegment
                        && value.Segment?.Status == PinyinBookSegmentStatus.Completed
                        && value.Segment.Index == 0)
                    {
                        canceledAfterFirstSegment = true;
                        cancellation.Cancel();
                    }
                }),
                cancellation.Token));

            var cachePath = destinationPath + ".kkindle-pinyin-cache.jsonl";
            Assert.True(File.Exists(cachePath));
            Assert.False(File.Exists(destinationPath));

            var resumeInfo = await new PinyinBookService().FindResumeAsync(
                sourcePath,
                destinationPath,
                options);
            Assert.NotNull(resumeInfo);
            Assert.Equal(1, resumeInfo.CompletedSegments);
            Assert.Equal(2, resumeInfo.TotalSegments);
            Assert.Equal(1, resumeInfo.IncompleteSegments);

            var resumedProgress = new List<PinyinBookProgress>();
            var result = await new PinyinBookService().GenerateAsync(
                sourcePath,
                destinationPath,
                options,
                new TestHelpers.InlineProgress<PinyinBookProgress>(resumedProgress.Add),
                resumeMode: PinyinBookResumeMode.Resume);

            Assert.Equal(1, result.AnnotatedPageCount);
            Assert.Equal(0, result.FailedSegmentCount);
            Assert.True(File.Exists(destinationPath));
            Assert.False(File.Exists(cachePath));
            Assert.Contains(
                resumedProgress,
                value => value.Segment?.ProcessFlow.Contains("恢复本地缓存", StringComparison.Ordinal) == true);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task KeepsFailedPinyinSegmentsForASecondAiReview()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "ai-failure.epub");
            var destinationPath = Path.Combine(root, "ai-failure-output.epub");
            CreateEpub(sourcePath, "行。 ");
            using var aiClient = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ =>
                throw new HttpRequestException("暂时无法连接")));
            var settings = new AiConnectionSettings
            {
                Provider = "custom",
                BaseUrl = "https://api.example.com/v1",
                Model = "pinyin-review",
                ApiKey = "sk-test"
            };
            var options = new PinyinBookOptions { EnableAiReview = true };

            var result = await new PinyinBookService(null, aiClient, settings).GenerateAsync(
                sourcePath,
                destinationPath,
                options);

            Assert.NotNull(result.AiReviewError);
            Assert.True(result.FailedSegmentCount > 0);
            Assert.True(File.Exists(destinationPath));
            var resumeInfo = await new PinyinBookService().FindResumeAsync(
                sourcePath,
                destinationPath,
                options);
            Assert.NotNull(resumeInfo);
            Assert.True(resumeInfo.FailedSegments > 0);
            Assert.Equal(1, resumeInfo.CompletedSegments);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static void CreateEpub(string path, string? headingText = null)
    {
        headingText ??= "银行的行为。你好！";
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" /></manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>中文标题</title></head>
              <body>
                <h1>{headingText}</h1>
                <p><ruby>中<rt>zhōng</rt></ruby>国</p>
                <pre>不要注音</pre>
              </body>
            </html>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/image.txt", "image-bytes");
    }

    private static void CreateMultiSegmentEpub(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" /></manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body><h1>银行的行为。</h1><p>第二段内容。</p></body>
            </html>
            """);
    }

    private static string ReadArchiveEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(
            archive.GetEntry(entryName)!.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    private sealed class RecordingConverter(Action<string> createEpub) : IBookFormatConverter
    {
        public bool Called { get; private set; }

        public Task ConvertAsync(
            string sourcePath,
            string destinationPath,
            IProgress<FormatConversionProgress>? progress = null,
            CancellationToken cancellationToken = default,
            FormatConversionMetadata? metadata = null)
        {
            Called = true;
            createEpub(destinationPath);
            return Task.CompletedTask;
        }
    }
}
