using System.Buffers.Binary;

namespace Squeezr.Core.Indexing.Trigram;

public sealed record TrigramSegmentStats(
    int TotalSegments,
    int LiveSegments,
    int TombstonedSegments,
    long TombstonedBytes,
    long FileBytes,
    long ValidEnd)
{
    public double TombstoneRatio => FileBytes > 0 ? (double)TombstonedBytes / FileBytes : 0.0;
}

public static class TrigramSegment
{
    public const uint TombstoneFlag = 1u;
    public const uint TrailerMagic = 0x544D4753u;
    public const int RecordHeaderSize = 16;
    public const int RecordTrailerSize = 8;

    private const long MaxPayloadBytes = 1L << 28;

    public static uint? TryReadVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < TrigramIndexFormat.HeaderSize)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[TrigramIndexFormat.HeaderSize];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != TrigramIndexFormat.Magic)
        {
            throw new InvalidDataException($"'{path}' is not a squeezr trigram index.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    }

    public static void WriteRecord(
        Stream target,
        uint fileId,
        uint repoId,
        uint flags,
        IReadOnlyDictionary<uint, int[]>? postings)
    {
        var payload = new MemoryStream();
        if (postings is not null)
        {
            EncodePayload(payload, postings);
        }

        var recordHeader = new byte[RecordHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader.AsSpan(0), fileId);
        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader.AsSpan(4), repoId);
        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader.AsSpan(12), (uint)payload.Length);

        var trailer = new byte[RecordTrailerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0), Fnv1a.Compute(payload.GetBuffer().AsSpan(0, (int)payload.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), TrailerMagic);

        target.Write(recordHeader);
        payload.WriteTo(target);
        target.Write(trailer);
    }

    public static Dictionary<uint, int[]> DecodePayload(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var hashCount = (int)ReadVarInt(payload, ref offset);
        var postings = new Dictionary<uint, int[]>(hashCount);
        for (var i = 0; i < hashCount; i++)
        {
            if (offset + 4 > payload.Length)
            {
                throw new FormatException("Segment payload ended inside a hash entry.");
            }

            var hash = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            offset += 4;
            var positionCount = (int)ReadVarInt(payload, ref offset);
            if (positionCount > payload.Length - offset)
            {
                throw new FormatException("Segment payload position count is out of bounds.");
            }

            var positions = new int[positionCount];
            var previous = 0;
            for (var j = 0; j < positionCount; j++)
            {
                previous += (int)ReadVarInt(payload, ref offset);
                positions[j] = previous;
            }

            postings[hash] = positions;
        }

        return postings;
    }

    public static TrigramSegmentStats Scan(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < TrigramIndexFormat.HeaderSize)
        {
            return new TrigramSegmentStats(0, 0, 0, 0, stream.Length, 0);
        }

        var position = (long)TrigramIndexFormat.HeaderSize;
        var validEnd = position;
        var totalSegments = 0;
        long tombstonedBytes = 0;
        var liveBytesByFile = new Dictionary<uint, long>();

        Span<byte> recordHeader = stackalloc byte[RecordHeaderSize];
        while (position + RecordHeaderSize + RecordTrailerSize <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(recordHeader);
            var fileId = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[..4]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[8..12]);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[12..16]);
            var recordBytes = (long)RecordHeaderSize + payloadLength + RecordTrailerSize;
            if (payloadLength > MaxPayloadBytes || position + recordBytes > stream.Length)
            {
                break;
            }

            totalSegments++;
            if ((flags & TombstoneFlag) != 0)
            {
                tombstonedBytes += recordBytes;
                if (liveBytesByFile.Remove(fileId, out var deadBytes))
                {
                    tombstonedBytes += deadBytes;
                }
            }
            else if (liveBytesByFile.TryGetValue(fileId, out var supersededBytes))
            {
                tombstonedBytes += supersededBytes;
                liveBytesByFile[fileId] = recordBytes;
            }
            else
            {
                liveBytesByFile[fileId] = recordBytes;
            }

            validEnd = position + recordBytes;
            position = validEnd;
        }

        return new TrigramSegmentStats(
            totalSegments,
            liveBytesByFile.Count,
            totalSegments - liveBytesByFile.Count,
            tombstonedBytes,
            stream.Length,
            validEnd);
    }

    private static void EncodePayload(MemoryStream target, IReadOnlyDictionary<uint, int[]> postings)
    {
        WriteVarInt(target, (uint)postings.Count);
        Span<byte> hashBytes = stackalloc byte[4];
        foreach (var (hash, positions) in postings.OrderBy(kv => kv.Key, Comparer<uint>.Default))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hashBytes, hash);
            target.Write(hashBytes);
            WriteVarInt(target, (uint)positions.Length);

            var previous = 0;
            var first = true;
            foreach (var position in positions)
            {
                if (!first && position <= previous)
                {
                    throw new ArgumentException("Positions must be in strictly ascending order.", nameof(postings));
                }

                WriteVarInt(target, (uint)(position - previous));
                previous = position;
                first = false;
            }
        }
    }

    private static uint ReadVarInt(ReadOnlySpan<byte> source, ref int offset)
    {
        var result = 0u;
        var shift = 0;
        while (true)
        {
            if (offset >= source.Length || shift > 28)
            {
                throw new FormatException("Malformed varint in segment payload.");
            }

            var b = source[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    private static void WriteVarInt(MemoryStream target, uint value)
    {
        Span<byte> scratch = stackalloc byte[5];
        var written = VarInt.Write(scratch, value);
        target.Write(scratch[..written]);
    }
}
