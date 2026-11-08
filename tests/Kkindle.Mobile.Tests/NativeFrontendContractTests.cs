using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.iOSHead;
using Kkindle.Mobile.Views;
using Xunit;
using static Kkindle.iOSHead.IOSNativeJson;

namespace Kkindle.Mobile.Tests;

public sealed class NativeFrontendContractTests
{
    [Fact]
    public void ReaderEventUsesSwiftFieldNamesAndNumericEnums()
    {
        var state = new NativeReaderState(true, true, false, "测试书", "第一章", "3 / 9", 32.5,
            false, null, TtsPlaybackState.Paused, "已暂停", "所选文字");
        using var document = JsonDocument.Parse(Serialize(Data(("type", "reader"), ("reader", state))));
        var reader = document.RootElement.GetProperty("reader");
        Assert.Equal("reader", document.RootElement.GetProperty("type").GetString());
        Assert.True(reader.GetProperty("ready").GetBoolean());
        Assert.Equal(3, reader.GetProperty("speechState").GetInt32());
        Assert.Equal(32.5, reader.GetProperty("progress").GetDouble());
        Assert.Equal("所选文字", reader.GetProperty("selectedText").GetString());
        Assert.Equal(JsonValueKind.Null, reader.GetProperty("error").ValueKind);

        using var preferences = JsonDocument.Parse(Serialize(new MobilePreferences { ReaderTheme = ReaderTheme.Ink }));
        Assert.Equal(5, preferences.RootElement.GetProperty("readerTheme").GetInt32());
        Assert.Equal(1.35, preferences.RootElement.GetProperty("readerFontScale").GetDouble());
        Assert.Equal(0, preferences.RootElement.GetProperty("readerParagraphSpacing").GetDouble());
    }

    [Fact]
    public void MaterialLocationSurvivesTheSwiftJsonRoundTrip()
    {
        var location = new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "OEBPS/chapter.xhtml", "note-3",
            2, 450, 32.5, 1, DateTimeOffset.UtcNow)
        {
            ContentPosition = new ReaderContentPosition { TextOffset = 719, ImageIndex = 2, PageIndex = 4, SliceFraction = 0.35 }
        };
        var material = new NativeMaterial(Guid.NewGuid().ToString(), location.BookId, "测试书", "bookmark",
            "正文", "", "第一章", location.UpdatedAt, location);
        using var document = JsonDocument.Parse(Serialize(new[] { material }));
        var encoded = document.RootElement[0].GetProperty("location");
        Assert.Equal(location.BookId.ToString(), encoded.GetProperty("bookId").GetString());
        var restored = encoded.Deserialize(IOSNativeJsonContext.Default.ReaderProgressRow);
        Assert.Equal(location, restored);
        Assert.Equal(719, restored!.ContentPosition!.TextOffset);
        // The list shows the short location; only exports use the full one.
        Assert.Equal("第一章", material.ExportLocation);
        var highlight = material with { LocationText = "2026/11/08", ExportLocation = location.ChapterPath };
        using var highlightDocument = JsonDocument.Parse(Serialize(new[] { highlight }));
        Assert.Equal("2026/11/08", highlightDocument.RootElement[0].GetProperty("locationText").GetString());
        Assert.False(highlightDocument.RootElement[0].TryGetProperty("exportLocation", out _));
    }

    [Fact]
    public void SwiftSettingsFormsKeepMissingServiceDefaults()
    {
        const string sync = """{"provider":1,"enabled":false,"webDavEndpoint":"https://example.invalid/kkindle","webDavUsername":"test","webDavPassword":"","prefix":"test"}""";
        using var syncDocument = JsonDocument.Parse(sync);
        var settings = Read(syncDocument.RootElement, new S3SyncSettings(), IOSNativeJsonContext.Default.S3SyncSettings);
        Assert.Equal(SyncProvider.WebDav, settings.Provider);
        Assert.False(settings.Enabled);
        Assert.Equal(new S3SyncSettings().TimeoutSeconds, settings.TimeoutSeconds);
        Assert.Equal(new S3SyncSettings().ConcurrentRequests, settings.ConcurrentRequests);

        const string preferences = """{"readerTheme":5,"readerFontScale":1.45,"readerParagraphIndent":true,"readerVerticalWriting":true}""";
        using var preferencesDocument = JsonDocument.Parse(preferences);
        var choice = Read(preferencesDocument.RootElement, new MobilePreferences(), IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(ReaderTheme.Ink, choice.ReaderTheme);
        Assert.True(choice.ReaderParagraphIndent);
        Assert.True(choice.ReaderVerticalWriting);
        Assert.Equal(1.45, choice.ReaderFontScale);
        Assert.Equal(24, choice.ReaderBodyPadding);
        Assert.Equal(0, choice.ReaderParagraphSpacing);

        using var spacingDocument = JsonDocument.Parse("""{"readerParagraphSpacing":0.5}""");
        var spaced = Read(spacingDocument.RootElement, choice, IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(0.5, spaced.ReaderParagraphSpacing);
        Assert.Equal(choice.ReaderLineHeight, spaced.ReaderLineHeight);
    }

    [Fact]
    public void FeatureRepliesSerializeWithOnlyGeneratedMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var bookId = Guid.NewGuid();
        var dashboard = new ReadingDashboard(1, 0, 120, 25, 0, 0,
            [new ReadingDashboardBook(bookId, Guid.NewGuid(), 25, 120, now) { Title = "测试书", IsInLibrary = true }],
            [new ReadingDashboardDay(new DateOnly(2026, 11, 7), 120)]);
        object[] replies =
        [
            Data(("books", new object[] { Data(("id", bookId), ("progress", 25.0), ("collectionIds", new Guid[] { Guid.NewGuid() }), ("updatedAt", now)) }),
                ("collections", new[] { Data(("id", Guid.NewGuid()), ("count", 1), ("automatic", false)) }), ("preferences", new MobilePreferences())),
            Data(("dashboard", dashboard), ("medals", ReadingMedalCatalog.Build(dashboard).ToArray()), ("streak", 1)),
            new AccountStatus("http://127.0.0.1:5088", new AccountUser(bookId, "test_reader"), now.AddDays(30)),
            Data(("period", 2), ("today", "2026-11-08"), ("buckets", new ReadingPeriodBucket[]
                { new(new(2026, 11, 1), new(2026, 11, 30), 120) })),
            new BookCollection { Id = Guid.NewGuid(), Name = "测试收藏夹" },
            new NativeReaderTocItem[] { new(0, "第一章", 0, true) },
            new NativeReaderSearchItem[] { new(0, "第一章", "匹配的原文") },
            new AiConnectionSettings(), new KindleEmailSettings(),
            Data(("settings", new TtsSettings()), ("voices", new TtsVoiceInfo[] { new("voice", "中文", "zh-CN") })),
            new DictionaryDefinition[] { new("dictionary", "测试词典", "dict.json", 1, now) },
            new DictionaryEntry[] { new("word", "释义", "测试词典") },
            Data(("chapterCount", 1), ("detectedEncoding", "Utf8"),
                ("chapters", new TxtChapterPreviewItem[] { new(1, 1, "第一章", false, 20) }), ("hasNextPage", false))
        ];
        foreach (var reply in replies)
        {
            // The context has no reflection fallback. An unregistered runtime
            // type would throw here, as it would when a native control loads it.
            using var document = JsonDocument.Parse(Serialize(Data(("ok", true), ("result", reply))));
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, document.RootElement.GetProperty("result").ValueKind);
        }
    }

    [Fact]
    public void MembershipDevicesAndOrdersUseTheGeneratedNativeWireContract()
    {
        var user = Guid.NewGuid(); var device = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var member = new AccountMembership(user, AccountRegion.International, "lifetime", true, null, 5, 1, 1,
            128, 1024, 64, 2048, 1024, 256, 4096, null);
        var status = new DeviceFeatureStatus(member, [new(device, "Fictional native device", "iOS", "active", now, now)], device);
        using var document = JsonDocument.Parse(Serialize(status));
        Assert.Equal(1, document.RootElement.GetProperty("membership").GetProperty("region").GetInt32());
        Assert.Equal(5, document.RootElement.GetProperty("membership").GetProperty("deviceLimit").GetInt32());
        Assert.True(document.RootElement.GetProperty("isCurrentDeviceActive").GetBoolean());
        Assert.True(document.RootElement.GetProperty("canUseFeatures").GetBoolean());
        var restored = document.RootElement.Deserialize(IOSNativeJsonContext.Default.DeviceFeatureStatus)!;
        Assert.Equal(device, restored.CurrentDeviceId); Assert.Equal(user, restored.Membership.AccountId);
        using var products = JsonDocument.Parse(Serialize(new MembershipProduct[]
            { new("fictional", "Fictional product", "lifetime", 1, "USD", 0, 0, "Local fixture only") }));
        Assert.Equal(1, products.RootElement[0].GetProperty("priceMinor").GetInt32());
        using var order = JsonDocument.Parse(Serialize(new PurchaseOrder(Guid.NewGuid(), user, "fictional", "Fictional product", "lifetime",
            1, "USD", 0, 0, "pending", now, now.AddDays(2), null)));
        Assert.Equal("pending", order.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, order.RootElement.GetProperty("fulfilledAt").ValueKind);
    }
}
