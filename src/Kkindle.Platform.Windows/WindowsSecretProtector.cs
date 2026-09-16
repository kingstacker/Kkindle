using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.Windows;

/// <summary>
/// DPAPI-backed <see cref="ISecretProtector"/>: secrets are encrypted with the
/// current Windows user's key, so copying a settings file to another machine
/// or account yields nothing.
///
/// The blob format is whatever CryptProtectData produces. It must stay
/// byte-compatible with what earlier Kkindle versions wrote, otherwise users
/// silently lose their stored API key and SMTP password on
/// upgrade — this class was extracted verbatim from AiServices.cs for that
/// reason, including the now-generic description string.
/// </summary>
public sealed class WindowsSecretProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    // DPAPI treats the description as metadata that plays no part in
    // decryption; keep the stable value for existing protected settings.
    private const string BlobDescription = "Kkindle AI API Key";

    public byte[] Protect(byte[] value) => Transform(value, protect: true);

    public byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (value.Length == 0) return [];
        var input = CreateBlob(value);
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, BlobDescription, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[value.Length], 0, input.Data, value.Length);
                Marshal.FreeHGlobal(input.Data);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob { Length = value.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
