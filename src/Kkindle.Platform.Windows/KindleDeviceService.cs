using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Platform.Windows;

public sealed class KindleDeviceService : IKindleDeviceService
{
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private const int FileOperationRetryAttempts = 30;
    private const int FileOperationRetryDelayMilliseconds = 100;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".prc", ".kfx"
    };
    private readonly IMetadataService _metadata;
    private readonly string? _coverCacheDirectory;
    private readonly KindleScanCacheStore? _scanCache;

    public KindleDeviceService()
        : this(null, new BookMetadataService())
    {
    }

    public KindleDeviceService(AppPaths? paths, IMetadataService metadata)
    {
        _metadata = metadata;
        _coverCacheDirectory = paths is null ? null : Path.Combine(paths.Covers, "kindle");
        _scanCache = paths is null ? null : new KindleScanCacheStore(paths);
        if (_coverCacheDirectory is not null) Directory.CreateDirectory(_coverCacheDirectory);
    }

    public async Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<KindleDevice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                var documents = Path.Combine(drive.RootDirectory.FullName, "documents");
                if (!Directory.Exists(documents)) continue;
                devices.Add(new KindleDevice
                {
                    RootPath = drive.RootDirectory.FullName,
                    VolumeSerial = GetVolumeSerial(drive.RootDirectory.FullName),
                    Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Kindle" : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsReady = true
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var wpdDevices = await Task.Run(() => WpdKindleAccess.DetectDevices(cancellationToken), cancellationToken);
        devices.AddRange(wpdDevices.Where(wpd => devices.All(disk =>
            !string.Equals(disk.Identity, wpd.Identity, StringComparison.OrdinalIgnoreCase))));
        return devices;
    }

    public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default) =>
        ScanBooksProgressivelyAsync(device, progress: null, cancellationToken);

    public async Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var enumerated = device.Transport == KindleTransport.Wpd
            ? await Task.Run(
                () => WpdKindleAccess.ScanBooks(device, SupportedExtensions, cancellationToken),
                cancellationToken)
            : await Task.Run(() => EnumerateMassStorageBooks(device, cancellationToken), cancellationToken);
        foreach (var book in enumerated) SetFallbackMetadata(book);

        var cachedEntries = _scanCache is null
            ? new Dictionary<string, KindleScanCacheEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, KindleScanCacheEntry>(
                await _scanCache.GetDeviceEntriesAsync(device.Identity, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        var currentEntries = new List<KindleScanCacheEntry>(enumerated.Count);
        var visibleBooks = new List<KindleBook>(enumerated.Count);
        var pending = new List<KindleBook>();

        foreach (var book in enumerated)
        {
            var key = NormalizeDevicePath(book.RelativePath);
            if (cachedEntries.TryGetValue(key, out var cached)
                && cached.Matches(book.Size, book.ModifiedAt)
                // A cache entry without a cover is incomplete. Re-enrich it so
                // Kindle's system thumbnail can be copied when the book itself
                // does not contain an embedded cover.
                && (device.Transport != KindleTransport.Wpd
                    ? cached.CoverPath is null || File.Exists(cached.CoverPath)
                    : cached.CoverPath is not null && File.Exists(cached.CoverPath)))
            {
                ApplyCachedBook(book, cached);
                currentEntries.Add(cached);
                if (cached.IsDictionary) continue;
                visibleBooks.Add(book);
                continue;
            }

            visibleBooks.Add(book);
            pending.Add(book);
        }

        progress?.Report(new KindleScanProgress(
            KindleScanStage.Enumerated,
            visibleBooks.Select(CloneBook).ToArray(),
            [],
            enumerated.Count - pending.Count,
            enumerated.Count));

        var changed = new List<KindleBook>();
        var removed = new List<string>();
        var processed = enumerated.Count - pending.Count;
        foreach (var book in pending)
        {
            var stopWpdEnrichment = false;
            cancellationToken.ThrowIfCancellationRequested();
            var isDictionary = false;
            var cacheable = true;
            using var enrichmentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            enrichmentTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var enrichment = Task.Run(async () =>
            {
                if (device.Transport == KindleTransport.Wpd)
                {
                    isDictionary = await EnrichWpdBookAsync(device, book, enrichmentTimeout.Token);
                }
                else
                {
                    var path = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
                    isDictionary = await KindleBookClassifier.IsDictionaryAsync(path, enrichmentTimeout.Token);
                    if (!isDictionary)
                    {
                        book.Sha256 = await Hashing.Sha256Async(path, enrichmentTimeout.Token);
                        await EnrichBookAsync(device, book, path, enrichmentTimeout.Token);
                    }
                }
            }, cancellationToken);
            try
            {
                await enrichment;
            }
            catch (OperationCanceledException) when (
                enrichmentTimeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // A single book's enrichment timed out; keep the fallback card.
                cacheable = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
            {
                // Keep the quickly enumerated fallback card when enrichment fails.
                cacheable = false;
                // A Kindle-side disconnect first surfaces as a WPD I/O failure. Do
                // not continue reopening the device for every remaining book.
                stopWpdEnrichment = device.Transport == KindleTransport.Wpd;
            }

            processed++;
            if (cacheable) currentEntries.Add(CreateCacheEntry(device, book, isDictionary));
            if (isDictionary)
            {
                visibleBooks.RemoveAll(candidate => candidate.RelativePath.Equals(
                    book.RelativePath,
                    StringComparison.OrdinalIgnoreCase));
                removed.Add(book.RelativePath);
            }
            else
            {
                changed.Add(book);
            }

            if (changed.Count + removed.Count >= 8 || processed == enumerated.Count)
            {
                progress?.Report(new KindleScanProgress(
                    KindleScanStage.Enriched,
                    changed.ToArray(),
                    removed.ToArray(),
                    processed,
                    enumerated.Count));
                changed.Clear();
                removed.Clear();
            }
            if (stopWpdEnrichment) break;
        }

        if (_scanCache is not null)
        {
            try
            {
                await _scanCache.ReplaceDeviceEntriesAsync(device.Identity, currentEntries, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (TimeoutException)
            {
                // The cache write must never block the scan result.
            }
        }
        return visibleBooks;
    }

    private static IReadOnlyList<KindleBook> EnumerateMassStorageBooks(
        KindleDevice device,
        CancellationToken cancellationToken)
    {
        var documents = GetDocumentsRoot(device);
        if (!Directory.Exists(documents)) return [];
        var books = new List<KindleBook>();
        foreach (var path in Directory.EnumerateFiles(documents, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentsRelativePath = Path.GetRelativePath(documents, path);
            if (IsDictionaryPath(documentsRelativePath)) continue;
            if (!SupportedExtensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                var info = new FileInfo(path);
                books.Add(new KindleBook
                {
                    RelativePath = Path.GetRelativePath(device.RootPath, path),
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc,
                    IsManagedByKkindle = false
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return books;
    }

    private static void ApplyCachedBook(KindleBook book, KindleScanCacheEntry cached)
    {
        book.Format = cached.Format;
        book.Sha256 = cached.Sha256;
        book.Title = cached.Title;
        book.Authors = cached.Authors;
        book.CoverPath = cached.CoverPath is not null && File.Exists(cached.CoverPath)
            ? cached.CoverPath
            : null;
    }

    private static KindleScanCacheEntry CreateCacheEntry(
        KindleDevice device,
        KindleBook book,
        bool isDictionary) => new()
    {
        DeviceIdentity = device.Identity,
        RelativePath = book.RelativePath,
        Size = book.Size,
        ModifiedAt = book.ModifiedAt,
        Format = book.Format,
        Sha256 = book.Sha256,
        Title = book.Title,
        Authors = book.Authors,
        CoverPath = book.CoverPath,
        IsDictionary = isDictionary
    };

    private static KindleBook CloneBook(KindleBook book) => new()
    {
        RelativePath = book.RelativePath,
        Title = book.Title,
        Authors = book.Authors,
        Format = book.Format,
        Size = book.Size,
        Sha256 = book.Sha256,
        CoverPath = book.CoverPath,
        ModifiedAt = book.ModifiedAt,
        IsManagedByKkindle = book.IsManagedByKkindle
    };

    private static string NormalizeDevicePath(string path) => path.Replace('/', '\\').TrimStart('\\');

    private static bool IsDictionaryPath(string relativePath)
    {
        var firstSeparator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var firstSegment = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
        return firstSegment.Equals("dictionaries", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> EnrichWpdBookAsync(
        KindleDevice device,
        KindleBook book,
        CancellationToken cancellationToken)
    {
        SetFallbackMetadata(book);
        var cachedCover = FindCachedCover(device, book);
        if (_coverCacheDirectory is null || book.Size <= 0 || book.Size > MaximumMetadataFileSize) return false;
        if (book.Format is not ("epub" or "mobi" or "azw" or "azw3" or "kfx")) return false;

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "metadata", Guid.NewGuid().ToString("N"));
        try
        {
            var localPath = await Task.Run(
                () => WpdKindleAccess.CopyBookToLocal(device, book, stagingDirectory, cancellationToken),
                cancellationToken);
            book.Sha256 = await Hashing.Sha256Async(localPath, cancellationToken);
            var isDictionary = await KindleBookClassifier.IsDictionaryAsync(localPath, cancellationToken);
            if (isDictionary) return true;
            if (cachedCover is not null)
            {
                book.CoverPath = cachedCover;
                return false;
            }
            await EnrichBookAsync(device, book, localPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(book.CoverPath))
            {
                var thumbnailName = await KindleThumbnailService.ReadThumbnailFileNameAsync(localPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(thumbnailName))
                {
                    var thumbnailPath = WpdKindleAccess.CopyStorageFileToLocal(
                        device,
                        $@"system\thumbnails\{thumbnailName}",
                        stagingDirectory,
                        cancellationToken);
                    var coverPath = Path.Combine(_coverCacheDirectory, GetCoverCacheKey(device, book) + ".jpg");
                    File.Copy(thumbnailPath, coverPath, overwrite: true);
                    book.CoverPath = coverPath;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
        {
            // A missing cover must not hide an otherwise readable device book.
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private async Task EnrichBookAsync(
        KindleDevice device,
        KindleBook book,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        SetFallbackMetadata(book);
        try
        {
            var metadata = await _metadata.ReadMetadataAsync(sourcePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
            if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
            if (metadata.CoverBytes is { Length: > 0 } && _coverCacheDirectory is not null)
            {
                var extension = metadata.CoverExtension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? ".png"
                    : ".jpg";
                var coverPath = Path.Combine(_coverCacheDirectory, GetCoverCacheKey(device, book) + extension);
                await File.WriteAllBytesAsync(coverPath, metadata.CoverBytes, cancellationToken);
                book.CoverPath = coverPath;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // File metadata is best effort; filename, format and size remain available.
        }
    }

    private string? FindCachedCover(KindleDevice device, KindleBook book)
    {
        if (_coverCacheDirectory is null) return null;
        var key = GetCoverCacheKey(device, book);
        foreach (var extension in new[] { ".jpg", ".png" })
        {
            var path = Path.Combine(_coverCacheDirectory, key + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string GetCoverCacheKey(KindleDevice device, KindleBook book)
    {
        var identity = $"{device.Identity}\n{book.RelativePath}\n{book.Size}\n{book.ModifiedAt?.UtcTicks ?? 0}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static void SetFallbackMetadata(KindleBook book)
    {
        var fileName = Path.GetFileNameWithoutExtension(book.RelativePath);
        var identifierSeparator = fileName.LastIndexOf('_');
        if (identifierSeparator > 0)
        {
            var suffix = fileName[(identifierSeparator + 1)..];
            if (suffix.Length == 32 && suffix.All(Uri.IsHexDigit)) fileName = fileName[..identifierSeparator];
        }
        book.Title = fileName.Replace('_', ' ').Trim();
        book.Authors = "未知作者";
    }

    public async Task SendBookAsync(KindleDevice device, BookFile bookFile, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, string? coverOverridePath = null)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        var thumbnail = await KindleThumbnailService.CreateAsync(sourcePath, _metadata, cancellationToken, coverOverridePath);
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(
                () => WpdKindleAccess.SendBook(device, sourcePath, thumbnail, progress, cancellationToken),
                cancellationToken);
            return;
        }
        var documents = GetDocumentsRoot(device);
        Directory.CreateDirectory(documents);
        var fileName = KindleTransferPolicy.CreateSafeFileName(
            Path.GetFileNameWithoutExtension(sourcePath),
            Path.GetExtension(sourcePath));
        // Re-sending a book replaces the existing copy instead of creating a
        // "title (2).azw3" duplicate, so updated covers and metadata actually
        // reach the book's existing Kindle entry.
        var destination = Path.Combine(documents, fileName);
        var temporary = destination + ".kkindle-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            await CopyAsync(sourcePath, temporary, total, progress, cancellationToken);
            var hash = await Hashing.Sha256Async(temporary, cancellationToken);
            if (!string.Equals(hash, bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备上的文件未被替换。");
            File.Move(temporary, destination, true);
            if (thumbnail is not null)
            {
                progress?.Report(new TransferProgress(total, total, "正在同步 Kindle 书架封面"));
                await WriteMassStorageThumbnailAsync(device, thumbnail, cancellationToken);
            }
        }
        finally
        {
            await TryDeleteAsync(temporary);
        }
    }

    private static async Task WriteMassStorageThumbnailAsync(
        KindleDevice device,
        KindleThumbnail thumbnail,
        CancellationToken cancellationToken)
    {
        var deviceRoot = Path.GetFullPath(device.RootPath);
        var thumbnailDirectory = Path.GetFullPath(Path.Combine(deviceRoot, "system", "thumbnails"));
        EnsureUnderRoot(thumbnailDirectory, deviceRoot);
        Directory.CreateDirectory(thumbnailDirectory);
        var target = Path.GetFullPath(Path.Combine(thumbnailDirectory, thumbnail.FileName));
        EnsureUnderRoot(target, thumbnailDirectory);
        var temporary = target + ".kkindle-part";
        try
        {
            await File.WriteAllBytesAsync(temporary, thumbnail.JpegBytes, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            await TryDeleteAsync(temporary);
        }
    }

    public async Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (device.Transport == KindleTransport.Wpd)
        {
            progress?.Report(new TransferProgress(0, book.Size, $"正在从 Kindle 读取 {book.FileName}"));
            var exportedPath = await Task.Run(
                () => WpdKindleAccess.CopyBookToLocal(device, book, destinationRoot, cancellationToken),
                cancellationToken);
            progress?.Report(new TransferProgress(book.Size, book.Size, $"已读取 {book.FileName}"));
            return exportedPath;
        }

        var documents = GetDocumentsRoot(device);
        var sourcePath = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
        EnsureUnderRoot(sourcePath, documents);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Kindle 书籍不存在。", book.RelativePath);

        var destinationPath = GetUniqueDestination(destinationRoot, GetSafeFileName(book.FileName));
        var temporaryPath = destinationPath + ".kkindle-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            await CopyAsync(sourcePath, temporaryPath, total, progress, cancellationToken, "正在导出");
            File.Move(temporaryPath, destinationPath, true);
            return destinationPath;
        }
        finally
        {
            await TryDeleteAsync(temporaryPath);
        }
    }

    public async Task RemoveBookAsync(
        KindleDevice device,
        KindleBook book,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.RemoveBook(device, book, cancellationToken), cancellationToken);
            return;
        }

        var documents = GetDocumentsRoot(device);
        var path = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
        EnsureUnderRoot(path, documents);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 书籍不存在。", book.RelativePath);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不能删除链接形式的设备文件。");

        File.Delete(path);
    }

    public async Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default)
    {
        if (device.Transport == KindleTransport.Wpd)
            return await Task.Run(() => WpdKindleAccess.ScanResources(device, kind, cancellationToken), cancellationToken);

        var root = GetResourceRoot(device, kind);
        if (!Directory.Exists(root)) return [];
        var resources = new List<KindleDeviceResource>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!KindleResourcePolicy.IsSupportedFile(kind, path)) continue;
            try
            {
                var info = new FileInfo(path);
                resources.Add(new KindleDeviceResource
                {
                    Kind = kind,
                    RelativePath = Path.GetRelativePath(device.RootPath, path),
                    Size = info.Length,
                    Sha256 = await Hashing.Sha256Async(path, cancellationToken),
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return resources.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task SendResourceAsync(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!KindleResourcePolicy.IsSupportedFile(kind, sourcePath))
            throw new InvalidDataException(kind == KindleResourceKind.Font
                ? "Kindle 字体仅支持 TTF 和 OTF。"
                : "Kindle 字典仅支持 AZW、AZW3、MOBI、PRC 和 KFX。");
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.SendResource(device, kind, sourcePath, progress, cancellationToken), cancellationToken);
            return;
        }

        var root = GetResourceRoot(device, kind);
        Directory.CreateDirectory(root);
        var fileName = GetSafeFileName(Path.GetFileName(sourcePath));
        var destination = GetUniqueDestination(root, fileName);
        // A unique temporary name prevents a stale partial file from a
        // previous interrupted transfer from blocking the next import.
        var temporary = $"{destination}.{Guid.NewGuid():N}.kkindle-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            // Hash the exact bytes while the source stream is already open.
            // Re-opening a dragged font here races Explorer/font preview and
            // was the cause of the sharing-violation error shown in the UI.
            var sourceHash = await CopyAndHashAsync(sourcePath, temporary, total, progress, cancellationToken);
            var targetHash = await HashFileAsync(temporary, cancellationToken);
            if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，Kindle 资源未写入。");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
        }
        finally
        {
            await TryDeleteAsync(temporary);
        }
    }

    public async Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!KindleResourcePolicy.TryGetPathWithinRoot(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("导出路径无效。"));
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.CopyResourceToLocal(device, resource, destination, cancellationToken), cancellationToken);
            return;
        }

        var root = GetResourceRoot(device, resource.Kind);
        var source = Path.GetFullPath(Path.Combine(device.RootPath, resource.RelativePath));
        EnsureUnderRoot(source, root);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不允许读取设备目录中的链接文件。");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    public async Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!KindleResourcePolicy.TryGetPathWithinRoot(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.RemoveResource(device, resource, cancellationToken), cancellationToken);
            return;
        }
        var root = GetResourceRoot(device, resource.Kind);
        var path = Path.GetFullPath(Path.Combine(device.RootPath, resource.RelativePath));
        EnsureUnderRoot(path, root);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不允许删除设备目录中的链接文件。");
        if (File.Exists(path)) await DeleteFileAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue)
    {
        string text;
        if (device.Transport == KindleTransport.Wpd)
            text = await Task.Run(() => WpdKindleAccess.ReadClippingsText(device, cancellationToken), cancellationToken);
        else
        {
            var path = GetClippingsPath(device);
            if (!File.Exists(path)) return [];
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            if (maxItems != int.MaxValue)
                return await KindleClippingsParser.ParseAsync(reader, maxItems, cancellationToken);
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        return KindleClippingsParser.Parse(text, maxItems);
    }

    public Task DeleteClippingAsync(
        KindleDevice device,
        string clippingId,
        CancellationToken cancellationToken = default)
        => DeleteClippingsAsync(device, [clippingId], cancellationToken);

    public async Task DeleteClippingsAsync(
        KindleDevice device,
        IReadOnlyCollection<string> clippingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        if (clippingIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Kindle 笔记标识无效。", nameof(clippingIds));
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
        {
            var current = await Task.Run(() => WpdKindleAccess.ReadClippingsText(device, cancellationToken), cancellationToken);
            var clippings = KindleClippingsParser.Parse(current);
            EnsureClippingsExist(clippings, ids);
            var updated = KindleClippingsParser.BuildDocument(clippings.Where(item => !ids.Contains(item.Id)));
            await Task.Run(() => WpdKindleAccess.ReplaceClippingsText(device, updated, cancellationToken), cancellationToken);
            return;
        }

        var path = GetClippingsPath(device);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。", path);
        string currentText;
        using (var reader = new StreamReader(path, Encoding.UTF8, true))
            currentText = await reader.ReadToEndAsync(cancellationToken);
        var currentClippings = KindleClippingsParser.Parse(currentText);
        EnsureClippingsExist(currentClippings, ids);
        var updatedText = KindleClippingsParser.BuildDocument(currentClippings.Where(item => !ids.Contains(item.Id)));
        var temporary = path + ".kkindle-part";
        var backup = path + ".kkindle-backup";
        try
        {
            File.Copy(path, backup, true);
            await File.WriteAllTextAsync(temporary, updatedText, new UTF8Encoding(true), cancellationToken);
            File.Move(temporary, path, true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup)) File.Copy(backup, path, true);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    private static void EnsureClippingsExist(IReadOnlyList<KindleClipping> clippings, HashSet<string> ids)
    {
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个 Kindle 划线笔记不存在。");
    }

    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
            return Task.Run(() => WpdKindleAccess.CloseDeviceSession(device, cancellationToken), cancellationToken);
        return Task.Run(() => EjectDrive(device.RootPath, cancellationToken), cancellationToken);
    }

    private static string GetDocumentsRoot(KindleDevice device)
    {
        var root = Path.GetFullPath(device.RootPath);
        var documents = Path.GetFullPath(Path.Combine(root, "documents"));
        EnsureUnderRoot(documents, root);
        return documents;
    }

    private static string GetResourceRoot(KindleDevice device, KindleResourceKind kind)
    {
        var deviceRoot = Path.GetFullPath(device.RootPath);
        var resourceRoot = Path.GetFullPath(Path.Combine(deviceRoot, KindleResourcePolicy.RootRelativePath(kind)));
        EnsureUnderRoot(resourceRoot, deviceRoot);
        if (Directory.Exists(resourceRoot) && (File.GetAttributes(resourceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Kindle 资源目录不能是链接或联接点。");
        return resourceRoot;
    }

    private static string GetClippingsPath(KindleDevice device)
    {
        var documents = GetDocumentsRoot(device);
        var path = Path.GetFullPath(Path.Combine(documents, "My Clippings.txt"));
        EnsureUnderRoot(path, documents);
        return path;
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("设备路径不在允许的目录范围内。");
    }

    private static async Task CopyAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action = "正在发送")
    {
        await CopyCoreAsync(source, target, total, progress, cancellationToken, action, hash: null);
    }

    private static async Task<string> CopyAndHashAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action = "正在发送")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyCoreAsync(source, target, total, progress, cancellationToken, action, hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyCoreAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action,
        IncrementalHash? hash)
    {
        await using var input = await OpenFileStreamAsync(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        await using var output = await OpenFileStreamAsync(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash?.AppendData(buffer, 0, read);
            copied += read;
            progress?.Report(new TransferProgress(copied, total, $"{action} {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenFileStreamAsync(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<FileStream> OpenFileStreamAsync(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        CancellationToken cancellationToken)
    {
        IOException? lastSharingException = null;
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, mode, access, share, 128 * 1024, options);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                lastSharingException = exception;
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }

        throw lastSharingException ?? new IOException($"无法打开传输文件：{path}");
    }

    private static async Task MoveFileAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task TryDeleteAsync(string path)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException exception)
            {
                // Cleanup must never replace the real transfer exception. A
                // just-created file can briefly be scanned by Explorer or an
                // antivirus process, so give that handle time to close.
                if (!IsSharingViolation(exception) || attempt + 1 >= FileOperationRetryAttempts) return;
                await Task.Delay(FileOperationRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33;
    }

    private static string GetUniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string GetSafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "book.bin" : value;
    }

    private static string GetVolumeSerial(string root)
    {
        return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0)
            ? serial.ToString("X8")
            : root.TrimEnd('\\');
    }

    private static void EjectDrive(string root, CancellationToken cancellationToken)
    {
        var driveLetter = root.TrimEnd('\\').TrimEnd('/');
        if (driveLetter.Length < 2) throw new InvalidOperationException("无法确定设备盘符。");
        var handle = CreateFile(
            $@"\\.\{driveLetter}",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw CreateEjectIOException("无法打开 Kindle 卷", Marshal.GetLastWin32Error());

        try
        {
            // A volume can be briefly busy while Explorer closes enumeration
            // handles. Locking also flushes cached writes before removal.
            var locked = false;
            var lockError = 0;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                locked = DeviceIoControl(
                    handle,
                    FsctlLockVolume,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);
                if (locked) break;
                lockError = Marshal.GetLastWin32Error();
                Thread.Sleep(150);
            }
            if (!locked)
                throw CreateEjectIOException("Kindle 正在被程序占用，无法锁定卷", lockError);

            if (!DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw CreateEjectIOException("无法卸载 Kindle 卷", Marshal.GetLastWin32Error());

            // Clear any removable-media lock before asking the storage stack
            // to eject. Some USB mass-storage drivers require this explicitly.
            var allowRemoval = (byte)0;
            DeviceIoControl(
                handle,
                IoctlStorageMediaRemoval,
                ref allowRemoval,
                1,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);

            if (!DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw CreateEjectIOException("Windows 存储驱动拒绝弹出 Kindle", Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(handle); }
    }

    private static IOException CreateEjectIOException(string operation, int errorCode)
    {
        var detail = errorCode == 0 ? "未知 Windows 错误" : new Win32Exception(errorCode).Message;
        return new IOException($"{operation}：{detail}（错误 {errorCode}）。");
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804;
    private const uint IoctlStorageEjectMedia = 0x002D4808;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string? rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint controlCode, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint controlCode, ref byte input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
