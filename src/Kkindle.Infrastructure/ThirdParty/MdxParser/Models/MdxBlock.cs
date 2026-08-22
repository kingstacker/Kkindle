using System.Text;
using System.Text.Json.Serialization;

namespace MdxParser.Models
{
    public enum BlockType
    {
        RecordData = 1,
        RecordIndex = 2,
        KeyData = 3,
        KeyIndex = 4
    }
    public class MdxBlock : AbsoluteBlock
    {
        public BlockType Type { get; set; }

        private long m_TotalSize;
        private long m_BlockSize;
        private int m_BlockCount;
        private readonly MdxDocument document;

        [JsonIgnore]
        public MdxDocument Document { get { return document; } }

        [JsonIgnore]
        public Stream Stream { get => document.Stream; }

        public List<IBlocks> KeyBlocks { get; set; } = new List<IBlocks>();
        public long TotalSize { get => m_TotalSize; set => m_TotalSize = value; }
        public long BlockSize { get => m_BlockSize; set => m_BlockSize = value; }
        public int BlockCount { get => m_BlockCount; set => m_BlockCount = value; }
        public MdxBlock(MdxDocument document, long start)
        {
            this.document = document;
            this.StartIndex = start;
            if (document.Version >= 3.0)
            {
                parseStream(document.Stream);
            }
            else
            {
                if (StartIndex == document.BlockStartIndex)
                    parseKeyBlockV2(document.Stream);
                else
                    parseRecordBlockV2(document.Stream);
            }
        }
        private void parseStream(Stream stream)
        {
            if(stream.Position != StartIndex)
                stream.Position = StartIndex;


            Type = (BlockType)stream.ReadByte();
            stream.Seek(3, SeekOrigin.Current);

            m_TotalSize = document.readNumber(stream);
            Size = TotalSize + 4 + document.NumberWidth;

            //stream.Seek(TotalSize, SeekOrigin.Current);


            m_BlockCount = readInt32(stream);
            m_BlockSize = document.readNumber(stream);

            // read key data
            for (int i = 0; i < BlockCount; i++)
            {
                KeyBlock keyBlock = new KeyBlock(this, stream.Position);
                KeyBlocks.Add(keyBlock);
            }
        }
        private void parseKeyBlockV2(Stream stream)
        {
            // 读取Key Block引导块，2.0及以上为8*5字节，2.0以下为4*4字节；
            if (stream.Position != StartIndex)
                stream.Position = StartIndex;
            Type = BlockType.KeyData;
            // number of key blocks
            m_BlockCount = (int)document.readNumber(stream);
            // number of entries
            var num_entries = (int)document.readNumber(stream);
            // number of bytes of key block info after decompression
            if (document.Version>=2.0)
            {
                var num_decompressed = (int)document.readNumber(stream);
            }
            // number of bytes of key block info
            var num_info = (int)document.readNumber(stream);
            // number of bytes of key block
            m_BlockSize = (int)document.readNumber(stream);
            if (document.Version >= 2.0)
            {
                Size = 8 * 5 + 4 + num_info + m_BlockSize;
                // 4 bytes: adler checksum of previous 5 numbers
                var adler32Buff = new byte[4];
                readBytes(stream, adler32Buff);
                //Adler32 adler32 = new Adler32();
                //if (Adler32.ComputeHash(blockBuff, 0, blockBuff.Length) != BitConverter.ToUInt32(adler32Buff, 0))
                //    throw new Exception("key block is adler32 valid.");
            }
            else
                Size = 4 * 4 + num_info + m_BlockSize;

            var keyBlockInfoBuff = new byte[num_info];
            stream.Read(keyBlockInfoBuff);

            // read key block info, which indicates key block's compressed and decompressed size
            List<(int,int)> keyBlockInfoList = decodeKeyBlockInfo(keyBlockInfoBuff).ToList();
            //if (num_key_blocks != keyBlockInfoList.Count)
            //    throw new Exception("key Block size is wrong.");
            //keyBlockInfoList.ForEach((s) =>
            //{
            //    Console.WriteLine("key block: [{0},{1}]", s.Item1, s.Item2);
            //});
            //// read key block
            //var keyBlockCompressedBuff = new byte[key_block_size];
            //stream.Read(keyBlockCompressedBuff);

            //// extract key block
            //IEnumerable<(string, string)> keyList = decodeKeyBlock(keyBlockCompressedBuff, keyBlockInfoList);
            //_recordBlockOffset = stream.Position;

            // read key data
            for (int i = 0; i < BlockCount; i++)
            {
                KeyBlockV2 keyBlock = new KeyBlockV2(this, stream.Position, (uint)keyBlockInfoList[i].Item1, (uint)keyBlockInfoList[i].Item2);
                KeyBlocks.Add(keyBlock);
            }
            //return keyList;
        }
        private IEnumerable<(int,int)> decodeKeyBlockInfo(byte[] keyBlockInfoData)
        {
            byte[] keyBlockInfo;
            if (document.Version >= 2.0)
            {
                if (!keyBlockInfoData[0..4].SequenceEqual(new byte[] { 2, 0, 0, 0 }))
                    throw new Exception("");
                if ((document.Encrypt & 2) == 2)
                {

                    var encryptData = new byte[8];
                    Array.Copy(keyBlockInfoData[4..8], 0, encryptData, 0, 4);
                    Array.Copy(new byte[] { 0x95, 0x36, 0, 0 }, 0, encryptData, 4, 4);
                    var key = ripemd128(encryptData);

                    var decompressedData = fastDecrypt(keyBlockInfoData[8..], key);
                    keyBlockInfoData = keyBlockInfoData[..8].Concat(decompressedData).ToArray();
                }
                keyBlockInfo = ZipDecompress(keyBlockInfoData[8..]);

                var adler32 = BitConverter.ToInt32(keyBlockInfoData[4..8]);
                var computeadler = adler32Compute(keyBlockInfo);
                //Adler32 adler = new Adler32();

            }
            else
            {
                keyBlockInfo = keyBlockInfoData;
            }
            // decode
            int i = 0, num_entries=0;
            int byteWidth = document.Version >= 2.0 ? 2 : 1;
            int textTerm = document.Version >= 2.0 ? 1 : 0;
            int headSize, tailSize;
            int keyBlockCompressedSize,keyBlockDecompressedSize;
            while (i<keyBlockInfo.Length)
            {
                // number of entries in current key block
                num_entries += (int)document.readNumber(keyBlockInfo[i..(i+document.NumberWidth)]);
                i += document.NumberWidth;
                // text head size
                headSize = readWidth(keyBlockInfo[i..(i + byteWidth)]);
                i += byteWidth;
                if (document.Encoding != Encoding.GetEncoding("UTF-16"))
                    i += headSize + textTerm;
                else
                    i += (headSize + textTerm) * 2;
                // text tail size
                tailSize = readWidth(keyBlockInfo[i..(i + byteWidth)]);
                i += byteWidth;
                if (document.Encoding != Encoding.GetEncoding("UTF-16"))
                    i += tailSize + textTerm;
                else
                    i += (tailSize + textTerm) * 2;
                // key block compressed size
                keyBlockCompressedSize = (int)document.readNumber(keyBlockInfo[i..(i + document.NumberWidth)]);
                i += document.NumberWidth;
                keyBlockDecompressedSize = (int)document.readNumber(keyBlockInfo[i..(i + document.NumberWidth)]);
                i += document.NumberWidth;
                yield return (keyBlockCompressedSize, keyBlockDecompressedSize);
            }
        }
        private int readWidth(byte[] data)
        {
            if (data.Length>1)
                return BitConverter.ToInt16(Enumerable.Reverse(data).ToArray(), 0);
            else
                return data[0];
        }
        private void parseRecordBlockV2(Stream stream)
        {
            if (stream.Position != StartIndex)
                stream.Position = StartIndex;

            Type = BlockType.RecordData;

            m_BlockCount = (int)document.readNumber(stream);
            var num_entries = (int)document.readNumber(stream);

            var num_width = (int)document.readNumber(stream);
            m_BlockSize = (int)document.readNumber(stream);

            Size = document.NumberWidth * 4 + num_width + m_BlockSize;
            //byte[] num_bytes = new byte[num_width];
            //document.readBytes(stream, num_bytes);
            List<(int,int)> blockSizes = new List<(int, int)>();
            for (int i = 0; i < BlockCount; i++)
            {
                blockSizes.Add(((int)document.readNumber(stream), (int)document.readNumber(stream)));
            }
            foreach(var blockSize in blockSizes)
            {
                KeyBlockV2 keyBlock = new KeyBlockV2(this, stream.Position, (uint)blockSize.Item1, (uint)blockSize.Item2);
                KeyBlocks.Add(keyBlock);
            }
        }
    }
}
