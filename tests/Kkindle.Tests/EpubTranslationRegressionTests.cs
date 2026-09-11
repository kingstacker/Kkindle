using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kkindle.Core;
using Kkindle.Infrastructure;
using static Kkindle.Tests.TranslationTestBook;

namespace Kkindle.Tests;

public sealed class EpubTranslationRegressionTests
{
    [Theory]
    [InlineData(BookTranslationOutputMode.Translated)]
    [InlineData(BookTranslationOutputMode.Bilingual)]
    public async Task TranslatesOuterTextAroundNestedParagraphsListsAndCells(BookTranslationOutputMode mode)
    {
        using var book = new TranslationTestBook("""
            <div>Outer before.<p>Inner text.</p>Outer after.</div>
            <ul><li>Parent item<ul><li>Child item</li></ul></li></ul>
            <table><tr><td>Cell before.<div>Cell inside.</div>Cell after.</td></tr></table>
            <div hidden="hidden"><p>Hidden text.</p></div>
            """);
        var map = new Dictionary<string, string>
        {
            ["Outer before."] = "外层前文。", ["Inner text."] = "内部正文。", ["Outer after."] = "外层后文。",
            ["Parent item"] = "父项", ["Child item"] = "子项",
            ["Cell before."] = "单元格前文。", ["Cell inside."] = "单元格内部。", ["Cell after."] = "单元格后文。"
        };
        var handler = new GoogleHandler(text => map.Aggregate(text, (value, pair) => value.Replace(pair.Key, pair.Value, StringComparison.Ordinal)));
        using var service = book.Service(handler);
        var result = await service.TranslateAsync(book.Source, book.Output, Settings with { OutputMode = mode });
        var body = Body(Assert.Single(result.OutputPaths));
        Assert.Equal(8, result.SegmentCount);
        foreach (var pair in map)
        {
            Assert.Contains(pair.Value, body.Value, StringComparison.Ordinal);
            if (mode == BookTranslationOutputMode.Translated) Assert.DoesNotContain(pair.Key, body.Value, StringComparison.Ordinal);
            else Assert.Contains(pair.Key, body.Value, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(handler.Inputs, text => text.Contains("Hidden text.", StringComparison.Ordinal));
        Assert.Contains("Hidden text.", body.Value, StringComparison.Ordinal);
        var outer = body.Elements().First();
        Assert.True(outer.Value.IndexOf("外层前文。", StringComparison.Ordinal) < outer.Value.IndexOf("内部正文。", StringComparison.Ordinal));
        Assert.True(outer.Value.IndexOf("内部正文。", StringComparison.Ordinal) < outer.Value.IndexOf("外层后文。", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BookTranslationOutputMode.Translated)]
    [InlineData(BookTranslationOutputMode.Bilingual)]
    public async Task KeepsFootnoteNumbersLinksAndEmphasisAttachedToTheirText(BookTranslationOutputMode mode)
    {
        using var book = new TranslationTestBook("""
            <p id="p1">Important <a href="notes.xhtml#fn1" epub:type="noteref" id="ref1">1</a> sentence with <em id="em1">emphasis</em> and <a href="https://example.test/guide">a link</a>.</p>
            """);
        var handler = new GoogleHandler(text => text.Replace("Important", "重要").Replace("sentence with", "句子带有")
            .Replace("emphasis", "强调").Replace(" and ", " 和 ").Replace("a link", "链接"));
        using var service = book.Service(handler);
        var updates = new List<BookTranslationProgress>();
        var result = await service.TranslateAsync(book.Source, book.Output, Settings with { OutputMode = mode },
            new TestHelpers.InlineProgress<BookTranslationProgress>(updates.Add));
        var body = Body(result.OutputPaths[0]);
        var translated = mode == BookTranslationOutputMode.Translated ? body
            : body.Descendants().Single(element => element.Attribute("class")?.Value == "kkindle-translation");
        var note = translated.Descendants().Single(element => element.Attribute("href")?.Value == "notes.xhtml#fn1");
        Assert.Equal("1", note.Value);
        Assert.Equal("强调", translated.Descendants().Single(element => element.Name.LocalName == "em").Value);
        Assert.Equal("链接", translated.Descendants().Single(element => element.Attribute("href")?.Value == "https://example.test/guide").Value);
        Assert.DoesNotContain("__KKINDLE_", body.ToString(), StringComparison.Ordinal);
        var ids = body.DescendantsAndSelf().Attributes("id").Select(attribute => attribute.Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(updates.Where(update => update.Segment is not null), update =>
        {
            Assert.DoesNotContain("__KKINDLE_", update.Segment!.OriginalText, StringComparison.Ordinal);
            Assert.DoesNotContain("__KKINDLE_", update.Segment.TranslatedText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FallsBackToTextRunsWhenProviderRemovesInlineMarkers()
    {
        using var book = new TranslationTestBook("<p>Important <a href=\"notes.xhtml#fn1\" epub:type=\"noteref\">1</a> sentence with <em>emphasis</em>.</p>");
        var handler = new GoogleHandler(text => Regex.Replace(text, @"__KKINDLE_INLINE_\d+__", "")
            .Replace("Important", "重要").Replace("sentence with", "句子带有").Replace("emphasis", "强调"));
        using var service = book.Service(handler);
        var result = await service.TranslateAsync(book.Source, book.Output, Settings);
        var body = Body(result.OutputPaths[0]);
        Assert.Equal("1", body.Descendants().Single(element => element.Name.LocalName == "a").Value);
        Assert.Equal("强调", body.Descendants().Single(element => element.Name.LocalName == "em").Value);
        Assert.DoesNotContain("__KKINDLE_", body.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Inputs.Count(text => text.Contains("__KKINDLE_INLINE_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EmojiAndCombiningCharactersSurviveRenderingAndCachedResume()
    {
        using var book = new TranslationTestBook("<p>Happy <em>birthday</em>🎂 👨‍👩‍👧‍👦 é</p>");
        var handler = new GoogleHandler(text => text.Replace("Happy", "生日").Replace("birthday", "快乐"));
        using var service = book.Service(handler);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(
            book.Source, book.Output, Settings,
            new TestHelpers.InlineProgress<BookTranslationProgress>(value =>
            {
                if (value.Segment?.Status == BookTranslationSegmentStatus.Completed) cancel.Cancel();
            }), cancel.Token));
        Assert.Equal(1, (await service.FindResumeAsync(book.Source, book.Output, Settings))!.CompletedSegments);

        var noNetwork = new GoogleHandler(_ => throw new InvalidOperationException("Completed text must be reused"));
        using var resumedService = book.Service(noNetwork);
        var result = await resumedService.TranslateAsync(book.Source, book.Output, Settings, resumeMode: BookTranslationResumeMode.Resume);
        Assert.Empty(noNetwork.Inputs);
        var body = Body(result.OutputPaths[0]);
        Assert.Contains("🎂 👨‍👩‍👧‍👦 é", body.Value, StringComparison.Ordinal);
        Assert.Equal("快乐", body.Descendants().Single(element => element.Name.LocalName == "em").Value);
        Assert.Equal(body.Value, XmlConvert.VerifyXmlChars(body.Value));
        Assert.False(File.Exists(book.CachePath));
    }

    [Theory]
    [InlineData(BookTranslationOutputMode.Translated)]
    [InlineData(BookTranslationOutputMode.Bilingual)]
    public async Task TranslatesRubyBaseWithoutSendingOrReplacingPronunciations(BookTranslationOutputMode mode)
    {
        using var book = new TranslationTestBook("<p><ruby>银<rp>(</rp><rt>yín</rt><rp>)</rp></ruby><ruby><rb>行</rb><rt>háng</rt></ruby></p>");
        var handler = new GoogleHandler(_ => "bank");
        using var service = book.Service(handler);
        var result = await service.TranslateAsync(book.Source, book.Output, Settings with
        {
            SourceLanguage = "zh-CN", TargetLanguage = "en", OutputMode = mode
        });
        Assert.Equal("银行", Assert.Single(handler.Inputs));
        var body = Body(result.OutputPaths[0]);
        if (mode == BookTranslationOutputMode.Translated)
        {
            Assert.Equal("bank", body.Value);
            Assert.DoesNotContain(body.Descendants(), element => element.Name.LocalName is "ruby" or "rt" or "rp");
        }
        else
        {
            Assert.Equal(2, body.Descendants().Count(element => element.Name.LocalName == "rt"));
            Assert.Equal("bank", body.Descendants().Single(element => element.Attribute("class")?.Value == "kkindle-translation").Value);
        }
    }

    [Fact]
    public async Task ResumesOnlyTheFailedPartOfALongParagraph()
    {
        var source = new string('A', 1399) + "." + new string('B', 1399) + "." + new string('C', 100) + ".";
        using var book = new TranslationTestBook("<p>" + source + "</p>");
        var firstHandler = new GoogleHandler(text => text[0] switch
        {
            'A' => "第一部分。", 'B' => "第二部分。", _ => throw new HttpRequestException("last part failed")
        });
        using (var service = book.Service(firstHandler))
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(book.Source, book.Output, Settings));
        Assert.True(File.Exists(book.CachePath));
        var retry = new GoogleHandler(_ => "第三部分。");
        using var resumed = book.Service(retry);
        var result = await resumed.TranslateAsync(book.Source, book.Output, Settings, resumeMode: BookTranslationResumeMode.Resume);
        Assert.StartsWith("C", Assert.Single(retry.Inputs), StringComparison.Ordinal);
        Assert.Equal("第一部分。 第二部分。 第三部分。", Body(result.OutputPaths[0]).Value);
    }

    [Fact]
    public async Task SwitchingFromAiToSmallerWebRequestsReusesCompletedChunks()
    {
        var source = new string('A', 5499) + "." + new string('B', 5499) + "." + new string('C', 500) + ".";
        using var book = new TranslationTestBook("<p>" + source + "</p>");
        await new AiSettingsStore(book.Paths, new TestHelpers.PlaintextSecretProtector()).SaveAsync(AiConfiguration());
        using var ai = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var text = document.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            return text[0] switch
            {
                'A' => ChatReply("第一部分。"), 'B' => ChatReply("第二部分。"),
                _ => throw new HttpRequestException("last part failed")
            };
        }));
        using (var service = book.Service(ai: ai))
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(
                book.Source, book.Output, Settings with { Provider = BookTranslationProvider.Ai }));

        var retry = new GoogleHandler(_ => "第三部分。");
        using var resumed = book.Service(retry);
        var result = await resumed.TranslateAsync(book.Source, book.Output, Settings, resumeMode: BookTranslationResumeMode.Resume);
        Assert.StartsWith("C", Assert.Single(retry.Inputs), StringComparison.Ordinal);
        Assert.Equal("第一部分。 第二部分。 第三部分。", Body(result.OutputPaths[0]).Value);
    }

    [Fact]
    public async Task DoesNotTrustCompletedTextFromTheOldUncheckedCacheFormat()
    {
        using var book = new TranslationTestBook("<p>Hello.</p>");
        var handler = new GoogleHandler(_ => "完整译文。");
        using var service = book.Service(handler);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(book.Source, book.Output, Settings,
            new TestHelpers.InlineProgress<BookTranslationProgress>(value =>
            {
                if (value.Segment?.Status == BookTranslationSegmentStatus.Completed) cancel.Cancel();
            }), cancel.Token));
        var lines = await File.ReadAllLinesAsync(book.CachePath);
        for (var index = 0; index < lines.Length; index++)
        {
            var node = JsonNode.Parse(lines[index])!;
            if (node["Kind"]?.GetValue<string>() == "header") node["Version"] = 1;
            else if (node["TranslatedText"] is not null) node["TranslatedText"] = "只有开头，";
            lines[index] = node.ToJsonString();
        }
        await File.WriteAllLinesAsync(book.CachePath, lines);
        Assert.Null(await service.FindResumeAsync(book.Source, book.Output, Settings));
        var requests = handler.Inputs.Count;
        var result = await service.TranslateAsync(book.Source, book.Output, Settings, resumeMode: BookTranslationResumeMode.Resume);
        Assert.True(handler.Inputs.Count > requests);
        Assert.Equal("完整译文。", Body(result.OutputPaths[0]).Value);
    }

    [Fact]
    public async Task StopsBatchingAfterTheFirstMarkerValidationFailure()
    {
        using var book = new TranslationTestBook("<p>First.</p><p>Second.</p><p>Third.</p><p>Fourth.</p>");
        var handler = new GoogleHandler(_ => "译文。");
        using var service = book.Service(handler);
        var result = await service.TranslateAsync(book.Source, book.Output, Settings);
        Assert.Equal(4, result.SegmentCount);
        Assert.Equal(5, handler.Inputs.Count);
        Assert.Single(handler.Inputs, text => text.Contains("__KKINDLE_SEG_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CountsMarkersTowardRequestLimitAndKeepsUnicodeGraphemesWhole()
    {
        using var book = new TranslationTestBook("<p>" + new string('A', 1399) + "👨‍👩‍👧‍👦" + new string('B', 1400) + "</p><p>Short paragraph.</p>");
        var handler = new GoogleHandler(text => text);
        using var service = book.Service(handler);
        await service.TranslateAsync(book.Source, book.Output, Settings);
        Assert.All(handler.Inputs, text =>
        {
            Assert.True(text.Length <= 1400);
            Assert.Equal(text, XmlConvert.VerifyXmlChars(text));
            if (text.Contains("👨", StringComparison.Ordinal)) Assert.Contains("👨‍👩‍👧‍👦", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UpdatesNcxOnlyTocAndLanguageMetadataForBothOutputModes()
    {
        using var book = new TranslationTestBook("<h1>Chapter one</h1><p lang=\"en\" xml:lang=\"en\">Hello world.</p>", "Chapter one");
        var handler = new GoogleHandler(text => text.Replace("Chapter one", "第一章").Replace("Hello world.", "你好，世界。"));
        using var service = book.Service(handler);
        var result = await service.TranslateAsync(book.Source, book.Output, Settings with
        {
            OutputMode = BookTranslationOutputMode.Translated | BookTranslationOutputMode.Bilingual
        });
        foreach (var path in result.OutputPaths)
        {
            var bilingual = path.EndsWith("_双语版.epub", StringComparison.Ordinal);
            var ncx = ReadXml(path, "OEBPS/toc.ncx");
            Assert.Equal(bilingual ? "Chapter one / 第一章" : "第一章", ncx.Descendants().Single(element => element.Name.LocalName == "text").Value);
            var languages = ReadXml(path, "OEBPS/content.opf").Descendants().Where(element => element.Name.LocalName == "language").Select(element => element.Value).ToArray();
            Assert.Contains("zh-CN", languages);
            var page = ReadXml(path);
            if (bilingual)
            {
                Assert.Contains("en", languages);
                Assert.Equal("en", page.Root!.Attribute("lang")?.Value);
                Assert.All(page.Descendants().Where(element => element.Attribute("class")?.Value == "kkindle-translation"),
                    element => Assert.Equal("zh-CN", element.Attribute(XNamespace.Xml + "lang")?.Value));
            }
            else
            {
                Assert.Single(languages);
                Assert.Equal("zh-CN", page.Root!.Attribute("lang")?.Value);
                Assert.All(page.Descendants().Attributes().Where(attribute => attribute.Name.LocalName == "lang"),
                    attribute => Assert.Equal("zh-CN", attribute.Value));
            }
        }
    }

    [Fact]
    public async Task AddsMissingPackageLanguage()
    {
        using var book = new TranslationTestBook("<p>Hello.</p>", hasLanguage: false);
        using var service = book.Service(new GoogleHandler(_ => "你好。"));
        var result = await service.TranslateAsync(book.Source, book.Output, Settings);
        Assert.Equal("zh-CN", ReadXml(result.OutputPaths[0], "OEBPS/content.opf").Descendants().Single(element => element.Name.LocalName == "language").Value);
    }

    [Fact]
    public async Task ReloadsAiConnectionSettingsForTheNextBook()
    {
        using var book = new TranslationTestBook("<p>Hello.</p>");
        var store = new AiSettingsStore(book.Paths, new TestHelpers.PlaintextSecretProtector());
        var configuration = AiConfiguration();
        await store.SaveAsync(configuration);
        var models = new List<string>();
        using var ai = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            models.Add(document.RootElement.GetProperty("model").GetString()!);
            return ChatReply("你好。");
        }));
        using var service = book.Service(ai: ai);
        var settings = Settings with { Provider = BookTranslationProvider.Ai };
        await service.TranslateAsync(book.Source, book.Output, settings);
        configuration.Model = "new-model";
        await store.SaveAsync(configuration);
        await service.TranslateAsync(book.Source, Path.Combine(book.Root, "second"), settings);
        Assert.Equal(["old-model", "new-model"], models);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("eof")]
    [InlineData("error")]
    public async Task IncompleteAiTranslationFailsAndKeepsItsResumeCache(string ending)
    {
        using var book = new TranslationTestBook("<p>The beginning. The important final condition.</p>");
        await new AiSettingsStore(book.Paths, new TestHelpers.PlaintextSecretProtector()).SaveAsync(AiConfiguration());
        using var ai = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ => ChatReply("只有开头，", ending)));
        using var service = book.Service(ai: ai);
        var settings = Settings with { Provider = BookTranslationProvider.Ai };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(book.Source, book.Output, settings));
        Assert.Empty(Directory.GetFiles(book.Output, "*.epub"));
        Assert.True(File.Exists(book.CachePath));
        var resume = await service.FindResumeAsync(book.Source, book.Output, settings);
        Assert.NotNull(resume);
        Assert.Equal(0, resume.CompletedSegments);
        Assert.Equal(1, resume.FailedSegments);
    }

    [Fact]
    public async Task ScanningProgressAndRenderingRunOutsideTheCallingUiContext()
    {
        using var book = new TranslationTestBook(string.Concat(Enumerable.Range(0, 200).Select(index => $"<p>Paragraph {index}.</p>")));
        using var service = book.Service(new GoogleHandler(text => text.Replace("Paragraph", "段落")));
        var context = new TestUiContext();
        var callbacks = new List<(string Stage, bool OnUi)>();
        var previous = SynchronizationContext.Current;
        Task<BookTranslationResult> task;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            task = service.TranslateAsync(book.Source, book.Output, Settings,
                new TestHelpers.InlineProgress<BookTranslationProgress>(value =>
                    callbacks.Add((value.Stage, ReferenceEquals(SynchronizationContext.Current, context)))));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await task;
        Assert.Contains(callbacks, value => value.Stage == "正在扫描 EPUB");
        Assert.Contains(callbacks, value => value.Stage == "正在生成 EPUB");
        Assert.All(callbacks, value => Assert.False(value.OnUi));
    }

    private static AiConnectionSettings AiConfiguration() => new()
    {
        Provider = "custom", BaseUrl = "https://example.test/v1", Model = "old-model", ApiKey = "test-key"
    };

    private static HttpResponseMessage ChatReply(string text, string ending = "stop")
    {
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });
        var suffix = ending switch
        {
            "eof" => string.Empty,
            "error" => "data: {\"error\":{\"message\":\"interrupted\"}}\n\ndata: [DONE]\n\n",
            _ => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"" + ending + "\"}]}\n\ndata: [DONE]\n\n"
        };
        return new HttpResponseMessage { Content = new StringContent("data: " + payload + "\n\n" + suffix, Encoding.UTF8, "text/event-stream") };
    }

    private sealed class TestUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        });
    }
}
