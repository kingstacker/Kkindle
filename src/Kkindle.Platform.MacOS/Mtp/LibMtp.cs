using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kkindle.Platform.MacOS.Mtp;

/// <summary>
/// Minimal libmtp binding. macOS has no MTP driver, so Kindle devices that
/// expose MTP (instead of USB mass storage) can only be reached through
/// libmtp. Calibre uses the same library on macOS.
/// </summary>
internal static class LibMtp
{
    internal const string LibraryName = "libmtp";

    // LIBMTP filetype values (libmtp.h). Folders are type 0.
    internal const int FileTypeFolder = 0;
    internal const int FileTypeUnknown = 44;

    // LIBMTP_FILES_AND_FOLDERS_ROOT
    internal const uint FilesAndFoldersRoot = 0xFFFFFFFF;

    internal const int ErrorNone = 0;

    [ModuleInitializer]
    internal static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(typeof(LibMtp).Assembly, static (name, _, _) =>
        {
            if (name != LibraryName) return IntPtr.Zero;
            var baseDirectory = AppContext.BaseDirectory;
            // The MCP helper lives in Contents/MacOS/mcp while the bundled
            // dylibs stay next to the main executable, so the parent
            // directory is searched as well as the current one.
            var parentDirectory = Path.GetFullPath(Path.Combine(baseDirectory, ".."));
            foreach (var directory in new[] { baseDirectory, parentDirectory })
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(directory, "libmtp.9.dylib"),
                             Path.Combine(directory, "libmtp.dylib"),
                             Path.Combine(directory, "runtimes", "osx-x64", "native", "libmtp.9.dylib")
                         })
                {
                    if (System.IO.File.Exists(candidate)) return NativeLibrary.Load(candidate);
                }
            }
            return NativeLibrary.Load("libmtp.9.dylib");
        });

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceEntry
    {
        public IntPtr Vendor;
        public ushort VendorId;
        public IntPtr Product;
        public ushort ProductId;
        public uint DeviceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawDevice
    {
        public DeviceEntry Entry;
        public uint BusLocation;
        public byte DevNum;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct File
    {
        public uint ItemId;
        public uint ParentId;
        public uint StorageId;
        public IntPtr FileName;
        public ulong FileSize;
        public long ModificationDate;
        public int FileType;
        public IntPtr Next;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceStorage
    {
        public uint Id;
        public ushort StorageType;
        public ushort FilesystemType;
        public ushort AccessCapability;
        public ulong MaxCapacity;
        public ulong FreeSpaceInBytes;
        public ulong FreeSpaceInObjects;
        public IntPtr StorageDescription;
        public IntPtr VolumeIdentifier;
        public IntPtr Next;
        public IntPtr Prev;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Error
    {
        public int ErrorNumber;
        public IntPtr ErrorText;
        public IntPtr Next;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(ulong sent, ulong total, IntPtr data);

    // LIBMTP_mtpdevice_struct: object_bitsize, params, usbinfo, storage, ...
    // Only the storage pointer is needed; it follows three 8-byte fields.
    internal const int DeviceStorageOffset = 24;

    // libmtp_file_t::next offset (8 bytes of ids + utf8 pointer + size + time + enum + padding).
    internal const int FileNextOffset = 48;

    [DllImport(LibraryName)] internal static extern void LIBMTP_Init();
    [DllImport(LibraryName)] internal static extern int LIBMTP_Detect_Raw_Devices(out IntPtr devices, out int count);
    [DllImport(LibraryName)] internal static extern IntPtr LIBMTP_Open_Raw_Device_Uncached(IntPtr rawDevice);
    [DllImport(LibraryName)] internal static extern void LIBMTP_Release_Device(IntPtr device);
    [DllImport(LibraryName)] internal static extern IntPtr LIBMTP_Get_Modelname(IntPtr device);
    [DllImport(LibraryName)] internal static extern IntPtr LIBMTP_Get_Manufacturername(IntPtr device);
    [DllImport(LibraryName)] internal static extern IntPtr LIBMTP_Get_Serialnumber(IntPtr device);
    [DllImport(LibraryName)] internal static extern int LIBMTP_Get_Storage(IntPtr device, int sortBy);
    [DllImport(LibraryName)] internal static extern IntPtr LIBMTP_Get_Files_And_Folders(IntPtr device, uint storageId, uint parentId);
    [DllImport(LibraryName)] internal static extern IntPtr LIBMTP_new_file_t();
    [DllImport(LibraryName)] internal static extern void LIBMTP_destroy_file_t(IntPtr file);
    [DllImport(LibraryName)] internal static extern int LIBMTP_Send_File_From_File(
        IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr file,
        ProgressCallback? progress, IntPtr data);
    [DllImport(LibraryName)] internal static extern int LIBMTP_Get_File_To_File(
        IntPtr device, uint fileId, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ProgressCallback? progress, IntPtr data);
    [DllImport(LibraryName)] internal static extern int LIBMTP_Delete_Object(IntPtr device, uint objectId);
    [DllImport(LibraryName)] internal static extern uint LIBMTP_Create_Folder(
        IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint parentId, uint storageId);
    [DllImport(LibraryName)] internal static extern void LIBMTP_Dump_Errorstack(IntPtr device);
    [DllImport(LibraryName)] internal static extern void LIBMTP_FreeMemory(IntPtr memory);

    internal static string ToString(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;

    internal static IReadOnlyList<string> ReadErrorStack(IntPtr device)
    {
        if (device == IntPtr.Zero) return [];
        var messages = new List<string>();
        // errorstack is the fifth 8-byte field of LIBMTP_mtpdevice_struct.
        var error = Marshal.ReadIntPtr(device, 32);
        while (error != IntPtr.Zero && messages.Count < 8)
        {
            var entry = Marshal.PtrToStructure<Error>(error);
            messages.Add($"[{entry.ErrorNumber}] {ToString(entry.ErrorText)}");
            error = entry.Next;
        }
        return messages;
    }
}
