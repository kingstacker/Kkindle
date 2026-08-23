using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Windows;

namespace Kkindle.Tests.Windows;

public sealed class KindleDeviceTests
{
    [Fact]
    public async Task PersistentScanCacheSkipsUnchangedBookHashingAndMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "device", "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(documents, "cached.pdf");
        await File.WriteAllTextAsync(source, "first cached version");
        var paths = new AppPaths(Path.Combine(root, "app"));
        var device = new KindleDevice
        {
            RootPath = Path.Combine(root, "device"),
            VolumeSerial = "CACHE-DEVICE",
            Name = "Cached Kindle",
            IsReady = true
        };
        try
        {
            var firstMetadata = new CountingMetadataService();
            var firstService = new KindleDeviceService(paths, firstMetadata);
            var firstBook = Assert.Single(await firstService.ScanBooksAsync(device));
            Assert.Equal(1, firstMetadata.ReadCount);
            Assert.NotEmpty(firstBook.Sha256);

            var cachedMetadata = new CountingMetadataService();
            var cachedService = new KindleDeviceService(paths, cachedMetadata);
            var cachedBook = Assert.Single(await cachedService.ScanBooksAsync(device));
            Assert.Equal(0, cachedMetadata.ReadCount);
            Assert.Equal(firstBook.Sha256, cachedBook.Sha256);
            Assert.Equal("缓存测试书", cachedBook.Title);

            await File.AppendAllTextAsync(source, " changed");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
            var changedMetadata = new CountingMetadataService();
            var changedService = new KindleDeviceService(paths, changedMetadata);
            var changedBook = Assert.Single(await changedService.ScanBooksAsync(device));
            Assert.Equal(1, changedMetadata.ReadCount);
            Assert.NotEqual(firstBook.Sha256, changedBook.Sha256);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ProgressiveScanReportsFastListBeforeEnrichedBooks()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        await File.WriteAllTextAsync(Path.Combine(documents, "progress.pdf"), "progressive scan");
        try
        {
            var updates = new List<KindleScanProgress>();
            var service = new KindleDeviceService(null, new CountingMetadataService());
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            var books = await service.ScanBooksProgressivelyAsync(
                device,
                new TestHelpers.InlineProgress<KindleScanProgress>(updates.Add));

            Assert.Equal(KindleScanStage.Enumerated, updates.First().Stage);
            Assert.Empty(Assert.Single(updates.First().Books).Sha256);
            Assert.Contains(updates, update => update.Stage == KindleScanStage.Enriched);
            Assert.NotEmpty(Assert.Single(books).Sha256);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void DeviceIdentityUsesVolumeSerialAcrossDriveLetterChanges()
    {
        var firstDetection = new KindleDevice { RootPath = @"E:\", VolumeSerial = "A1B2C3D4" };
        var secondDetection = new KindleDevice { RootPath = @"F:\", VolumeSerial = "a1b2c3d4" };
        var unidentifiedDevice = new KindleDevice { RootPath = @"F:\" };

        Assert.Equal(firstDetection.Identity, secondDetection.Identity, ignoreCase: true);
        Assert.NotEqual(firstDetection.RootPath, secondDetection.RootPath);
        Assert.NotEqual(firstDetection.Identity, unidentifiedDevice.Identity);
    }

    [Fact]
    public async Task SendsAndScansBooksInDocumentsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var source = Path.Combine(root, "source.epub");
        await File.WriteAllTextAsync(source, "hello kindle");
        try
        {
            var hash = await ComputeHashAsync(source);
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var file = new BookFile { Format = "epub", Sha256 = hash, RelativePath = "source.epub" };
            var service = new KindleDeviceService();

            await service.SendBookAsync(device, file, source);
            var books = await service.ScanBooksAsync(device);

            var book = Assert.Single(books);
            Assert.Equal("source.epub", book.FileName);
            Assert.Equal(hash, book.Sha256);
            Assert.True(File.Exists(Path.Combine(root, "documents", "source.epub")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ScanBooksExcludesKindleDictionaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        var dictionaries = Path.Combine(documents, "Dictionaries");
        Directory.CreateDirectory(dictionaries);
        await File.WriteAllTextAsync(Path.Combine(documents, "novel.azw3"), "book");
        await File.WriteAllTextAsync(Path.Combine(dictionaries, "english.azw3"), "dictionary");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            var books = await service.ScanBooksAsync(device);

            var book = Assert.Single(books);
            Assert.Equal("novel.azw3", book.FileName);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ScanBooksExcludesDictionaryTaggedBookOutsideDictionaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var downloads = Path.Combine(root, "documents", "Downloads", "Items01");
        Directory.CreateDirectory(downloads);
        var dictionaryPath = Path.Combine(downloads, "dictionary.azw");
        await File.WriteAllBytesAsync(dictionaryPath, CreateDictionaryTaggedKindleFile());
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            var books = await service.ScanBooksAsync(device);

            Assert.Empty(books);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task SameNameWithDifferentContentReplacesExistingBook()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(root, "first");
        var secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstSource = Path.Combine(firstDirectory, "book.epub");
        var secondSource = Path.Combine(secondDirectory, "book.epub");
        await File.WriteAllTextAsync(firstSource, "first book");
        await File.WriteAllTextAsync(secondSource, "second book");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            await service.SendBookAsync(device, new BookFile { Sha256 = await ComputeHashAsync(firstSource) }, firstSource);
            await service.SendBookAsync(device, new BookFile { Sha256 = await ComputeHashAsync(secondSource) }, secondSource);

            var books = await service.ScanBooksAsync(device);

            // Re-sending overwrites the device copy so updated covers and
            // metadata reach the existing entry instead of a "(2)" duplicate.
            var book = Assert.Single(books);
            Assert.Equal("book.epub", book.FileName);
            Assert.Equal("second book", await File.ReadAllTextAsync(Path.Combine(root, "documents", "book.epub")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ExportsBookFromDriveConnectedKindle()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(documents, "export.epub");
        await File.WriteAllTextAsync(source, "exported book");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var book = new KindleBook { RelativePath = Path.Combine("documents", "export.epub") };

            var exportedPath = await service.ExportBookAsync(device, book, destination);

            Assert.Equal("exported book", await File.ReadAllTextAsync(exportedPath));
            Assert.Equal("export.epub", Path.GetFileName(exportedPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(destination, true); } catch { }
        }
    }

    [Fact]
    public async Task RemovesBookFromDriveConnectedKindle()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(documents, "remove.azw3");
        await File.WriteAllTextAsync(source, "book to remove");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var book = new KindleBook { RelativePath = Path.Combine("documents", "remove.azw3") };

            await service.RemoveBookAsync(device, book);

            Assert.False(File.Exists(source));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task RefusesToRemoveBookOutsideDocumentsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var outside = Path.Combine(root, "outside.azw3");
        await File.WriteAllTextAsync(outside, "must stay");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var book = new KindleBook { RelativePath = "outside.azw3" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveBookAsync(device, book));

            Assert.True(File.Exists(outside));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task HashMismatchCleansPartialTransferFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(root, "mismatch.epub");
        await File.WriteAllTextAsync(source, "content whose hash will not match");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var wrongHash = new string('0', 64);

            await Assert.ThrowsAsync<IOException>(() =>
                service.SendBookAsync(device, new BookFile { Sha256 = wrongHash }, source));

            Assert.Empty(Directory.EnumerateFiles(documents, "*.kkindle-part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(documents, "mismatch.epub")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task CancellationCleansPartialTransferFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(root, "cancel.epub");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var file = new BookFile { Sha256 = await ComputeHashAsync(source) };
            using var cancellation = new CancellationTokenSource();
            var progress = new TestHelpers.InlineProgress<TransferProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SendBookAsync(device, file, source, progress, cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(documents, "*.kkindle-part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(documents, "cancel.epub")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ManagesKindleFontsInsideFontsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var sourceDirectory = Path.Combine(root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "reader.ttf");
        var exported = Path.Combine(root, "exported.ttf");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            await service.SendResourceAsync(device, KindleResourceKind.Font, source);
            var font = Assert.Single(await service.ScanResourcesAsync(device, KindleResourceKind.Font));
            Assert.Equal(Path.Combine("fonts", "reader.ttf"), font.RelativePath);
            Assert.Equal(5, font.Size);
            Assert.NotEmpty(font.Sha256);

            await service.ExportResourceAsync(device, font, exported);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(exported));

            await service.RemoveResourceAsync(device, font);
            Assert.Empty(await service.ScanResourcesAsync(device, KindleResourceKind.Font));
            Assert.True(Directory.Exists(Path.Combine(root, "documents")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ManagesKindleDictionariesInsideDedicatedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var source = Path.Combine(root, "english.azw3");
        await File.WriteAllBytesAsync(source, [9, 8, 7]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            await service.SendResourceAsync(device, KindleResourceKind.Dictionary, source);
            var dictionary = Assert.Single(await service.ScanResourcesAsync(device, KindleResourceKind.Dictionary));
            Assert.Equal(Path.Combine("documents", "dictionaries", "english.azw3"), dictionary.RelativePath);
            Assert.Empty(await service.ScanResourcesAsync(device, KindleResourceKind.Font));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ResourceOperationsRejectWrongFormatsAndPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var wrong = Path.Combine(root, "not-a-font.exe");
        var outside = Path.Combine(root, "outside.ttf");
        await File.WriteAllTextAsync(wrong, "wrong");
        await File.WriteAllTextAsync(outside, "keep");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.SendResourceAsync(device, KindleResourceKind.Font, wrong));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RemoveResourceAsync(device, new KindleDeviceResource
                {
                    Kind = KindleResourceKind.Font,
                    RelativePath = Path.Combine("fonts", "..", "outside.ttf")
                }));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData(KindleResourceKind.Font, "fonts/font.otf", true)]
    [InlineData(KindleResourceKind.Font, "documents/font.otf", false)]
    [InlineData(KindleResourceKind.Dictionary, "documents/dictionaries/main.mobi", true)]
    [InlineData(KindleResourceKind.Dictionary, "documents/main.mobi", false)]
    public void ResourcePolicyConfinesFilesToExpectedKindleDirectory(
        KindleResourceKind kind,
        string path,
        bool expected)
    {
        Assert.Equal(expected, KindleResourcePolicy.TryGetPathWithinRoot(kind, path, out _));
    }

    [Fact]
    public async Task CancelledResourceTransferLeavesNoPartialFont()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var source = Path.Combine(root, "large.ttf");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            using var cancellation = new CancellationTokenSource();
            var progress = new TestHelpers.InlineProgress<TransferProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SendResourceAsync(device, KindleResourceKind.Font, source, progress, cancellation.Token));
            var fonts = Path.Combine(root, "fonts");
            Assert.Empty(Directory.EnumerateFiles(fonts, "*.kkindle-part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(fonts, "large.ttf")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ReadsAndDeletesIndividualKindleClippingWithoutLosingOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var clippingsPath = Path.Combine(documents, "My Clippings.txt");
        var content = """
            Scale (Geoffrey West)
            - Your Highlight on page 12 | Location 180-182 | Added on Sunday, August 9, 2026

            Cities are living systems.
            ==========
            规模（杰弗里·韦斯特）
            - 您在位置 220 的笔记 | 添加于 2026年8月10日星期一

            复习这一段
            ==========
            """;
        await File.WriteAllTextAsync(clippingsPath, content, new System.Text.UTF8Encoding(true));
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var items = await service.ReadClippingsAsync(device);
            Assert.Equal(2, items.Count);
            Assert.Equal(KindleClippingType.Highlight, items[0].Type);
            Assert.Equal("Cities are living systems.", items[0].Content);
            Assert.Equal(new DateTime(2026, 8, 9), items[0].AddedAt?.Date);
            Assert.Equal(KindleClippingType.Note, items[1].Type);
            Assert.Equal("规模", items[1].BookTitle);
            Assert.Equal("杰弗里·韦斯特", items[1].Author);
            Assert.Equal(new DateTime(2026, 8, 10), items[1].AddedAt?.Date);

            await service.DeleteClippingAsync(device, items[0].Id);
            var remaining = Assert.Single(await service.ReadClippingsAsync(device));
            Assert.Equal("复习这一段", remaining.Content);
            Assert.DoesNotContain("Cities are living systems", await File.ReadAllTextAsync(clippingsPath));
            Assert.False(File.Exists(clippingsPath + ".kkindle-part"));
            Assert.False(File.Exists(clippingsPath + ".kkindle-backup"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ClippingsParserKeepsDuplicateRecordsIndividuallyAddressable()
    {
        const string block = "Book (Author)\n- Your Highlight at Location 1\n\nSame quote";
        var parsed = KindleClippingsParser.Parse($"{block}\n==========\n{block}\n==========\n");

        Assert.Equal(2, parsed.Count);
        Assert.NotEqual(parsed[0].Id, parsed[1].Id);
        var rebuilt = KindleClippingsParser.BuildDocument([parsed[1]]);
        Assert.Single(KindleClippingsParser.Parse(rebuilt));
        Assert.EndsWith("==========\r\n", rebuilt);
    }

    [Fact]
    public void ClippingsParserPairsHighlightAndNoteAtTheSameLocation()
    {
        const string content = """
            凡人修仙传（忘语）
            - 您在位置 #12144-12145的标注 | 添加于 2026年8月19日星期三 下午7:14:35

            绿衣少女纤细单薄的身子。
            ==========
            凡人修仙传（忘语）
            - 您在位置 #12145 的笔记 | 添加于 2026年8月19日星期三 下午7:14:46

            好看
            ==========
            """;

        var pair = Assert.Single(KindleClippingsParser.PairForDisplay(KindleClippingsParser.Parse(content)));
        Assert.Equal(KindleClippingType.Highlight, pair.Clipping.Type);
        Assert.Equal("绿衣少女纤细单薄的身子。", pair.Clipping.Content);
        Assert.NotNull(pair.PairedNote);
        Assert.Equal("好看", pair.PairedNote!.Content);
    }

    [Fact]
    public async Task DeletesDuplicateKindleClippingsInOneRewrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var clippingsPath = Path.Combine(documents, "My Clippings.txt");
        const string block = "Book (Author)\n- Your Highlight at Location 1 | Added on August 9, 2026\n\nSame quote";
        await File.WriteAllTextAsync(clippingsPath, $"{block}\n==========\n{block}\n==========\n", new System.Text.UTF8Encoding(true));
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var items = await service.ReadClippingsAsync(device);

            await service.DeleteClippingsAsync(device, items.Select(item => item.Id).ToArray());

            Assert.Empty(await service.ReadClippingsAsync(device));
            Assert.False(File.Exists(clippingsPath + ".kkindle-part"));
            Assert.False(File.Exists(clippingsPath + ".kkindle-backup"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ClippingsParserHandlesBomAndCjkMetadata()
    {
        const string content = "\uFEFF吾輩は猫である（夏目漱石）\n- 位置No. 3-4のハイライト | 作成日: 2026年8月11日\n\n名前はまだ無い。\n==========\n";

        var item = Assert.Single(KindleClippingsParser.Parse(content));

        Assert.Equal("吾輩は猫である", item.BookTitle);
        Assert.Equal("夏目漱石", item.Author);
        Assert.Equal(KindleClippingType.Highlight, item.Type);
        Assert.Equal(new DateTime(2026, 8, 11), item.AddedAt?.Date);
    }

    [Fact]
    public void BuildsKindleShelfThumbnailNameFromAzw3ExthMetadata()
    {
        var bytes = new byte[160];
        "EXTH"u8.CopyTo(bytes.AsSpan(32));
        WriteBigEndian(bytes, 36, 68);
        WriteBigEndian(bytes, 40, 2);
        WriteBigEndian(bytes, 44, 113);
        WriteBigEndian(bytes, 48, 44);
        "1f0297e3-39ec-412a-8c08-6cc41d5d2711"u8.CopyTo(bytes.AsSpan(52));
        WriteBigEndian(bytes, 88, 501);
        WriteBigEndian(bytes, 92, 12);
        "EBOK"u8.CopyTo(bytes.AsSpan(96));

        var fileName = KindleThumbnailService.ReadThumbnailFileName(bytes);

        Assert.Equal(
            "thumbnail_1f0297e3-39ec-412a-8c08-6cc41d5d2711_EBOK_portrait.jpg",
            fileName);
    }

    [Fact]
    public void DefaultsMissingKindleCdeTypeToEbook()
    {
        var bytes = new byte[100];
        "EXTH"u8.CopyTo(bytes.AsSpan(16));
        WriteBigEndian(bytes, 20, 56);
        WriteBigEndian(bytes, 24, 1);
        WriteBigEndian(bytes, 28, 113);
        WriteBigEndian(bytes, 32, 44);
        "1f0297e3-39ec-412a-8c08-6cc41d5d2711"u8.CopyTo(bytes.AsSpan(36));

        Assert.Equal(
            "thumbnail_1f0297e3-39ec-412a-8c08-6cc41d5d2711_EBOK_portrait.jpg",
            KindleThumbnailService.ReadThumbnailFileName(bytes));
    }

    [Fact]
    public async Task ReadsKindleCoverRecordInsteadOfLargestEmbeddedImage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "KkindleTests",
            Guid.NewGuid().ToString("N"),
            "cover.azw3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const int recordTableEnd = 102;
        const int firstRecordOffset = recordTableEnd;
        const int coverRecordOffset = 400;
        const int interiorRecordOffset = 600;
        var bytes = new byte[interiorRecordOffset + 12 * 1024];
        WriteBigEndianShort(bytes, 76, 3);
        WriteBigEndian(bytes, 78, firstRecordOffset);
        WriteBigEndian(bytes, 86, coverRecordOffset);
        WriteBigEndian(bytes, 94, interiorRecordOffset);

        "MOBI"u8.CopyTo(bytes.AsSpan(firstRecordOffset + 16));
        WriteBigEndian(bytes, firstRecordOffset + 16 + 0x5C, 1);
        var exthOffset = firstRecordOffset + 160;
        "EXTH"u8.CopyTo(bytes.AsSpan(exthOffset));
        WriteBigEndian(bytes, exthOffset + 4, 24);
        WriteBigEndian(bytes, exthOffset + 8, 1);
        WriteBigEndian(bytes, exthOffset + 12, 201);
        WriteBigEndian(bytes, exthOffset + 16, 12);
        WriteBigEndian(bytes, exthOffset + 20, 0);

        WriteTestJpeg(bytes, coverRecordOffset, 0x42, 180);
        WriteTestJpeg(bytes, interiorRecordOffset, 0x99, 10 * 1024);
        await File.WriteAllBytesAsync(path, bytes);

        try
        {
            var metadata = await new BookMetadataService().ReadMetadataAsync(path);

            Assert.NotNull(metadata.CoverBytes);
            Assert.Equal((byte)0x42, metadata.CoverBytes![8]);
            Assert.Equal(180, metadata.CoverBytes.Length);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RecognizesStructurallyValidKindleReadyAzw3()
    {
        var path = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"), "ready.azw3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[2048];
        "BOOKMOBI"u8.CopyTo(bytes.AsSpan(60));
        "EXTH"u8.CopyTo(bytes.AsSpan(128));
        WriteBigEndian(bytes, 132, 68);
        WriteBigEndian(bytes, 136, 2);
        WriteBigEndian(bytes, 140, 113);
        WriteBigEndian(bytes, 144, 44);
        "1f0297e3-39ec-412a-8c08-6cc41d5d2711"u8.CopyTo(bytes.AsSpan(148));
        WriteBigEndian(bytes, 184, 501);
        WriteBigEndian(bytes, 188, 12);
        "EBOK"u8.CopyTo(bytes.AsSpan(192));
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            Assert.True(await KindleThumbnailService.IsKindleReadyAzw3Async(path));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ChineseAzw3RequiresFontCompatibilityMarker(bool includeOverride, bool expectedRebuild)
    {
        var bytes = new byte[128];
        "EXTH"u8.CopyTo(bytes.AsSpan(16));
        var headerLength = includeOverride ? 38 : 26;
        WriteBigEndian(bytes, 20, headerLength);
        WriteBigEndian(bytes, 24, includeOverride ? 2 : 1);
        WriteBigEndian(bytes, 28, 524);
        WriteBigEndian(bytes, 32, 10);
        "zh"u8.CopyTo(bytes.AsSpan(36));
        if (includeOverride)
        {
            WriteBigEndian(bytes, 38, 528);
            WriteBigEndian(bytes, 42, 12);
            "true"u8.CopyTo(bytes.AsSpan(46));
        }

        Assert.Equal(expectedRebuild, KindleThumbnailService.RequiresCjkFontCompatibilityRebuild(bytes));
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] CreateDictionaryTaggedKindleFile()
    {
        var bytes = new byte[128];
        "EXTH"u8.CopyTo(bytes.AsSpan(32));
        WriteBigEndian(bytes, 36, 32);
        WriteBigEndian(bytes, 40, 1);
        WriteBigEndian(bytes, 44, 105);
        WriteBigEndian(bytes, 48, 20);
        "Dictionaries"u8.CopyTo(bytes.AsSpan(52));
        return bytes;
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static void WriteBigEndianShort(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static void WriteTestJpeg(byte[] bytes, int offset, byte marker, int length)
    {
        bytes[offset] = 0xFF;
        bytes[offset + 1] = 0xD8;
        bytes[offset + 2] = 0xFF;
        bytes[offset + 3] = 0xE0;
        bytes[offset + 8] = marker;
        bytes[offset + length - 2] = 0xFF;
        bytes[offset + length - 1] = 0xD9;
    }

    private sealed class CountingMetadataService : IMetadataService
    {
        public int ReadCount { get; private set; }

        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new BookMetadata { Title = "缓存测试书", Authors = "测试作者" });
        }
    }
}
