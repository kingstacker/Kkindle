using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class KindleAnnotationSyncServiceTests
{
    [Fact]
    public async Task RepeatedChecksDoNotCopyBooksWithoutAnnotationsAgain()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            s.DeviceBook.ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
            s.DeviceBook.Size = 1234;
            var foreign = DeviceHighlight(s) with
            {
                Book = new KindleAnnotationBookIdentity("foreign-EBOK-Other:12345678-0", "foreign", "Other:12345678")
            };
            var fake = Fake(s, [foreign]);
            await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task AnalysisReusesDeviceTextButWritePlanReadsFreshText()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            s.DeviceBook.ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
            s.DeviceBook.Size = 1234;
            await s.Reader.SaveAnnotationAsync(Local(s, "computer note", DateTimeOffset.UtcNow));
            var fake = Fake(s, []);
            await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, fake.ExportCount);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NoteClearedOnComputerIsNotRestoredFromKindle()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var highlight = DeviceHighlight(s);
            var fake = Fake(s, [highlight, DeviceNote(highlight, "device-note", "kindle note")]);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            var imported = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            Assert.Equal("kindle note", imported.Note);

            imported.Note = string.Empty;
            imported.UpdatedAt = DateTimeOffset.UtcNow;
            await s.Reader.SaveAnnotationAsync(imported);
            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            Assert.Equal(0, result.ChangesToComputer);
            Assert.Equal(0, result.ChangesToDevice);
            Assert.Empty(fake.Mutations);
            Assert.True(string.IsNullOrEmpty(Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id)).Note));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NoteDifferingOnlyInSurroundingWhitespaceIsNotADifference()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var highlight = DeviceHighlight(s);
            var fake = Fake(s, [highlight, DeviceNote(highlight, "device-note", "kindle note")]);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            var imported = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            imported.Note = "kindle note\r\n";
            imported.UpdatedAt = DateTimeOffset.UtcNow;
            await s.Reader.SaveAnnotationAsync(imported);

            Assert.False((await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake)).HasChanges);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task KindleClockAheadDoesNotOverwriteComputerNote()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var highlight = DeviceHighlight(s);
            var fake = Fake(s, [highlight, DeviceNote(highlight, "device-note", "kindle note")]);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            var imported = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            imported.Note = "computer note";
            imported.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            await s.Reader.SaveAnnotationAsync(imported);

            var future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds();
            var skewed = Fake(s, [highlight, DeviceNote(highlight, "device-note", "kindle note") with { ModifiedTime = future }]);
            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], skewed);

            Assert.Equal(0, result.ChangesToComputer);
            Assert.Equal(1, result.UnsupportedCount);
            Assert.Equal("computer note", Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id)).Note);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NewNoteOnRepositionedHighlightUsesCorrectedRange()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var local = Local(s, "new computer note", DateTimeOffset.UtcNow);
            var body = await NativeBody(root, s, null);
            local.ChapterPath = body.Path;
            local.StartOffset = body.Text.IndexOf(local.SelectedText, StringComparison.Ordinal);
            local.EndOffset = local.StartOffset + local.SelectedText.Length;
            local.Prefix = body.Text[Math.Max(0, local.StartOffset - 16)..local.StartOffset];
            local.Suffix = body.Text[local.EndOffset..Math.Min(body.Text.Length, local.EndOffset + 16)];
            await s.Reader.SaveAnnotationAsync(local);
            var legacy = s.Raw with { Text = s.Raw.UnassembledText!, UnassembledText = null };
            Assert.True(legacy.TryFindRange(local.SelectedText, null, null, out var oldStart, out var oldEnd));
            var stale = DeviceHighlight(s) with
            {
                AnnotationId = "kkindle.highlight." + local.Id.ToString("N"),
                Start = oldStart,
                End = oldEnd,
                ModifiedTime = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds()
            };
            var fake = Fake(s, [stale]);

            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            var highlight = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 1);
            var note = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 3);
            Assert.Equal(1537, highlight.Start);
            Assert.Equal(1558, highlight.End);
            Assert.InRange(note.Start, highlight.Start, highlight.End);
            Assert.Equal(highlight.End, note.End);
            Assert.Equal(1, result.ChangesToDevice);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PushedHighlightKeepsComputerCreationTime()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var created = DateTimeOffset.UtcNow.AddDays(-3);
            var local = Local(s, "computer note", created);
            local.UpdatedAt = DateTimeOffset.UtcNow;
            await s.Reader.SaveAnnotationAsync(local);
            var saved = Assert.Single(await s.Reader.GetAnnotationsAsync(s.File.Id));
            var fake = Fake(s, []);

            var result = await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);

            var highlight = Assert.Single(fake.Mutations, mutation => mutation.Dataset == 1);
            Assert.Equal(saved.CreatedAt.ToUnixTimeMilliseconds(), highlight.CreatedTime);
            Assert.True(highlight.CreatedTime < highlight.ModifiedTime);
            Assert.Equal(1, result.ChangesToDevice);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
