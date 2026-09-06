namespace Squeezr.Core.Indexing.Trigram;

public static class VarInt
{
    public static int Write(Span<byte> destination, uint value)
    {
        var written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[written++] = (byte)value;
        return written;
    }

    public static uint Read(ReadOnlySpan<byte> source, out int bytesRead)
    {
        uint result = 0;
        var shift = 0;
        var read = 0;
        while (true)
        {
            var b = source[read++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        bytesRead = read;
        return result;
    }
}
