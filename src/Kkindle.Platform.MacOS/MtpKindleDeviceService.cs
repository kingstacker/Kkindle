using System.Diagnostics;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.MacOS.Mtp;

namespace Kkindle.Platform.MacOS;

/// <summary>
/// Kindle devices that expose MTP instead of USB mass storage. macOS has no
/// MTP stack of its own, so this service talks to libmtp (the same library
/// calibre uses on macOS).
/// </summary>
public sealed class MtpKindleDeviceService : IKindleDeviceService
{
    private const ushort AmazonVendorId = 0x1949;
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private static readonly TimeSpan DetectionCacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IMetadataService _metadata;
    private readonly KindleScanCacheStore _scanCache;
    private readonly IBookFormatConverter _formatConverter;
    private readonly string _coverCacheDirectory;
    private readonly object _detectionSync = new();
    private IReadOnlyList<KindleDevice>? _detectionCache;
    private DateTime _detectionCacheAt;

    public MtpKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        IBookFormatConverter? formatConverter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _scanCache = new KindleScanCacheStore(paths);
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _coverCacheDirectory = Path.Combine(paths.Covers, "kindle-mtp");
        Directory.CreateDirectory(_coverCacheDirectory);
    }

    public async Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!MtpDeviceSession.IsSupported) return [];
        cancellationToken.ThrowIfCancellationRequested();
        lock (_detectionSync)
        {
            if (_detectionCache is not null && DateTime.UtcNow - _detectionCacheAt < DetectionCacheLifetime)
                return _detectionCache;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = await Task.Run(() => Detect(cancellationToken), cancellationToken).ConfigureAwait(false);
            lock (_detectionSync)
            {
                _detectionCache = devices;
                _detectionCacheAt = DateTime.UtcNow;
            }
            return devices;
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        _scanCache.GetCachedBooksAsync(device.Identity, cancellationToken);

    public async Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KindleBook>? results = null;
        await ScanBooksProgressivelyAsync(
            device,
            new InlineProgress<KindleScanProgress>(progress =>
            {
                if (progress.Stage == KindleScanStage.Enriched) results = progress.Books;
            }),
            cancellationToken).ConfigureAwait(false);
        return results ?? [];
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public async Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var identity = device.Identity;
        var cached = await _scanCache.GetDeviceEntriesAsync(identity, cancellationToken).ConfigureAwait(false);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(device, cached, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SendBookAsync(
        KindleDevice device,
        BookFile bookFile,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? coverOverridePath = null)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        string? convertedDirectory = null;
        try
        {
            if (DeviceTransferPolicy.RequiresKindleConversion(device.Profile, bookFile))
            {
                convertedDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "mtp-send", Guid.NewGuid().ToString("N"));
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
                bookFile = new BookFile
                {
                    Id = bookFile.Id,
                    BookId = bookFile.BookId,
                    Format = "azw3",
                    RelativePath = Path.GetFileName(convertedPath),
                    Size = new FileInfo(convertedPath).Length,
                    Sha256 = await Hashing.Sha256Async(convertedPath, cancellationToken).ConfigureAwait(false)
                };
                sourcePath = convertedPath;
            }

            if (!device.Profile.SupportsBookFile(sourcePath))
                throw new NotSupportedException(UiText.Get("当前设备不支持此书籍格式。"));

            var fileName = KindleTransferPolicy.CreateSafeFileName(
                Path.GetFileNameWithoutExtension(sourcePath),
                Path.GetExtension(sourcePath),
                bookFile.Id);
            var total = new FileInfo(sourcePath).Length;

            await WithSessionAsync(device, session =>
            {
                var folder = session.EnsureDirectory(device.Profile.BooksDirectory);
                session.Upload(
                    folder.RelativePath,
                    sourcePath,
                    fileName,
                    (sent, size) => progress?.Report(new TransferProgress(sent, size, "正在发送到设备")));
                return true;
            }, cancellationToken).ConfigureAwait(false);
            progress?.Report(new TransferProgress(total, total, "已发送"));
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

    public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath))
            throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        return WithSessionAsync(device, session =>
        {
            var entry = session.Find(book.RelativePath)
                ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
            session.Delete(entry);
            return true;
        }, cancellationToken);
    }

    public async Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath))
            throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destination = UniqueDestination(destinationRoot, SafeFileName(book.FileName));
        var temporary = destination + ".kkindle-part";
        try
        {
            await WithSessionAsync(device, session =>
            {
                var entry = session.Find(book.RelativePath)
                    ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
                session.Download(entry, temporary,
                    (sent, total) => progress?.Report(new TransferProgress(sent, total, "正在导出")));
                return true;
            }, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(kind)) return Task.FromResult<IReadOnlyList<KindleDeviceResource>>([]);
        var directory = device.Profile.ResourceDirectory(kind);
        return WithSessionAsync<IReadOnlyList<KindleDeviceResource>>(device, session =>
        {
            return session.List(directory)
                .Where(entry => !entry.IsFolder
                    && device.Profile.SupportsResourceFile(kind, entry.RelativePath))
                .Select(entry => new KindleDeviceResource
                {
                    Kind = kind,
                    RelativePath = entry.RelativePath,
                    Size = entry.Size,
                    ModifiedAt = entry.ModifiedAt
                })
                .OrderBy(resource => resource.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task SendResourceAsync(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(kind))
            throw new NotSupportedException(UiText.Get("当前设备暂不支持此资源管理功能。"));
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("资源文件不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath))
            throw new NotSupportedException(UiText.Get("当前设备不支持此资源格式。"));
        var directory = device.Profile.ResourceDirectory(kind);
        var fileName = SafeFileName(Path.GetFileName(sourcePath));
        return WithSessionAsync(device, session =>
        {
            session.Upload(directory, sourcePath, fileName,
                (sent, total) => progress?.Report(new TransferProgress(sent, total, "正在发送资源")));
            return true;
        }, cancellationToken);
    }

    public Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        return WithSessionAsync(device, session =>
        {
            var entry = session.Find(resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            session.Download(entry, destination);
            return true;
        }, cancellationToken);
    }

    public Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(resource.Kind))
            throw new NotSupportedException(UiText.Get("当前设备暂不支持此资源管理功能。"));
        return WithSessionAsync(device, session =>
        {
            var entry = session.Find(resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            session.Delete(entry);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue)
    {
        var text = await ReadClippingsTextAsync(device, cancellationToken).ConfigureAwait(false);
        return text is null ? [] : KindleClippingsParser.Parse(text, maxItems);
    }

    public Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default) =>
        DeleteClippingsAsync(device, [clippingId], cancellationToken);

    public async Task DeleteClippingsAsync(
        KindleDevice device,
        IReadOnlyCollection<string> clippingIds,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.CanDeleteNotes)
            throw new NotSupportedException(UiText.Get("当前设备的笔记仅支持读取和导出。"));
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        var text = await ReadClippingsTextAsync(device, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。");
        var clippings = KindleClippingsParser.Parse(text);
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个设备划线笔记不存在。");
        var updated = KindleClippingsParser.BuildDocument(clippings.Where(item => !ids.Contains(item.Id)));
        var temporary = Path.Combine(Path.GetTempPath(), "Kkindle", $"clippings-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
            var relativePath = $"{device.Profile.BooksDirectory}/My Clippings.txt";
            await WithSessionAsync(device, session =>
            {
                session.Upload(
                    device.Profile.BooksDirectory,
                    temporary,
                    "My Clippings.txt");
                return true;
            }, cancellationToken).ConfigureAwait(false);
            _ = relativePath;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        // MTP sessions are opened per operation and released immediately; there
        // is nothing left open to eject. libmtp closes the PTP session on
        // release, which is all macOS needs before the cable is unplugged.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private IReadOnlyList<KindleDevice> Detect(CancellationToken cancellationToken)
    {
        var devices = new List<KindleDevice>();
        foreach (var raw in MtpDeviceSession.Detect())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (raw.VendorId != AmazonVendorId) continue;
            try
            {
                using var session = MtpDeviceSession.Open(raw);
                var serial = string.IsNullOrWhiteSpace(session.SerialNumber)
                    ? $"{session.Model}"
                    : session.SerialNumber.Trim();
                devices.Add(new KindleDevice
                {
                    Profile = ReaderDeviceProfiles.Kindle,
                    RootPath = $"mtp://{raw.BusLocation}/{raw.DevNum}",
                    VolumeSerial = $"mtp:{serial}",
                    Name = session.Model,
                    TotalBytes = session.PrimaryStorage.TotalBytes,
                    FreeBytes = session.PrimaryStorage.FreeBytes,
                    IsReady = true,
                    Transport = KindleTransport.Mtp
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                Debug.WriteLine($"MTP device {raw.VendorId:X4}:{raw.ProductId:X4} unavailable: {exception.Message}");
            }
        }
        return devices;
    }

    private IReadOnlyList<KindleBook> Scan(
        KindleDevice device,
        IReadOnlyDictionary<string, KindleScanCacheEntry> cached,
        IProgress<KindleScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var session = OpenSession(device);
        var documents = device.Profile.BooksDirectory;
        var books = new List<KindleBook>();
        foreach (var entry in session.Enumerate(documents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsFolder || !device.Profile.IsBookPath(entry.RelativePath)) continue;
            var book = new KindleBook
            {
                RelativePath = entry.RelativePath,
                Format = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant(),
                Size = entry.Size,
                ModifiedAt = entry.ModifiedAt,
                IsManagedByKkindle = false
            };
            SetFallbackMetadata(book);
            books.Add(book);
        }

        progress?.Report(new KindleScanProgress(
            KindleScanStage.Enumerated,
            books.Select(CloneBook).ToArray(),
            [],
            0,
            books.Count));

        var entries = new List<KindleScanCacheEntry>(books.Count);
        var visible = new List<KindleBook>(books.Count);
        var changed = new List<KindleBook>(8);
        var removed = new List<string>(8);
        var processed = 0;
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "mtp-scan");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var book in books)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDictionary = false;
                try
                {
                    if (cached.TryGetValue(book.RelativePath.Replace('/', '\\').TrimStart('\\'), out var entry)
                        && entry.Matches(book.Size, book.ModifiedAt)
                        && (entry.CoverPath is null || File.Exists(entry.CoverPath)))
                    {
                        isDictionary = entry.IsDictionary;
                        book.Format = entry.Format;
                        book.Title = entry.Title;
                        book.Authors = entry.Authors;
                        book.Sha256 = entry.Sha256;
                        book.CoverPath = entry.CoverPath;
                    }
                    else
                    {
                        var remote = session.Find(book.RelativePath);
                        if (remote is not null && book.Size <= MaximumMetadataFileSize)
                        {
                            var staged = Path.Combine(
                                stagingDirectory,
                                $"{Guid.NewGuid():N}{Path.GetExtension(book.FileName)}");
                            try
                            {
                                session.Download(remote, staged);
                                isDictionary = KindleBookClassifier.IsDictionaryAsync(staged, cancellationToken)
                                    .GetAwaiter().GetResult();
                                if (!isDictionary)
                                {
                                    book.Sha256 = Hashing.Sha256Async(staged, cancellationToken)
                                        .GetAwaiter().GetResult();
                                    var metadata = _metadata.ReadMetadataAsync(staged, cancellationToken)
                                        .GetAwaiter().GetResult();
                                    if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
                                    if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
                                    book.CoverPath = WriteCover(book.Sha256, metadata);
                                }
                            }
                            finally
                            {
                                TryDelete(staged);
                            }
                        }
                    }

                    entries.Add(new KindleScanCacheEntry
                    {
                        DeviceIdentity = device.Identity,
                        RelativePath = book.RelativePath,
                        Size = book.Size,
                        ModifiedAt = book.ModifiedAt,
                        Format = book.Format,
                        Title = book.Title,
                        Authors = book.Authors,
                        Sha256 = book.Sha256,
                        CoverPath = book.CoverPath,
                        IsDictionary = isDictionary
                    });
                    if (!isDictionary) visible.Add(book);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    visible.Add(book);
                }

                processed++;
                if (isDictionary) removed.Add(book.RelativePath);
                else changed.Add(CloneBook(book));
                if (changed.Count + removed.Count >= 8 || processed == books.Count)
                {
                    progress?.Report(new KindleScanProgress(
                        KindleScanStage.Enriched,
                        changed.ToArray(),
                        removed.ToArray(),
                        processed,
                        books.Count));
                    changed.Clear();
                    removed.Clear();
                }
            }

            try
            {
                _scanCache.ReplaceDeviceEntriesAsync(device.Identity, entries, cancellationToken)
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

            return visible;
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<T> WithSessionAsync<T>(
        KindleDevice device,
        Func<MtpDeviceSession, T> action,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var session = OpenSession(device);
                return action(session);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static MtpDeviceSession OpenSession(KindleDevice device)
    {
        var raw = MtpDeviceSession.Detect().FirstOrDefault(candidate =>
            candidate.VendorId == AmazonVendorId
            && device.RootPath == $"mtp://{candidate.BusLocation}/{candidate.DevNum}");
        if (raw is null)
        {
            // Bus/device numbers change across replugs; fall back to the first
            // Amazon device so the transfer still reaches a single connected Kindle.
            raw = MtpDeviceSession.Detect().FirstOrDefault(candidate => candidate.VendorId == AmazonVendorId);
        }
        if (raw is null) throw new IOException("Kindle 设备已断开，请重新连接后再试。");
        return MtpDeviceSession.Open(raw);
    }

    private async Task<string?> ReadClippingsTextAsync(KindleDevice device, CancellationToken cancellationToken)
    {
        var relativePath = $"{device.Profile.BooksDirectory}/My Clippings.txt";
        var temporary = Path.Combine(Path.GetTempPath(), "Kkindle", $"clippings-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            var found = await WithSessionAsync(device, session =>
            {
                var entry = session.Find(relativePath);
                if (entry is null) return false;
                session.Download(entry, temporary);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            if (!found) return null;
            return await File.ReadAllTextAsync(temporary, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private string? WriteCover(string sha256, BookMetadata metadata)
    {
        var coverBytes = metadata.CoverBytes;
        if (coverBytes is not { Length: > 0 }) return null;
        var extension = string.IsNullOrWhiteSpace(metadata.CoverExtension) ? ".jpg" : metadata.CoverExtension;
        var name = string.IsNullOrWhiteSpace(sha256) ? Guid.NewGuid().ToString("N") : sha256;
        var path = Path.Combine(_coverCacheDirectory, $"{name}{extension}");
        try
        {
            if (!File.Exists(path)) File.WriteAllBytes(path, coverBytes);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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
        var title = fileName.Replace('_', ' ').Trim();
        book.Title = title.Length == 0 ? "未命名书籍" : title;
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

    private static string SafeFileName(string name) =>
        new(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());

    private static string UniqueDestination(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("目标目录中同名文件过多。");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
