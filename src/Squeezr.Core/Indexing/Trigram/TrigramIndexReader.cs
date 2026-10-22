using System.Buffers;
using System.Buffers.Binary;

namespace Squeezr.Core.Indexing.Trigram;

public sealed class TrigramIndexReader
{
    private readonly List<(uint RepoId, uint FileId)> _docs = [];
    private readonly List<Dictionary<uint, int[]>> _docPostings = [];
    private Dictionary<uint, TrigramPostings>? _inverted;

    public uint DocCount => (uint)_docs.Count;

    public uint Version { get; }

    public TrigramSegmentStats? SegmentStats { get; }

    public TrigramIndexReader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < TrigramIndexFormat.HeaderSize)
        {
            throw new InvalidDataException($"'{path}' is too small to be a trigram index ({stream.Length} bytes).");
        }

        var header = new byte[TrigramIndexFormat.HeaderSize];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0)) != TrigramIndexFormat.Magic)
        {
            throw new InvalidDataException($"'{path}' is not a squeezr trigram index.");
        }

        Version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (Version == 1)
        {
            LoadV1(stream, header, path);
        }
        else if (Version == TrigramIndexFormat.CurrentVersion)
        {
            SegmentStats = LoadV2(stream);
        }
        else
        {
            throw new InvalidDataException(
                $"Unsupported trigram index version {Version} (expected {TrigramIndexFormat.CurrentVersion}).");
        }
    }

    public (uint RepoId, uint FileId) GetDoc(uint docId)
    {
        if (docId >= _docs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(docId), docId, $"Index contains {_docs.Count} docs.");
        }

        return _docs[(int)docId];
    }

    public IReadOnlyDictionary<uint, int[]> GetDocumentPostings(uint docId)
    {
        if (docId >= _docPostings.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(docId), docId, $"Index contains {_docPostings.Count} docs.");
        }

        return _docPostings[(int)docId];
    }

    public TrigramPostings? Find(uint hash)
    {
        _inverted ??= BuildInverted();
        return _inverted.GetValueOrDefault(hash);
    }

    // same ascending DocIds Find serves after the inverted build, at O(docs) dictionary probes —
    // worth it when only a handful of hashes is asked, where building the inverted index costs
    // a full pass over every posting of every doc
    public uint[] ScanDocIds(uint hash)
    {
        var hits = new List<uint>();
        for (var docId = 0; docId < _docPostings.Count; docId++)
        {
            if (_docPostings[docId].ContainsKey(hash))
            {
                hits.Add((uint)docId);
            }
        }

        return [.. hits];
    }

    public uint[] IntersectTrigrams(IReadOnlyList<uint> hashes)
    {
        if (hashes.Count == 0)
        {
            return [];
        }

        var lists = new List<uint[]>(hashes.Count);
        foreach (var hash in hashes)
        {
            var postings = Find(hash);
            if (postings is null)
            {
                return [];
            }

            lists.Add(postings.DocIds);
        }

        return PostingListIntersect.Intersect(lists);
    }

    private void LoadV1(Stream stream, byte[] header, string path)
    {
        var docCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var docsOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12));
        var postingsOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20));
        var indexOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(28));

        if (docsOffset != TrigramIndexFormat.HeaderSize
            || docsOffset > postingsOffset
            || postingsOffset > indexOffset
            || indexOffset > (ulong)stream.Length
            || docsOffset + (ulong)docCount * TrigramIndexFormat.DocEntrySize > postingsOffset)
        {
            throw new InvalidDataException($"'{path}' has a corrupt header.");
        }

        var docEntry = new byte[TrigramIndexFormat.DocEntrySize];
        for (uint i = 0; i < docCount; i++)
        {
            stream.Position = (long)(docsOffset + (ulong)i * TrigramIndexFormat.DocEntrySize);
            stream.ReadExactly(docEntry);
            _docs.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(docEntry.AsSpan(0)),
                BinaryPrimitives.ReadUInt32LittleEndian(docEntry.AsSpan(4))));
            _docPostings.Add([]);
        }

        var keyCount = (int)(((ulong)stream.Length - indexOffset) / TrigramIndexFormat.IndexEntrySize);
        var indexEntry = new byte[TrigramIndexFormat.IndexEntrySize];
        for (var entry = 0; entry < keyCount; entry++)
        {
            stream.Position = (long)(indexOffset + (ulong)entry * TrigramIndexFormat.IndexEntrySize);
            stream.ReadExactly(indexEntry);
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(indexEntry.AsSpan(0));
            var listOffset = BinaryPrimitives.ReadUInt64LittleEndian(indexEntry.AsSpan(4));

            var endOffset = (ulong)entry + 1 < (ulong)keyCount
                ? postingsOffset + ReadV1IndexOffset(stream, indexOffset, entry + 1)
                : indexOffset;
            var length = (long)(endOffset - (postingsOffset + listOffset));
            if (length < 0 || postingsOffset + listOffset + (ulong)length > (ulong)stream.Length)
            {
                throw new InvalidDataException($"'{path}' has a corrupt posting list offset.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                stream.Position = (long)(postingsOffset + listOffset);
                stream.ReadExactly(buffer.AsSpan(0, (int)length));
                var postings = DecodeV1Postings(buffer.AsSpan(0, (int)length));
                for (var i = 0; i < postings.DocIds.Length; i++)
                {
                    _docPostings[(int)postings.DocIds[i]][hash] = postings.Positions[i];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private TrigramSegmentStats LoadV2(Stream stream)
    {
        var position = (long)TrigramIndexFormat.HeaderSize;
        var totalSegments = 0;
        long tombstonedBytes = 0;
        var liveSlots = new List<LiveSlot>();
        var liveIndexByFile = new Dictionary<uint, int>();

        var recordHeader = new byte[TrigramSegment.RecordHeaderSize];
        var trailer = new byte[TrigramSegment.RecordTrailerSize];
        while (position + TrigramSegment.RecordHeaderSize + TrigramSegment.RecordTrailerSize <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(recordHeader);
            var fileId = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(0));
            var repoId = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(4));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(8));
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(12));
            var recordBytes = (long)TrigramSegment.RecordHeaderSize + payloadLength + TrigramSegment.RecordTrailerSize;
            if (payloadLength > 1L << 28 || position + recordBytes > stream.Length)
            {
                break;
            }

            var payload = ArrayPool<byte>.Shared.Rent((int)payloadLength);
            Dictionary<uint, int[]>? postings = null;
            try
            {
                stream.ReadExactly(payload.AsSpan(0, (int)payloadLength));
                stream.ReadExactly(trailer);
                var checksum = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(0));
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4));
                if (magic != TrigramSegment.TrailerMagic
                    || checksum != Fnv1a.Compute(payload.AsSpan(0, (int)payloadLength)))
                {
                    break;
                }

                if ((flags & TrigramSegment.TombstoneFlag) == 0)
                {
                    postings = TrigramSegment.DecodePayload(payload.AsSpan(0, (int)payloadLength));
                }
            }
            catch (FormatException)
            {
                break;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }

            totalSegments++;
            if ((flags & TrigramSegment.TombstoneFlag) != 0)
            {
                tombstonedBytes += recordBytes;
                if (liveIndexByFile.Remove(fileId, out var removedSlot))
                {
                    tombstonedBytes += RecordBytesOf(liveSlots[removedSlot].Postings);
                    liveSlots[removedSlot] = LiveSlot.Dead;
                }
            }
            else if (liveIndexByFile.Remove(fileId, out var supersededSlot))
            {
                tombstonedBytes += RecordBytesOf(liveSlots[supersededSlot].Postings);
                liveSlots[supersededSlot] = new LiveSlot(repoId, fileId, postings!);
                liveIndexByFile[fileId] = supersededSlot;
            }
            else
            {
                liveIndexByFile[fileId] = liveSlots.Count;
                liveSlots.Add(new LiveSlot(repoId, fileId, postings!));
            }

            position += recordBytes;
        }

        foreach (var slot in liveSlots)
        {
            if (!slot.IsDead)
            {
                _docs.Add((slot.RepoId, slot.FileId));
                _docPostings.Add(slot.Postings);
            }
        }

        return new TrigramSegmentStats(
            totalSegments,
            _docs.Count,
            totalSegments - _docs.Count,
            tombstonedBytes,
            stream.Length,
            position);
    }

    private readonly record struct LiveSlot(uint RepoId, uint FileId, Dictionary<uint, int[]> Postings)
    {
        public static LiveSlot Dead { get; } = new(0u, 0u, new Dictionary<uint, int[]>());

        public bool IsDead => ReferenceEquals(Postings, Dead.Postings);
    }

    private static long RecordBytesOf(Dictionary<uint, int[]> postings) =>
        TrigramSegment.RecordHeaderSize
        + TrigramSegment.RecordTrailerSize
        + PayloadBytes(postings);

    private static long PayloadBytes(Dictionary<uint, int[]> postings)
    {
        long bytes = VarIntLength((uint)postings.Count);
        foreach (var (hash, positions) in postings)
        {
            bytes += 4 + VarIntLength((uint)positions.Length);
            var previous = 0;
            foreach (var position in positions)
            {
                bytes += VarIntLength((uint)(position - previous));
                previous = position;
            }
        }

        return bytes;
    }

    private static int VarIntLength(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    private static ulong ReadV1IndexOffset(Stream stream, ulong indexOffset, int entry)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.Position = (long)(indexOffset + (ulong)entry * TrigramIndexFormat.IndexEntrySize + 4);
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private Dictionary<uint, TrigramPostings> BuildInverted()
    {
        var builder = new Dictionary<uint, List<(uint DocId, int[] Positions)>>();
        for (var docId = 0u; docId < _docPostings.Count; docId++)
        {
            foreach (var (hash, positions) in _docPostings[(int)docId])
            {
                if (!builder.TryGetValue(hash, out var list))
                {
                    list = [];
                    builder[hash] = list;
                }

                list.Add((docId, positions));
            }
        }

        var inverted = new Dictionary<uint, TrigramPostings>(builder.Count);
        foreach (var (hash, list) in builder)
        {
            inverted[hash] = new TrigramPostings
            {
                DocIds = list.Select(l => l.DocId).ToArray(),
                Positions = list.Select(l => l.Positions).ToArray(),
            };
        }

        return inverted;
    }

    private static TrigramPostings DecodeV1Postings(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var docCount = (int)ReadVarInt(data, ref offset);
        var docIds = new uint[docCount];
        var positions = new int[docCount][];

        var previousDocId = 0u;
        for (var i = 0; i < docCount; i++)
        {
            var delta = ReadVarInt(data, ref offset);
            var docId = i % TrigramIndexFormat.PostingBlockSize == 0 ? delta : previousDocId + delta;
            docIds[i] = docId;

            var positionCount = (int)ReadVarInt(data, ref offset);
            var docPositions = new int[positionCount];
            var previousPosition = 0;
            for (var j = 0; j < positionCount; j++)
            {
                var positionDelta = (int)ReadVarInt(data, ref offset);
                docPositions[j] = previousPosition + positionDelta;
                previousPosition = docPositions[j];
            }

            positions[i] = docPositions;
            previousDocId = docId;
        }

        return new TrigramPostings { DocIds = docIds, Positions = positions };
    }

    private static uint ReadVarInt(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = VarInt.Read(data[offset..], out var read);
        offset += read;
        return value;
    }
}
