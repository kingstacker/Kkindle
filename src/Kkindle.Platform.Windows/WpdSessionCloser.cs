using System.Runtime.InteropServices;

namespace Kkindle.Platform.Windows;

internal static class WpdSessionCloser
{
    private static readonly Guid PortableDeviceClassId = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    private static readonly Guid PortableDeviceValuesClassId = new("0C15D503-D017-47CE-9016-7B3F978721CC");

    public static void CloseSession(string shellPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devicePath = GetPortableDevicePath(shellPath);
        IPortableDevice? device = null;
        object? clientInfo = null;
        try
        {
            device = CreateComObject<IPortableDevice>(PortableDeviceClassId);
            clientInfo = CreateComObject<object>(PortableDeviceValuesClassId);
            var openResult = device.Open(devicePath, clientInfo);
            if (openResult < 0)
                throw new COMException($"无法打开设备的原生 WPD 会话（HRESULT 0x{openResult:X8}）。", openResult);

            cancellationToken.ThrowIfCancellationRequested();
            var closeResult = device.Close();
            if (closeResult < 0)
                throw new COMException($"无法关闭设备的原生 WPD 会话（HRESULT 0x{closeResult:X8}）。", closeResult);
        }
        finally
        {
            Release(clientInfo);
            Release(device);
        }
    }

    private static string GetPortableDevicePath(string shellPath)
    {
        var start = shellPath.IndexOf(@"\\?\", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new IOException("无法从设备的 Shell 路径确定 WPD 设备路径。");
        return shellPath[start..];
    }

    private static T CreateComObject<T>(Guid classId) where T : class
    {
        var type = Type.GetTypeFromCLSID(classId, throwOnError: true)
            ?? throw new COMException($"Windows 未注册 COM 类 {classId}。");
        return (T)(Activator.CreateInstance(type)
            ?? throw new COMException($"无法创建 COM 类 {classId}。"));
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevice
    {
        [PreserveSig]
        int Open(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Interface)] object clientInfo);

        [PreserveSig] int SendCommand(uint flags, IntPtr parameters, out IntPtr results);
        [PreserveSig] int Content(out IntPtr content);
        [PreserveSig] int Capabilities(out IntPtr capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }
}
