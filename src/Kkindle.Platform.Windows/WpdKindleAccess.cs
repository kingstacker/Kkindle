using System.Runtime.InteropServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Platform.Windows;

internal static class WpdKindleAccess
{
    private const int MyComputerShellFolder = 17;
    private static readonly Guid WpdObjectPropertySet = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");

    public static void ReleaseDeviceSessions(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Shell.Application exposes WPD objects through COM wrappers. Releasing
        // their final references closes the WPD session, matching calibre's MTP
        // eject semantics without placing the physical device in pending-eject.
        FlushReleasedComObjects();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static void CloseDeviceSession(KindleDevice device, CancellationToken cancellationToken)
    {
        ReleaseDeviceSessions(cancellationToken);
        WpdSessionCloser.CloseSession(device.RootPath, cancellationToken);
        FlushReleasedComObjects();
    }

    public static IReadOnlyList<KindleDevice> DetectDevices(CancellationToken cancellationToken)
    {
        var devices = new List<KindleDevice>();
        dynamic? shell = null;
        dynamic? computer = null;
        dynamic? items = null;
        try
        {
            shell = CreateShell();
            computer = shell.NameSpace(MyComputerShellFolder);
            items = computer.Items();
            for (var index = 0; index < (int)items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? item = null;
                dynamic? storage = null;
                try
                {
                    item = items.Item(index);
                    if (!(bool)item.IsFolder) continue;
                    string name = Convert.ToString(item.Name) ?? string.Empty;
                    string shellPath = Convert.ToString(item.Path) ?? string.Empty;
                    // WPD shell entries contain their native device path. Local disks
                    // and unrelated virtual folders must not be probed as readers.
                    if (!shellPath.Contains(@"\\?\", StringComparison.OrdinalIgnoreCase)) continue;
                    storage = FindFirstStorage(item);
                    if (storage is null) continue;
                    bool HasDirectory(string relativePath)
                    {
                        dynamic? folder = null;
                        try
                        {
                            folder = FindItemByRelativePath(storage, relativePath);
                            return folder is not null && (bool)folder.IsFolder;
                        }
                        finally { Release(folder); }
                    }
                    var profile = ReaderDeviceProfiles.Detect(name, HasDirectory, shellPath);
                    if (profile is null) continue;
                    devices.Add(new KindleDevice
                    {
                        Profile = profile,
                        RootPath = shellPath,
                        VolumeSerial = shellPath,
                        Name = string.IsNullOrWhiteSpace(name) ? profile.DisplayName : name,
                        TotalBytes = ReadInt64Property(storage, "System.Capacity"),
                        FreeBytes = ReadInt64Property(storage, "System.FreeSpace"),
                        IsReady = true,
                        Transport = KindleTransport.Wpd
                    });
                }
                catch (COMException)
                {
                    // A locked or disconnected device must not hide other readers.
                }
                finally
                {
                    Release(storage);
                    Release(item);
                }
            }
        }
        catch (COMException)
        {
            return devices;
        }
        finally
        {
            Release(items);
            Release(computer);
            Release(shell);
            FlushReleasedComObjects();
        }
        return devices;
    }

    public static IReadOnlyList<KindleBook> ScanBooks(
        KindleDevice device,
        IReadOnlySet<string> supportedExtensions,
        CancellationToken cancellationToken)
    {
        var books = new List<KindleBook>();
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? documents = storage is null ? null : FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            if (documents is null) return books;

            var folders = new Stack<(object Folder, string RelativePath)>();
            folders.Push((documents.GetFolder, string.Empty));
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = folders.Pop();
                dynamic folder = entry.Folder;
                dynamic children = folder.Items();
                for (var index = 0; index < (int)children.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dynamic child = children.Item(index);
                    var name = Convert.ToString(child.Name) ?? string.Empty;
                    var relativePath = string.IsNullOrEmpty(entry.RelativePath)
                        ? name
                        : $"{entry.RelativePath}\\{name}";
                    if ((bool)child.IsFolder)
                    {
                        var isRootDictionaryFolder = string.IsNullOrEmpty(entry.RelativePath)
                            && name.Equals("dictionaries", StringComparison.OrdinalIgnoreCase);
                        if (!name.StartsWith('.') && !name.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase)
                            && !name.Equals("system", StringComparison.OrdinalIgnoreCase)
                            && !name.Equals("fonts", StringComparison.OrdinalIgnoreCase) && !isRootDictionaryFolder)
                            folders.Push((child.GetFolder, relativePath));
                        continue;
                    }

                    var extension = Path.GetExtension(name);
                    if (!supportedExtensions.Contains(extension) || !device.Profile.IsBookPath(relativePath)) continue;
                    books.Add(new KindleBook
                    {
                        RelativePath = relativePath,
                        Format = extension.TrimStart('.').ToLowerInvariant(),
                        Size = ReadInt64Property(child, "System.Size"),
                        ModifiedAt = ReadDateTimeOffsetProperty(child, "System.DateModified"),
                        Sha256 = string.Empty,
                        IsManagedByKkindle = false
                    });
                }
            }
        }
        catch (COMException)
        {
            return books;
        }
        finally
        {
            Release(shell);
            FlushReleasedComObjects();
        }
        return books.OrderBy(book => book.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<KindleDeviceResource> ScanResources(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken)
    {
        var resources = new List<KindleDeviceResource>();
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? resourceRoot = null;
        Stack<(object Folder, string RelativePath)>? folders = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath);
            storage = kindle is null ? null : FindFirstStorage(kindle);
            var rootRelative = device.Profile.ResourceDirectory(kind);
            resourceRoot = storage is null ? null : FindItemByRelativePath(storage, rootRelative);
            if (resourceRoot is null || !(bool)resourceRoot.IsFolder) return resources;

            folders = new Stack<(object Folder, string RelativePath)>();
            folders.Push((resourceRoot.GetFolder, rootRelative));
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = folders.Pop();
                dynamic? folder = entry.Folder;
                dynamic? children = null;
                try
                {
                    children = folder.Items();
                    for (var index = 0; index < (int)children.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        dynamic? child = null;
                        try
                        {
                            child = children.Item(index);
                            var name = Convert.ToString(child.Name) ?? string.Empty;
                            var relativePath = $"{entry.RelativePath}\\{name}";
                            if ((bool)child.IsFolder)
                            {
                                folders.Push((child.GetFolder, relativePath));
                                continue;
                            }
                            if (!device.Profile.SupportsResourceFile(kind, name)) continue;
                            resources.Add(new KindleDeviceResource
                            {
                                Kind = kind,
                                RelativePath = relativePath,
                                Size = ReadInt64Property(child, "System.Size")
                            });
                        }
                        finally
                        {
                            Release(child);
                        }
                    }
                }
                finally
                {
                    Release(children);
                    Release(folder);
                }
            }
        }
        catch (COMException exception)
        {
            throw new IOException("无法读取 MTP 设备资源目录。", exception);
        }
        finally
        {
            if (folders is not null)
            {
                while (folders.Count > 0)
                    Release(folders.Pop().Folder);
            }
            Release(resourceRoot);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
        return resources.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static void SendResource(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath)) throw new InvalidDataException("设备资源格式不受支持。");
        var source = new FileInfo(sourcePath);
        var rootRelative = device.Profile.ResourceDirectory(kind);
        string? targetRelativePath = null;
        var completed = false;
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            dynamic targetRoot = FindOrCreateFolderPath(storage, rootRelative, cancellationToken);
            var finalName = GetUniqueFileName(targetRoot, source.Name);
            targetRelativePath = $"{rootRelative}\\{finalName}";
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TransferProgress(0, source.Length, $"正在发送 {source.Name}"));
            WpdNativeTransfer.SendFile(
                device.RootPath,
                GetWpdObjectId(targetRoot),
                sourcePath,
                finalName,
                progress,
                cancellationToken);
            WaitForStorageItem(device, targetRelativePath, source.Length, progress, source.Name, cancellationToken);
            progress?.Report(new TransferProgress(source.Length, source.Length, $"已发送 {finalName}"));
            completed = true;
        }
        catch (COMException exception)
        {
            throw new IOException("MTP 资源传输失败，请确认设备仍保持连接。", exception);
        }
        finally
        {
            Release(shell);
            if (!completed && targetRelativePath is not null) TryRemoveStorageItem(device, targetRelativePath);
        }
    }

    public static void CopyResourceToLocal(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("不能导出设备文件夹。");
            WpdNativeTransfer.CopyFileToLocal(
                device.RootPath,
                GetWpdObjectId(item),
                destinationPath,
                resource.Size,
                cancellationToken);
        }
        catch (COMException exception)
        {
            throw new IOException("无法从 MTP 设备导出资源。", exception);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    public static void RemoveResource(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken)
    {
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("不能删除设备文件夹。");
            cancellationToken.ThrowIfCancellationRequested();
            ShellFileOperation.DeletePermanently((object)item);
            var startedAt = DateTime.UtcNow;
            while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadStorageItemState(device, resource.RelativePath).Exists) return;
                Thread.Sleep(250);
            }
            throw new TimeoutException("等待设备删除资源超时。");
        }
        catch (COMException exception)
        {
            throw new IOException("无法删除 MTP 设备资源。", exception);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    public static string ReadClippingsText(KindleDevice device, CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "clippings-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, @"documents\My Clippings.txt");
            if (item is null) return string.Empty;
            var size = ReadInt64Property(item, "System.Size");
            var localPath = Path.Combine(stagingDirectory, "My Clippings.txt");
            WpdNativeTransfer.CopyFileToLocal(
                device.RootPath,
                GetWpdObjectId(item),
                localPath,
                size,
                cancellationToken);
            using var reader = new StreamReader(localPath, System.Text.Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (COMException exception)
        {
            throw new IOException("无法读取 MTP Kindle 的 My Clippings.txt。", exception);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
            try { Directory.Delete(stagingDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void ReplaceClippingsText(KindleDevice device, string text, CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "clippings-write", Guid.NewGuid().ToString("N"));
        var originalDirectory = Path.Combine(stagingDirectory, "original");
        var updatedDirectory = Path.Combine(stagingDirectory, "updated");
        Directory.CreateDirectory(originalDirectory);
        Directory.CreateDirectory(updatedDirectory);
        var originalPath = Path.Combine(originalDirectory, "My Clippings.txt");
        var updatedPath = Path.Combine(updatedDirectory, "My Clippings.txt");
        File.WriteAllText(updatedPath, text, new System.Text.UTF8Encoding(true));
        dynamic? shell = null;
        dynamic? documents = null;
        dynamic? item = null;
        var documentsObjectId = string.Empty;
        var originalCopied = false;
        var deleteAttempted = false;
        var replacementVerified = false;
        var preserveOriginalCopy = false;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, @"documents\My Clippings.txt")
                ?? throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。");
            documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory) ?? throw new IOException("设备上不存在 documents 目录。");
            documentsObjectId = GetWpdObjectId(documents);
            var originalSize = ReadInt64Property(item, "System.Size");
            WpdNativeTransfer.CopyFileToLocal(
                device.RootPath,
                GetWpdObjectId(item),
                originalPath,
                originalSize,
                cancellationToken);
            originalCopied = true;

            cancellationToken.ThrowIfCancellationRequested();
            deleteAttempted = true;
            ShellFileOperation.DeletePermanently((object)item);
            var deleteStarted = DateTime.UtcNow;
            while (DateTime.UtcNow - deleteStarted < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadStorageItemState(device, @"documents\My Clippings.txt").Exists) break;
                Thread.Sleep(250);
            }
            if (ReadStorageItemState(device, @"documents\My Clippings.txt").Exists)
                throw new TimeoutException("等待 Kindle 更新 My Clippings.txt 超时。");

            WpdNativeTransfer.SendFile(
                device.RootPath,
                documentsObjectId,
                updatedPath,
                "My Clippings.txt",
                null,
                cancellationToken);
            WaitForStorageItem(device, @"documents\My Clippings.txt", new FileInfo(updatedPath).Length, null, "My Clippings.txt", cancellationToken);
            replacementVerified = true;
        }
        catch (Exception exception) when (exception is COMException or IOException or TimeoutException or OperationCanceledException)
        {
            if (originalCopied && deleteAttempted && !replacementVerified
                && !string.IsNullOrWhiteSpace(documentsObjectId))
            {
                try
                {
                    var current = ReadStorageItemState(device, @"documents\My Clippings.txt");
                    if (current.Exists)
                    {
                        TryRemoveStorageItem(device, @"documents\My Clippings.txt");
                        var cleanupStarted = DateTime.UtcNow;
                        while (DateTime.UtcNow - cleanupStarted < TimeSpan.FromSeconds(20)
                            && ReadStorageItemState(device, @"documents\My Clippings.txt").Exists)
                        {
                            Thread.Sleep(250);
                        }
                        if (ReadStorageItemState(device, @"documents\My Clippings.txt").Exists)
                            throw new TimeoutException("无法清理未完成的 My Clippings.txt，原始副本需要保留。");
                    }

                    WpdNativeTransfer.SendFile(
                        device.RootPath,
                        documentsObjectId,
                        originalPath,
                        "My Clippings.txt",
                        null,
                        CancellationToken.None);
                    WaitForStorageItem(device, @"documents\My Clippings.txt", new FileInfo(originalPath).Length, null, "My Clippings.txt", CancellationToken.None);
                }
                catch
                {
                    preserveOriginalCopy = true;
                }
            }
            if (preserveOriginalCopy)
                throw new IOException(
                    $"MTP 更新 My Clippings.txt 失败，原始副本已保留在：{originalPath}",
                    exception);
            if (exception is COMException com) throw new IOException("无法更新 MTP Kindle 的 My Clippings.txt。", com);
            throw;
        }
        finally
        {
            Release(item);
            Release(documents);
            Release(shell);
            FlushReleasedComObjects();
            if (!preserveOriginalCopy)
            {
                try { Directory.Delete(stagingDirectory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string CopyBookToLocal(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, Path.Combine(device.Profile.BooksDirectory, book.RelativePath))
                ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
            var destinationPath = GetUniqueLocalDestination(destinationDirectory, book.FileName);
            try
            {
                WpdNativeTransfer.CopyFileToLocal(
                    device.RootPath,
                    GetWpdObjectId(item),
                    destinationPath,
                    book.Size,
                    cancellationToken);
            }
            catch (COMException)
            {
                // Some Kindle firmware exposes the object through Shell but
                // rejects WPD_RESOURCE_DEFAULT. IFileOperation uses the same
                // Shell object and remains compatible with that firmware.
                ShellFileOperation.CopyToLocal((object)item, destinationPath, book.Size, cancellationToken);
            }
            return destinationPath;
        }
        catch (COMException exception)
        {
            throw new IOException("无法从 MTP 设备读取书籍。", exception);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    public static string CopyStorageFileToLocal(
        KindleDevice device,
        string relativePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException("设备文件路径无效。");

        Directory.CreateDirectory(destinationDirectory);
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, relativePath)
                ?? throw new FileNotFoundException("设备文件不存在。", relativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("设备目标不能是文件夹。");
            var fileName = Path.GetFileName(relativePath);
            var destinationPath = GetUniqueLocalDestination(destinationDirectory, fileName);
            var expectedSize = ReadInt64Property(item, "System.Size");
            try
            {
                WpdNativeTransfer.CopyFileToLocal(
                    device.RootPath,
                    GetWpdObjectId(item),
                    destinationPath,
                    expectedSize,
                    cancellationToken);
            }
            catch (COMException)
            {
                ShellFileOperation.CopyToLocal((object)item, destinationPath, expectedSize, cancellationToken);
            }
            return destinationPath;
        }
        catch (COMException exception)
        {
            throw new IOException("无法从 MTP 设备读取书籍封面。", exception);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    public static void RemoveBook(
        KindleDevice device,
        KindleBook book,
        CancellationToken cancellationToken)
    {
        if (!device.Profile.IsBookPath(book.RelativePath))
            throw new InvalidOperationException("设备书籍路径无效。");
        if (string.IsNullOrWhiteSpace(book.RelativePath)
            || book.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException("设备文件路径无效。");

        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            dynamic? documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory)
                ?? throw new IOException("设备上不存在 documents 目录。");
            dynamic? item = FindItemByRelativePath(documents, book.RelativePath)
                ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("不能删除设备文件夹。");

            cancellationToken.ThrowIfCancellationRequested();
            ShellFileOperation.DeletePermanently((object)item);
            var startedAt = DateTime.UtcNow;
            while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentItemExists(device, book.RelativePath)) return;
                Thread.Sleep(250);
            }
            throw new TimeoutException("等待设备删除文件超时。");
        }
        catch (COMException exception)
        {
            throw new IOException("无法删除 MTP 设备上的书籍。", exception);
        }
        finally
        {
            Release(shell);
        }
    }

    public static void SendBook(
        KindleDevice device,
        string sourcePath,
        string finalName,
        KindleThumbnail? thumbnail,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var extension = Path.GetExtension(finalName);
        var transferName = $"kkindle-transfer-{Guid.NewGuid():N}{extension}";
        var backupName = $"kkindle-backup-{Guid.NewGuid():N}{extension}";
        var transferCompleted = false;
        var previousBookRenamed = false;
        var previousBookRenameStarted = false;
        var preserveTransfer = false;
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            dynamic? documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory)
                ?? throw new IOException("设备上不存在 documents 目录。");

            if (Path.GetFileName(finalName) != finalName
                || string.IsNullOrWhiteSpace(finalName)
                || !Path.GetExtension(finalName).Equals(sourceInfo.Extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("设备目标文件名无效。");

            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new TransferProgress(0, sourceInfo.Length, $"正在发送 {sourceInfo.Name}"));
            WpdNativeTransfer.SendFile(
                device.RootPath,
                GetWpdObjectId(documents),
                sourcePath,
                transferName,
                progress,
                cancellationToken);

            var timeout = TimeSpan.FromMinutes(Math.Clamp(2 + sourceInfo.Length / (100d * 1024 * 1024), 2, 30));
            WaitForItem(
                device,
                transferName,
                sourceInfo.Length,
                timeout,
                progress,
                sourceInfo.Name,
                cancellationToken);
            var finalSize = WaitForItem(
                device,
                transferName,
                sourceInfo.Length,
                TimeSpan.FromSeconds(15),
                progress,
                sourceInfo.Name,
                cancellationToken);
            if (finalSize != sourceInfo.Length)
                throw new IOException("设备文件大小校验失败。");
            if (thumbnail is not null)
            {
                progress?.Report(new TransferProgress(sourceInfo.Length, sourceInfo.Length, "正在同步设备书架封面"));
                var stagedThumbnail = Path.Combine(stagingDirectory, thumbnail.FileName);
                File.WriteAllBytes(stagedThumbnail, thumbnail.JpegBytes);
                UploadBookThumbnail(
                    device,
                    storage,
                    stagedThumbnail,
                    thumbnail.FileName,
                    thumbnail.JpegBytes.LongLength,
                    cancellationToken);
            }

            var existing = FindChild(documents, finalName);
            if (existing is not null)
            {
                try
                {
                    if ((bool)existing.IsFolder)
                        throw new InvalidOperationException("设备上同名目标是文件夹，无法替换书籍。");
                    previousBookRenameStarted = true;
                    ShellFileOperation.RenamePermanently((object)existing, backupName);
                    previousBookRenamed = true;
                }
                finally
                {
                    Release(existing);
                }
            }

            RenameDocumentItem(documents, device, transferName, finalName);
            transferCompleted = true;
            WaitForStorageItem(
                device,
                Path.Combine(device.Profile.BooksDirectory, finalName),
                sourceInfo.Length,
                null,
                finalName,
                cancellationToken);

            if (previousBookRenamed)
            {
                // The verified new copy is now in place. A cleanup failure
                // may leave a backup on-device, but cannot remove the new book.
                TryRemoveDocumentItem(device, backupName);
                previousBookRenamed = false;
            }
            progress?.Report(new TransferProgress(sourceInfo.Length, sourceInfo.Length, $"已发送 {finalName}"));
        }
        catch (Exception exception)
        {
            if (!previousBookRenamed && previousBookRenameStarted)
                previousBookRenamed = ReadStorageItemState(
                    device,
                    Path.Combine(device.Profile.BooksDirectory, backupName)).Exists;
            if (previousBookRenamed)
            {
                try
                {
                    TryRemoveDocumentItem(device, finalName);
                    RenameDocumentItem(null, device, backupName, finalName);
                    previousBookRenamed = false;
                }
                catch
                {
                    // Keep both the old backup and the staged/new copy when
                    // the device does not permit rollback after an interrupted
                    // rename. The user can recover either complete file.
                    preserveTransfer = true;
                }
            }
            if (exception is COMException com)
                throw new IOException("MTP 传输失败，请确认设备仍保持连接。", com);
            throw;
        }
        finally
        {
            Release(shell);
            if (!transferCompleted && !preserveTransfer)
                TryRemoveDocumentItem(device, transferName);
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RenameDocumentItem(dynamic? documents, KindleDevice device, string sourceName, string targetName)
    {
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? folder = null;
        dynamic? item = null;
        try
        {
            if (documents is null)
            {
                shell = CreateShell();
                kindle = FindDevice(shell, device.RootPath)
                    ?? throw new IOException("设备已断开连接。");
                storage = FindFirstStorage(kindle)
                    ?? throw new IOException("无法读取设备内部存储。");
                folder = FindItemByRelativePath(storage, device.Profile.BooksDirectory)
                    ?? throw new IOException("设备上不存在 documents 目录。");
            }
            item = FindChild(documents ?? folder, sourceName)
                ?? throw new FileNotFoundException("MTP 设备上找不到待重命名的书籍。", sourceName);
            if ((bool)item.IsFolder)
                throw new InvalidOperationException("不能重命名设备上的文件夹来替换书籍。");
            ShellFileOperation.RenamePermanently((object)item, targetName);
        }
        finally
        {
            Release(item);
            Release(folder);
            Release(storage);
            Release(kindle);
            Release(shell);
        }
    }

    private static void UploadBookThumbnail(
        KindleDevice device,
        dynamic storage,
        string stagedThumbnail,
        string thumbnailFileName,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (thumbnailFileName.IndexOfAny(['\\', '/']) >= 0)
            throw new InvalidOperationException("设备缩略图文件名无效。");

        dynamic thumbnailFolder = FindOrCreateFolderPath(storage, @"system\thumbnails", cancellationToken);
        dynamic? existing = FindChild(thumbnailFolder, thumbnailFileName);
        if (existing is not null)
        {
            if ((bool)existing.IsFolder)
                throw new InvalidOperationException("设备缩略图目标不能是文件夹。");
            ShellFileOperation.DeletePermanently((object)existing);
            var deleteStartedAt = DateTime.UtcNow;
            var relativePath = $@"system\thumbnails\{thumbnailFileName}";
            while (DateTime.UtcNow - deleteStartedAt < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadStorageItemState(device, relativePath).Exists) break;
                Thread.Sleep(250);
            }
            if (ReadStorageItemState(device, relativePath).Exists)
                throw new TimeoutException("等待设备更新旧封面超时。");
        }

        WpdNativeTransfer.SendFile(
            device.RootPath,
            GetWpdObjectId(thumbnailFolder),
            stagedThumbnail,
            thumbnailFileName,
            null,
            cancellationToken);
        WaitForStorageItem(
            device,
            $@"system\thumbnails\{thumbnailFileName}",
            expectedSize,
            null,
            thumbnailFileName,
            cancellationToken);
    }

    private static dynamic CreateShell()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new PlatformNotSupportedException("Windows Shell 不可用。");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法启动 Windows Shell。");
    }

    private static dynamic? FindDevice(dynamic shell, string shellPath)
    {
        dynamic computer = shell.NameSpace(MyComputerShellFolder);
        dynamic items = computer.Items();
        for (var index = 0; index < (int)items.Count; index++)
        {
            dynamic item = items.Item(index);
            if (string.Equals(Convert.ToString(item.Path), shellPath, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private static dynamic? FindFirstStorage(dynamic device)
    {
        dynamic children = device.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
        {
            dynamic child = children.Item(index);
            if ((bool)child.IsFolder) return child;
        }
        return null;
    }

    private static dynamic? FindChild(dynamic parent, string name)
    {
        dynamic children = parent.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
        {
            dynamic child = children.Item(index);
            if (string.Equals(Convert.ToString(child.Name), name, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private static string GetUniqueLocalDestination(string directory, string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        var destination = Path.Combine(directory, safeName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 2; ; index++)
        {
            destination = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(destination)) return destination;
        }
    }

    private static string GetWpdObjectId(dynamic shellItem)
    {
        var value = shellItem.ExtendedProperty($"{{{WpdObjectPropertySet}}} 2");
        var objectId = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(objectId))
            throw new IOException("无法读取设备目标目录的 WPD 对象 ID。");
        return objectId;
    }

    private static dynamic? FindItemByRelativePath(dynamic root, string relativePath)
    {
        dynamic? current = root;
        foreach (var segment in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = current is null ? null : FindChild(current, segment);
            if (current is null) return null;
        }
        return current;
    }

    private static dynamic FindOrCreateFolderPath(dynamic storage, string relativePath, CancellationToken cancellationToken)
    {
        dynamic current = storage;
        foreach (var segment in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic? child = FindChild(current, segment);
            if (child is null)
            {
                dynamic folder = current.GetFolder;
                folder.NewFolder(segment);
                var startedAt = DateTime.UtcNow;
                while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(10))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    child = FindChild(current, segment);
                    if (child is not null) break;
                    Thread.Sleep(200);
                }
            }
            if (child is null || !(bool)child.IsFolder)
                throw new IOException($"无法创建设备目录：{relativePath}");
            current = child;
        }
        return current;
    }

    private static string GetUniqueFileName(dynamic folderItem, string fileName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic children = folderItem.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
            names.Add(Convert.ToString(children.Item(index).Name) ?? string.Empty);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? fileName : $"{stem} ({index}){extension}";
            if (!names.Contains(candidate) && !names.Contains(candidate + ".kkindle-part"))
                return candidate;
        }
    }

    private static long WaitForItem(
        KindleDevice device,
        string name,
        long expectedSize,
        TimeSpan timeout,
        IProgress<TransferProgress>? progress,
        string displayName,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadDocumentItemState(device, name);
            if (state.Exists)
            {
                var size = state.Size;
                progress?.Report(new TransferProgress(Math.Min(size, expectedSize), expectedSize, $"正在发送 {displayName}"));
                if (size == expectedSize) return size;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("等待设备完成文件写入超时。");
    }

    private static (bool Exists, long Size) ReadDocumentItemState(KindleDevice device, string relativePath)
    {
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? documents = storage is null ? null : FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            dynamic? item = documents is null ? null : FindItemByRelativePath(documents, relativePath);
            if (item is null) return (false, 0);
            long size = ReadInt64Property(item, "System.Size");
            return (true, size);
        }
        catch (COMException)
        {
            return (false, 0);
        }
        finally
        {
            Release(shell);
        }
    }

    private static bool DocumentItemExists(KindleDevice device, string relativePath) =>
        ReadDocumentItemState(device, relativePath).Exists;

    private static void WaitForStorageItem(
        KindleDevice device,
        string relativePath,
        long expectedSize,
        IProgress<TransferProgress>? progress,
        string displayName,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(2 + expectedSize / (100d * 1024 * 1024), 2, 30));
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadStorageItemState(device, relativePath);
            if (state.Exists)
            {
                progress?.Report(new TransferProgress(Math.Min(state.Size, expectedSize), expectedSize, $"正在发送 {displayName}"));
                if (state.Size == expectedSize) return;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("等待设备完成资源写入超时。");
    }

    private static (bool Exists, long Size) ReadStorageItemState(KindleDevice device, string relativePath)
    {
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath);
            storage = kindle is null ? null : FindFirstStorage(kindle);
            item = storage is null ? null : FindItemByRelativePath(storage, relativePath);
            return item is null ? (false, 0L) : (true, (long)ReadInt64Property(item, "System.Size"));
        }
        catch (COMException)
        {
            return (false, 0);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    private static void TryRemoveStorageItem(KindleDevice device, string relativePath)
    {
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? item = storage is null ? null : FindItemByRelativePath(storage, relativePath);
            if (item is not null && !(bool)item.IsFolder) ShellFileOperation.DeletePermanently((object)item);
        }
        catch (COMException) { }
        finally { Release(shell); }
    }

    private static void TryRemoveDocumentItem(KindleDevice device, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0) return;

        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? documents = storage is null ? null : FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            dynamic? item = documents is null ? null : FindChild(documents, name);
            if (item is not null && !(bool)item.IsFolder)
            {
                ShellFileOperation.DeletePermanently((object)item);
            }
        }
        catch (COMException) { }
        finally
        {
            Release(shell);
        }
    }

    private static long ReadInt64Property(dynamic item, string propertyName)
    {
        try { return ConvertToInt64(item.ExtendedProperty(propertyName)); }
        catch (COMException) { return 0; }
    }

    private static DateTimeOffset? ReadDateTimeOffsetProperty(dynamic item, string propertyName)
    {
        try
        {
            var value = item.ExtendedProperty(propertyName);
            if (value is DateTime dateTime) return new DateTimeOffset(dateTime.ToUniversalTime());
            return DateTimeOffset.TryParse((string?)Convert.ToString(value), out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (COMException) { return null; }
    }

    private static long ConvertToInt64(object? value)
    {
        try { return Convert.ToInt64(value); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void FlushReleasedComObjects()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

}
