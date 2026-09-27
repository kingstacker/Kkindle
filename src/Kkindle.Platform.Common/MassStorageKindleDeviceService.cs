using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Platform.Common;

/// <summary>
/// Cross-platform reader implementation for devices exposed as a mounted
/// filesystem. Windows keeps its WPD implementation for MTP-only devices.
/// </summary>
public sealed class MassStorageKindleDeviceService : IKindleDeviceService
{
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private const int FileOperationRetryAttempts = 30;
    private const int FileOperationRetryDelayMilliseconds = 100;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".prc", ".kfx"
    };

    private readonly IMetadataService _metadata;
    private readonly IBookFormatConverter _formatConverter;
    private readonly string _coverCacheDirectory;
    private readonly IReadOnlyList<string> _mountRoots;
    private readonly Func<KindleDevice, CancellationToken, Task> _eject;

    public MassStorageKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        IEnumerable<string>? mountRoots = null,
        Func<KindleDevice, CancellationToken, Task>? eject = null,
        IBookFormatConverter? formatConverter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _coverCacheDirectory = Path.Combine(paths.Covers, "kindle");
        Directory.CreateDirectory(_coverCacheDirectory);
        _mountRoots = (mountRoots ?? DefaultMountRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        _eject = eject ?? ((_, _) => throw new PlatformNotSupportedException("This platform head has no safe-eject implementation."));
    }

    public Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<KindleDevice>>(() => DetectDevices(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        ScanBooksProgressivelyAsync(device, null, cancellationToken);

    public async Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        var documents = GetDocumentsRoot(device);
        if (!Directory.Exists(documents)) return [];

        var books = new List<KindleBook>();
        foreach (var path in EnumerateFiles(documents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var withinDocuments = Path.GetRelativePath(documents, path);
            if (!device.Profile.IsBookPath(withinDocuments)) continue;
            try
            {
                var info = new FileInfo(path);
                var book = new KindleBook
                {
                    RelativePath = Path.GetRelativePath(device.RootPath, path),
                    Format = info.Extension.TrimStart('.').ToLowerInvariant(),
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc
                };
                SetFallbackMetadata(book);
                books.Add(book);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        progress?.Report(new KindleScanProgress(
            KindleScanStage.Enumerated,
            books.Select(CloneBook).ToArray(),
            [],
            0,
            books.Count));

        var visible = new List<KindleBook>(books.Count);
        var processed = 0;
        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveWithinRoot(device.RootPath, book.RelativePath, documents);
            var isDictionary = false;
            try
            {
                isDictionary = await KindleBookClassifier.IsDictionaryAsync(source, cancellationToken);
                if (!isDictionary)
                {
                    book.Sha256 = await Hashing.Sha256Async(source, cancellationToken);
                    await EnrichBookAsync(device, book, source, cancellationToken);
                    visible.Add(book);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                visible.Add(book);
            }

            processed++;
            progress?.Report(new KindleScanProgress(
                KindleScanStage.Enriched,
                isDictionary ? [] : [CloneBook(book)],
                isDictionary ? [book.RelativePath] : [],
                processed,
                books.Count));
        }
        return visible;
    }

    public async Task SendBookAsync(
        KindleDevice device,
        BookFile bookFile,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? coverOverridePath = null)
    {
        EnsureMassStorage(device);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        string? convertedDirectory = null;
        try
        {
            if (DeviceTransferPolicy.RequiresKindleConversion(device.Profile, bookFile))
            {
                convertedDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "usb-send", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(convertedDirectory);
                var displayTitle = Path.GetFileNameWithoutExtension(sourcePath);
                var convertedPath = Path.Combine(
                    convertedDirectory,
                    KindleTransferPolicy.CreateSafeFileName(displayTitle, ".azw3"));
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    convertedPath,
                    cancellationToken: cancellationToken,
                    metadata: new FormatConversionMetadata(displayTitle, string.Empty, coverOverridePath));
                var convertedInfo = new FileInfo(convertedPath);
                bookFile = new BookFile
                {
                    Id = bookFile.Id,
                    BookId = bookFile.BookId,
                    Format = "azw3",
                    RelativePath = Path.GetFileName(convertedPath),
                    Size = convertedInfo.Length,
                    Sha256 = await Hashing.Sha256Async(convertedPath, cancellationToken)
                };
                sourcePath = convertedPath;
            }

        if (!device.Profile.SupportsBookFile(sourcePath))
            throw new NotSupportedException(UiText.Get("当前设备不支持此书籍格式。"));
        var documents = GetDocumentsRoot(device);
        Directory.CreateDirectory(documents);
        var fileName = KindleTransferPolicy.CreateSafeFileName(
            Path.GetFileNameWithoutExtension(sourcePath),
            Path.GetExtension(sourcePath),
            bookFile.Id);
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
            if (!string.IsNullOrWhiteSpace(bookFile.Sha256)
                && !hash.Equals(bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备上的文件未被替换。");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);

            var thumbnail = device.Profile.UsesKindleThumbnails
                ? await KindleThumbnailService.CreateAsync(sourcePath, _metadata, cancellationToken, coverOverridePath)
                : null;
            if (thumbnail is not null)
                await WriteThumbnailAsync(device, thumbnail, cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
        }
        finally
        {
            if (convertedDirectory is not null)
            {
                try { Directory.Delete(convertedDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath)) throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveWithinRoot(device.RootPath, book.RelativePath, GetDocumentsRoot(device));
        RejectLink(path);
        if (!File.Exists(path)) throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
        File.Delete(path);
        await Task.CompletedTask;
    }

    public async Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath)) throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        EnsureMassStorage(device);
        var source = ResolveWithinRoot(device.RootPath, book.RelativePath, GetDocumentsRoot(device));
        RejectLink(source);
        if (!File.Exists(source)) throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destination = GetUniqueDestination(destinationRoot, SafeFileName(book.FileName));
        var temporary = destination + ".kkindle-part";
        try
        {
            await CopyAsync(source, temporary, new FileInfo(source).Length, progress, cancellationToken, "正在导出");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
            return destination;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!device.Profile.SupportsResource(kind)) return [];
        var root = GetResourceRoot(device, kind);
        if (!Directory.Exists(root)) return [];
        var resources = new List<KindleDeviceResource>();
        foreach (var path in EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!device.Profile.SupportsResourceFile(kind, path)) continue;
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
        EnsureMassStorage(device);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath))
            throw new InvalidDataException(UiText.Get("当前设备不支持此资源格式。"));
        var root = GetResourceRoot(device, kind);
        Directory.CreateDirectory(root);
        var destination = GetUniqueDestination(root, SafeFileName(Path.GetFileName(sourcePath)));
        var temporary = $"{destination}.{Guid.NewGuid():N}.kkindle-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            var sourceHash = await CopyAndHashAsync(sourcePath, temporary, total, progress, cancellationToken);
            var targetHash = await HashFileAsync(temporary, cancellationToken);
            if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备资源未写入。");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var source = ResolveWithinRoot(device.RootPath, resource.RelativePath, GetResourceRoot(device, resource.Kind));
        RejectLink(source);
        if (!File.Exists(source)) throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("导出路径无效。"));
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    public async Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var path = ResolveWithinRoot(device.RootPath, resource.RelativePath, GetResourceRoot(device, resource.Kind));
        RejectLink(path);
        if (File.Exists(path)) await DeleteFileAsync(path, cancellationToken);
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue)
    {
        EnsureMassStorage(device);
        if (!device.Profile.SupportsClippings)
            return device.Profile.SupportsKoboNotes
                ? await KoboNotesReader.ReadAsync(Path.Combine(device.RootPath, ".kobo", "KoboReader.sqlite"), maxItems, cancellationToken)
                : [];
        var path = GetClippingsPath(device);
        if (!File.Exists(path)) return [];
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        return await KindleClippingsParser.ParseAsync(reader, maxItems, cancellationToken);
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
        EnsureMassStorage(device);
        if (!device.Profile.CanDeleteNotes)
            throw new NotSupportedException(UiText.Get("当前设备的笔记仅支持读取和导出。"));
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        if (clippingIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("设备笔记标识无效。", nameof(clippingIds));
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        var path = GetClippingsPath(device);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。", path);
        string currentText;
        using (var reader = new StreamReader(path, Encoding.UTF8, true))
            currentText = await reader.ReadToEndAsync(cancellationToken);
        var clippings = KindleClippingsParser.Parse(currentText);
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个设备划线笔记不存在。");
        var updated = KindleClippingsParser.BuildDocument(clippings.Where(item => !ids.Contains(item.Id)));
        var temporary = path + ".kkindle-part";
        var backup = path + ".kkindle-backup";
        try
        {
            File.Copy(path, backup, true);
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(true), cancellationToken);
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
            TryDelete(temporary);
            TryDelete(backup);
        }
    }

    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        return _eject(device, cancellationToken);
    }

    private IReadOnlyList<KindleDevice> DetectDevices(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(PathComparer);
        foreach (var mountRoot in _mountRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(mountRoot)) continue;
            if (ReaderDeviceProfiles.DetectMounted(mountRoot) is not null) roots.Add(mountRoot);
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(mountRoot))
                    if (ReaderDeviceProfiles.DetectMounted(directory) is not null) roots.Add(Path.GetFullPath(directory));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady) continue;
                var root = Path.GetFullPath(drive.RootDirectory.FullName);
                var knownMount = _mountRoots.Any(mount => IsWithinOrEqual(root, mount));
                if ((drive.DriveType == DriveType.Removable || knownMount)
                    && ReaderDeviceProfiles.DetectMounted(root, drive.VolumeLabel) is not null) roots.Add(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return roots.Select(CreateDevice).OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static KindleDevice CreateDevice(string root)
    {
        var name = new DirectoryInfo(root).Name;
        long total = 0;
        long free = 0;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) name = drive.VolumeLabel;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var profile = ReaderDeviceProfiles.DetectMounted(root, name);
        if (profile is null || profile.Family == ReaderDeviceFamily.Generic)
            profile = ReaderDeviceProfiles.DetectMounted(root) ?? profile;
        return new KindleDevice
        {
            Profile = profile ?? throw new IOException("设备书籍目录已不可用。"),
            RootPath = Path.GetFullPath(root),
            VolumeSerial = Path.GetFullPath(root),
            Name = string.IsNullOrWhiteSpace(name) ? "阅读设备" : name,
            TotalBytes = total,
            FreeBytes = free,
            IsReady = true,
            Transport = KindleTransport.MassStorage
        };
    }

    private async Task EnrichBookAsync(KindleDevice device, KindleBook book, string source, CancellationToken cancellationToken)
    {
        if (book.Size <= 0 || book.Size > MaximumMetadataFileSize) return;
        try
        {
            var metadata = await _metadata.ReadMetadataAsync(source, cancellationToken);
            if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
            if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
            var coverBytes = metadata.CoverBytes;
            var coverExtension = metadata.CoverExtension;
            if (device.Profile.UsesKindleThumbnails && coverBytes is not { Length: > 0 })
            {
                // Some Kindle books keep the cover only in the device
                // thumbnail cache (for example encrypted KFX files).
                var thumbnailName = await KindleThumbnailService.ReadThumbnailFileNameAsync(source, cancellationToken);
                if (!string.IsNullOrWhiteSpace(thumbnailName))
                {
                    var thumbnailPath = ResolveWithinRoot(
                        device.RootPath,
                        Path.Combine("system", "thumbnails", thumbnailName),
                        device.RootPath);
                    if (File.Exists(thumbnailPath))
                    {
                        coverBytes = await File.ReadAllBytesAsync(thumbnailPath, cancellationToken);
                        coverExtension = ".jpg";
                    }
                }
            }

            if (coverBytes is { Length: > 0 })
            {
                var extension = coverExtension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                var identity = $"{device.Identity}\n{book.RelativePath}\n{book.Size}\n{book.ModifiedAt?.UtcTicks ?? 0}";
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                var coverPath = Path.Combine(_coverCacheDirectory, key + extension);
                await File.WriteAllBytesAsync(coverPath, coverBytes, cancellationToken);
                book.CoverPath = coverPath;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
    }

    private static async Task WriteThumbnailAsync(KindleDevice device, KindleThumbnail thumbnail, CancellationToken cancellationToken)
    {
        var deviceRoot = Path.GetFullPath(device.RootPath);
        var directory = Path.GetFullPath(Path.Combine(deviceRoot, "system", "thumbnails"));
        EnsureUnderRoot(directory, deviceRoot);
        Directory.CreateDirectory(directory);
        var target = ResolveWithinRoot(directory, thumbnail.FileName, directory);
        var temporary = target + ".kkindle-part";
        try
        {
            await File.WriteAllBytesAsync(temporary, thumbnail.JpegBytes, cancellationToken);
            File.Move(temporary, target, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*", options);
    }

    private static string GetDocumentsRoot(KindleDevice device)
    {
        return ReaderDevicePaths.ResolveDirectory(device.RootPath, device.Profile.BooksDirectory);
    }

    private static string GetResourceRoot(KindleDevice device, KindleResourceKind kind)
    {
        var resourceRoot = ReaderDevicePaths.ResolveDirectory(device.RootPath, device.Profile.ResourceDirectory(kind));
        if (Directory.Exists(resourceRoot)) RejectLink(resourceRoot);
        return resourceRoot;
    }

    private static string GetClippingsPath(KindleDevice device) =>
        ResolveWithinRoot(GetDocumentsRoot(device), "My Clippings.txt", GetDocumentsRoot(device));

    private static string ResolveWithinRoot(string basePath, string relativePath, string allowedRoot)
    {
        var path = Path.GetFullPath(Path.Combine(basePath, relativePath));
        EnsureUnderRoot(path, allowedRoot);
        RejectLinksInPath(path, allowedRoot);
        return path;
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("设备路径不在允许的目录范围内。");
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不允许操作设备目录中的链接。");
    }

    private static void RejectLinksInPath(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root);
        RejectLink(normalizedRoot);
        var relative = Path.GetRelativePath(normalizedRoot, Path.GetFullPath(path));
        if (relative == ".") return;
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectLink(current);
        }
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

    private static string SafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "book.bin" : value;
    }

    private static void SetFallbackMetadata(KindleBook book)
    {
        var fileName = Path.GetFileNameWithoutExtension(book.RelativePath);
        var separator = fileName.LastIndexOf('_');
        if (separator > 0)
        {
            var suffix = fileName[(separator + 1)..];
            if (suffix.Length == 32 && suffix.All(Uri.IsHexDigit)) fileName = fileName[..separator];
        }
        book.Title = fileName.Replace('_', ' ').Trim();
        book.Authors = "未知作者";
    }

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

    private static bool IsDictionaryPath(string relativePath)
    {
        var separator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var first = separator < 0 ? relativePath : relativePath[..separator];
        return first.Equals("dictionaries", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureMassStorage(KindleDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Transport != KindleTransport.MassStorage)
            throw new NotSupportedException("This service supports mounted USB storage only.");
    }

    private static bool IsWithinOrEqual(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> DefaultMountRoots()
    {
        var user = Environment.UserName;
        if (OperatingSystem.IsMacOS()) return ["/Volumes"];
        if (OperatingSystem.IsLinux()) return [$"/media/{user}", $"/run/media/{user}"];
        return [];
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
