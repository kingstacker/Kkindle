using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>One installation identity, protected by the OS and excluded from portable data.</summary>
internal sealed class DeviceIdentityStore(AppPaths paths, ISecretProtector protector)
{
    private Guid? _identity;
    public async Task<DeviceEnrollmentRequest> GetAsync(CancellationToken token)
    {
        paths.EnsureDirectories();
        if (_identity is null)
        {
            using var lease = await SettingsWriteLock.AcquireAsync(paths, token);
            var path = Path.Combine(paths.Data, "device-identity.bin");
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length is < 16 or > 16384) throw new InvalidOperationException("本机设备身份文件无效，请联系管理员。");
                byte[] plain;
                try { plain = protector.Unprotect(await File.ReadAllBytesAsync(path, token)); }
                catch (CryptographicException) { throw new InvalidOperationException("无法读取本机设备身份，请检查系统密钥库。设备身份不会随备份迁移。"); }
                try
                {
                    if (plain.Length != 16 || new Guid(plain) == Guid.Empty) throw new InvalidOperationException("本机设备身份文件无效。");
                    _identity = new Guid(plain);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            else
            {
                var identity = Guid.NewGuid();
                var plain = identity.ToByteArray();
                byte[] encrypted;
                try { encrypted = protector.Protect(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, encrypted, token);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.Move(temporary, path, overwrite: false);
                    _identity = identity;
                }
                finally { File.Delete(temporary); }
            }
        }
        var platform = OperatingSystem.IsIOS() ? "iOS" : OperatingSystem.IsAndroid() ? "Android"
            : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        return new() { DeviceId = _identity.Value, DeviceName = platform + " · " + _identity.Value.ToString("N")[..8], Platform = platform };
    }
}
