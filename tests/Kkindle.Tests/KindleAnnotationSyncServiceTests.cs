using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class KindleAnnotationSyncServiceTests
{
    [Fact]
    public async Task PullsDeviceHighlightAndLaterPushesNewerComputerNote()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var azw3 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3");
            var epub = Path.Combine(root, "native.epub");
            await Azw3ReaderService.PrepareEpubAsync(azw3, epub);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            await library.ImportAsync([epub]);
            var book = Assert.Single(await library.SearchAsync());
            var file = Assert.Single(book.Files);
            var readerData = new ReaderDataService(paths);
            await readerData.InitializeAsync();
            var raw = await KindleBookTextService.ReadAsync(azw3);
            Assert.True(raw.TryFindRange("你好，世界。😀", null, null, out var start, out var end));
            var bookId = raw.CreateBookId()!;
            var record = new KindleAnnotationRecord(
                "device-highlight",
                new KindleAnnotationBookIdentity(bookId, raw.Asin, raw.PalmName),
                1,
                KindleClippingType.Highlight,
                start,
                end,
                1_793_450_000_000,
                1_793_450_000_000,
                string.Empty,
                "{}",
                book.Title);
            var fake = new FakeKindleService(new KindleAnnotationSnapshot(
                @"system\ksdk\.annotations\amzn1.account.test\ksdk_annotation_v1.db",
                new string('a', 64),
                [record]), azw3);
            var sync = new KindleAnnotationSyncService(
                paths,
                library,
                readerData,
                new EpubBookContentService(readerData));
            var device = new KindleDevice
            {
                RootPath = @"\\?\usb#test",
                VolumeSerial = "KINDLE-TEST",
                Name = "Kindle Test",
                Transport = KindleTransport.Wpd
            };
            var deviceBook = new KindleBook
            {
                RelativePath = "documents/native.azw3",
                Title = book.Title,
                Authors = book.Authors,
                Format = "azw3"
            };

            var pullPreview = await sync.AnalyzeAsync(device, [deviceBook], fake);

            Assert.True(pullPreview.HasChanges);
            Assert.Equal(1, pullPreview.Books.Sum(item => item.ChangesToComputer));
            var pull = await sync.SynchronizeAsync(device, [deviceBook], fake);
            Assert.Equal(1, pull.ChangesToComputer);
            var imported = Assert.Single(await readerData.GetAnnotationsAsync(file.Id));
            Assert.Equal("你好，世界。😀", imported.SelectedText);
            var deviceEmpty = new FakeKindleService(
                fake.Snapshot with { Records = [] },
                azw3);
            var localOnlyPreview = await sync.AnalyzeAsync(device, [deviceBook], deviceEmpty);
            Assert.True(localOnlyPreview.HasChanges);
            Assert.Equal(1, localOnlyPreview.Books.Sum(item => item.ChangesToDevice));

            imported.Note = "电脑更新";
            imported.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            await readerData.SaveAnnotationAsync(imported);
            var pushPreview = await sync.AnalyzeAsync(device, [deviceBook], fake);
            Assert.True(pushPreview.HasChanges);
            Assert.Equal(1, pushPreview.Books.Sum(item => item.ChangesToDevice));
            var push = await sync.SynchronizeAsync(device, [deviceBook], fake);
            Assert.Equal(1, push.ChangesToDevice);
            var mutation = Assert.Single(fake.Mutations);
            Assert.Equal(3, mutation.Dataset);
            Assert.Contains("电脑更新", mutation.SerializedPayload);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private sealed class FakeKindleService(
        KindleAnnotationSnapshot snapshot,
        string sourceBook) : IKindleDeviceService
    {
        public KindleAnnotationSnapshot Snapshot { get; private set; } = snapshot;
        public List<KindleAnnotationMutation> Mutations { get; } = [];
        public int ExportCount { get; private set; }
        public int WriteCount { get; private set; }
        public bool SnapshotUnavailable { get; init; }
        public bool ApplyMutationsToSnapshot { get; init; }
        public Exception? ExportFailure { get; init; }
        public Exception? WriteFailure { get; set; }

        public Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
            KindleDevice device,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<KindleAnnotationSnapshot?>(SnapshotUnavailable ? null : Snapshot);

        public Task<string> ExportBookAsync(
            KindleDevice device,
            KindleBook book,
            string destinationDirectory,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExportCount++;
            if (ExportFailure is not null) throw ExportFailure;
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, "native.azw3");
            File.Copy(sourceBook, destination, overwrite: true);
            return Task.FromResult(destination);
        }

        public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
            KindleDevice device,
            KindleAnnotationSnapshot currentSnapshot,
            IReadOnlyList<KindleAnnotationMutation> mutations,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (WriteFailure is not null) throw WriteFailure;
            Mutations.AddRange(mutations);
            if (ApplyMutationsToSnapshot)
                Snapshot = Snapshot with
                {
                    Records = Snapshot.Records.Where(record => !mutations.Any(mutation =>
                            mutation.AnnotationId == record.AnnotationId && mutation.BookId == record.Book.BookId))
                        .Concat(mutations.Select(mutation => new KindleAnnotationRecord(mutation.AnnotationId,
                            new KindleAnnotationBookIdentity(mutation.BookId,
                                Snapshot.Records.FirstOrDefault()?.Book.Asin, Snapshot.Records.FirstOrDefault()?.Book.Guid),
                            mutation.Dataset, mutation.Dataset == 1 ? KindleClippingType.Highlight : KindleClippingType.Note,
                            mutation.Start, mutation.End, mutation.CreatedTime, mutation.ModifiedTime,
                            mutation.NoteText, mutation.SerializedPayload)))
                        .ToArray()
                };
            return Task.FromResult(new KindleAnnotationWriteResult(
                mutations.Count,
                currentSnapshot.Sha256,
                currentSnapshot.Sha256,
                null,
                null));
        }

        public Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
            KindleDevice device,
            IProgress<KindleScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SendBookAsync(
            KindleDevice device,
            BookFile bookFile,
            string sourcePath,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? coverOverridePath = null) =>
            throw new NotSupportedException();
        public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
            KindleDevice device,
            KindleResourceKind kind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SendResourceAsync(
            KindleDevice device,
            KindleResourceKind kind,
            string sourcePath,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task ExportResourceAsync(
            KindleDevice device,
            KindleDeviceResource resource,
            string destinationPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RemoveResourceAsync(
            KindleDevice device,
            KindleDeviceResource resource,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
            KindleDevice device,
            CancellationToken cancellationToken = default,
            int maxItems = int.MaxValue) =>
            throw new NotSupportedException();
        public Task DeleteClippingAsync(
            KindleDevice device,
            string clippingId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteClippingsAsync(
            KindleDevice device,
            IReadOnlyCollection<string> clippingIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
