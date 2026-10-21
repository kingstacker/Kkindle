using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class MdxDictionaryEncodingTests
{
    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16")]
    [InlineData("GBK")]
    public async Task ImportsUsingDeclaredEncodingAndLooksUpDefinition(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "sample.mdx");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.css"), ".sense { color: blue; }");
            var encoding = Encoding.GetEncoding(encodingName);
            await File.WriteAllBytesAsync(file, CreateMdx(encodingName, encoding));
            var service = new DictionaryService(new AppPaths(Path.Combine(root, "app")));
            var imported = await service.ImportAsync(file);
            Assert.Equal(1, imported.EntryCount);
            var entry = Assert.Single(await service.LookupAsync("apple"));
            Assert.Contains("苹果", entry.Definition);
            Assert.Contains("<p>", entry.Html);
            Assert.Contains(".sense", entry.Stylesheet);
            // Reimporting a dictionary from the old plain-text format upgrades it in place.
            var database = Directory.GetFiles(root, "*.rich.db", SearchOption.AllDirectories).Single();
            File.Delete(database);
            Assert.Null(Assert.Single(await service.LookupAsync("apple")).Html);
            Assert.Equal(imported.Id, (await service.ImportAsync(file)).Id);
            Assert.NotNull(Assert.Single(await service.LookupAsync("APPLE")).Html);
            await service.RemoveAsync(imported.Id);
            Assert.False(File.Exists(database));
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] CreateMdx(string name, Encoding encoding)
    {
        using var output = new MemoryStream();
        var header = Encoding.Unicode.GetBytes($"<Dictionary GeneratedByEngineVersion=\"2.0\" RequiredEngineVersion=\"2.0\" Encoding=\"{name}\" Encrypted=\"No\"/>\0");
        WriteNumber(output, header.Length, 4);
        output.Write(header);
        output.Write(BitConverter.GetBytes(Adler(header)));
        var word = encoding.GetBytes("apple\0");
        var key = new byte[8].Concat(word).ToArray();
        var record = encoding.GetBytes("<p>苹果 fruit</p>\0");
        using var info = new MemoryStream();
        WriteNumber(info, 1);
        for (var i = 0; i < 2; i++)
        {
            WriteNumber(info, 5, 2);
            info.Write(word);
        }
        WriteNumber(info, key.Length + 8);
        WriteNumber(info, key.Length);
        using var compressed = new MemoryStream();
        compressed.Write(new byte[] { 2, 0, 0, 0 });
        WriteNumber(compressed, Adler(info.ToArray()), 4);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) info.WriteTo(zlib);
        using var numbers = new MemoryStream();
        foreach (var number in new long[] { 1, 1, info.Length, compressed.Length, key.Length + 8 }) WriteNumber(numbers, number);
        numbers.WriteTo(output);
        WriteNumber(output, Adler(numbers.ToArray()), 4);
        compressed.WriteTo(output);
        output.Write(new byte[8]); // uncompressed block marker and unused checksum
        output.Write(key);
        foreach (var number in new long[] { 1, 1, 16, record.Length + 8, record.Length + 8, record.Length }) WriteNumber(output, number);
        output.Write(new byte[8]);
        output.Write(record);
        return output.ToArray();
    }

    private static void WriteNumber(Stream stream, long value, int width = 8)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes[(8 - width)..]);
    }

    private static uint Adler(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
