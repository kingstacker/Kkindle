using System.Security.Cryptography;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Kkindle.Core;
using CipherMode = Javax.Crypto.CipherMode;
using IKey = Java.Security.IKey;
using KeyStore = Java.Security.KeyStore;

namespace Kkindle.AndroidHead;

/// <summary>
/// AES-GCM encryption whose key is itself sealed by the hardware-backed Android
/// Keystore, so a copy of the app's files alone cannot decrypt stored secrets.
/// </summary>
/// <remarks>
/// Deliberately standalone rather than deriving from <c>AesGcmSecretProtector</c>:
/// that base uses a named <see cref="Mutex"/>, which is not guaranteed on Android,
/// and the protected format never travels between devices anyway.
/// </remarks>
public sealed class AndroidSecretProtector : ISecretProtector
{
    private static readonly object Gate = new();
    private static readonly byte[] Header = [0x4B, 0x4B, 0x53, 0x01];
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string KeystoreProvider = "AndroidKeyStore";
    private const string KeystoreAlias = "kkindle-secret-wrapper";
    private const string WrapTransformation = "AES/GCM/NoPadding";

    private readonly string _keyDirectory;
    private readonly string _keyPath;
    private readonly string _wrappedKeyPath;

    public AndroidSecretProtector(string keyDirectory)
    {
        _keyDirectory = keyDirectory;
        _keyPath = Path.Combine(keyDirectory, "secret.key");
        _wrappedKeyPath = Path.Combine(keyDirectory, "secret.key.wrapped");
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
            try
            {
                Directory.CreateDirectory(_keyDirectory);
                if (File.Exists(_wrappedKeyPath))
                {
                    if (TryUnwrapStoredKey() is { } stored) return stored;
                    // The wrapping key is gone for good (restored from a backup
                    // onto another phone, lock screen reset): the secrets it
                    // protected are unreadable either way. Starting a new key keeps
                    // settings savable; the old file is kept beside it.
                    File.Move(_wrappedKeyPath, _wrappedKeyPath + $".lost-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
                }

                // Keys from earlier builds were stored in the clear; keep their
                // secrets readable by moving the same key under the Keystore.
                var key = ReadLegacyKey() ?? RandomNumberGenerator.GetBytes(KeySize);
                var temporary = _wrappedKeyPath + ".tmp";
                File.WriteAllText(temporary, Convert.ToBase64String(Wrap(key)));
                File.Move(temporary, _wrappedKeyPath, overwrite: true);
                File.Delete(_keyPath);
                return key;
            }
            catch (Java.Lang.Exception exception)
            {
                // Callers handle CryptographicException; a raw Java exception would
                // escape their handling and crash the settings page.
                throw new CryptographicException("Android Keystore failed: " + exception.Message, exception);
            }
        }
    }

    /// <returns>Null only when the stored key can never be recovered.</returns>
    private byte[]? TryUnwrapStoredKey()
    {
        byte[] wrapped;
        try
        {
            wrapped = Convert.FromBase64String(File.ReadAllText(_wrappedKeyPath).Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        if (wrapped.Length < 2 || wrapped[0] == 0 || wrapped.Length <= 1 + wrapped[0] + TagSize) return null;
        try
        {
            return Unwrap(wrapped);
        }
        catch (Exception exception) when (exception is AEADBadTagException
            or Android.Security.Keystore.KeyPermanentlyInvalidatedException
            or UnrecoverableKeyException)
        {
            return null;
        }
    }

    private byte[]? ReadLegacyKey()
    {
        if (!File.Exists(_keyPath)) return null;
        try
        {
            var stored = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
            return stored.Length == KeySize ? stored : null;
        }
        catch (FormatException)
        {
            // A corrupt key only invalidates stored secrets.
            return null;
        }
    }

    private static IKey GetKeystoreKey()
    {
        var keyStore = KeyStore.GetInstance(KeystoreProvider)
            ?? throw new CryptographicException("Android Keystore is unavailable.");
        keyStore.Load(null);
        if (keyStore.ContainsAlias(KeystoreAlias))
            return keyStore.GetKey(KeystoreAlias, null)
                ?? throw new CryptographicException("The Keystore entry could not be read.");

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreProvider)
            ?? throw new CryptographicException("Android Keystore cannot generate keys.");
        generator.Init(new KeyGenParameterSpec.Builder(KeystoreAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(KeySize * 8)!
            .Build());
        return generator.GenerateKey()
            ?? throw new CryptographicException("Android Keystore did not return a key.");
    }

    private static byte[] Wrap(byte[] key)
    {
        var cipher = Cipher.GetInstance(WrapTransformation)
            ?? throw new CryptographicException("AES-GCM is unavailable.");
        cipher.Init(CipherMode.EncryptMode, GetKeystoreKey());
        var iv = cipher.GetIV() ?? throw new CryptographicException("The Keystore did not return an IV.");
        var sealedKey = cipher.DoFinal(key) ?? throw new CryptographicException("The key could not be wrapped.");
        return [(byte)iv.Length, .. iv, .. sealedKey];
    }

    private static byte[] Unwrap(byte[] wrapped)
    {
        var ivLength = wrapped[0];
        var cipher = Cipher.GetInstance(WrapTransformation)
            ?? throw new CryptographicException("AES-GCM is unavailable.");
        cipher.Init(CipherMode.DecryptMode, GetKeystoreKey(),
            new GCMParameterSpec(TagSize * 8, wrapped, 1, ivLength));
        return cipher.DoFinal(wrapped, 1 + ivLength, wrapped.Length - 1 - ivLength)
            ?? throw new CryptographicException("The key could not be unwrapped.");
    }
}
