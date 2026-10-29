using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Xunit;

namespace Kkindle.Platform.Common.Tests;

public sealed class MassStorageKindleDeviceServiceTests
{
    [Fact]
    public async Task DisconnectedDeviceDoesNotReportAnEmptyVerifiedLibrary()
    {
        var root = CreateTempDirectory();
        try
        {
            var service = new MassStorageKindleDeviceService(new AppPaths(Path.Combine(root, "app")), new CountingMetadataService(), [root]);
            var device = new KindleDevice { RootPath = Path.Combine(root, "missing"), Transport = KindleTransport.MassStorage };
            await Assert.ThrowsAsync<IOException>(() => service.ScanBooksAsync(device));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task LargeLibraryRescanReusesCoverlessMetadataAndRechecksChangedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            var documents = Path.Combine(kindle, "documents");
            Directory.CreateDirectory(documents);
            for (var i = 0; i < 1000; i++) await File.WriteAllTextAsync(Path.Combine(documents, $"book-{i}.epub"), $"payload {i}");
            var metadata = new CountingMetadataService();
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new MassStorageKindleDeviceService(paths, metadata, [root]);
            var device = new KindleDevice { RootPath = kindle, Transport = KindleTransport.MassStorage };
            var first = await service.ScanBooksAsync(device);
            Assert.Equal(1000, first.Count);
            Assert.Equal(1000, metadata.Reads);
            // A fresh service must reuse the persisted cache, including missing covers.
            service = new MassStorageKindleDeviceService(paths, metadata, [root]);
            Assert.Equal(1000, (await service.GetCachedBooksAsync(device)).Count);
            Assert.Empty(await service.GetCachedBooksAsync(new KindleDevice { RootPath = Path.Combine(root, "other") }));
            Assert.Equal(1000, (await service.ScanBooksAsync(device)).Count);
            Assert.Equal(1000, metadata.Reads);
            await File.AppendAllTextAsync(Path.Combine(documents, "book-0.epub"), "new edition");
            var third = await service.ScanBooksAsync(device);
            Assert.Equal(1001, metadata.Reads);
            Assert.NotEqual(first.Single(book => book.RelativePath.EndsWith("book-0.epub")).Sha256,
                third.Single(book => book.RelativePath.EndsWith("book-0.epub")).Sha256);
        }
        finally { TryDelete(root); }
    }

    private sealed class CountingMetadataService : IMetadataService
    {
        public int Reads;
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(new BookMetadata { Title = "Coverless", Authors = "Author" });
        }
    }

    [Fact]
    public async Task DetectSendScanExportAndRemove_RoundTripsMountedKindle()
    {
        var root = CreateTempDirectory();
        try
        {
            var mounts = Path.Combine(root, "mounts");
            var kindle = Path.Combine(mounts, "Kindle");
            Directory.CreateDirectory(Path.Combine(kindle, "documents"));
            var service = new MassStorageKindleDeviceService(
                new AppPaths(Path.Combine(root, "appdata")),
                new FakeMetadataService(),
                [mounts],
                (_, _) => Task.CompletedTask);

            var device = Assert.Single(
                await service.DetectDevicesAsync(),
                candidate =>
                    Path.GetFullPath(candidate.RootPath).Equals(
                        Path.GetFullPath(kindle),
                        StringComparison.OrdinalIgnoreCase));
            var source = Path.Combine(root, "sample.epub");
            var bytes = "epub payload"u8.ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            var sourceFile = new BookFile { Sha256 = hash };
            await service.SendBookAsync(device, sourceFile, source);
            var book = Assert.Single(await service.ScanBooksAsync(device));
            Assert.Equal("测试书籍", book.Title);
            Assert.Equal(hash, book.Sha256);

            var exportDirectory = Path.Combine(root, "export");
            var exported = await service.ExportBookAsync(device, book, exportDirectory);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(exported));

            var readingState = Path.ChangeExtension(Path.Combine(device.RootPath, book.RelativePath), ".sdr");
            Directory.CreateDirectory(readingState);
            await File.WriteAllTextAsync(Path.Combine(readingState, "notes"), "saved annotation");
            await service.SendBookAsync(device, sourceFile, source);
            Assert.True(Directory.Exists(readingState));
            await service.RemoveBookAsync(device, book);
            Assert.Empty(await service.ScanBooksAsync(device));
            Assert.False(Directory.Exists(readingState));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(readingState)!, "kkindle-reading-backup-*"));
            Directory.CreateDirectory(readingState);
            await File.WriteAllTextAsync(Path.Combine(readingState, "stale"), "old edition");
            await service.SendBookAsync(device, sourceFile, source);
            Assert.False(Directory.Exists(readingState));
            Assert.Single(await service.ScanBooksAsync(device));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(readingState)!, "kkindle-reading-backup-*"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RemoveBook_RejectsTraversalOutsideDocuments()
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            Directory.CreateDirectory(Path.Combine(kindle, "documents"));
            var service = new MassStorageKindleDeviceService(
                new AppPaths(Path.Combine(root, "appdata")),
                new FakeMetadataService(),
                [root]);
            var device = new KindleDevice { RootPath = kindle, Transport = KindleTransport.MassStorage };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RemoveBookAsync(device, new KindleBook { RelativePath = Path.Combine("..", "outside.epub") }));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadAndBatchDeleteClippings_RoundTripsMountedKindle()
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            var documents = Path.Combine(kindle, "documents");
            Directory.CreateDirectory(documents);
            const string block = "Book (Author)\n- Your Highlight at Location 1 | Added on August 9, 2026\n\nSame quote";
            await File.WriteAllTextAsync(
                Path.Combine(documents, "My Clippings.txt"),
                $"{block}\n==========\n{block}\n==========\n");
            var service = new MassStorageKindleDeviceService(
                new AppPaths(Path.Combine(root, "appdata")),
                new FakeMetadataService(),
                [root]);
            var device = new KindleDevice { RootPath = kindle, Transport = KindleTransport.MassStorage };
            var clippings = await service.ReadClippingsAsync(device);

            await service.DeleteClippingsAsync(device, clippings.Select(item => item.Id).ToArray());

            Assert.Empty(await service.ReadClippingsAsync(device));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("Kobo", ".kobo", ReaderDeviceFamily.Kobo)]
    [InlineData("掌阅", "Books", ReaderDeviceFamily.IReader)]
    [InlineData("Hanvon", "books", ReaderDeviceFamily.Hanvon)]
    [InlineData("Reader", "Books", ReaderDeviceFamily.Generic)]
    public async Task OtherReadersRoundTripNativeEpubWithoutKindleDirectories(string name, string marker, ReaderDeviceFamily family)
    {
        var root = CreateTempDirectory();
        try
        {
            var mounts = Path.Combine(root, "mounts");
            var readerRoot = Path.Combine(mounts, name);
            Directory.CreateDirectory(Path.Combine(readerRoot, marker));
            var service = new MassStorageKindleDeviceService(new AppPaths(Path.Combine(root, "app")), new FakeMetadataService(), [mounts]);
            var device = Assert.Single(await service.DetectDevicesAsync(), candidate => Path.GetFullPath(candidate.RootPath) == Path.GetFullPath(readerRoot));
            Assert.Equal(family, device.Profile.Family);
            var source = Path.Combine(root, "sample.epub");
            await File.WriteAllTextAsync(source, "native epub payload");
            await service.SendBookAsync(device, new BookFile { Format = "epub", Sha256 = await Hashing.Sha256Async(source) }, source);
            var book = Assert.Single(await service.ScanBooksAsync(device));
            var exported = await service.ExportBookAsync(device, book, Path.Combine(root, "export"));
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(exported));
            Assert.False(Directory.Exists(Path.Combine(readerRoot, "documents")));
            Assert.False(Directory.Exists(Path.Combine(readerRoot, "system")));
            await service.RemoveBookAsync(device, book);
            Assert.Empty(await service.ScanBooksAsync(device));
        }
        finally { TryDelete(root); }
    }

    private sealed class FakeMetadataService : IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BookMetadata { Title = "测试书籍", Authors = "测试作者" });
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KkindlePlatformTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }
}
