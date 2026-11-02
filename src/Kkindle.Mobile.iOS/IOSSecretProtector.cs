using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.iOSHead;

/// <summary>
/// AES-GCM encryption for API keys and SMTP passwords. The key lives inside the
/// app sandbox under Library, which iOS excludes from the Files app and
/// protects with Data Protection while the device is locked.
/// </summary>
/// <remarks>
/// Deliberately standalone rather than deriving from <c>AesGcmSecretProtector</c>:
/// that base uses a named <see cref="Mutex"/>, which iOS does not guarantee, and
/// the protected format never travels between devices anyway.
/// </remarks>
public sealed class IOSSecretProtector : ISecretProtector
{
    private static readonly object Gate = new();
    private static readonly byte[] Header = [0x4B, 0x4B, 0x53, 0x01];
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyDirectory;
    private readonly string _keyPath;

    public IOSSecretProtector(string keyDirectory)
    {
        _keyDirectory = keyDirectory;
        _keyPath = Path.Combine(keyDirectory, "secret.key");
    }

    public byte[] Protect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return [];

        var key = GetOrCreateKey();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var output = new byte[Header.Length + NonceSize + TagSize + value.Length];
            Header.CopyTo(output, 0);
            nonce.CopyTo(output, Header.Length);
            var tag = output.AsSpan(Header.Length + NonceSize, TagSize);
            var ciphertext = output.AsSpan(Header.Length + NonceSize + TagSize);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, value, ciphertext, tag, Header);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Unprotect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return [];
        if (value.Length < Header.Length + NonceSize + TagSize
            || !value.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            throw new CryptographicException("The protected secret has an unsupported format.");
        }

        var key = GetOrCreateKey();
        try
        {
            var nonce = value.AsSpan(Header.Length, NonceSize);
            var tag = value.AsSpan(Header.Length + NonceSize, TagSize);
            var ciphertext = value.AsSpan(Header.Length + NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Header);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] GetOrCreateKey()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(_keyDirectory);
            if (File.Exists(_keyPath))
            {
                try
                {
                    var stored = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
                    if (stored.Length == KeySize) return stored;
                }
                catch (FormatException)
                {
                    // A corrupt key only invalidates stored secrets.
                }
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllText(_keyPath, Convert.ToBase64String(key));
            return key;
        }
    }
}
