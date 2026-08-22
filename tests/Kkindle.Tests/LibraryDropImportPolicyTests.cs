using Avalonia.Input;

namespace Kkindle.Tests;

public sealed class LibraryDropImportPolicyTests
{
    [Fact]
    public void AcceptsAdvertisedFileFormatBeforeLinuxPayloadIsMaterialized()
    {
        using var transfer = new LazyFileDataTransfer();

        Assert.True(LibraryDropImportPolicy.CanAccept(transfer));
        Assert.Empty(LibraryDropImportPolicy.GetLocalPaths(transfer));
    }

    [Fact]
    public void RecursivelyExpandsSupportedBooksFromDroppedFolder()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            var epub = Path.Combine(root, "one.epub");
            var pdf = Path.Combine(nested, "two.PDF");
            File.WriteAllText(epub, "epub");
            File.WriteAllText(pdf, "pdf");
            File.WriteAllText(Path.Combine(nested, "ignore.txt"), "text");

            var files = LibraryDropImportPolicy.ExpandImportableFiles([root]);

            Assert.Equal(2, files.Length);
            Assert.Contains(Path.GetFullPath(epub), files);
            Assert.Contains(Path.GetFullPath(pdf), files);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void ReadsLinuxUriListFolderDrop()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var format = DataFormat.CreateStringPlatformFormat("text/uri-list");
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(format, new Uri(root).AbsoluteUri + "\r\n"));

            Assert.True(LibraryDropImportPolicy.CanAccept(transfer));
            Assert.Equal([Path.GetFullPath(root)], LibraryDropImportPolicy.GetLocalPaths(transfer));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private sealed class LazyFileDataTransfer : IDataTransfer
    {
        public IReadOnlyList<DataFormat> Formats { get; } = [DataFormat.File];

        public IReadOnlyList<IDataTransferItem> Items { get; } = [];

        public void Dispose()
        {
        }
    }
}
