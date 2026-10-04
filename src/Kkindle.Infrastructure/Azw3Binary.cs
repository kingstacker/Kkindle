// KF8 binary layout and decompression algorithms follow KindleUnpack.
// See ThirdParty/KindleUnpack/NOTICE.md for attribution and license.
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Kkindle.Infrastructure;

internal static class Azw3Binary
{
    internal const int MaximumOutput = 256 * 1024 * 1024;
    internal static InvalidDataException Invalid() => new("AZW3 文件结构损坏或超出阅读器支持的范围。");
    internal static byte[] Slice(byte[] data, int start, int length)
    {
        if (start < 0 || length < 0 || start > data.Length - length) throw Invalid();
        return data.AsSpan(start, length).ToArray();
    }
    internal static int U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(Slice(data, offset, 2));
    internal static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(Slice(data, offset, 4));
    internal static int Int(byte[] data, int offset) => checked((int)U32(data, offset));
    internal static bool Magic(byte[] data, string magic) => data.AsSpan().StartsWith(Encoding.ASCII.GetBytes(magic));
    internal static int Vwi(byte[] data, ref int position, int end)
    {
        uint value = 0;
        for (var i = 0; i < 5 && position < end; i++)
        {
            var b = data[position++];
            if (value > int.MaxValue >> 7) throw Invalid();
            value = (value << 7) | (uint)(b & 127);
            if ((b & 128) != 0) return checked((int)value);
        }
        throw Invalid();
    }
    internal sealed record IndexEntry(string Text, Dictionary<int, List<int>> Tags)
    {
        internal int Value(int tag, int item = 0, int fallback = 0) => Tags.TryGetValue(tag, out var values) && item < values.Count ? values[item] : fallback;
    }
    internal sealed record Index(List<IndexEntry> Entries, Dictionary<int, string> Strings);

    internal static Index ReadIndex(Func<int, byte[]> record, int number, Encoding encoding, CancellationToken token)
    {
        var main = record(number);
        if (!Magic(main, "INDX")) throw Invalid();
        var count = Int(main, 24);
        var stringCount = Int(main, 52);
        var tagStart = Int(main, 4);
        if (count > 65535 || stringCount > 65535 || !Magic(Slice(main, tagStart, 4), "TAGX")) throw Invalid();
        var tagLength = Int(main, tagStart + 4);
        var controlCount = Int(main, tagStart + 8);
        if (tagLength < 12 || (tagLength - 12) % 4 != 0 || controlCount > 256) throw Invalid();
        var tagTable = Slice(main, tagStart + 12, tagLength - 12);
        var strings = new Dictionary<int, string>();
        for (var i = 0; i < stringCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var data = record(number + count + 1 + i);
            var p = 0;
            while (p < data.Length && data[p] != 0)
            {
                var start = p;
                var length = Vwi(data, ref p, data.Length);
                strings.Add(checked(i * 65536 + start), encoding.GetString(Slice(data, p, length)));
                p += length;
            }
        }
        var entries = new List<IndexEntry>();
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var data = record(number + 1 + i);
            if (!Magic(data, "INDX")) throw Invalid();
            var idxt = Int(data, 20);
            var entryCount = Int(data, 24);
            if (entryCount > 65535 || !Magic(Slice(data, idxt, 4), "IDXT")) throw Invalid();
            for (var j = 0; j < entryCount; j++)
            {
                var start = U16(data, idxt + 4 + j * 2);
                var end = j + 1 < entryCount ? U16(data, idxt + 6 + j * 2) : idxt;
                if (start >= end) throw Invalid();
                var labelLength = data[start];
                var text = encoding.GetString(Slice(data, start + 1, labelLength));
                var controls = start + 1 + labelLength;
                var p = controls + controlCount;
                var control = 0;
                var pending = new List<(int Tag, int Count, int Bytes, int Values)>();
                for (var t = 0; t < tagTable.Length; t += 4)
                {
                    var tag = tagTable[t]; var values = tagTable[t + 1]; var mask = tagTable[t + 2];
                    if (tagTable[t + 3] == 1) { control++; continue; }
                    if (control >= controlCount || controls + control >= end) throw Invalid();
                    var value = data[controls + control] & mask;
                    if (value == 0) continue;
                    if (value == mask && BitOperations.PopCount((uint)mask) > 1)
                        pending.Add((tag, -1, Vwi(data, ref p, end), values));
                    else
                    {
                        var shift = BitOperations.TrailingZeroCount((uint)mask);
                        pending.Add((tag, value >> shift, 0, values));
                    }
                }
                var tags = new Dictionary<int, List<int>>();
                foreach (var tag in pending)
                {
                    var values = new List<int>();
                    if (tag.Count >= 0)
                        for (var v = 0; v < tag.Count; v++)
                            for (var item = 0; item < tag.Values; item++)
                                values.Add(Vwi(data, ref p, end));
                    else
                    {
                        var stop = checked(p + tag.Bytes);
                        if (stop > end) throw Invalid();
                        while (p < stop) values.Add(Vwi(data, ref p, stop));
                    }
                    tags[tag.Tag] = values;
                }
                entries.Add(new IndexEntry(text, tags));
            }
        }
        return new Index(entries, strings);
    }

    internal static byte[] PalmDoc(byte[] input)
    {
        var output = new List<byte>();
        for (var p = 0; p < input.Length;)
        {
            var b = input[p++];
            if (b is >= 1 and <= 8) { output.AddRange(Slice(input, p, b)); p += b; }
            else if (b < 128) output.Add(b);
            else if (b >= 192) { output.Add(32); output.Add((byte)(b ^ 128)); }
            else
            {
                if (p >= input.Length) throw Invalid();
                var pair = (b << 8) | input[p++];
                var distance = (pair >> 3) & 2047;
                var length = (pair & 7) + 3;
                if (distance == 0 || distance > output.Count) throw Invalid();
                for (var i = 0; i < length; i++) output.Add(output[output.Count - distance]);
            }
            if (output.Count > 65536) throw Invalid();
        }
        return output.ToArray();
    }

    internal sealed class HuffDic
    {
        private readonly (int Length, bool Terminal, uint Max)[] _first = new (int, bool, uint)[256];
        private readonly uint[] _min = new uint[33];
        private readonly uint[] _max = new uint[33];
        private readonly List<(byte[] Data, bool Literal)> _dictionary = [];
        private readonly HashSet<int> _expanding = [];
        private readonly CancellationToken _token;
        internal HuffDic(Func<int, byte[]> record, int first, int count, CancellationToken token)
        {
            _token = token;
            var huff = record(first);
            if (!Magic(huff, "HUFF") || count < 2 || count > 65535) throw Invalid();
            var table1 = Int(huff, 8); var table2 = Int(huff, 12);
            for (var i = 0; i < 256; i++)
            {
                var word = U32(huff, table1 + i * 4);
                var length = (int)(word & 31);
                if (length == 0) throw Invalid();
                _first[i] = (length, (word & 128) != 0, unchecked((uint)(((ulong)(word >> 8) + 1) << (32 - length)) - 1));
            }
            for (var length = 1; length <= 32; length++)
            {
                _min[length] = U32(huff, table2 + (length - 1) * 8) << (32 - length);
                _max[length] = unchecked((uint)(((ulong)U32(huff, table2 + (length - 1) * 8 + 4) + 1) << (32 - length)) - 1);
            }
            for (var r = 1; r < count; r++)
            {
                var cdic = record(first + r);
                if (!Magic(cdic, "CDIC")) throw Invalid();
                var total = Int(cdic, 8); var bits = Int(cdic, 12);
                if (bits > 16 || total > 1_000_000) throw Invalid();
                var n = Math.Min(1 << bits, total - _dictionary.Count);
                for (var i = 0; i < n; i++)
                {
                    var offset = 16 + U16(cdic, 16 + i * 2);
                    var length = U16(cdic, offset);
                    _dictionary.Add((Slice(cdic, offset + 2, length & 32767), (length & 32768) != 0));
                }
            }
        }
        internal byte[] Decode(byte[] data, int depth = 0)
        {
            if (depth > 64) throw Invalid();
            using var output = new MemoryStream();
            var bit = 0;
            while (bit < data.Length * 8)
            {
                _token.ThrowIfCancellationRequested();
                uint code = 0;
                for (var b = 0; b < 32; b++)
                {
                    var at = bit + b;
                    code = (code << 1) | (at < data.Length * 8 ? (uint)((data[at / 8] >> (7 - at % 8)) & 1) : 0);
                }
                var (length, terminal, max) = _first[code >> 24];
                if (!terminal)
                {
                    while (length <= 32 && code < _min[length]) length++;
                    if (length > 32) throw Invalid();
                    max = _max[length];
                }
                bit += length;
                if (bit > data.Length * 8) break;
                var index = checked((int)((max - code) >> (32 - length)));
                if (index >= _dictionary.Count) throw Invalid();
                var phrase = _dictionary[index];
                if (!phrase.Literal)
                {
                    if (!_expanding.Add(index)) throw Invalid();
                    phrase = (Decode(phrase.Data, depth + 1), true);
                    _expanding.Remove(index);
                    _dictionary[index] = phrase;
                }
                if (output.Length + phrase.Data.Length > 65536) throw Invalid();
                output.Write(phrase.Data);
            }
            return output.ToArray();
        }
    }
}
