using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Kkindle.Core;

namespace Kkindle.Platform.Windows;

/// <summary>
/// Sends a file through the native WPD stream API instead of Shell.Application.CopyHere.
/// The latter is allowed to ignore its "no UI" flag and can show the Windows copy dialog.
/// </summary>
internal static class WpdNativeTransfer
{
    private static readonly Guid PortableDeviceClassId = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    private static readonly Guid PortableDeviceValuesClassId = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    private static readonly Guid WpdObjectPropertySet = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");

    private static readonly PropertyKey WpdObjectParentId = new(WpdObjectPropertySet, 3);
    private static readonly PropertyKey WpdObjectName = new(WpdObjectPropertySet, 4);
    private static readonly PropertyKey WpdObjectSize = new(WpdObjectPropertySet, 11);
    private static readonly PropertyKey WpdObjectOriginalFileName = new(WpdObjectPropertySet, 12);

    public static void SendFile(
        string shellPath,
        string parentObjectId,
        string sourcePath,
        string fileName,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentObjectId))
            throw new IOException("Kindle 目标目录的 WPD 对象 ID 为空。");
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['\\', '/']) >= 0)
            throw new InvalidOperationException("Kindle 目标文件名无效。");

        var sourceInfo = new FileInfo(sourcePath);
        var devicePath = GetPortableDevicePath(shellPath);
        IPortableDevice? device = null;
        IPortableDeviceValues? clientInfo = null;
        IPortableDeviceContent? content = null;
        IPortableDeviceValues? properties = null;
        IStream? destination = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            device = CreateComObject<IPortableDevice>(PortableDeviceClassId);
            clientInfo = CreateComObject<IPortableDeviceValues>(PortableDeviceValuesClassId);
            ThrowIfFailed(device.Open(devicePath, clientInfo), "打开 Kindle 的原生 WPD 会话");

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed(device.Content(out content), "获取 Kindle 的 WPD 内容接口");

            properties = CreateComObject<IPortableDeviceValues>(PortableDeviceValuesClassId);
            SetStringValue(properties, WpdObjectParentId, parentObjectId, "目标目录");
            SetUnsignedLargeIntegerValue(properties, WpdObjectSize, checked((ulong)sourceInfo.Length), "文件大小");
            SetStringValue(properties, WpdObjectOriginalFileName, fileName, "原始文件名");
            SetStringValue(properties, WpdObjectName, fileName, "文件名");

            uint optimalWriteBufferSize = 0;
            ThrowIfFailed(
                content.CreateObjectWithPropertiesAndData(
                    properties,
                    out destination,
                    ref optimalWriteBufferSize,
                    IntPtr.Zero),
                "创建 Kindle 目标文件");
            if (destination is null)
                throw new IOException("Kindle 未返回文件写入数据流。");

            CopyToDevice(
                sourcePath,
                sourceInfo.Length,
                destination,
                optimalWriteBufferSize,
                progress,
                fileName,
                cancellationToken);
        }
        catch
        {
            if (destination is not null)
            {
                try { destination.Revert(); }
                catch { }
            }
            throw;
        }
        finally
        {
            Release(destination);
            Release(properties);
            Release(content);
            Release(clientInfo);
            Release(device);
        }
    }

    private static void CopyToDevice(
        string sourcePath,
        long totalBytes,
        IStream destination,
        uint optimalWriteBufferSize,
        IProgress<TransferProgress>? progress,
        string displayName,
        CancellationToken cancellationToken)
    {
        var bufferSize = optimalWriteBufferSize is >= 4 * 1024 and <= 4 * 1024 * 1024
            ? checked((int)optimalWriteBufferSize)
            : 256 * 1024;
        var buffer = new byte[bufferSize];
        var bytesWritten = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            using var source = OpenSourceStream(sourcePath, bufferSize, cancellationToken);

            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;

                Marshal.WriteInt32(bytesWritten, 0);
                destination.Write(buffer, read, bytesWritten);
                var written = Marshal.ReadInt32(bytesWritten);
                if (written != read)
                    throw new IOException($"Kindle 数据流写入不完整（请求 {read} 字节，实际 {written} 字节）。");

                copied += written;
                progress?.Report(new TransferProgress(copied, totalBytes, $"正在发送 {displayName}"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            destination.Commit(0);
        }
        finally
        {
            Marshal.FreeHGlobal(bytesWritten);
        }
    }

    private static FileStream OpenSourceStream(
        string sourcePath,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        const int retryDelayMilliseconds = 100;
        IOException? lastSharingException = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize,
                    FileOptions.SequentialScan);
            }
            catch (IOException exception) when (IsSharingViolation(exception) && attempt + 1 < maxAttempts)
            {
                lastSharingException = exception;
                Thread.Sleep(retryDelayMilliseconds);
            }
        }

        throw lastSharingException ?? new IOException("无法打开 Kindle 传输源文件。");
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33;
    }

    private static void SetStringValue(
        IPortableDeviceValues values,
        PropertyKey key,
        string value,
        string description)
    {
        var result = values.SetStringValue(ref key, value);
        ThrowIfFailed(result, $"设置 WPD {description}");
    }

    private static void SetUnsignedLargeIntegerValue(
        IPortableDeviceValues values,
        PropertyKey key,
        ulong value,
        string description)
    {
        var result = values.SetUnsignedLargeIntegerValue(ref key, value);
        ThrowIfFailed(result, $"设置 WPD {description}");
    }

    private static string GetPortableDevicePath(string shellPath)
    {
        var start = shellPath.IndexOf(@"\\?\", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new IOException("无法从 Kindle 的 Shell 路径确定 WPD 设备路径。");
        return shellPath[start..];
    }

    private static T CreateComObject<T>(Guid classId) where T : class
    {
        var type = Type.GetTypeFromCLSID(classId, throwOnError: true)
            ?? throw new COMException($"Windows 未注册 COM 类 {classId}。");
        return (T)(Activator.CreateInstance(type)
            ?? throw new COMException($"无法创建 COM 类 {classId}。"));
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"{operation}失败（HRESULT 0x{result:X8}）。", result);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevice
    {
        [PreserveSig]
        int Open(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Interface)] IPortableDeviceValues clientInfo);

        [PreserveSig] int SendCommand(uint flags, IntPtr parameters, out IntPtr results);
        [PreserveSig] int Content([MarshalAs(UnmanagedType.Interface)] out IPortableDeviceContent content);
        [PreserveSig] int Capabilities(out IntPtr capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceContent
    {
        [PreserveSig]
        int EnumObjects(
            uint flags,
            [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId,
            IntPtr filter,
            IntPtr enumObjectIds);

        [PreserveSig] int Properties(out IntPtr properties);
        [PreserveSig] int Transfer(out IntPtr resources);
        [PreserveSig] int CreateObjectWithPropertiesOnly(IntPtr values, IntPtr objectId);

        [PreserveSig]
        int CreateObjectWithPropertiesAndData(
            [MarshalAs(UnmanagedType.Interface)] IPortableDeviceValues values,
            [MarshalAs(UnmanagedType.Interface)] out IStream data,
            ref uint optimalWriteBufferSize,
            IntPtr cookie);

        [PreserveSig] int Delete(uint options, IntPtr objectIds, out IntPtr results);
        [PreserveSig] int GetObjectIDsFromPersistentUniqueIDs(IntPtr ids, out IntPtr objectIds);
        [PreserveSig] int Cancel();
        [PreserveSig] int Move(IntPtr ids, [MarshalAs(UnmanagedType.LPWStr)] string destinationId, out IntPtr results);
        [PreserveSig] int Copy(IntPtr ids, [MarshalAs(UnmanagedType.LPWStr)] string destinationId, out IntPtr results);
    }

    [ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
        [PreserveSig] int SetSignedIntegerValue(ref PropertyKey key, int value);
        [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int value);
        [PreserveSig] int SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
        [PreserveSig] int SetSignedLargeIntegerValue(ref PropertyKey key, long value);
        [PreserveSig] int GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
        [PreserveSig] int SetFloatValue(ref PropertyKey key, float value);
        [PreserveSig] int GetFloatValue(ref PropertyKey key, out float value);
        [PreserveSig] int SetErrorValue(ref PropertyKey key, int value);
        [PreserveSig] int GetErrorValue(ref PropertyKey key, out int value);
        [PreserveSig] int SetKeyValue(ref PropertyKey key, ref PropertyKey value);
        [PreserveSig] int GetKeyValue(ref PropertyKey key, out PropertyKey value);
        [PreserveSig] int SetBoolValue(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] bool value);
        [PreserveSig] int GetBoolValue(ref PropertyKey key, [MarshalAs(UnmanagedType.Bool)] out bool value);
        [PreserveSig] int SetIUnknownValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIUnknownValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetGuidValue(ref PropertyKey key, ref Guid value);
        [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
        [PreserveSig] int SetBufferValue(ref PropertyKey key, IntPtr value, uint valueSize);
        [PreserveSig] int GetBufferValue(ref PropertyKey key, out IntPtr value, out uint valueSize);
        [PreserveSig] int SetIPortableDeviceValuesValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIPortableDeviceValuesValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetIPortableDeviceKeyCollectionValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIPortableDeviceKeyCollectionValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetIPortableDeviceValuesCollectionValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIPortableDeviceValuesCollectionValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int RemoveValue(ref PropertyKey key);
        [PreserveSig] int CopyValuesFromPropertyStore(IntPtr store);
        [PreserveSig] int CopyValuesToPropertyStore(IntPtr store);
        [PreserveSig] int Clear();
    }
}
