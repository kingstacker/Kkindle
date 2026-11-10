using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class KindleAnnotationSyncServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAnnotationSnapshotDoesNotReportAgreement(bool synchronize)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var fake = new FakeKindleService(Fake(s, []).Snapshot, s.Azw3) { SnapshotUnavailable = true };
            if (synchronize)
                await Assert.ThrowsAsync<NotSupportedException>(() => s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake));
            else
                await Assert.ThrowsAsync<NotSupportedException>(() => s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake));
            Assert.Equal(0, fake.WriteCount);
            Assert.Empty(await s.Reader.GetAnnotationsAsync(s.File.Id));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PreviewDoesNotPromiseImportWhenLocalEditionIsMissing()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var library = new SqliteBookLibraryService(new AppPaths(Path.Combine(root, "app")), new BookMetadataService());
            File.Delete(library.GetAbsoluteFilePath(s.File));
            var fake = Fake(s, [DeviceHighlight(s)]);
            var preview = await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.False(preview.HasChanges);
            Assert.Equal(1, preview.UnsupportedCount);
            Assert.Equal(0, preview.BookCount);
            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(0, result.ChangesToComputer);
            Assert.Equal(1, result.UnsupportedCount);
            Assert.Empty(fake.Mutations);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrUnreadableSharedBookIsExplicitlySkipped(bool unreadable)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            await s.Reader.SaveAnnotationAsync(Local(s, "保留本地笔记", DateTimeOffset.UtcNow));
            var fake = new FakeKindleService(Fake(s, [DeviceHighlight(s)]).Snapshot, s.Azw3)
            {
                ExportFailure = unreadable ? new IOException("虚构的设备读取失败") : null
            };
            if (!unreadable) s.DeviceBook.Format = "kfx";
            var preview = await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.False(preview.HasChanges);
            Assert.Equal(1, preview.SkippedBookCount);
            Assert.Equal(0, preview.ComparedBookCount);
            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, result.SkippedBookCount);
            Assert.Equal("保留本地笔记", Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id)).Note);
            Assert.Empty(fake.Mutations);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NoCommonBooksIsDistinguishedFromAnEmptySharedBook()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var fake = Fake(s, []);
            var empty = await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, empty.ComparedBookCount);
            var unrelated = new KindleBook { RelativePath = "unrelated.azw3", Title = "无关测试书籍", Authors = "不同作者", Format = "azw3" };
            var noCommon = await s.Sync.AnalyzeAsync(s.Device, [unrelated], fake);
            Assert.Equal(0, noCommon.ComparedBookCount);
            Assert.False(noCommon.HasChanges);
            Assert.Equal(0, noCommon.SkippedBookCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PartialWriteFailureReportsCommittedImportsAndRetryDoesNotDuplicateThem()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var local = await PreciselyLocatedLocal(root, s);
            await s.Reader.SaveAnnotationAsync(local);
            var highlight = OtherDeviceHighlight(s);
            var fake = new FakeKindleService(Fake(s, [highlight]).Snapshot, s.Azw3)
            {
                ApplyMutationsToSnapshot = true,
                WriteFailure = new IOException("虚构的设备写入失败")
            };
            var error = await Assert.ThrowsAsync<KindleAnnotationSyncException>(() =>
                s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake));
            Assert.Equal(1, error.ChangesToComputer);
            Assert.IsType<IOException>(error.InnerException);
            var beforeRetry = await s.Reader.GetAnnotationsAsync(s.File.Id);
            Assert.Equal(2, beforeRetry.Count);
            Assert.Empty(fake.Mutations);

            fake.WriteFailure = null;
            var retry = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(0, retry.ChangesToComputer);
            Assert.Equal(1, retry.ChangesToDevice);
            Assert.Equal(beforeRetry.Select(item => item.Id).Order(),
                (await s.Reader.GetAnnotationsAsync(s.File.Id)).Select(item => item.Id).Order());
            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake)).HasChanges);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BidirectionalUnionReachesAgreementWithoutRepeatedWrites()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            await s.Reader.SaveAnnotationAsync(await PreciselyLocatedLocal(root, s));
            var fake = new FakeKindleService(Fake(s, [OtherDeviceHighlight(s)]).Snapshot, s.Azw3)
            {
                ApplyMutationsToSnapshot = true
            };
            var first = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, first.ChangesToComputer);
            Assert.Equal(1, first.ChangesToDevice);
            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake)).HasChanges);
            var second = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(0, second.ChangesToComputer);
            Assert.Equal(0, second.ChangesToDevice);
            Assert.Equal(1, fake.WriteCount);
            Assert.Equal(2, (await s.Reader.GetAnnotationsAsync(s.File.Id)).Count);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NativeNoteInsideOverlappingHighlightsIsNotAssignedArbitrarily()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            await s.Reader.SaveAnnotationAsync(await PreciselyLocatedLocal(root, s));
            var full = DeviceHighlight(s);
            var note = DeviceNote(full, "native-note", "保留设备笔记");
            var fake = Fake(s, [full, OtherDeviceHighlight(s), note]);
            var preview = await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.False(preview.HasChanges);
            Assert.True(preview.UnsupportedCount > 0);
            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(0, result.ChangesToDevice);
            Assert.Equal(0, result.ChangesToComputer);
            Assert.Equal("电脑笔记", Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id)).Note);
            Assert.Empty(fake.Mutations);
            Assert.Equal("保留设备笔记", fake.Snapshot.Records.Single(record => record.AnnotationId == "native-note").NoteText);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OwnedNoteOnNativeHighlightKeepsItsParentWhenSelectionsOverlap()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var full = DeviceHighlight(s);
            var first = Fake(s, [full]);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], first);
            var local = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            local.Note = "电脑为原生划线添加的笔记";
            local.UpdatedAt = DateTimeOffset.UtcNow;
            await s.Reader.SaveAnnotationAsync(local);
            var fake = new FakeKindleService(Fake(s, [full, OtherDeviceHighlight(s)]).Snapshot, s.Azw3)
            {
                ApplyMutationsToSnapshot = true
            };
            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, result.ChangesToDevice);
            Assert.Equal(1, result.ChangesToComputer);
            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake)).HasChanges);
            var untouched = (await s.Reader.GetAnnotationsAsync(s.File.Id)).Single(item => item.SelectedText == "世界。😀");
            Assert.True(string.IsNullOrWhiteSpace(untouched.Note));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task<ReaderAnnotation> PreciselyLocatedLocal(string root, Scenario s)
    {
        var local = Local(s, "电脑笔记", DateTimeOffset.UtcNow);
        var body = await NativeBody(root, s, null);
        local.ChapterPath = body.Path;
        local.StartOffset = body.Text.IndexOf(local.SelectedText, StringComparison.Ordinal);
        local.EndOffset = local.StartOffset + local.SelectedText.Length;
        local.Prefix = body.Text[Math.Max(0, local.StartOffset - 24)..local.StartOffset];
        local.Suffix = body.Text[local.EndOffset..Math.Min(body.Text.Length, local.EndOffset + 24)];
        return local;
    }

    private static KindleAnnotationRecord OtherDeviceHighlight(Scenario s)
    {
        Assert.True(s.Raw.TryFindRange("世界。😀", null, null, out var start, out var end));
        return DeviceHighlight(s) with { AnnotationId = "device-extra", Start = start, End = end - 1 };
    }
}
