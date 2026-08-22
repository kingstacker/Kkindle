using System.Buffers.Binary;
using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class BookMetadataTests
{
    [Fact]
    public async Task ReadsTitleAndAuthorsFromAzw3MobiMetadata()
    {
        var root = TestHelpers.CreateTempDirectory();
        var path = Path.Combine(root, "converted.azw3");
        try
        {
            var title = Encoding.UTF8.GetBytes("雪的练习生");
            var author = Encoding.UTF8.GetBytes("作者甲");
            const int firstRecordOffset = 86;
            const int mobiOffset = 16;
            const int exthOffset = 160;
            const int titleOffset = 260;
            var bytes = new byte[512];
            WriteUInt16(bytes, 76, 1);
            WriteUInt32(bytes, 78, firstRecordOffset);
            "MOBI"u8.CopyTo(bytes.AsSpan(firstRecordOffset + mobiOffset));
            WriteUInt32(bytes, firstRecordOffset + mobiOffset + 0x0C, 65001);
            WriteUInt32(bytes, firstRecordOffset + mobiOffset + 0x44, titleOffset);
            WriteUInt32(bytes, firstRecordOffset + mobiOffset + 0x48, title.Length);
            title.CopyTo(bytes.AsSpan(firstRecordOffset + titleOffset));

            var exth = firstRecordOffset + exthOffset;
            "EXTH"u8.CopyTo(bytes.AsSpan(exth));
            WriteUInt32(bytes, exth + 4, 20 + author.Length);
            WriteUInt32(bytes, exth + 8, 1);
            WriteUInt32(bytes, exth + 12, 100);
            WriteUInt32(bytes, exth + 16, 8 + author.Length);
            author.CopyTo(bytes.AsSpan(exth + 20));
            await File.WriteAllBytesAsync(path, bytes);

            var metadata = await new BookMetadataService().ReadMetadataAsync(path);

            Assert.Equal("雪的练习生", metadata.Title);
            Assert.Equal("作者甲", metadata.Authors);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), (uint)value);
}
