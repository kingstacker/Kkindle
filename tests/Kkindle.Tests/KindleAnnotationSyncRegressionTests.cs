using System.Reflection;
using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Layout;

namespace Kkindle.Tests;

public sealed partial class KindleAnnotationSyncServiceTests
{
    [Fact]
    public async Task KindlePositionsUseIndependentReconstructedByteOracle()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3");
        var expected = await File.ReadAllBytesAsync(Path.ChangeExtension(fixture, null) + "-assembled.dat");
        var raw = await KindleBookTextService.ReadAsync(fixture);
        Assert.Equal(expected, raw.Text);
        Assert.Equal("你好，世界。😀", raw.ExtractRange(1537, 1559));
        Assert.True(raw.TryFindRange("你好，世界。😀", null, null, out var start, out var end));
        Assert.Equal(1537, start);
        Assert.Equal(1559, end);
    }

    [Fact]
    public async Task ImportedAndRepairedAnnotationsUseNativeReaderCoordinates()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var scenario = await Setup(root);
            var highlight = DeviceHighlight(scenario);
            var note = DeviceNote(highlight, "device-note", "保留现有笔记");
            var fake = Fake(scenario, [highlight, note]);
            await scenario.Sync.SynchronizeAsync(scenario.Device, [scenario.DeviceBook], fake);
            var imported = Assert.Single(await scenario.Reader.GetAnnotationsAsync(scenario.File.Id));
            var body = await NativeBody(root, scenario, imported.ChapterPath);
            Assert.Equal(imported.SelectedText, body.Text[imported.StartOffset..imported.EndOffset]);
            var originalId = imported.Id;
            var originalTime = imported.UpdatedAt;
            imported.StartOffset += 17;
            imported.EndOffset += 17;
            await scenario.Reader.SaveAnnotationAsync(imported);
            var repaired = await scenario.Sync.SynchronizeAsync(scenario.Device, [scenario.DeviceBook], fake);
            var saved = Assert.Single(await scenario.Reader.GetAnnotationsAsync(scenario.File.Id));
            Assert.Equal(1, repaired.ChangesToComputer);
            Assert.Equal(0, repaired.ChangesToDevice);
            Assert.Equal(originalId, saved.Id);
            Assert.Equal("保留现有笔记", saved.Note);
            Assert.Equal(originalTime, saved.UpdatedAt);
            Assert.Equal(saved.SelectedText, body.Text[saved.StartOffset..saved.EndOffset]);
            Assert.False((await scenario.Sync.AnalyzeAsync(scenario.Device, [scenario.DeviceBook], fake)).HasChanges);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OwnedDeviceHighlightsAndNotesAreRepositionedWithoutDuplication()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var scenario = await Setup(root);
            var native = Local(scenario, "保留现有笔记", DateTimeOffset.UtcNow.AddHours(-2));
            var body = await NativeBody(root, scenario, null);
            native.ChapterPath = body.Path;
            native.StartOffset = body.Text.IndexOf(native.SelectedText, StringComparison.Ordinal);
            native.EndOffset = native.StartOffset + native.SelectedText.Length;
            native.Prefix = body.Text[Math.Max(0, native.StartOffset - 16)..native.StartOffset];
            native.Suffix = body.Text[native.EndOffset..Math.Min(body.Text.Length, native.EndOffset + 16)];
            await scenario.Reader.SaveAnnotationAsync(native);
            var legacy = scenario.Raw with { Text = scenario.Raw.UnassembledText!, UnassembledText = null };
            Assert.True(legacy.TryFindRange(native.SelectedText, null, null, out var oldStart, out var oldEnd));
            var highlight = DeviceHighlight(scenario) with
            {
                AnnotationId = "kkindle.highlight." + native.Id.ToString("N"), Start = oldStart, End = oldEnd
            };
            Assert.NotEqual(1537, oldStart);
            var note = DeviceNote(highlight, "kkindle.note." + native.Id.ToString("N"), native.Note);
            var fake = Fake(scenario, [highlight, note]);
            await scenario.Sync.SynchronizeAsync(scenario.Device, [scenario.DeviceBook], fake);
            var highMutation = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 1);
            var noteMutation = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 3);
            Assert.Equal(highlight.AnnotationId, highMutation.AnnotationId);
            Assert.Equal(1537, highMutation.Start);
            Assert.Equal(1559, highMutation.End);
            // The relocated note keeps its original payload, whose metadata may
            // escape non-ASCII text; compare the decoded note instead of raw JSON.
            using (var payload = JsonDocument.Parse(noteMutation.SerializedPayload))
            using (var metadata = JsonDocument.Parse(payload.RootElement.GetProperty("json_metadata").GetString()!))
                Assert.Equal(native.Note, metadata.RootElement.GetProperty("note_text").GetString());
            Assert.InRange(noteMutation.Start, highMutation.Start, highMutation.End);
            Assert.Equal(highMutation.End, noteMutation.End);
            Assert.Equal(native.Note, Assert.Single(await scenario.Reader.GetAnnotationsAsync(scenario.File.Id)).Note);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static KindleAnnotationRecord DeviceNote(KindleAnnotationRecord highlight, string id, string note)
        => highlight with
        {
            AnnotationId = id, Dataset = 3, Type = KindleClippingType.Note,
            Start = highlight.End - 3, NoteText = note,
            SerializedPayload = JsonSerializer.Serialize(new { type = "NOTE", json_metadata = JsonSerializer.Serialize(new { note_text = note }) })
        };

    private static async Task<(string Text, string Path)> NativeBody(string root, Scenario scenario, string? relative)
    {
        var paths = new AppPaths(Path.Combine(root, "app"));
        var library = new SqliteBookLibraryService(paths, new BookMetadataService());
        var document = await new EpubReaderPreparationService(paths).PrepareAsync(library.GetAbsoluteFilePath(scenario.File), scenario.File.Sha256);
        var chapter = relative is null ? document.Chapters.First(path => new XhtmlChapterLoader().Load(path).BodyText.Contains("你好，世界。😀"))
            : document.Chapters.Single(path => Path.GetRelativePath(document.RootPath, path).Replace('\\', '/') == relative);
        return (new XhtmlChapterLoader().Load(chapter).BodyText, Path.GetRelativePath(document.RootPath, chapter).Replace('\\', '/'));
    }
    [Fact]
    public async Task DeviceListResolvesHighlightFromExactBookWithoutWriting()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var scenario = await Setup(root);
            var record = DeviceHighlight(scenario);
            var clipping = new KindleClipping
            {
                Id = "kindle-db:" + record.AnnotationId, BookTitle = "旧缓存书名",
                Type = KindleClippingType.Highlight, Content = "划线原文待定位",
                SourceBook = record.Book, StartPosition = record.Start, EndPosition = record.End, IsReadOnly = true,
                IsTextResolved = true, TextResolutionVersion = 0
            };
            var fake = Fake(scenario, [record]);
            await KindleAnnotationClippingTextResolver.ResolveAsync(scenario.Device, [scenario.DeviceBook], [clipping], fake);
            Assert.True(clipping.IsTextResolved);
            Assert.Equal("你好，世界。😀", clipping.Content);
            Assert.Equal(scenario.Raw.Title, clipping.BookTitle);
            Assert.Empty(fake.Mutations);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task DeviceListDoesNotReadSameTitleFromDifferentBookIdentity()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var scenario = await Setup(root);
            var clipping = new KindleClipping
            {
                BookTitle = scenario.Book.Title, Type = KindleClippingType.Highlight, Content = "划线原文待定位",
                SourceBook = new("different-book", "foreign-asin", "foreign-guid"), StartPosition = 0, EndPosition = 10
            };
            var fake = Fake(scenario, []);
            await KindleAnnotationClippingTextResolver.ResolveAsync(scenario.Device, [scenario.DeviceBook], [clipping], fake);
            Assert.False(clipping.IsTextResolved);
            Assert.DoesNotContain("待定位", clipping.Content);
            Assert.Empty(fake.Mutations);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SyncReportsBookAndAnnotationProgressUntilComplete()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var scenario = await Setup(root);
            var record = DeviceHighlight(scenario);
            var updates = new AnnotationProgressCollector();
            await scenario.Sync.SynchronizeAsync(scenario.Device, [scenario.DeviceBook], Fake(scenario, [record]), progress: updates);
            Assert.Contains(updates.Values, value => value.Stage == KindleAnnotationSyncStage.ReadingDevice);
            Assert.Contains(updates.Values, value => value.Stage == KindleAnnotationSyncStage.ProcessingBooks && value.BookTitle == scenario.Book.Title);
            Assert.Contains(updates.Values, value => value.Stage == KindleAnnotationSyncStage.ImportingToComputer && value.Completed == value.Total && value.Total == 1);
            Assert.Equal(KindleAnnotationSyncStage.Completed, updates.Values[^1].Stage);
            Assert.Equal(100, updates.Values[^1].Percentage);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private sealed class AnnotationProgressCollector : IProgress<KindleAnnotationSyncProgress>
    {
        public List<KindleAnnotationSyncProgress> Values { get; } = [];
        public void Report(KindleAnnotationSyncProgress value) => Values.Add(value);
    }

    [Theory]
    [InlineData("<p>甲<b>乙</b>&amp;丙</p>", "甲乙&丙")]
    [InlineData("<p>alpha  <b>beta</b> gamma</p>", "alpha beta gamma")]
    public void MapsVisibleQuoteAcrossMarkupAndEntities(string markup, string quote)
    {
        var raw = Raw(markup);
        Assert.True(raw.TryFindRange(quote, null, null, out var start, out var end));
        Assert.Equal(quote, raw.ExtractRange(start, end));
    }

    [Fact]
    public void ExtractRangeDoesNotExtendSelectionToParagraphEnd()
    {
        var raw = Raw("<p>before selected trailing words</p>");
        Assert.True(raw.TryFindRange("selected", null, null, out var start, out var end));
        Assert.Equal("selected", raw.ExtractRange(start, end));
    }

    [Fact]
    public void AmbiguousQuoteIsNotWrittenAtFirstOccurrence()
    {
        var raw = Raw("<p>same quote in chapter one</p><p>same quote in chapter two</p>");
        Assert.False(raw.TryFindRange("same quote", "not present", "not present", out _, out _));
    }

    [Fact]
    public void LocalQuoteEndUsesWhitespaceMapping()
    {
        var chunks = new[]{new BookContentChunk(1,Guid.NewGuid(),Guid.NewGuid(),"hash",0,0,"Chapter","ch.xhtml",0,17,"alpha  beta gamma",0)};
        var locate=typeof(KindleAnnotationSyncService).GetMethod("LocateQuote",BindingFlags.Static|BindingFlags.NonPublic)!;
        var result=locate.Invoke(null,new object[]{chunks,"alpha beta"});
        Assert.NotNull(result);
        Assert.Equal("alpha  beta",result.GetType().GetProperty("SelectedText")!.GetValue(result));
    }

    [Fact]
    public async Task FirstPushUsesFullGuidFromDerivedBookIdentity()
    {
        var root=TestHelpers.CreateTempDirectory();
        try
        {
            var s=await Setup(root);
            await s.Reader.SaveAnnotationAsync(Local(s,"computer note",DateTimeOffset.UtcNow));
            var fake=Fake(s,[]);
            await s.Sync.SynchronizeAsync(s.Device,[s.DeviceBook],fake);
            var highlight=Assert.Single(fake.Mutations,m=>m.Dataset==1);
            using var payload=JsonDocument.Parse(highlight.SerializedPayload);
            Assert.Equal(s.Raw.PalmName+":"+s.Raw.UniqueId.ToString("X8"),payload.RootElement.GetProperty("book_data").GetProperty("guid").GetString());
        }
        finally{TestHelpers.TryDelete(root);}
    }

    [Fact]
    public async Task StaleSameTitleDifferentBookIdIsNotImported()
    {
        var root=TestHelpers.CreateTempDirectory();
        try
        {
            var s=await Setup(root);
            var highlight=DeviceHighlight(s) with{Book=new KindleAnnotationBookIdentity("foreign-edition-EBOK-Other:12345678-0","foreign-edition","Other:12345678")};
            var fake=Fake(s,[highlight]);
            var result=await s.Sync.SynchronizeAsync(s.Device,[s.DeviceBook],fake);
            Assert.Equal(0,result.ChangesToComputer);
            Assert.Empty(await s.Reader.GetAnnotationsAsync(s.File.Id));
        }
        finally{TestHelpers.TryDelete(root);}
    }

    [Fact]
    public async Task NewerHighlightWithoutNoteDoesNotEraseExistingNonemptyNote()
    {
        var root=TestHelpers.CreateTempDirectory();
        try
        {
            var s=await Setup(root);
            await s.Reader.SaveAnnotationAsync(Local(s,"important computer note",DateTimeOffset.UtcNow.AddHours(-2)));
            var fake=Fake(s,[DeviceHighlight(s)]);
            await s.Sync.SynchronizeAsync(s.Device,[s.DeviceBook],fake);
            var annotation=Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            Assert.Equal("important computer note",annotation.Note);
        }
        finally{TestHelpers.TryDelete(root);}
    }

    private static KindleRawBookText Raw(string markup)=>new(Encoding.UTF8.GetBytes(markup),new UTF8Encoding(false,true),"Book","test-asin",123,"Book","Author");
    private static FakeKindleService Fake(Scenario s,IReadOnlyList<KindleAnnotationRecord> records)=>new(new KindleAnnotationSnapshot("system/ksdk/.annotations/amzn1.account.test/ksdk_annotation_v1.db",new string('a',64),records),s.Azw3);
    private static ReaderAnnotation Local(Scenario s,string note,DateTimeOffset updated)=>new(){BookId=s.Book.Id,BookFileId=s.File.Id,ChapterPath="Text/chapter.xhtml",StartOffset=0,EndOffset=9,SelectedText="你好，世界。😀",Note=note,CreatedAt=updated,UpdatedAt=updated};
    private static KindleAnnotationRecord DeviceHighlight(Scenario s)
    {
        Assert.True(s.Raw.TryFindRange("你好，世界。😀",null,null,out var start,out var end));
        var time=DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        return new("device-highlight",new(s.Raw.CreateBookId()!,s.Raw.Asin,s.Raw.PalmName+":"+s.Raw.UniqueId.ToString("X8")),1,KindleClippingType.Highlight,start,end,time,time,"","{}",s.Book.Title);
    }
    private static async Task<Scenario> Setup(string root)
    {
        var paths=new AppPaths(Path.Combine(root,"app"));paths.EnsureDirectories();
        var azw3=Path.Combine(AppContext.BaseDirectory,"Fixtures","Azw3","native-compressed.azw3");
        var epub=Path.Combine(root,"native.epub");await Azw3ReaderService.PrepareEpubAsync(azw3,epub);
        var library=new SqliteBookLibraryService(paths,new BookMetadataService());await library.InitializeAsync();await library.ImportAsync([epub]);
        var book=Assert.Single(await library.SearchAsync());var file=Assert.Single(book.Files);
        var reader=new ReaderDataService(paths);await reader.InitializeAsync();
        var raw=await KindleBookTextService.ReadAsync(azw3);
        var device=new KindleDevice{RootPath=@"\\?\usb#review",VolumeSerial="REVIEW",Transport=KindleTransport.Wpd,Name="Kindle Review"};
        var deviceBook=new KindleBook{RelativePath="native.azw3",Title=book.Title,Authors=book.Authors,Format="azw3"};
        return new(book,file,reader,raw,device,deviceBook,azw3,new(paths,library,reader,new EpubBookContentService(reader)));
    }
    private sealed record Scenario(Book Book,BookFile File,ReaderDataService Reader,KindleRawBookText Raw,KindleDevice Device,KindleBook DeviceBook,string Azw3,KindleAnnotationSyncService Sync);
}
