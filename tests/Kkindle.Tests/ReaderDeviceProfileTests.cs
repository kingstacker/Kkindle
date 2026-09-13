using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderDeviceProfileTests
{
    [Theory]
    [InlineData("Kindle Paperwhite", "documents", ReaderDeviceFamily.Kindle)]
    [InlineData("Reader", ".kobo", ReaderDeviceFamily.Kobo)]
    [InlineData("掌阅 Ocean", "Books", ReaderDeviceFamily.IReader)]
    [InlineData("Hanvon N10", "books", ReaderDeviceFamily.Hanvon)]
    [InlineData("USB Reader", "Books", ReaderDeviceFamily.Generic)]
    [InlineData("USB Reader", "documents", ReaderDeviceFamily.Generic)]
    public void DetectsReaderFamilyAndDoesNotTreatEveryDocumentsFolderAsKindle(string name, string directory, ReaderDeviceFamily expected)
    {
        var profile = ReaderDeviceProfiles.Detect(name, path => path == directory);
        Assert.NotNull(profile);
        Assert.Equal(expected, profile.Family);
    }

    [Fact]
    public void RecognizesRenamedKindleByItsStorageSignatureOrUsbVendor()
    {
        Assert.Equal(ReaderDeviceFamily.Kindle, ReaderDeviceProfiles.Detect("Reader",
            path => path is "documents" or "system/thumbnails")!.Family);
        Assert.Equal(ReaderDeviceFamily.Kindle, ReaderDeviceProfiles.Detect("Reader",
            path => path == "documents", "usb#vid_1949&pid_9981")!.Family);
        Assert.Null(ReaderDeviceProfiles.Detect("USB drive", _ => false));
    }

    [Fact]
    public void NativeEpubIsPreferredForOtherReadersWithoutKindleConversion()
    {
        var epub = new BookFile { Format = ".EPUB" };
        var azw3 = new BookFile { Format = "azw3" };
        var pdf = new BookFile { Format = "pdf" };
        var kobo = ReaderDeviceProfiles.Kobo;
        Assert.Equal([epub, pdf], DeviceTransferPolicy.GetCandidates(kobo, [azw3, pdf, epub]));
        Assert.False(DeviceTransferPolicy.RequiresKindleConversion(kobo, epub));
        Assert.True(DeviceTransferPolicy.RequiresKindleConversion(ReaderDeviceProfiles.Kindle, epub));
        Assert.Same(azw3, DeviceTransferPolicy.GetCandidates(ReaderDeviceProfiles.Kindle, [epub, azw3])[0]);
    }

    [Fact]
    public void GenericCapabilitiesFollowDetectedDirectories()
    {
        var reader = ReaderDeviceProfiles.Detect("掌阅", path => path == "Books")!;
        Assert.False(reader.SupportsResource(KindleResourceKind.Font));
        Assert.False(reader.SupportsResource(KindleResourceKind.Dictionary));
        Assert.False(reader.CanReadNotes);
        var withFonts = ReaderDeviceProfiles.Detect("掌阅", path => path is "Books" or "Fonts")!;
        Assert.Equal("Fonts", withFonts.FontsDirectory);
        Assert.True(withFonts.SupportsResourceFile(KindleResourceKind.Font, "宋体.ttf"));
        Assert.False(withFonts.SupportsResourceFile(KindleResourceKind.Dictionary, "词典.azw3"));
    }

    [Theory]
    [InlineData(".kobo/custom-dict/dicthtml-en.zip", true)]
    [InlineData(".kobo/custom-dict/../KoboReader.sqlite", false)]
    [InlineData(".kobo/custom-dict-escape/dicthtml-en.zip", false)]
    [InlineData("documents/dictionaries/book.azw3", false)]
    [InlineData(".kobo/custom-dict/book.azw3", false)]
    public void KoboDictionariesAreConfinedToTheirOwnDirectoryAndFormat(string path, bool expected)
    {
        Assert.Equal(expected, ReaderDeviceProfiles.Kobo.TryGetResourcePath(KindleResourceKind.Dictionary, path, out _));
    }

    [Theory]
    [InlineData("书籍.epub", true)]
    [InlineData("Books/书籍.pdf", true)]
    [InlineData(".kobo/书籍.epub", false)]
    [InlineData("system/书籍.epub", false)]
    [InlineData("fonts/书籍.epub", false)]
    [InlineData("My Clippings.txt", false)]
    [InlineData("../书籍.epub", false)]
    public void DeviceBookScanningExcludesSystemAndResourceFiles(string path, bool expected) =>
        Assert.Equal(expected, ReaderDeviceProfiles.Kobo.IsBookPath(path));

    [Fact]
    public void DisplayNameDoesNotChangeActualDeviceCapabilities()
    {
        var device = new KindleDevice { Name = "Kindle Paperwhite", Profile = ReaderDeviceProfiles.Kobo };
        Assert.False(device.Profile.UsesKindleThumbnails);
        Assert.False(device.Profile.CanDeleteNotes);
        Assert.False(DeviceTransferPolicy.RequiresKindleConversion(device.Profile, new BookFile { Format = "epub" }));
    }
}
