using System.Runtime.InteropServices;

namespace Kkindle.Platform.MacOS.Mtp;

internal sealed record MtpRawDevice(
    uint BusLocation,
    byte DevNum,
    ushort VendorId,
    ushort ProductId,
    byte[] Template);

internal sealed record MtpStorageInfo(uint Id, string Description, long FreeBytes, long TotalBytes);

internal sealed class MtpEntry
{
    public required uint ItemId { get; init; }
    public required uint ParentId { get; init; }
    public required uint StorageId { get; init; }
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required bool IsFolder { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
}

/// <summary>
/// One open libmtp session against a device. libmtp is not thread-safe, so a
/// session must never be used concurrently; the device service serializes all
/// access with a global gate.
/// </summary>
internal sealed class MtpDeviceSession : IDisposable
{
    private const int MaximumDepth = 16;
    private static readonly int RawDeviceSize = Marshal.SizeOf<LibMtp.RawDevice>();
    private static int _initialized;

    private static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0) LibMtp.LIBMTP_Init();
    }

    private readonly IntPtr _device;
    private readonly IntPtr _rawDevice;
    private readonly Dictionary<(uint StorageId, uint ParentId), IReadOnlyList<MtpEntry>> _children = [];
    private bool _disposed;

    private MtpDeviceSession(IntPtr device, IntPtr rawDevice, MtpRawDevice raw)
    {
        _device = device;
        _rawDevice = rawDevice;
        Raw = raw;
        Model = LibMtp.ToString(LibMtp.LIBMTP_Get_Modelname(device));
        Manufacturer = LibMtp.ToString(LibMtp.LIBMTP_Get_Manufacturername(device));
        SerialNumber = LibMtp.ToString(LibMtp.LIBMTP_Get_Serialnumber(device));
        if (string.IsNullOrWhiteSpace(Model)) Model = "Kindle";

        if (LibMtp.LIBMTP_Get_Storage(device, 0) < 0) Throw("无法读取设备存储信息");
        var storages = new List<MtpStorageInfo>();
        for (var storage = Marshal.ReadIntPtr(device, LibMtp.DeviceStorageOffset);
             storage != IntPtr.Zero;
             storage = Marshal.ReadIntPtr(storage, 56))
        {
            var info = Marshal.PtrToStructure<LibMtp.DeviceStorage>(storage);
            storages.Add(new MtpStorageInfo(
                info.Id,
                LibMtp.ToString(info.StorageDescription),
                (long)info.FreeSpaceInBytes,
                (long)info.MaxCapacity));
        }
        if (storages.Count == 0) Throw("设备没有可用的存储");
        Storages = storages;
        PrimaryStorage = storages.OrderByDescending(storage => storage.TotalBytes).First();
    }

    public MtpRawDevice Raw { get; }
    public string Model { get; }
    public string Manufacturer { get; }
    public string SerialNumber { get; }
    public IReadOnlyList<MtpStorageInfo> Storages { get; }
    public MtpStorageInfo PrimaryStorage { get; }

    public static bool IsSupported => OperatingSystem.IsMacOS();

    public static IReadOnlyList<MtpRawDevice> Detect()
    {
        EnsureInitialized();
        var result = LibMtp.LIBMTP_Detect_Raw_Devices(out var devices, out var count);
        if (devices == IntPtr.Zero) return [];
        try
        {
            if (result != LibMtp.ErrorNone) return [];
            var raws = new List<MtpRawDevice>(Math.Max(0, count));
            for (var index = 0; index < count; index++)
            {
                var pointer = devices + index * RawDeviceSize;
                var raw = Marshal.PtrToStructure<LibMtp.RawDevice>(pointer);
                var template = new byte[RawDeviceSize];
                Marshal.Copy(pointer, template, 0, RawDeviceSize);
                raws.Add(new MtpRawDevice(
                    raw.BusLocation,
                    raw.DevNum,
                    raw.Entry.VendorId,
                    raw.Entry.ProductId,
                    template));
            }
            return raws;
        }
        finally
        {
            LibMtp.LIBMTP_FreeMemory(devices);
        }
    }

    public static MtpDeviceSession Open(MtpRawDevice raw)
    {
        // libmtp keeps a reference to the raw device descriptor for the life
        // of the session, so the copy must outlive LIBMTP_Open_Raw_Device.
        var native = Marshal.AllocHGlobal(raw.Template.Length);
        Marshal.Copy(raw.Template, 0, native, raw.Template.Length);
        var device = LibMtp.LIBMTP_Open_Raw_Device_Uncached(native);
        if (device == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(native);
            throw new IOException("无法打开 MTP 设备。");
        }
        return new MtpDeviceSession(device, native, raw);
    }

    public IReadOnlyList<MtpEntry> List(string relativeDirectory)
    {
        var segments = Split(relativeDirectory);
        var parent = Resolve(segments);
        return parent is null ? [] : Children(parent.Value.StorageId, parent.Value.ParentId, parent.Value.RelativePath);
    }

    public MtpEntry? Find(string relativePath)
    {
        var segments = Split(relativePath);
        if (segments.Length == 0) return null;
        var parent = Resolve(segments[..^1]);
        if (parent is null) return null;
        return Children(parent.Value.StorageId, parent.Value.ParentId, parent.Value.RelativePath)
            .FirstOrDefault(entry => entry.Name.Equals(segments[^1], StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<MtpEntry> Enumerate(string relativeDirectory)
    {
        var segments = Split(relativeDirectory);
        var root = Resolve(segments);
        if (root is null) return [];
        var results = new List<MtpEntry>();
        EnumerateChildren(root.Value.StorageId, root.Value.ParentId, root.Value.RelativePath, 0, results);
        return results;
    }

    public MtpEntry EnsureDirectory(string relativeDirectory)
    {
        var segments = Split(relativeDirectory);
        var storageId = PrimaryStorage.Id;
        uint parentId = LibMtp.FilesAndFoldersRoot;
        var parentPath = string.Empty;
        foreach (var segment in segments)
        {
            var currentPath = parentPath.Length == 0 ? segment : $"{parentPath}/{segment}";
            var existing = Children(storageId, parentId, parentPath)
                .FirstOrDefault(entry => entry.IsFolder && entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                parentId = existing.ItemId;
                parentPath = existing.RelativePath;
                continue;
            }
            var created = LibMtp.LIBMTP_Create_Folder(_device, segment, parentId, storageId);
            if (created == 0) Throw($"无法创建设备文件夹 {currentPath}");
            Invalidate(storageId, parentId);
            parentId = created;
            parentPath = currentPath;
        }
        return new MtpEntry
        {
            ItemId = parentId,
            ParentId = 0,
            StorageId = storageId,
            Name = segments.Length == 0 ? string.Empty : segments[^1],
            RelativePath = Normalize(relativeDirectory),
            IsFolder = true
        };
    }

    public void Download(MtpEntry entry, string destinationPath, Action<long, long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsFolder) throw new InvalidOperationException("不能直接下载文件夹。");
        LibMtp.ProgressCallback? callback = progress is null
            ? null
            : (sent, total, _) =>
            {
                progress((long)sent, (long)total);
                return 0;
            };
        var result = LibMtp.LIBMTP_Get_File_To_File(_device, entry.ItemId, destinationPath, callback, IntPtr.Zero);
        if (result < 0) Throw($"无法从设备读取 {entry.RelativePath}");
    }

    public MtpEntry Upload(string relativeDirectory, string sourcePath, string fileName, Action<long, long>? progress = null)
    {
        var directory = EnsureDirectory(relativeDirectory);
        var existing = Children(directory.StorageId, directory.ItemId, directory.RelativePath)
            .FirstOrDefault(entry => !entry.IsFolder && entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Delete(existing);

        var size = new FileInfo(sourcePath).Length;
        var file = LibMtp.LIBMTP_new_file_t();
        if (file == IntPtr.Zero) throw new IOException("无法创建设备文件结构。");
        try
        {
            // libmtp takes ownership of the filename pointer and frees it with
            // LIBMTP_destroy_file_t.
            var info = new LibMtp.File
            {
                ItemId = 0,
                ParentId = directory.ItemId,
                StorageId = directory.StorageId,
                FileName = Marshal.StringToCoTaskMemUTF8(fileName),
                FileSize = (ulong)size,
                ModificationDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileType = LibMtp.FileTypeUnknown,
                Next = IntPtr.Zero
            };
            Marshal.StructureToPtr(info, file, false);
            LibMtp.ProgressCallback? callback = progress is null
                ? null
                : (sent, total, _) =>
                {
                    progress((long)sent, (long)total);
                    return 0;
                };
            var result = LibMtp.LIBMTP_Send_File_From_File(_device, sourcePath, file, callback, IntPtr.Zero);
            if (result < 0) Throw($"无法写入设备文件 {fileName}");
        }
        finally
        {
            LibMtp.LIBMTP_destroy_file_t(file);
        }

        Invalidate(directory.StorageId, directory.ItemId);
        return Children(directory.StorageId, directory.ItemId, directory.RelativePath)
            .FirstOrDefault(entry => !entry.IsFolder && entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ?? new MtpEntry
            {
                ItemId = 0,
                ParentId = directory.ItemId,
                StorageId = directory.StorageId,
                Name = fileName,
                RelativePath = Normalize($"{directory.RelativePath}/{fileName}"),
                IsFolder = false,
                Size = size
            };
    }

    public void Delete(MtpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (LibMtp.LIBMTP_Delete_Object(_device, entry.ItemId) != 0)
            Throw($"无法删除设备文件 {entry.RelativePath}");
        Invalidate(entry.StorageId, entry.ParentId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LibMtp.LIBMTP_Release_Device(_device);
        Marshal.FreeHGlobal(_rawDevice);
    }

    private void EnumerateChildren(uint storageId, uint parentId, string relativePath, int depth, List<MtpEntry> results)
    {
        if (depth > MaximumDepth) return;
        foreach (var entry in Children(storageId, parentId, relativePath))
        {
            results.Add(entry);
            if (entry.IsFolder)
                EnumerateChildren(entry.StorageId, entry.ItemId, entry.RelativePath, depth + 1, results);
        }
    }

    private (uint StorageId, uint ParentId, string RelativePath)? Resolve(string[] segments)
    {
        var storageId = PrimaryStorage.Id;
        uint parentId = LibMtp.FilesAndFoldersRoot;
        var relativePath = string.Empty;
        foreach (var segment in segments)
        {
            var match = Children(storageId, parentId, relativePath)
                .FirstOrDefault(entry => entry.IsFolder && entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            storageId = match.StorageId;
            parentId = match.ItemId;
            relativePath = match.RelativePath;
        }
        return (storageId, parentId, relativePath);
    }

    private IReadOnlyList<MtpEntry> Children(uint storageId, uint parentId, string relativeParentPath)
    {
        if (_children.TryGetValue((storageId, parentId), out var cached)) return cached;
        var list = LibMtp.LIBMTP_Get_Files_And_Folders(_device, storageId, parentId);
        var entries = new List<MtpEntry>();
        try
        {
            var reference = list;
            while (reference != IntPtr.Zero)
            {
                var next = Marshal.ReadIntPtr(reference, LibMtp.FileNextOffset);
                var file = Marshal.PtrToStructure<LibMtp.File>(reference);
                var name = LibMtp.ToString(file.FileName);
                if (name.Length > 0)
                {
                    entries.Add(new MtpEntry
                    {
                        ItemId = file.ItemId,
                        ParentId = file.ParentId,
                        StorageId = file.StorageId,
                        Name = name,
                        RelativePath = Normalize($"{relativeParentPath}/{name}"),
                        IsFolder = file.FileType == LibMtp.FileTypeFolder,
                        Size = (long)file.FileSize,
                        ModifiedAt = file.ModificationDate > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(file.ModificationDate)
                            : null
                    });
                }
                reference = next;
            }
        }
        finally
        {
            // libmtp has no list destructor; every node is released individually.
            var reference = list;
            while (reference != IntPtr.Zero)
            {
                var next = Marshal.ReadIntPtr(reference, LibMtp.FileNextOffset);
                LibMtp.LIBMTP_destroy_file_t(reference);
                reference = next;
            }
        }
        _children[(storageId, parentId)] = entries;
        return entries;
    }

    private void Invalidate(uint storageId, uint parentId) => _children.Remove((storageId, parentId));

    private void Throw(string message)
    {
        var errors = LibMtp.ReadErrorStack(_device);
        var detail = errors.Count == 0 ? string.Empty : $" ({string.Join("; ", errors)})";
        throw new IOException(message + detail);
    }

    private static string[] Split(string? relativePath) =>
        (relativePath ?? string.Empty)
        .Replace('\\', '/')
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string relativePath) =>
        string.Join('/', Split(relativePath));
}
