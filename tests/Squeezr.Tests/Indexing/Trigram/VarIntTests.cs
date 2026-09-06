using Shouldly;
using Tg = Squeezr.Core.Indexing.Trigram;

namespace Squeezr.Tests.Indexing.Trigram;

public class VarIntTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(300u)]
    [InlineData(16384u)]
    [InlineData(1u << 20)]
    [InlineData(uint.MaxValue)]
    public void RoundTrip_ReturnsOriginalValue(uint value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var written = Tg.VarInt.Write(buffer, value);

        var read = Tg.VarInt.Read(buffer[..written], out var bytesRead);

        read.ShouldBe(value);
        bytesRead.ShouldBe(written);
    }

    [Fact]
    public void MaxValue_EncodesInFiveBytes()
    {
        Span<byte> buffer = stackalloc byte[5];
        Tg.VarInt.Write(buffer, uint.MaxValue).ShouldBe(5);
    }

    [Fact]
    public void SequentialValues_ReadBackInOrder()
    {
        var values = new uint[] { 0, 127, 128, 300, 16_384, 2_000_000, uint.MaxValue };
        var buffer = new byte[values.Length * 5];
        var offset = 0;
        foreach (var value in values)
        {
            offset += Tg.VarInt.Write(buffer.AsSpan(offset), value);
        }

        offset.ShouldBeLessThan(buffer.Length);

        var readOffset = 0;
        foreach (var expected in values)
        {
            Tg.VarInt.Read(buffer.AsSpan(readOffset), out var read).ShouldBe(expected);
            readOffset += read;
        }

        readOffset.ShouldBe(offset);
    }
}
