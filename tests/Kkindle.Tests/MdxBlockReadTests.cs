using MdxParser.Models;

namespace Kkindle.Tests;

public sealed class MdxBlockReadTests
{
    [Fact]
    public void ReadsCompleteNumbersAndBlocksWhenStreamReturnsShortReads()
    {
        var block = new TestBlock();
        using var signed = new ShortReadStream([1, 2, 3, 4]);
        Assert.Equal(0x01020304, block.readInt32(signed));
        using var unsigned = new ShortReadStream([0xf1, 2, 3, 4]);
        Assert.Equal(0xf1020304u, block.readUInt32(unsigned));
        using var wide = new ShortReadStream([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(0x0102030405060708L, block.readInt64(wide));
        using var bytes = new ShortReadStream([1, 2, 3, 4, 5]);
        var buffer = new byte[5];
        block.readBytes(bytes, buffer);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer);
    }

    [Fact]
    public void RejectsTruncatedNumbersInsteadOfPaddingThemWithZeros()
    {
        var block = new TestBlock();
        using var stream = new ShortReadStream([1, 2]);
        Assert.Throws<EndOfStreamException>(() => block.readInt32(stream));
    }

    private sealed class TestBlock : AbsoluteBlock;

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(2, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(2, buffer.Length)]);
    }
}
