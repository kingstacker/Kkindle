using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.Platform.Common;

public abstract class AesGcmSecretProtector : ISecretProtector
{
    private const string KeyInitializationMutexName = "Kkindle.SecretProtectionKey.Initialize";
    private static ReadOnlySpan<byte> Header => [0x4B, 0x4B, 0x53, 0x01];
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public byte[] Protect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return [];
        var key = GetOrCreateKeySafely();
        ValidateKey(key);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var output = new byte[Header.Length + NonceSize + TagSize + value.Length];
            Header.CopyTo(output);
            nonce.CopyTo(output.AsSpan(Header.Length, NonceSize));
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
        if (value.Length < Header.Length + NonceSize + TagSize || !value.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new CryptographicException("The protected secret has an unsupported format.");
        var key = GetOrCreateKeySafely();
        ValidateKey(key);
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

    protected abstract byte[] GetOrCreateKey();

    private byte[] GetOrCreateKeySafely()
    {
        using var mutex = new Mutex(initiallyOwned: false, KeyInitializationMutexName);
        try
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            return GetOrCreateKey();
        }
        finally
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
    }

    protected static byte[] ParseStoredKey(string encoded)
    {
        try
        {
            var key = Convert.FromBase64String(encoded.Trim());
            ValidateKey(key);
            return key;
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The operating-system key store returned an invalid Kkindle key.", exception);
        }
    }

    protected static byte[] CreateKey() => RandomNumberGenerator.GetBytes(KeySize);

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new CryptographicException($"Kkindle requires a {KeySize}-byte secret-protection key.");
    }
}
