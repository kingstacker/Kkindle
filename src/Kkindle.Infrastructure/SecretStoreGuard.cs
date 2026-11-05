using System.Collections.Concurrent;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Remembers settings files whose secrets could not be decrypted because the
/// OS secret store was temporarily unavailable (<see cref="SecretStoreUnavailableException"/>).
/// Such a file was loaded with empty secrets; saving it would overwrite the
/// still-valid encrypted credentials with nothing. Saves to these files are
/// refused for the rest of the process lifetime; a restart after unlocking the
/// Keychain loads them normally again. The flag is static because several
/// store instances can point at the same file.
/// </summary>
internal static class SecretStoreGuard
{
    private static readonly ConcurrentDictionary<string, byte> Unreadable = new(StringComparer.Ordinal);

    public static void MarkUnreadable(string settingsPath) =>
        Unreadable[Path.GetFullPath(settingsPath)] = 0;

    public static void ThrowIfUnreadable(string settingsPath)
    {
        if (Unreadable.ContainsKey(Path.GetFullPath(settingsPath)))
            throw new SecretStoreUnavailableException(UiText.Get(
                "无法访问系统钥匙串，已保存的凭据暂时读不出来。为避免覆盖它们，本次运行不会保存这些设置。请解锁钥匙串后重启 Kkindle。"));
    }
}
