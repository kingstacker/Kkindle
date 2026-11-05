using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

/// <summary>
/// Kindle stores highlights as closed byte ranges: start is the first byte of the
/// first character and end is the last byte of the last character. Captured from
/// highlights made on a Kindle Scribe, whose end falls inside a multi-byte
/// character and is directly followed by "&lt;/p&gt;" at a paragraph end.
/// </summary>
public sealed partial class KindleAnnotationSyncServiceTests
{
    [Fact]
    public void ClosedEndOnLastByteOfMultiByteCharacterKeepsExactlyThatCharacter()
    {
        var raw = Raw("<p>甲乙丙丁</p>");
        Assert.Equal("乙丙", raw.ExtractDeviceRange(6, 11));
        Assert.Equal(12, raw.DeviceEndToExclusive(11));
    }

    [Fact]
    public void ClosedEndOnSingleByteCharacterKeepsThatCharacter()
    {
        var raw = Raw("<p>alpha beta gamma</p>");
        Assert.Equal("alpha", raw.ExtractDeviceRange(3, 7));
    }

    [Fact]
    public void EndOnMarkupExcludesTheMarkup()
    {
        var raw = Raw("<p>甲乙</p>");
        Assert.Equal("甲乙", raw.ExtractDeviceRange(3, 9));
        Assert.Equal(9, raw.DeviceEndToExclusive(9));
    }

    [Fact]
    public void OwnedExclusiveEndOnLeadByteIsNotExtendedOverTheNextCharacter()
    {
        var raw = Raw("<p>甲乙丙</p>");
        Assert.Equal("甲乙", raw.ExtractDeviceRange(3, 9, writtenByKkindle: true));
        Assert.Equal("甲乙", raw.ExtractDeviceRange(3, 8, writtenByKkindle: true));
    }

    [Fact]
    public void LastCharacterStartHandlesMultiByteAndMarkupEnds()
    {
        var raw = Raw("<p>甲😀</p>");
        Assert.Equal(6, raw.LastCharacterStart(3, 9));
        Assert.Equal(6, raw.LastCharacterStart(3, 10));
        Assert.Equal(3, raw.LastCharacterStart(3, 5));
    }

    [Fact]
    public void WindowsCodePageBytesAreNeverTreatedAsContinuationBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(1252);
        var raw = new KindleRawBookText(encoding.GetBytes("<p>a”b c</p>"), encoding,
            "Book", "test-asin", 123, "Book", "Author");
        Assert.Equal("a", raw.ExtractDeviceRange(3, 3));
        Assert.Equal(4, raw.DeviceEndToExclusive(3));
    }

    [Fact]
    public void QuotesNeverResolveIntoMarkupOrTheDocumentHead()
    {
        var markup = "<html><head><title>独特标题</title></head><body>"
            + "<p title=\"独特标题\">x</p><p>独特<b>标题</b></p></body></html>";
        var raw = Raw(markup);
        Assert.True(raw.TryFindRange("独特标题", null, null, out var start, out var end));
        Assert.Equal(Encoding.UTF8.GetByteCount(markup[..markup.LastIndexOf("独特", StringComparison.Ordinal)]), start);
        Assert.Equal("独特标题", raw.ExtractRange(start, end));
    }

    [Fact]
    public void RepeatedQuoteUsesContextAndToleratesDistantContextDifferences()
    {
        const string markup = "<p>前文一 重复词 后文</p><p>前文二 重复词 后文</p>";
        var raw = Raw(markup);
        var second = Encoding.UTF8.GetByteCount(markup[..markup.LastIndexOf("重复词", StringComparison.Ordinal)]);
        Assert.True(raw.TryFindRange("重复词", "噪噪噪噪噪词后文前文二", null, out var start, out _));
        Assert.Equal(second, start);
        Assert.False(raw.TryFindRange("重复词", null, "后文", out _, out _));
        Assert.False(raw.TryFindRange("重复词", null, null, out _, out _));
    }

    [Fact]
    public async Task PushedHighlightUsesClosedRangeAndIsStableOnTheNextSync()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var local = await SavePositioned(root, s, "电脑笔记");
            var fake = Fake(s, []);

            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            var highlight = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 1);
            var note = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 3);
            Assert.Equal((1537, 1558), (highlight.Start, highlight.End));
            // The note sits on the last character, a 4-byte emoji.
            Assert.Equal((1555, 1558), (note.Start, note.End));
            Assert.Equal(local.SelectedText, s.Raw.ExtractDeviceRange(highlight.Start, highlight.End));

            var next = Fake(s, ToRecords(s, fake.Mutations));
            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], next)).HasChanges);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], next);
            Assert.Empty(next.Mutations);
            Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RecordsWrittenWithExclusiveEndAreRepairedOnceWithoutDuplication()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var local = await SavePositioned(root, s, "已有笔记");
            var stale = DeviceHighlight(s) with
            {
                AnnotationId = "kkindle.highlight." + local.Id.ToString("N"),
                End = 1559
            };
            var staleNote = DeviceNote(stale, "kkindle.note." + local.Id.ToString("N"), local.Note) with
            {
                Start = 1556
            };
            var fake = Fake(s, [stale, staleNote]);

            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            var highlight = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 1);
            var note = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 3);
            Assert.Equal((1537, 1558), (highlight.Start, highlight.End));
            Assert.Equal((1555, 1558), (note.Start, note.End));
            Assert.Equal(1, result.ChangesToDevice);
            Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));

            var next = Fake(s, [
                stale with { End = highlight.End },
                staleNote with { Start = note.Start, End = note.End }]);
            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], next)).HasChanges);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task HighlightMadeOnKindleImportsTheExactTextAndNativeRecordIsUntouched()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var fake = Fake(s, [DeviceHighlight(s)]);

            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            var imported = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            Assert.Equal("你好，世界。😀", imported.SelectedText);
            var body = await NativeBody(root, s, imported.ChapterPath);
            Assert.Equal(imported.SelectedText, body.Text[imported.StartOffset..imported.EndOffset]);
            Assert.Empty(fake.Mutations);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task AnnotationImportedWithTruncatedLastCharacterIsRepairedNotDuplicated()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            Assert.True(s.Raw.TryFindRange("第0段", null, null, out var start, out _));
            // "第" is 3 bytes, so the closed end sits on the ASCII "0".
            var highlight = DeviceHighlight(s) with { Start = start, End = start + 3 };
            var body = await NativeBody(root, s, null);
            var offset = body.Text.IndexOf("第0段", StringComparison.Ordinal);
            var truncated = Local(s, string.Empty, highlight.CreatedAt);
            truncated.ChapterPath = body.Path;
            truncated.StartOffset = offset;
            truncated.EndOffset = offset + 1;
            truncated.SelectedText = "第";
            truncated.Prefix = body.Text[Math.Max(0, offset - 16)..offset];
            truncated.Suffix = body.Text[(offset + 1)..(offset + 17)];
            await s.Reader.SaveAnnotationAsync(truncated);
            var fake = Fake(s, [highlight]);

            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            var repaired = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            Assert.Equal(truncated.Id, repaired.Id);
            Assert.Equal("第0", repaired.SelectedText);
            Assert.Equal((offset, offset + 2), (repaired.StartOffset, repaired.EndOffset));
            Assert.Empty(fake.Mutations);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task<ReaderAnnotation> SavePositioned(string root, Scenario s, string note)
    {
        var local = Local(s, note, DateTimeOffset.UtcNow);
        var body = await NativeBody(root, s, null);
        local.ChapterPath = body.Path;
        local.StartOffset = body.Text.IndexOf(local.SelectedText, StringComparison.Ordinal);
        local.EndOffset = local.StartOffset + local.SelectedText.Length;
        local.Prefix = body.Text[Math.Max(0, local.StartOffset - 16)..local.StartOffset];
        local.Suffix = body.Text[local.EndOffset..Math.Min(body.Text.Length, local.EndOffset + 16)];
        await s.Reader.SaveAnnotationAsync(local);
        return local;
    }

    private static IReadOnlyList<KindleAnnotationRecord> ToRecords(
        Scenario s, IEnumerable<KindleAnnotationMutation> mutations) => mutations.Select(mutation =>
            new KindleAnnotationRecord(
                mutation.AnnotationId,
                new(mutation.BookId, s.Raw.Asin, s.Raw.CreateGuid()),
                mutation.Dataset,
                mutation.Dataset == 1 ? KindleClippingType.Highlight : KindleClippingType.Note,
                mutation.Start,
                mutation.End,
                mutation.CreatedTime,
                mutation.ModifiedTime,
                mutation.NoteText,
                mutation.SerializedPayload,
                s.Book.Title)).ToArray();
}

public sealed partial class KindleAnnotationSyncServiceTests
{
    [Fact]
    public async Task KkindleRecordImportedUnderANewIdIsStillMovedToTheClosedRange()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            // Written by an earlier version for an annotation that no longer exists
            // locally: exclusive end, so on the Kindle it covers one extra byte.
            var stale = DeviceHighlight(s) with
            {
                AnnotationId = "kkindle.highlight." + Guid.NewGuid().ToString("N"),
                End = 1559
            };
            var first = Fake(s, [stale]);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], first);
            var imported = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            Assert.Equal("你好，世界。😀", imported.SelectedText);

            var second = Fake(s, [stale]);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], second);
            var moved = Assert.Single(second.Mutations);
            Assert.Equal(stale.AnnotationId, moved.AnnotationId);
            Assert.Equal((1537, 1558), (moved.Start, moved.End));
            Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));

            var third = Fake(s, [stale with { End = 1558 }]);
            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], third)).HasChanges);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
