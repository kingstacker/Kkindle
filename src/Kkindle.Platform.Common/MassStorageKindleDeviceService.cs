using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Platform.Common;

/// <summary>
/// Cross-platform Kindle implementation for devices exposed as a mounted
/// filesystem. Windows keeps its WPD implementation for MTP-only devices.
/// </summary>
public sealed class MassStorageKindleDeviceService : IKindleDeviceService
{
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".prc", ".kfx"
    };

    private readonly IMetadataService _metadata;
    private readonly string _coverCacheDirectory;
    private readonly IReadOnlyList<string> _mountRoots;
    private readonly Func<KindleDevice, CancellationToken, Task> _eject;

    public MassStorageKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        IEnumerable<string>? mountRoots = null,
        Func<KindleDevice, CancellationToken, Task>? eject = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
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
            if (IsDictionaryPath(withinDocuments) || !SupportedExtensions.Contains(Path.GetExtension(path))) continue;
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
            if (!string.IsNullOrWhiteSpace(bookFile.Sha256)
                && !hash.Equals(bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备上的文件未被替换。");
            File.Move(temporary, destination, true);

            var thumbnail = await KindleThumbnailService.CreateAsync(sourcePath, _metadata, cancellationToken, coverOverridePath);
            if (thumbnail is not null)
                await WriteThumbnailAsync(device, thumbnail, cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveWithinRoot(device.RootPath, book.RelativePath, GetDocumentsRoot(device));
        RejectLink(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 书籍不存在。", book.RelativePath);
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
        EnsureMassStorage(device);
        var source = ResolveWithinRoot(device.RootPath, book.RelativePath, GetDocumentsRoot(device));
        RejectLink(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Kindle 书籍不存在。", book.RelativePath);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destination = GetUniqueDestination(destinationRoot, SafeFileName(book.FileName));
        var temporary = destination + ".kkindle-part";
        try
        {
            await CopyAsync(source, temporary, new FileInfo(source).Length, progress, cancellationToken, "正在导出");
            File.Move(temporary, destination, true);
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
        var root = GetResourceRoot(device, kind);
        if (!Directory.Exists(root)) return [];
        var resources = new List<KindleDeviceResource>();
        foreach (var path in EnumerateFiles(root))
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
        EnsureMassStorage(device);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!KindleResourcePolicy.IsSupportedFile(kind, sourcePath))
            throw new InvalidDataException(kind == KindleResourceKind.Font
                ? "Kindle 字体仅支持 TTF 和 OTF。"
                : "Kindle 字典仅支持 AZW、AZW3、MOBI、PRC 和 KFX。");
        var root = GetResourceRoot(device, kind);
        Directory.CreateDirectory(root);
        var destination = GetUniqueDestination(root, SafeFileName(Path.GetFileName(sourcePath)));
        var temporary = destination + ".kkindle-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            await CopyAsync(sourcePath, temporary, total, progress, cancellationToken);
            var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
            var targetHash = await Hashing.Sha256Async(temporary, cancellationToken);
            if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，Kindle 资源未写入。");
            File.Move(temporary, destination, true);
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
        if (!KindleResourcePolicy.TryGetPathWithinRoot(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var source = ResolveWithinRoot(device.RootPath, resource.RelativePath, GetResourceRoot(device, resource.Kind));
        RejectLink(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Kindle 资源不存在。", resource.RelativePath);
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
        if (!KindleResourcePolicy.TryGetPathWithinRoot(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var path = ResolveWithinRoot(device.RootPath, resource.RelativePath, GetResourceRoot(device, resource.Kind));
        RejectLink(path);
        if (File.Exists(path)) File.Delete(path);
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        var path = GetClippingsPath(device);
        if (!File.Exists(path)) return [];
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        return KindleClippingsParser.Parse(await reader.ReadToEndAsync(cancellationToken));
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
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        if (clippingIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Kindle 笔记标识无效。", nameof(clippingIds));
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        var path = GetClippingsPath(device);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。", path);
        string currentText;
        using (var reader = new StreamReader(path, Encoding.UTF8, true))
            currentText = await reader.ReadToEndAsync(cancellationToken);
        var clippings = KindleClippingsParser.Parse(currentText);
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个 Kindle 划线笔记不存在。");
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
            if (Directory.Exists(Path.Combine(mountRoot, "documents"))) roots.Add(mountRoot);
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(mountRoot))
                    if (Directory.Exists(Path.Combine(directory, "documents"))) roots.Add(Path.GetFullPath(directory));
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
                    && Directory.Exists(Path.Combine(root, "documents"))) roots.Add(root);
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
        return new KindleDevice
        {
            RootPath = Path.GetFullPath(root),
            VolumeSerial = Path.GetFullPath(root),
            Name = string.IsNullOrWhiteSpace(name) ? "Kindle" : name,
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
            if (coverBytes is not { Length: > 0 })
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
        var root = Path.GetFullPath(device.RootPath);
        var documents = Path.GetFullPath(Path.Combine(root, "documents"));
        EnsureUnderRoot(documents, root);
        return documents;
    }

    private static string GetResourceRoot(KindleDevice device, KindleResourceKind kind)
    {
        var root = Path.GetFullPath(device.RootPath);
        var resourceRoot = Path.GetFullPath(Path.Combine(root, KindleResourcePolicy.RootRelativePath(kind)));
        EnsureUnderRoot(resourceRoot, root);
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
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new TransferProgress(copied, total, $"{action} {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
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
