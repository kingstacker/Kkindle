using MdxParser.Models;
using NeoSmart.Hashing.XXHash;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace MdxParser
{
    public class MdxDocument : AbsoluteBlock
    {
        #region property
        private Stream m_Stream;
        private Encoding m_Encoding;
        private int m_Encrypt;
        private byte[] m_EncryptKey;
        private long m_BlockStartIndex;
        private Dictionary<string, (string, string)> m_StyleSheet;
        public MdxHeader Header { get; set; }
        [JsonIgnore]
        public Stream Stream { get => m_Stream; set => m_Stream = value; }
        [JsonIgnore]
        public byte[]? EncryptKey
        {
            get
            {
                if (m_EncryptKey == null)
                {
                    if (Header.UUID != null)
                    {
                        var mid = (Header.UUID.Length + 1) / 2;
                        var fDigest = XXHash64.Hash(Encoding.ASCII.GetBytes(Header.UUID.Substring(0, mid)));
                        var eDigest = XXHash64.Hash(Encoding.ASCII.GetBytes(Header.UUID.Substring(mid)));
                        List<byte> bytes = new List<byte>();
                        bytes.AddRange(Enumerable.Reverse(BitConverter.GetBytes(fDigest)));
                        bytes.AddRange(Enumerable.Reverse(BitConverter.GetBytes(eDigest)));
                        return bytes.ToArray();
                    }
                }
                return m_EncryptKey;
            }
        }

        [JsonIgnore]
        public Encoding Encoding
        {
            get
            {
                if (m_Encoding == null)
                {
                    if (Version >= 3.0)
                        m_Encoding = Encoding.UTF8;
                    else if ("GBK".Equals(Header?.Encoding) || "GB2312".Equals(Header?.Encoding))
                        m_Encoding = Encoding.GetEncoding("GB18030");
                    else if (!string.IsNullOrEmpty(Header?.Encoding))
                        m_Encoding = Encoding.GetEncoding(Header.Encoding);
                    else
                        m_Encoding = Encoding.UTF8;
                }
                return m_Encoding;
            }
        }
        public int Encrypt { get => m_Encrypt; set => m_Encrypt = value; }
        public long BlockStartIndex { get => m_BlockStartIndex; set => m_BlockStartIndex = value; }
        public float? Version { get => Header?.GeneratedByEngineVersion; }
        /// <summary>
        /// 数量长度，4或8
        // before version 2.0, number is 4 bytes integer
        // version 2.0 and above uses 8 bytes
        /// </summary>
        public int NumberWidth { get => Version < 2.0 ? 4 : 8; }

        private Dictionary<BlockType, MdxBlock> m_Blocks = new Dictionary<BlockType, MdxBlock>();
        public Dictionary<BlockType, MdxBlock> Blocks => m_Blocks;

        public List<KeyData> KeyList { get => m_KeyList; }
        #endregion

        #region Ctr
        public MdxDocument(string path) : this(path, Encoding.UTF8) { }
        public MdxDocument(string path, Encoding encoding) : this(File.OpenRead(path), encoding)
        {
        }
        public MdxDocument(Stream stream, Encoding encoding)
        {
            Stream = stream;

            m_Encoding = encoding ?? Encoding.UTF8;

            Header = readHeader();

            readBlocks();
        }
        #endregion

        #region private
        private void readBlocks()
        {
            if (Version >= 3.0)
            {
                readBlockV3();
            }
            else
            {
                // if no regcode is given, try brutal force (only for engine <= 2)
                if (Encrypt == 1 && EncryptKey == null)
                {

                    Console.WriteLine("Try Brutal Force on Encrypted Key Blocks");
                    readKeysBrutal();
                }
                else
                    readKeysV2();
            }
        }

        private void readBlockV3()
        {
            Stream.Position = BlockStartIndex;
            long currentIndex = BlockStartIndex;
            while (currentIndex < Stream.Length)
            {
                var block = new MdxBlock(this, currentIndex);
                Blocks.Add(block.Type, block);
                currentIndex = block.EndIndex;
            }
        }
        private void readKeysV2()
        {
            Stream.Position = BlockStartIndex;
            long currentIndex = BlockStartIndex;
            while (currentIndex < Stream.Length)
            {
                var block = new MdxBlock(this, currentIndex);
                Blocks.Add(block.Type, block);
                currentIndex = block.EndIndex;
            }



            //    # read key block
            //    key_block_compressed = f.read(key_block_size)
            //    # extract key block
            //    key_list = self._decode_key_block(key_block_compressed, key_block_info_list)

            //    self._record_block_offset = f.tell()
            //    f.close()

            //    return key_list
        }

        private void readKeysBrutal()
        {
            throw new NotImplementedException();
        }


        private MdxHeader readHeader()
        {
            Stream.Position = 0;

            var headSize = readInt32(Stream);
            var headStartIndex = 4;


            var headBuf = new byte[headSize];
            readBytes(Stream, headBuf);

            byte[] utf8headBuf = headBuf[(headBuf.Length - 2)..].SequenceEqual(new byte[] { 0, 0 }) ?
                Encoding.Convert(Encoding.GetEncoding("utf-16"), Encoding.UTF8, headBuf) :
                headBuf;

            var headStr = Encoding.UTF8.GetString(utf8headBuf, 0, utf8headBuf.Length - 1);  // head以字节为x00结尾，此处去除；
            var adler32 = (uint)readInt32B(Stream);
            var headHash = adler32Compute(headBuf);
            if (headHash != adler32)
                throw new Exception("document is adler32 valid.");

            BlockStartIndex = Stream.Position;

            MdxHeader header = parseHeader(headStr);
            header.Size = headSize + 4;
            header.StartIndex = headStartIndex;


            // encryption flag
            if (header.Encrypted == null || header.Encrypted.Equals("No"))
                m_Encrypt = 0;
            else if (header.Encrypted.Equals("Yes"))
                m_Encrypt = 1;
            else
                int.TryParse(header.Encrypted, out m_Encrypt);

            // stylesheet attribute if present takes form of:
            // style_number # 1-255
            // style_begin  # or ''
            // style_end    # or ''
            // store stylesheet in dict in the form of
            // {'number' : ('style_begin', 'style_end')}
            m_StyleSheet = new Dictionary<string, (string, string)>();
            if (header.StyleSheet != null)
            {
                var lines = header.StyleSheet.Split('\n');
                if (lines.Length > 2)
                {
                    for (int i = 0; i < lines.Length; i = i + 3)
                    {
                        m_StyleSheet.Add(lines[i], (lines[i + 1], lines[i + 2]));
                    }
                }
            }
            return header;
        }
        private MdxHeader parseHeader(string headStr)
        {
            if (headStr.StartsWith("<Dictionary"))
                headStr = "<ZDB" + headStr.Substring(11);
            else if (headStr.StartsWith("<Library_Data"))
            {
                headStr = "<ZDB" + headStr.Substring(13);
                m_Encoding = Encoding.GetEncoding("utf-16");
            }
            // <Dictionary GeneratedByEngineVersion="1.2" RequiredEngineVersion="1.2"
            // <ZDB GeneratedByEngineVersion = "3.0" RequiredEngineVersion = "3.0"
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(MdxHeader));
            TextReader reader = new StringReader(headStr);

            return (MdxHeader)xmlSerializer.Deserialize(reader);
        }
        #endregion
        private List<KeyData> m_KeyList;
        [JsonIgnore]
        public List<KeyData> KeyDatas
        {
            get
            {
                if (m_KeyList == null)
                {
                    m_KeyList = new List<KeyData>();
                    Blocks[BlockType.KeyData].KeyBlocks.ForEach(kb =>
                    {
                        m_KeyList.AddRange(splitKeyBlock(kb.getDecompressedData()));
                    });

                }
                return m_KeyList;
            }
        }
        private IEnumerable<KeyData> splitKeyBlock(byte[] keyData)
        {
            int startIndex = 0, endIndex = 0, width;
            byte[] delimiter;
            // key text ends with '\x00'
            if (Encoding == Encoding.GetEncoding("utf-16"))
            {
                delimiter = new byte[] { 0, 0 };
                width = 2;
            }
            else
            {
                delimiter = new byte[] { 0 };
                width = 1;
            }
            while (startIndex < keyData.Length)
            {
                // the corresponding record's offset in record block
                var keyId = readNumber(keyData[startIndex..(startIndex + NumberWidth)]);
                int i = startIndex + NumberWidth;
                while (i < keyData.Length)
                {

                    if (keyData[i..(i + width)].SequenceEqual(delimiter))
                    {
                        endIndex = i;
                        break;
                    }
                    i += width;
                }
                if (endIndex < startIndex + NumberWidth)
                    endIndex = keyData.Length;
                var keyText = Encoding.GetString(keyData[(startIndex + NumberWidth)..endIndex]);
                var key = KeyData.of(keyId, keyText);
                key.StartIndex = startIndex;
                key.Size = endIndex + width - startIndex;
                startIndex = endIndex + width;
                yield return key;
            }
        }

        private List<RecordData> m_RecordList;
        [JsonIgnore]
        public List<RecordData> RecordDatas
        {
            get
            {
                if (m_RecordList == null)
                {
                    m_RecordList = m_RecordList ?? new List<RecordData>();
                    int offset = 0, i = 0;
                    Blocks[BlockType.RecordData].KeyBlocks.ForEach(b =>
                    {
                        var recordData = b.getDecompressedData();
                        while (i < KeyDatas.Count)
                        {
                            long endIndex = 0, startIndex = KeyDatas[i].Id;
                            var key = KeyDatas[i];
                            if (key.Id - offset >= recordData.Length)
                                break;
                            // record end index
                            if (i < KeyDatas.Count - 1)
                                endIndex = KeyDatas[i + 1].Id;
                            else
                                endIndex = recordData.Length + offset;
                            var data = recordData[(int)(startIndex - offset)..(int)(endIndex - offset)];
                            if (KeyDatas[i].Text.StartsWith("\\"))
                                m_RecordList.Add(RecordData.of(KeyDatas[i].Text, data));
                            else
                                m_RecordList.Add(RecordData.of(KeyDatas[i].Text, Encoding.GetString(data)));
                            i++;
                        }
                        offset += recordData.Length;
                    });
                }
                return m_RecordList;
            }
        }
        public long readNumber(Stream stream)
        {
            if (NumberWidth == 8)
                return readInt64(stream);
            else
                return readInt32(stream);
        }
        public long readNumber(byte[] bytes)
        {
            if (NumberWidth == 8)
                return BitConverter.ToInt64(Enumerable.Reverse(bytes).ToArray());
            else
                return BitConverter.ToInt32(Enumerable.Reverse(bytes).ToArray());
        }
    }
}
