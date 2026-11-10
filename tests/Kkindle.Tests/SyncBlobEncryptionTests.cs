using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class SyncBlobEncryptionTests
{
    private const string Passphrase = "fictional-padded-blob-test-key";
    private static readonly MethodInfo Encrypt = typeof(S3SyncService).GetMethod("EncryptFileToPathAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo Decrypt = typeof(S3SyncService).GetMethod("DecryptBlobToPathAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo PayloadLength = typeof(S3SyncService).GetMethod("PaddedBlobPayloadLength", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(65530)]
    [InlineData(1048568)]
    [InlineData(1048576)]
    [InlineData(1048577)]
    [InlineData(2097289)]
    public async Task PaddedBlobsRoundTripExactlyAndExposeOnlyPaddedLengths(int length)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(length);
            var service = new S3SyncService(new AppPaths(root), new TestHelpers.PlaintextSecretProtector());
            var encrypted = await EncryptBytesAsync(service, root, bytes);
            Assert.True(encrypted.AsSpan().StartsWith("KKINDLE-SYNC3"u8));
            var padded = (long)PayloadLength.Invoke(null, [(long)length])!;
            var chunks = (padded + 1024 * 1024 - 1) / (1024 * 1024);
            Assert.Equal(37 + padded + 32 * chunks, encrypted.LongLength);
            Assert.Equal(bytes, await DecryptBytesAsync(service, root, encrypted));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NearbyDifferentPlaintextLengthsHaveIdenticalCiphertextLength()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new S3SyncService(new AppPaths(root), new TestHelpers.PlaintextSecretProtector());
            var shorter = RandomNumberGenerator.GetBytes(100_000);
            var longer = RandomNumberGenerator.GetBytes(100_001);
            var first = await EncryptBytesAsync(service, root, shorter);
            var second = await EncryptBytesAsync(service, root, longer);
            Assert.Equal(first.Length, second.Length);
            Assert.Equal(shorter, await DecryptBytesAsync(service, root, first));
            Assert.Equal(longer, await DecryptBytesAsync(service, root, second));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("chunk-size")]
    [InlineData("chunk-length")]
    [InlineData("early-end")]
    [InlineData("missing-end")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("wrong-key")]
    public async Task PaddedBlobsRejectCorruptionTruncationAndIncorrectKeys(string mode)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new S3SyncService(new AppPaths(root), new TestHelpers.PlaintextSecretProtector());
            var encrypted = await EncryptBytesAsync(service, root, RandomNumberGenerator.GetBytes(1024 * 1024 + 137));
            switch (mode)
            {
                case "ciphertext": encrypted[65] ^= 0x80; break;
                case "chunk-size": BinaryPrimitives.WriteInt32LittleEndian(encrypted.AsSpan(29), 1024 * 1024 + 1); break;
                case "chunk-length": BinaryPrimitives.WriteInt32LittleEndian(encrypted.AsSpan(33), 1024 * 1024 - 1); break;
                case "early-end": BinaryPrimitives.WriteInt32LittleEndian(encrypted.AsSpan(33), 0); break;
                case "missing-end": encrypted = encrypted[..^4]; break;
                case "truncated": encrypted = encrypted[..^100]; break;
                case "trailing": encrypted = [.. encrypted, 0x01]; break;
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => DecryptBytesAsync(service, root, encrypted,
                mode == "wrong-key" ? "fictional-wrong-sync-key" : Passphrase));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReleasedStreamingFormatStillDecrypts()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new S3SyncService(new AppPaths(root), new TestHelpers.PlaintextSecretProtector());
            var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 137);
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = Rfc2898DeriveBytes.Pbkdf2(Passphrase, salt, 120_000, HashAlgorithmName.SHA256, 32);
            using var aes = new AesGcm(key, 16);
            using var output = new MemoryStream();
            output.Write("KKINDLE-SYNC2"u8); output.Write(salt);
            var integer = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(integer, 1024 * 1024); output.Write(integer);
            for (var offset = 0; offset < bytes.Length; offset += 1024 * 1024)
            {
                var count = Math.Min(1024 * 1024, bytes.Length - offset);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[count]; var tag = new byte[16];
                var aad = new byte[17]; "KKINDLE-SYNC2"u8.CopyTo(aad);
                BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(13), offset / (1024 * 1024));
                aes.Encrypt(nonce, bytes.AsSpan(offset, count), ciphertext, tag, aad);
                BinaryPrimitives.WriteInt32LittleEndian(integer, count);
                output.Write(integer); output.Write(nonce); output.Write(tag); output.Write(ciphertext);
            }
            output.Write(new byte[4]);
            Assert.Equal(bytes, await DecryptBytesAsync(service, root, output.ToArray()));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task<byte[]> EncryptBytesAsync(S3SyncService service, string root, byte[] bytes)
    {
        var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".plain");
        var target = Path.Combine(root, Guid.NewGuid().ToString("N") + ".encrypted");
        await File.WriteAllBytesAsync(source, bytes);
        await (Task)Encrypt.Invoke(service, [source, target, Passphrase, CancellationToken.None])!;
        return await File.ReadAllBytesAsync(target);
    }

    private static async Task<byte[]> DecryptBytesAsync(S3SyncService service, string root, byte[] bytes, string passphrase = Passphrase)
    {
        using var input = new MemoryStream(bytes, writable: false);
        var target = Path.Combine(root, Guid.NewGuid().ToString("N") + ".decrypted");
        await (Task)Decrypt.Invoke(service, [input, target, passphrase, CancellationToken.None])!;
        return await File.ReadAllBytesAsync(target);
    }
}
