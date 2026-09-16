using System.Runtime.InteropServices;

namespace Kkindle.Platform.Windows;

internal static class ShellFileOperation
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofNoErrorUi = 0x0400;

    public static void DeletePermanently(object shellFolderItem)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { DeleteOnStaThread(shellFolderItem); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("等待 Windows 删除设备文件超时。");
        if (failure is not null) throw new IOException("Windows 无法删除设备文件。", failure);
    }

    public static void CopyToLocal(
        object shellFolderItem,
        string destinationPath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("设备文件导出目录无效。"));
        var temporary = $"{destination}.{Guid.NewGuid():N}.kkindle-part";
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CopyOnStaThread(shellFolderItem, temporary, cancellationToken); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120)))
            throw new TimeoutException("等待 Windows 读取设备文件超时。");
        if (failure is OperationCanceledException)
            throw failure;
        if (failure is not null)
            throw new IOException("Windows 无法从 MTP 设备读取文件。", failure);

        try
        {
            if (!File.Exists(temporary))
                throw new IOException("Windows 未生成设备文件副本。");
            if (expectedSize > 0 && new FileInfo(temporary).Length != expectedSize)
                throw new IOException("Windows 读取的设备文件大小不完整。");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void DeleteOnStaThread(object shellFolderItem)
    {
        var itemGuid = typeof(IShellItem).GUID;
        var unknown = Marshal.GetIUnknownForObject(shellFolderItem);
        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetIDListFromObject(unknown, out itemIdList));
        }
        finally
        {
            Marshal.Release(unknown);
        }
        IShellItem item;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(itemIdList, ref itemGuid, out item));
        }
        finally
        {
            if (itemIdList != IntPtr.Zero) Marshal.FreeCoTaskMem(itemIdList);
        }
        var operation = (IFileOperation)new FileOperation();
        try
        {
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofNoErrorUi));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted) throw new OperationCanceledException("设备文件删除已取消。");
        }
        finally
        {
            if (Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
            if (Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }

    private static void CopyOnStaThread(
        object shellFolderItem,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var itemGuid = typeof(IShellItem).GUID;
        var unknown = Marshal.GetIUnknownForObject(shellFolderItem);
        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetIDListFromObject(unknown, out itemIdList));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        IShellItem? source = null;
        IShellItem? destination = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(itemIdList, ref itemGuid, out source));
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                Path.GetDirectoryName(temporaryPath)
                    ?? throw new InvalidOperationException("设备文件导出目录无效。"),
                IntPtr.Zero,
                ref itemGuid,
                out destination));

            var operation = (IFileOperation)new FileOperation();
            try
            {
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofNoErrorUi));
                Marshal.ThrowExceptionForHR(operation.CopyItem(
                    source,
                    destination,
                    Path.GetFileName(temporaryPath),
                    IntPtr.Zero));
                Marshal.ThrowExceptionForHR(operation.PerformOperations());
                Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
                if (aborted) throw new OperationCanceledException("设备文件读取已取消。", cancellationToken);
            }
            finally
            {
                if (Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
            }
        }
        finally
        {
            if (itemIdList != IntPtr.Zero) Marshal.FreeCoTaskMem(itemIdList);
            if (destination is not null && Marshal.IsComObject(destination)) Marshal.FinalReleaseComObject(destination);
            if (source is not null && Marshal.IsComObject(source)) Marshal.FinalReleaseComObject(source);
        }
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetIDListFromObject(IntPtr unknown, out IntPtr itemIdList);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(
        IntPtr itemIdList,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private class FileOperation;

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint nameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, [MarshalAs(UnmanagedType.Interface)] IShellItem destination);
        [PreserveSig] int CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string copyName, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, [MarshalAs(UnmanagedType.Interface)] IShellItem destination);
        [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
