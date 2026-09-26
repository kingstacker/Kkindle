using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using Kkindle.Core;
using Kkindle.Platform.Common;

namespace Kkindle.McpServer;

/// <summary>
/// Reads the same Windows DPAPI blobs written by the desktop head's
/// WindowsSecretProtector. The MCP server is intentionally kept independent
/// from the Avalonia/Windows head, so the small compatibility boundary lives
/// here. Non-Windows heads use their own platform secret stores and are not
/// silently treated as decryptable by this standalone server.
/// </summary>
internal sealed class McpSecretProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private const string BlobDescription = "Kkindle AI API Key";
    private static readonly Lazy<ISecretProtector> PlatformProtector = new(
        static () => new McpKeyringSecretProtector());

    public byte[] Protect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OperatingSystem.IsWindows()
            ? Transform(value, protect: true)
            : PlatformProtector.Value.Protect(value);
    }

    public byte[] Unprotect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OperatingSystem.IsWindows()
            ? Transform(value, protect: false)
            : PlatformProtector.Value.Unprotect(value);
    }

    private sealed class McpKeyringSecretProtector : AesGcmSecretProtector
    {
        private const string LinuxApplication = "Kkindle";
        private const string LinuxPurpose = "secret-protection-key";
        private const string MacService = "Kkindle.SecretProtectionKey";

        protected override byte[] GetOrCreateKey()
        {
            if (OperatingSystem.IsLinux()) return GetLinuxKey();
            if (OperatingSystem.IsMacOS()) return GetMacKey();
            throw new PlatformNotSupportedException("MCP secret decryption is not configured for this platform.");
        }

        private static byte[] GetLinuxKey()
        {
            var lookupArgs = new[]
            {
                "lookup", "application", LinuxApplication, "purpose", LinuxPurpose
            };
            var lookup = RunProcess("secret-tool", lookupArgs, allowFailure: true);
            if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.Output))
                return ParseStoredKey(lookup.Output);
            if (lookup.ExitCode == 0 || !IsMissingSecret(lookup.Error))
                throw new InvalidOperationException(
                    $"Unable to read the existing Kkindle key from Secret Service; no new key was created. {lookup.Error}");

            var key = CreateKey();
            var store = RunProcess(
                "secret-tool",
                ["store", "--label=Kkindle secret protection key", "application", LinuxApplication, "purpose", LinuxPurpose],
                Convert.ToBase64String(key) + Environment.NewLine,
                allowFailure: true);
            var racedLookup = RunProcess("secret-tool", lookupArgs, allowFailure: true);
            if (racedLookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(racedLookup.Output))
                return ParseStoredKey(racedLookup.Output);
            if (store.ExitCode == 0)
                throw new InvalidOperationException("Secret Service did not retain the generated Kkindle key.");
            throw new InvalidOperationException($"Unable to store the Kkindle key in Secret Service: {store.Error}");
        }

        private static byte[] GetMacKey()
        {
            var account = Environment.UserName;
            var lookupArgs = new[]
            {
                "find-generic-password", "-s", MacService, "-a", account, "-w"
            };
            var lookup = RunProcess("/usr/bin/security", lookupArgs, allowFailure: true);
            if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.Output))
                return ParseStoredKey(lookup.Output);
            if (lookup.ExitCode == 0 || !IsMissingSecret(lookup.Error))
                throw new InvalidOperationException(
                    $"Unable to read the existing Kkindle key from Keychain; no new key was created. {lookup.Error}");

            var key = CreateKey();
            // security warns that -w with a value on its argv is insecure.
            // Interactive mode receives the command over stdin, keeping the
            // generated key out of the process list.
            var command = "add-generic-password -s " + QuoteArgument(MacService)
                + " -a " + QuoteArgument(account)
                + " -w " + QuoteArgument(Convert.ToBase64String(key));
            var store = RunProcess("/usr/bin/security", ["-i"], command + Environment.NewLine, allowFailure: true);
            var racedLookup = RunProcess("/usr/bin/security", lookupArgs, allowFailure: true);
            if (racedLookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(racedLookup.Output))
                return ParseStoredKey(racedLookup.Output);
            if (store.ExitCode == 0 && string.IsNullOrWhiteSpace(store.Error))
                throw new InvalidOperationException("Keychain did not retain the generated Kkindle key.");
            throw new InvalidOperationException($"Unable to store the Kkindle key in Keychain: {store.Error}");
        }

        private static bool IsMissingSecret(string error) =>
            string.IsNullOrWhiteSpace(error)
            || error.Contains("no such secret", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no matching secret", StringComparison.OrdinalIgnoreCase)
            || error.Contains("item could not be found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("errsecitemnotfound", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase);

        private static string QuoteArgument(string value) =>
            "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        private static CommandResult RunProcess(
            string executable,
            IReadOnlyList<string> arguments,
            string? standardInput = null,
            bool allowFailure = false)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            try
            {
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (standardInput is not null)
                {
                    process.StandardInput.Write(standardInput);
                    process.StandardInput.Close();
                }
                process.WaitForExit();
                Task.WaitAll(outputTask, errorTask);
                var result = new CommandResult(process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
                if (!allowFailure && result.ExitCode != 0)
                    throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {result.Error}");
                return result;
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    $"Kkindle requires {Path.GetFileName(executable)} and an available platform keyring.",
                    exception);
            }
        }

        private sealed record CommandResult(int ExitCode, string Output, string Error);
    }

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (value.Length == 0) return [];

        var input = CreateBlob(value);
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref input,
                    BlobDescription,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!succeeded)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

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
