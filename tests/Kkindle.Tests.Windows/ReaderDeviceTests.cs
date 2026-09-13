using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Windows;

namespace Kkindle.Tests.Windows;

public sealed class ReaderDeviceTests
{
    [Fact]
    public async Task KoboUsesNativeBookAndDictionaryPathsAndProtectsItsDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        var deviceRoot = Path.Combine(root, "device");
        Directory.CreateDirectory(Path.Combine(deviceRoot, ".kobo"));
        try
        {
            var device = new KindleDevice { RootPath = deviceRoot, VolumeSerial = "KOBO", Name = "Kobo", Profile = ReaderDeviceProfiles.Kobo };
            var service = new KindleDeviceService(new AppPaths(Path.Combine(root, "app")), new Metadata());
            var source = Path.Combine(root, "书籍.epub");
            await File.WriteAllTextAsync(source, "native epub payload");
            var file = new BookFile { Format = "epub", Sha256 = await Hashing.Sha256Async(source) };
            await service.SendBookAsync(device, file, source);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(Path.Combine(deviceRoot, "书籍.epub")));
            Assert.False(Directory.Exists(Path.Combine(deviceRoot, "documents")));
            Assert.False(Directory.Exists(Path.Combine(deviceRoot, "system")));
            var book = Assert.Single(await service.ScanBooksAsync(device));
            var exported = await service.ExportBookAsync(device, book, Path.Combine(root, "export"));
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(exported));

            var dictionary = Path.Combine(root, "dicthtml-en.zip");
            await File.WriteAllTextAsync(dictionary, "dictionary payload");
            await service.SendResourceAsync(device, KindleResourceKind.Dictionary, dictionary);
            var resource = Assert.Single(await service.ScanResourcesAsync(device, KindleResourceKind.Dictionary));
            Assert.Equal(Path.Combine(".kobo", "custom-dict", "dicthtml-en.zip"), resource.RelativePath);
            await service.RemoveResourceAsync(device, resource);
            Assert.Empty(await service.ScanResourcesAsync(device, KindleResourceKind.Dictionary));

            var protectedPath = Path.Combine(deviceRoot, ".kobo", "KoboReader.sqlite");
            await File.WriteAllTextAsync(protectedPath, "protected database");
            var protectedBook = new KindleBook { RelativePath = Path.Combine(".kobo", "KoboReader.sqlite") };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveBookAsync(device, protectedBook));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportBookAsync(device, protectedBook, Path.Combine(root, "export")));
            await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteClippingsAsync(device, ["kobo:one"]));
            Assert.Equal("protected database", await File.ReadAllTextAsync(protectedPath));
            await service.RemoveBookAsync(device, book);
            Assert.Empty(await service.ScanBooksAsync(device));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GenericReaderRejectsKindleFilesAndUnsupportedResourceWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "device", "Books"));
        try
        {
            var device = new KindleDevice { RootPath = Path.Combine(root, "device"), Profile = new ReaderDeviceProfile() };
            var service = new KindleDeviceService(null, new Metadata());
            var book = Path.Combine(root, "book.azw3");
            var font = Path.Combine(root, "font.ttf");
            await File.WriteAllTextAsync(book, "book");
            await File.WriteAllTextAsync(font, "font");
            await Assert.ThrowsAsync<NotSupportedException>(() => service.SendBookAsync(device, new BookFile(), book));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.SendResourceAsync(device, KindleResourceKind.Font, font));
            Assert.Empty(await service.ScanResourcesAsync(device, KindleResourceKind.Dictionary));
            Assert.Empty(await service.ReadClippingsAsync(device));
            Assert.False(Directory.Exists(Path.Combine(device.RootPath, "fonts")));
            Assert.False(Directory.Exists(Path.Combine(device.RootPath, "documents")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Metadata : IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BookMetadata { Title = "书籍", Authors = "作者" });
    }
}
