using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    // SYNC3 keeps the actual byte length inside authenticated ciphertext. The
    // public framing exposes only Padme(length + 8), never the actual length.
    // Old SYNC1/SYNC2 remain read-only formats in DecryptBlobToPathAsync.
    private static long PaddedBlobPayloadLength(long originalLength)
    {
        if (originalLength < 0 || originalLength > long.MaxValue - sizeof(long))
            throw new InvalidDataException("云端同步对象的原始长度无效。");
        var length = (ulong)(originalLength + sizeof(long));
        var exponent = BitOperations.Log2(length);
        var exponentBits = exponent == 0 ? 1 : BitOperations.Log2((uint)exponent) + 1;
        var discardedBits = Math.Max(0, exponent - exponentBits);
        var unit = 1UL << discardedBits;
        var padded = (length + unit - 1) & ~(unit - 1);
        if (padded > long.MaxValue) throw new InvalidDataException("云端同步对象的补齐长度无效。");
        return (long)padded;
    }

    private async Task EncryptPaddedBlobToPathAsync(Stream input, string targetPath, string passphrase,
        CancellationToken token)
    {
        if (!input.CanSeek) throw new InvalidOperationException("加密同步对象需要可定位的本地文件流。");
        var originalLength = input.Length - input.Position;
        var payloadLength = PaddedBlobPayloadLength(originalLength);
        var material = GetEncryptionMaterial(passphrase);
        await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, BlobEncryptionChunkBytes, useAsync: true);
        await output.WriteAsync(PaddedEncryptionMagic, token);
        await output.WriteAsync(material.Salt, token);
        await WriteInt32Async(output, BlobEncryptionChunkBytes, token);

        var plaintext = new byte[BlobEncryptionChunkBytes];
        var ciphertext = new byte[BlobEncryptionChunkBytes];
        var nonce = new byte[EncryptionNonceBytes];
        var tag = new byte[EncryptionTagBytes];
        var remainingPayload = payloadLength;
        var remainingOriginal = originalLength;
        var chunkIndex = 0;
        try
        {
            using var aes = new AesGcm(material.Key, EncryptionTagBytes);
            while (remainingPayload > 0)
            {
                var length = (int)Math.Min(BlobEncryptionChunkBytes, remainingPayload);
                var offset = 0;
                if (chunkIndex == 0)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(plaintext, originalLength);
                    offset = sizeof(long);
                }
                var bookBytes = (int)Math.Min(remainingOriginal, length - offset);
                await ReadExactlyAsync(input, plaintext.AsMemory(offset, bookBytes), token);
                remainingOriginal -= bookBytes;
                offset += bookBytes;
                RandomNumberGenerator.Fill(plaintext.AsSpan(offset, length - offset));
                RandomNumberGenerator.Fill(nonce);
                aes.Encrypt(nonce, plaintext.AsSpan(0, length), ciphertext.AsSpan(0, length), tag,
                    BuildPaddedChunkAssociatedData(chunkIndex++, length, BlobEncryptionChunkBytes));
                await WriteInt32Async(output, length, token);
                await output.WriteAsync(nonce, token);
                await output.WriteAsync(tag, token);
                await output.WriteAsync(ciphertext.AsMemory(0, length), token);
                remainingPayload -= length;
            }
            if (await input.ReadAsync(plaintext.AsMemory(0, 1), token) != 0)
                throw new InvalidDataException("本地文件在加密过程中发生变化，已停止上传。");
            await WriteInt32Async(output, 0, token);
            await output.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    // The caller has consumed the magic. Every chunk authenticates its index,
    // length and public chunk size; the encrypted original length fixes the
    // total padded payload. Truncation, reordered chunks and trailing bytes all
    // fail before the temporary plaintext can replace a library file.
    private async Task DecryptPaddedBlobToPathAsync(Stream input, string targetPath, string passphrase,
        CancellationToken token)
    {
        var salt = new byte[EncryptionSaltBytes];
        await ReadExactlyAsync(input, salt, token);
        var chunkSize = await ReadInt32Async(input, token);
        if (chunkSize < sizeof(long) || chunkSize > 16 * 1024 * 1024)
            throw new InvalidDataException("云端同步对象的分块大小无效。");
        using var aes = new AesGcm(GetOrDeriveEncryptionKey(passphrase, salt), EncryptionTagBytes);
        await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, Math.Min(chunkSize, 128 * 1024), useAsync: true);
        var nonce = new byte[EncryptionNonceBytes];
        var tag = new byte[EncryptionTagBytes];
        var ciphertext = new byte[chunkSize];
        var plaintext = new byte[chunkSize];
        var chunkIndex = 0;
        long remainingPayload = 0;
        long remainingOriginal = 0;
        try
        {
            while (true)
            {
                var length = await ReadInt32Async(input, token);
                if (length == 0)
                {
                    if (chunkIndex == 0 || remainingPayload != 0 || remainingOriginal != 0)
                        throw new InvalidDataException("云端同步对象提前结束。");
                    if (await input.ReadAsync(ciphertext.AsMemory(0, 1), token) != 0)
                        throw new InvalidDataException("云端同步对象含有多余数据。");
                    break;
                }
                if (length < 0 || length > chunkSize || (chunkIndex == 0 && length < sizeof(long)))
                    throw new InvalidDataException("云端同步对象的分块长度无效。");
                await ReadExactlyAsync(input, nonce, token);
                await ReadExactlyAsync(input, tag, token);
                await ReadExactlyAsync(input, ciphertext.AsMemory(0, length), token);
                try
                {
                    aes.Decrypt(nonce, ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length),
                        BuildPaddedChunkAssociatedData(chunkIndex, length, chunkSize));
                }
                catch (CryptographicException exception)
                {
                    throw new InvalidDataException("云端同步加密密钥不匹配，或同步对象已损坏。", exception);
                }
                var offset = 0;
                if (chunkIndex++ == 0)
                {
                    remainingOriginal = BinaryPrimitives.ReadInt64LittleEndian(plaintext);
                    remainingPayload = PaddedBlobPayloadLength(remainingOriginal);
                    offset = sizeof(long);
                }
                if (length != Math.Min(chunkSize, remainingPayload))
                    throw new InvalidDataException("云端同步对象的补齐长度不匹配。");
                var bookBytes = (int)Math.Min(remainingOriginal, length - offset);
                await output.WriteAsync(plaintext.AsMemory(offset, bookBytes), token);
                remainingOriginal -= bookBytes;
                remainingPayload -= length;
            }
            await output.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] BuildPaddedChunkAssociatedData(int chunkIndex, int length, int chunkSize)
    {
        var result = new byte[PaddedEncryptionMagic.Length + 3 * sizeof(int)];
        PaddedEncryptionMagic.CopyTo(result, 0);
        var framing = result.AsSpan(PaddedEncryptionMagic.Length);
        BinaryPrimitives.WriteInt32LittleEndian(framing, chunkIndex);
        BinaryPrimitives.WriteInt32LittleEndian(framing[sizeof(int)..], length);
        BinaryPrimitives.WriteInt32LittleEndian(framing[(2 * sizeof(int))..], chunkSize);
        return result;
    }
}
