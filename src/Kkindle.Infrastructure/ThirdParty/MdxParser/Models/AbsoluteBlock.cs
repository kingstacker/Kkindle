using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using MdxParser.encrypt;
using MdxParser.encrypt;
using System.Buffers;

namespace MdxParser.Models
{
    public abstract class AbsoluteBlock
    {
        private long m_StartIndex;
        private long m_Size;

        public long StartIndex { get => m_StartIndex; set => m_StartIndex = value; }
        public long EndIndex { get => m_StartIndex + m_Size; }
        public long Size { get => m_Size; set => m_Size = value; }

        /// <summary>
        /// big endian for 4 byte
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public int readInt32B(Stream stream)
        {
            return readInt32(stream, false);
        }
        /// <summary>
        /// little endian for 4 byte
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="reversed">default little endian is true</param>
        /// <returns></returns>
        public int readInt32(Stream stream, bool reversed = true)
        {
            var intBuf = new byte[4];
            stream.Read(intBuf, 0, intBuf.Length);
            if (reversed)
                return BitConverter.ToInt32(Enumerable.Reverse(intBuf).ToArray(), 0);
            else
                return BitConverter.ToInt32(intBuf.ToArray(), 0);
        }
        /// <summary>
        /// little endian for 4 byte
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="reversed">default little endian is true</param>
        /// <returns></returns>
        public uint readUInt32(Stream stream, bool reversed = true)
        {
            var intBuf = new byte[4];
            stream.Read(intBuf, 0, intBuf.Length);
            if (reversed)
                return BitConverter.ToUInt32(Enumerable.Reverse(intBuf).ToArray(), 0);
            else
                return BitConverter.ToUInt32(intBuf.ToArray(), 0);
        }
        /// <summary>
        /// little endian for 8 byte
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public long readInt64(Stream stream)
        {
            var intBuf = new byte[8];
            stream.Read(intBuf, 0, intBuf.Length);
            return BitConverter.ToInt64(Enumerable.Reverse(intBuf).ToArray(), 0);
        }
        public void readBytes(Stream stream, byte[] data)
        {
            stream.Read(data);
        }
        protected byte[] ripemd128(byte[] data)
        {
            var digest = new RipeMD128();
            var rsData = new byte[digest.GetDigestSize()];

            byte[] msg = data ?? new byte[0];
            digest.BlockUpdate(msg, 0, msg.Length);
            digest.DoFinal(rsData, 0);
            return rsData;
        }
        protected byte[] fastDecrypt(byte[] data, byte[] key)
        {
            // XOR decryption
            byte previous = 0x36;
            for (var i = 0; i < data.Length; i++)
            {
                var t = (data[i] >> 4 | data[i] << 4) & 0xff;
                t = t ^ previous ^ (i & 0xff) ^ key[i % key.Length];
                previous = data[i];
                data[i] = (byte)t;
            }
            return data;
        }
        protected byte[] salsaDecrypt(byte[] cipher, byte[] encrypt_key)
        {
            var salsa20 = new Salsa20();
            salsa20.Rounds = 8;
            var encrypor = salsa20.CreateEncryptor(encrypt_key, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
            return encrypor.TransformFinalBlock(cipher, 0, cipher.Length);
        }
        protected byte[] ZipDecompress(byte[] data)
        {
            MemoryStream compressed = new MemoryStream(data);
            MemoryStream decompressed = new MemoryStream();
            InflaterInputStream inputStream = new InflaterInputStream(compressed);
            inputStream.CopyTo(decompressed);
            return decompressed.ToArray();
        }
        protected uint adler32Compute(byte[] data)
        {
            Adler32 adler32 = new Adler32();
            adler32.Update(data);
            return (uint)adler32.Value;
        }
        protected byte[] LzoDecompress(byte[] data, long size)
        {
            //MemoryStream compressed = new MemoryStream(data);
            //using (var decompressed = new LzoStream(compressed, CompressionMode.Decompress))
            //{
            //    byte[] buffer = new byte[decompressed.Length];
            //    decompressed.Read(buffer);
            //    return buffer;
            //}
            //LZOCompressor lzo = new LZOCompressor();
            //return lzo.Decompress(data);
            byte[] outBuff = new byte[size];
            return MiniLZO.Decompress(data, outBuff);
        }
    }
}
