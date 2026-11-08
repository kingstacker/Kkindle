using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kkindle.Infrastructure;

/// <summary>Chunked authenticated encryption for portable backups; the key is never included in the file.</summary>
internal static class BackupEncryption
{
    private static readonly byte[] Magic = "KKINDLE-BACKUP2"u8.ToArray();
    private const int ChunkBytes = 1024 * 1024;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Iterations = 600_000;
    private const long MaximumPlainBytes = 1024L * 1024 * 1024 * 1024;

    internal static Stream CreateWriter(Stream output, string passphrase) => new EncryptionStream(output, passphrase);

    internal static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken token)
    {
        var header = new byte[Magic.Length + SaltBytes + sizeof(int)];
        var plain = new byte[ChunkBytes];
        var cipher = new byte[ChunkBytes];
        byte[]? key = null;
        try
        {
            await input.ReadExactlyAsync(header, token);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length + SaltBytes)) != ChunkBytes)
                throw new InvalidDataException("此备份未加密或格式不受支持，只能导入加密的 .kkindle 备份。");
            key = DeriveKey(passphrase, header.AsSpan(Magic.Length, SaltBytes));
            using var aes = new AesGcm(key, TagBytes);
            var lengthBytes = new byte[sizeof(int)]; var nonce = new byte[NonceBytes]; var tag = new byte[TagBytes];
            long index = 0, total = 0;
            while (true)
            {
                await input.ReadExactlyAsync(lengthBytes, token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length < 0 || length > ChunkBytes || total > MaximumPlainBytes - length)
                    throw new InvalidDataException("加密备份的数据长度无效或超过限制。");
                await input.ReadExactlyAsync(nonce, token);
                await input.ReadExactlyAsync(tag, token);
                await input.ReadExactlyAsync(cipher.AsMemory(0, length), token);
                aes.Decrypt(nonce, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), AssociatedData(header, index++, length));
                if (length == 0)
                {
                    if (await input.ReadAsync(lengthBytes.AsMemory(0, 1), token) != 0)
                        throw new InvalidDataException("加密备份末尾包含多余数据。");
                    break;
                }
                await output.WriteAsync(plain.AsMemory(0, length), token); total += length;
            }
            await output.FlushAsync(token);
        }
        catch (EndOfStreamException)
        { throw new InvalidDataException("加密备份不完整或文件已损坏，无法导入。"); }
        catch (CryptographicException)
        { throw new InvalidDataException("无法解密备份：同步密钥不正确或文件已损坏。密钥不会上传，遗忘后无法恢复已有加密数据。"); }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] AssociatedData(byte[] header, long index, int length)
    {
        var data = new byte[header.Length + sizeof(long) + sizeof(int)];
        header.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(header.Length), index);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(header.Length + sizeof(long)), length);
        return data;
    }

    private sealed class EncryptionStream : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _header;
        private readonly byte[] _key;
        private readonly AesGcm _aes;
        private readonly byte[] _buffer = new byte[ChunkBytes];
        private int _count;
        private long _index, _position;
        private bool _disposed;

        internal EncryptionStream(Stream output, string passphrase)
        {
            _output = output;
            _header = new byte[Magic.Length + SaltBytes + sizeof(int)]; Magic.CopyTo(_header, 0);
            RandomNumberGenerator.Fill(_header.AsSpan(Magic.Length, SaltBytes));
            BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(Magic.Length + SaltBytes), ChunkBytes);
            _key = DeriveKey(passphrase, _header.AsSpan(Magic.Length, SaltBytes));
            _aes = new(_key, TagBytes);
            _output.Write(_header);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { ObjectDisposedException.ThrowIf(_disposed, this); _output.Flush(); }
        public override Task FlushAsync(CancellationToken cancellationToken) { ObjectDisposedException.ThrowIf(_disposed, this); return _output.FlushAsync(cancellationToken); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_position > MaximumPlainBytes - value.Length) throw new InvalidDataException("备份数据超过大小限制。");
            _position += value.Length;
            while (!value.IsEmpty)
            {
                var copy = Math.Min(value.Length, ChunkBytes - _count);
                value[..copy].CopyTo(_buffer.AsSpan(_count)); _count += copy; value = value[copy..];
                if (_count == ChunkBytes) { _output.Write(Record(_count)); _count = 0; }
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            if (_position > MaximumPlainBytes - value.Length) throw new InvalidDataException("备份数据超过大小限制。");
            _position += value.Length;
            while (!value.IsEmpty)
            {
                var copy = Math.Min(value.Length, ChunkBytes - _count);
                value[..copy].CopyTo(_buffer.AsMemory(_count)); _count += copy; value = value[copy..];
                if (_count == ChunkBytes) { await _output.WriteAsync(Record(_count), cancellationToken); _count = 0; }
            }
        }

        private byte[] Record(int length)
        {
            var record = new byte[sizeof(int) + NonceBytes + TagBytes + length];
            BinaryPrimitives.WriteInt32LittleEndian(record, length);
            var nonce = record.AsSpan(sizeof(int), NonceBytes); RandomNumberGenerator.Fill(nonce);
            var tag = record.AsSpan(sizeof(int) + NonceBytes, TagBytes);
            var ciphertext = record.AsSpan(sizeof(int) + NonceBytes + TagBytes, length);
            _aes.Encrypt(nonce, _buffer.AsSpan(0, length), ciphertext, tag, AssociatedData(_header, _index++, length));
            return record;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                try
                {
                    if (_count > 0) _output.Write(Record(_count));
                    _output.Write(Record(0)); _output.Flush();
                }
                finally { Clear(); }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            try
            {
                if (_count > 0) await _output.WriteAsync(Record(_count));
                await _output.WriteAsync(Record(0)); await _output.FlushAsync();
            }
            finally { Clear(); GC.SuppressFinalize(this); }
        }
        private void Clear()
        {
            _disposed = true; _aes.Dispose(); CryptographicOperations.ZeroMemory(_key); CryptographicOperations.ZeroMemory(_buffer);
        }
    }
}
