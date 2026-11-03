using System.Diagnostics;
using System.Security.Cryptography;
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
    private const string AnnotationRoot = "system/ksdk/.annotations";
    private const string AnnotationFileName = "ksdk_annotation_v1.db";
    private const string ThumbnailDirectory = "system/thumbnails";
    private static readonly TimeSpan DetectionCacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly AppPaths _paths;
    private readonly IMetadataService _metadata;
    private readonly KindleScanCacheStore _scanCache;
    private readonly IBookFormatConverter _formatConverter;
    private readonly string _coverCacheDirectory;
    private readonly object _detectionSync = new();
    private IReadOnlyList<KindleDevice>? _detectionCache;
    private DateTime _detectionCacheAt;
    private int _detectionGeneration;

    public MtpKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        IBookFormatConverter? formatConverter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
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
            int generation;
            lock (_detectionSync) generation = _detectionGeneration;
            var devices = await Task.Run(() => Detect(cancellationToken), cancellationToken).ConfigureAwait(false);
            lock (_detectionSync)
            {
                if (generation == _detectionGeneration)
                {
                    _detectionCache = devices;
                    _detectionCacheAt = DateTime.UtcNow;
                }
            }
            return devices;
        }
        finally
        {
            Gate.Release();
        }
    }

    public void InvalidateDetectionCache()
    {
        lock (_detectionSync)
        {
            _detectionGeneration++;
            _detectionCache = null;
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
            // Kindle bookshelves use the EXTH ASIN/CDE pair for cover names.
            // Books without an ASIN simply have no device thumbnail.
            var thumbnail = device.Profile.UsesKindleThumbnails
                ? await KindleThumbnailService.CreateAsync(
                    sourcePath, _metadata, cancellationToken, coverOverridePath).ConfigureAwait(false)
                : null;

            await WithSessionAsync(device, session =>
            {
                var folder = session.EnsureDirectory(device.Profile.BooksDirectory);
                var bookPath = $"{folder.RelativePath}/{fileName}";
                var isNewBook = session.Find(bookPath) is null;
                session.Upload(
                    folder.RelativePath,
                    sourcePath,
                    fileName,
                    (sent, size) => progress?.Report(new TransferProgress(sent, size, "正在发送到设备")));
                if (thumbnail is not null)
                {
                    progress?.Report(new TransferProgress(total, total, "正在同步设备书架封面"));
                    UploadThumbnail(session, thumbnail);
                }
                if (isNewBook)
                    DeleteSidecarFolder(session, device.Profile.BooksDirectory, fileName);
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
            var separator = entry.RelativePath.LastIndexOf('/');
            var directory = separator > 0 ? entry.RelativePath[..separator] : string.Empty;
            session.Delete(entry);
            // Reading progress, bookmarks and annotations live in the matching
            // .sdr folder; leaving it behind makes Amazon's device view think
            // the book is still installed.
            DeleteSidecarFolder(session, directory, entry.Name);
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
        if (!device.Profile.SupportsClippings) return [];

        // Account databases carry highlights and notes that never appear in
        // My Clippings.txt; the text file remains the fallback for firmware
        // without a readable database.
        try
        {
            var databaseNotes = await ReadAnnotationDatabaseClippingsAsync(
                device, cancellationToken, maxItems).ConfigureAwait(false);
            if (databaseNotes.Count > 0) return databaseNotes;
        }
        catch (Exception exception) when (IsAnnotationDatabaseFailure(exception))
        {
            Debug.WriteLine($"MTP annotation database unavailable: {exception.Message}");
        }

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
        if (clippingIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("设备笔记标识无效。", nameof(clippingIds));
        // Rows in the account database are read-only here; only the text file
        // can be rewritten safely without a full annotation sync transaction.
        if (clippingIds.Any(id => id.StartsWith("kindle-db:", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException(UiText.Get("Kindle 账户数据库中的批注当前仅支持读取和导出。"));
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
            await WithSessionAsync(device, session =>
            {
                session.Upload(
                    device.Profile.BooksDirectory,
                    temporary,
                    "My Clippings.txt");
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsClippings) return null;
        var stagingDirectory = CreateStagingDirectory("annotation-snapshot");
        try
        {
            var local = await WithSessionAsync(device, session =>
            {
                var database = FindAnnotationDatabases(session).FirstOrDefault();
                if (database is null) return null;
                var destination = Path.Combine(stagingDirectory, AnnotationFileName);
                session.Download(database, destination);
                return new LocalAnnotationDatabase(
                    database.RelativePath.Replace('\\', '/'),
                    destination,
                    FindSdrFolders(session, device));
            }, cancellationToken).ConfigureAwait(false);
            if (local is null) return null;

            var records = await KindleAnnotationDatabaseReader.ReadRecordsAsync(
                local.LocalPath, 100_000, cancellationToken).ConfigureAwait(false);
            var titleResolver = KindleAnnotationBookTitleResolver.Create(local.SdrFolders);
            var snapshotRecords = records.Select(record => new KindleAnnotationRecord(
                record.Id,
                new KindleAnnotationBookIdentity(
                    record.Book.BookId,
                    record.Book.Asin,
                    record.Book.Guid),
                record.Dataset,
                record.Type,
                record.Start,
                record.End,
                record.CreatedAt?.ToUnixTimeMilliseconds() ?? 0,
                record.UpdatedAt?.ToUnixTimeMilliseconds() ?? 0,
                record.Note,
                record.SerializedPayload,
                titleResolver.Resolve(record.Book) ?? string.Empty)).ToArray();
            await using var stream = File.OpenRead(local.LocalPath);
            var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            return new KindleAnnotationSnapshot(local.DatabaseRelativePath, sha256, snapshotRecords);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken = default) =>
        ApplyAnnotationMutationsAsync(device, snapshot, mutations, cancellationToken, null);

    public async Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress)
    {
        progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
        if (!device.Profile.SupportsClippings)
            throw new NotSupportedException(UiText.Get("当前设备暂不支持回写 Kindle 批注。"));
        if (mutations.Count == 0)
            return new KindleAnnotationWriteResult(0, snapshot.Sha256, snapshot.Sha256, null, null);
        ValidateAnnotationDatabasePath(snapshot.DatabaseRelativePath);

        var deviceKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(device.Identity))).ToLowerInvariant()[..16];
        var backupDirectory = Path.Combine(
            _paths.Backups,
            "KindleAnnotations",
            deviceKey,
            DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        var workingDirectory = Path.Combine(backupDirectory, "working");
        Directory.CreateDirectory(workingDirectory);
        var workingPath = Path.Combine(workingDirectory, AnnotationFileName);
        var localBackupPath = Path.Combine(backupDirectory, AnnotationFileName);
        var candidatePath = Path.Combine(backupDirectory, "ksdk_annotation_v1.candidate.db");
        var readbackDirectory = Path.Combine(workingDirectory, "readback");
        Directory.CreateDirectory(readbackDirectory);
        try
        {
            // Snapshot the database again and prove it still matches the scan.
            await WithSessionAsync(device, session =>
            {
                var database = FindAnnotationDatabase(session, snapshot.DatabaseRelativePath)
                    ?? throw new IOException("设备批注数据库路径已经变化。");
                RejectAnnotationSideFiles(session, database);
                session.Download(database, workingPath);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await VerifyHashAsync(
                workingPath, snapshot.Sha256,
                "Kindle 批注数据库在检测后已变化，请重新检测。", cancellationToken).ConfigureAwait(false);
            File.Copy(workingPath, localBackupPath, overwrite: false);

            var newHash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(
                workingPath,
                candidatePath,
                mutations,
                cancellationToken,
                progress).ConfigureAwait(false);

            var deviceBackup = await WithSessionAsync(device, session =>
                InstallAnnotationDatabase(
                    session,
                    snapshot.DatabaseRelativePath,
                    snapshot.Sha256,
                    candidatePath,
                    newHash,
                    localBackupPath,
                    readbackDirectory,
                    progress,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            return new KindleAnnotationWriteResult(
                mutations.Count,
                snapshot.Sha256,
                newHash,
                localBackupPath,
                deviceBackup);
        }
        finally
        {
            TryDelete(candidatePath);
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static string InstallAnnotationDatabase(
        MtpDeviceSession session,
        string databaseRelativePath,
        string expectedOriginalHash,
        string candidatePath,
        string candidateHash,
        string localBackupPath,
        string readbackDirectory,
        IProgress<KindleAnnotationSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = FindAnnotationDatabase(session, databaseRelativePath)
            ?? throw new IOException("设备批注数据库路径已经变化。");
        RejectAnnotationSideFiles(session, database);
        var parentDirectory = database.RelativePath[..database.RelativePath.LastIndexOf('/')];

        var currentReadback = Path.Combine(readbackDirectory, "current.db");
        session.Download(database, currentReadback);
        VerifyHash(currentReadback, expectedOriginalHash,
            "设备批注数据库在同步期间发生变化，已取消安装。");

        // Upload the untouched database and the candidate under temporary
        // names first, so both are proven readable before the original is
        // replaced. This mirrors the WPD transport.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stageName = $"{AnnotationFileName}.kkindle-sync-new-{stamp}";
        var backupName = $"{AnnotationFileName}.kkindle-backup-{stamp}";

        progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
        session.Upload(parentDirectory, localBackupPath, backupName);
        var backupReadback = Path.Combine(readbackDirectory, "backup.db");
        session.Download(
            session.Find($"{parentDirectory}/{backupName}")
                ?? throw new IOException("设备未确认写入批注备份。"),
            backupReadback);
        VerifyHash(backupReadback, expectedOriginalHash, "设备批注备份校验失败。");

        progress?.Report(new(KindleAnnotationSyncStage.PreparingWrite));
        session.Upload(parentDirectory, candidatePath, stageName);
        var stageReadback = Path.Combine(readbackDirectory, "stage.db");
        session.Download(
            session.Find($"{parentDirectory}/{stageName}")
                ?? throw new IOException("设备未确认写入批注快照。"),
            stageReadback);
        VerifyHash(stageReadback, candidateHash, "设备批注快照校验失败。");

        // Last freshness check immediately before deleting the original.
        database = FindAnnotationDatabase(session, databaseRelativePath)
            ?? throw new IOException("设备批注数据库已消失，已取消安装。");
        RejectAnnotationSideFiles(session, database);
        session.Download(database, currentReadback);
        VerifyHash(currentReadback, expectedOriginalHash,
            "设备批注数据库在同步期间发生变化，已取消安装。");

        progress?.Report(new(KindleAnnotationSyncStage.WritingToDevice));
        session.Delete(database);
        try
        {
            session.Upload(parentDirectory, candidatePath, AnnotationFileName);
            var installed = FindAnnotationDatabase(session, databaseRelativePath)
                ?? throw new IOException("设备未确认安装批注数据库。");
            var installedReadback = Path.Combine(readbackDirectory, "installed.db");
            session.Download(installed, installedReadback);
            VerifyHash(installedReadback, candidateHash, "设备批注数据库安装后校验失败。");
        }
        catch
        {
            // Never leave the device with a missing or partially written
            // database: the verified byte copy goes back under its own name.
            try
            {
                session.Upload(parentDirectory, localBackupPath, AnnotationFileName);
            }
            catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Restoring the Kindle annotation database failed: {restoreFailure.Message}");
            }
            throw;
        }

        try
        {
            if (session.Find($"{parentDirectory}/{stageName}") is { } staged) session.Delete(staged);
        }
        catch (IOException exception)
        {
            // Leaving a verified staging file behind cannot invalidate a
            // database that already passed its readback check.
            Debug.WriteLine($"Removing the staged annotation database failed: {exception.Message}");
        }

        progress?.Report(new(KindleAnnotationSyncStage.Verifying));
        return $"{parentDirectory}/{backupName}";
    }

    private async Task<IReadOnlyList<KindleClipping>> ReadAnnotationDatabaseClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken,
        int maxItems)
    {
        if (maxItems <= 0) return [];
        var stagingDirectory = CreateStagingDirectory("annotation-database-read");
        try
        {
            var databases = await WithSessionAsync(
                device, FindAnnotationDatabases, cancellationToken).ConfigureAwait(false);
            if (databases.Count == 0) return [];
            var sdrFolders = await WithSessionAsync(
                device, session => FindSdrFolders(session, device), cancellationToken).ConfigureAwait(false);
            var titleResolver = KindleAnnotationBookTitleResolver.Create(sdrFolders);
            var result = new List<KindleClipping>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < databases.Count && result.Count < maxItems; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var database = databases[index];
                var localPath = Path.Combine(stagingDirectory, $"annotation-{index}.db");
                await WithSessionAsync(device, session =>
                {
                    session.Download(database, localPath);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
                var notes = await KindleAnnotationDatabaseReader.ReadClippingsAsync(
                    localPath,
                    titleResolver.Resolve,
                    maxItems - result.Count,
                    cancellationToken).ConfigureAwait(false);
                foreach (var note in notes)
                    if (ids.Add(note.Id)) result.Add(note);
            }
            return result;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static IReadOnlyList<MtpEntry> FindAnnotationDatabases(MtpDeviceSession session)
    {
        var databases = session.Enumerate(AnnotationRoot)
            .Where(entry => !entry.IsFolder
                && entry.Name.Equals(AnnotationFileName, StringComparison.OrdinalIgnoreCase))
            .Take(32)
            .ToArray();
        var accounts = databases
            .Where(entry => IsAccountDatabase(entry.RelativePath))
            .ToArray();
        return accounts.Length > 0 ? accounts : databases;
    }

    private static MtpEntry? FindAnnotationDatabase(MtpDeviceSession session, string relativePath)
    {
        var entry = session.Find(relativePath);
        return entry is not null
            && !entry.IsFolder
            && entry.Name.Equals(AnnotationFileName, StringComparison.OrdinalIgnoreCase)
            ? entry
            : null;
    }

    private static IReadOnlyList<string> FindSdrFolders(MtpDeviceSession session, KindleDevice device) =>
        session.List(device.Profile.BooksDirectory)
            .Where(entry => entry.IsFolder
                && entry.Name.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name)
            .ToArray();

    private static bool IsAccountDatabase(string relativePath) =>
        relativePath.Split('/', '\\')
            .Any(segment => segment.StartsWith("amzn1.account.", StringComparison.OrdinalIgnoreCase));

    private static void RejectAnnotationSideFiles(MtpDeviceSession session, MtpEntry database)
    {
        var directory = database.RelativePath[..database.RelativePath.LastIndexOf('/')];
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (session.Find($"{directory}/{database.Name}{suffix}") is not null)
                throw new IOException("设备批注数据库带有 SQLite 侧文件，暂不能安全同步。");
        }
    }

    private static void ValidateAnnotationDatabasePath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (!normalized.StartsWith(AnnotationRoot + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(segment => segment is "." or "..")
            || !Path.GetFileName(normalized).Equals(AnnotationFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kindle 批注数据库路径无效。");
    }

    private static void VerifyHash(string path, string expected, string message)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }

    private static async Task VerifyHashAsync(
        string path, string expected, string message, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }

    private static bool IsAnnotationDatabaseFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or TimeoutException
            or Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException;

    private static void UploadThumbnail(MtpDeviceSession session, KindleThumbnail thumbnail)
    {
        var temporary = Path.Combine(
            Path.GetTempPath(), "Kkindle", $"thumbnail-{Guid.NewGuid():N}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            File.WriteAllBytes(temporary, thumbnail.JpegBytes);
            session.Upload(ThumbnailDirectory, temporary, thumbnail.FileName);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void DeleteSidecarFolder(MtpDeviceSession session, string directory, string fileName)
    {
        var sidecarName = Path.ChangeExtension(fileName, ".sdr");
        var sidecarPath = string.IsNullOrEmpty(directory) ? sidecarName : $"{directory}/{sidecarName}";
        if (session.Find(sidecarPath) is { IsFolder: true } sidecar)
            session.DeleteTree(sidecar);
    }

    private static string CreateStagingDirectory(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "Kkindle", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LocalAnnotationDatabase(
        string DatabaseRelativePath,
        string LocalPath,
        IReadOnlyList<string> SdrFolders);

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
        IReadOnlyList<MtpRawDevice> rawDevices;
        try
        {
            rawDevices = MtpDeviceSession.Detect();
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or BadImageFormatException
                                              or TypeInitializationException)
        {
            // Debug builds and source checkouts do not bundle libmtp. MTP is an
            // optional transport on macOS, so treat a missing native library as
            // "no MTP devices" instead of turning device detection into an error
            // in the sidebar. Packaged builds ship the dylib next to the app.
            Debug.WriteLine($"MTP unavailable: {exception.Message}");
            return [];
        }

        var devices = new List<KindleDevice>();
        foreach (var raw in rawDevices)
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
                                    // Sideloaded books often lack an embedded
                                    // cover, but the device shelf thumbnail is
                                    // still available next to the book.
                                    if (string.IsNullOrWhiteSpace(book.CoverPath))
                                        book.CoverPath = TryReadDeviceThumbnail(
                                            session, book, staged, stagingDirectory, cancellationToken);
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

    private string? TryReadDeviceThumbnail(
        MtpDeviceSession session,
        KindleBook book,
        string bookPath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var thumbnailName = KindleThumbnailService
                .ReadThumbnailFileNameAsync(bookPath, cancellationToken)
                .GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(thumbnailName)) return null;
            var remote = session.Find($"{ThumbnailDirectory}/{thumbnailName}");
            if (remote is null) return null;
            var local = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.jpg");
            try
            {
                session.Download(remote, local);
                var name = string.IsNullOrWhiteSpace(book.Sha256)
                    ? Guid.NewGuid().ToString("N")
                    : book.Sha256;
                var path = Path.Combine(_coverCacheDirectory, $"{name}.jpg");
                File.Copy(local, path, overwrite: true);
                return path;
            }
            finally
            {
                TryDelete(local);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
