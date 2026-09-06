using System.Buffers.Binary;
using Shouldly;
using Tg = Squeezr.Core.Indexing.Trigram;

namespace Squeezr.Tests.Indexing.Trigram;

public class TrigramIndexRoundTripTests
{
    private static readonly uint HashA = Tg.TrigramHasher.ComputeHash('a', 'b', 'c');
    private static readonly uint HashB = Tg.TrigramHasher.ComputeHash('x', 'y', 'z');
    private static readonly uint HashC = Tg.TrigramHasher.ComputeHash('q', 'w', 'e');

    [Fact]
    public void RoundTrip_FindsExactDocsAndPositions()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 10, new Dictionary<uint, int[]>
        {
            [HashA] = [0, 7],
            [HashB] = [3]
        }));
        writer.AddDocument(new Tg.TrigramDocument(1, 11, new Dictionary<uint, int[]>
        {
            [HashB] = [0, 5, 9]
        }));
        writer.AddDocument(new Tg.TrigramDocument(2, 20, new Dictionary<uint, int[]>
        {
            [HashA] = [2],
            [HashB] = [1]
        }));
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        reader.DocCount.ShouldBe(3u);

        var postingsA = reader.Find(HashA);
        postingsA.ShouldNotBeNull();
        postingsA!.DocIds.ShouldBe([0u, 2u]);
        postingsA.Positions[0].ShouldBe([0, 7]);
        postingsA.Positions[1].ShouldBe([2]);

        var postingsB = reader.Find(HashB);
        postingsB.ShouldNotBeNull();
        postingsB!.DocIds.ShouldBe([0u, 1u, 2u]);
        postingsB.Positions[1].ShouldBe([0, 5, 9]);

        reader.Find(HashC).ShouldBeNull();
    }

    [Fact]
    public void GetDoc_ReturnsRepoAndFileIds()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(7, 100, new Dictionary<uint, int[]> { [HashA] = [0] }));
        writer.AddDocument(new Tg.TrigramDocument(8, 200, new Dictionary<uint, int[]> { [HashA] = [0] }));
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        reader.GetDoc(0).ShouldBe((7u, 100u));
        reader.GetDoc(1).ShouldBe((8u, 200u));
    }

    [Fact]
    public void IntersectTrigrams_ReturnsOnlyDocsContainingAll()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 1, new Dictionary<uint, int[]> { [HashA] = [0], [HashB] = [1] }));
        writer.AddDocument(new Tg.TrigramDocument(1, 2, new Dictionary<uint, int[]> { [HashA] = [0] }));
        writer.AddDocument(new Tg.TrigramDocument(1, 3, new Dictionary<uint, int[]> { [HashA] = [0], [HashB] = [2], [HashC] = [5] }));
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        reader.IntersectTrigrams([HashA, HashB]).ShouldBe([0u, 2u]);
        reader.IntersectTrigrams([HashA, HashB, HashC]).ShouldBe([2u]);
        reader.IntersectTrigrams([HashA, HashC]).ShouldBe([2u]);
        reader.IntersectTrigrams([HashA, HashC, 999_999u]).ShouldBeEmpty();
    }

    [Fact]
    public void RoundTrip_SurvivesPostingBlockBoundaries()
    {
        const int docCount = 300;
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        for (var i = 0; i < docCount; i++)
        {
            writer.AddDocument(new Tg.TrigramDocument(1, (uint)i, new Dictionary<uint, int[]>
            {
                [HashA] = [i % 7, i % 7 + 20]
            }));
        }
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        var postings = reader.Find(HashA);
        postings.ShouldNotBeNull();
        postings!.DocIds.ShouldBe(Enumerable.Range(0, docCount).Select(i => (uint)i).ToArray());
        postings.Positions[299].ShouldBe([299 % 7, 299 % 7 + 20]);
    }

    [Fact]
    public void EmptyIndex_RoundTrips()
    {
        var path = NewIndexPath();
        new Tg.TrigramIndexWriter().Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        reader.DocCount.ShouldBe(0u);
        reader.Find(HashA).ShouldBeNull();
        reader.IntersectTrigrams([HashA]).ShouldBeEmpty();
    }

    [Fact]
    public void Reader_ThrowsOnWrongMagic()
    {
        var path = NewIndexPath();
        File.WriteAllBytes(path, new byte[Tg.TrigramIndexFormat.HeaderSize]);

        Should.Throw<InvalidDataException>(() => new Tg.TrigramIndexReader(path))
            .Message.ShouldContain("not a squeezr trigram index");
    }

    [Fact]
    public void Reader_ThrowsOnUnsupportedVersion()
    {
        var path = NewIndexPath();
        var header = new byte[Tg.TrigramIndexFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Tg.TrigramIndexFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 99u);
        File.WriteAllBytes(path, header);

        Should.Throw<InvalidDataException>(() => new Tg.TrigramIndexReader(path))
            .Message.ShouldContain("version 99");
    }

    [Fact]
    public void Dispose_ReleasesFileHandle()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 1, new Dictionary<uint, int[]> { [HashA] = [0] }));
        writer.Write(path);

        var reader = new Tg.TrigramIndexReader(path);
        reader.Find(HashA).ShouldNotBeNull();
        reader.Dispose();

        File.Delete(path);
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void Find_LastKeyInTable_DecodesExactly()
    {
        var low = Tg.TrigramHasher.ComputeHash('a', 'a', 'a');
        var high = Tg.TrigramHasher.ComputeHash('z', 'z', 'z');
        var (first, last) = low < high ? (low, high) : (high, low);

        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 1, new Dictionary<uint, int[]> { [first] = [0] }));
        writer.AddDocument(new Tg.TrigramDocument(1, 2, new Dictionary<uint, int[]> { [first] = [4], [last] = [7] }));
        writer.AddDocument(new Tg.TrigramDocument(1, 3, new Dictionary<uint, int[]> { [first] = [2] }));
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        var postings = reader.Find(last);
        postings.ShouldNotBeNull();
        postings!.DocIds.ShouldBe([1u]);
        postings.Positions[0].ShouldBe([7]);
    }

    [Fact]
    public void Reader_ThrowsOnTruncatedFile()
    {
        var path = NewIndexPath();
        var header = new byte[Tg.TrigramIndexFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Tg.TrigramIndexFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Tg.TrigramIndexFormat.CurrentVersion);
        File.WriteAllBytes(path, header[..12]);

        Should.Throw<InvalidDataException>(() => new Tg.TrigramIndexReader(path))
            .Message.ShouldContain("too small");
    }

    [Fact]
    public void Reader_ThrowsOnCorruptOffsets()
    {
        var path = NewIndexPath();
        var header = new byte[Tg.TrigramIndexFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Tg.TrigramIndexFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1u);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), (ulong)Tg.TrigramIndexFormat.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20), (ulong)Tg.TrigramIndexFormat.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(28), 1_000_000ul);
        File.WriteAllBytes(path, header);

        Should.Throw<InvalidDataException>(() => new Tg.TrigramIndexReader(path))
            .Message.ShouldContain("corrupt header");
    }

    [Fact]
    public void GetDoc_ThrowsWhenOutOfRange()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 1, new Dictionary<uint, int[]> { [HashA] = [0] }));
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        Should.Throw<ArgumentOutOfRangeException>(() => reader.GetDoc(1));
    }

    [Fact]
    public void Writer_ThrowsOnUnsortedPositions()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 1, new Dictionary<uint, int[]> { [HashA] = [5, 2] }));

        Should.Throw<ArgumentException>(() => writer.Write(path));
    }

    [Fact]
    public void AppendBatch_AddsTombstoneAndLiveSegments()
    {
        var path = NewIndexPath();
        Tg.TrigramIndexWriter.AppendBatch(
            path,
            [new Tg.TrigramDocument(1, 10, new Dictionary<uint, int[]> { [HashA] = [0, 7] })],
            []);
        Tg.TrigramIndexWriter.AppendBatch(
            path,
            [new Tg.TrigramDocument(1, 11, new Dictionary<uint, int[]> { [HashB] = [3] })],
            [(1u, 10u)]);

        using var reader = new Tg.TrigramIndexReader(path);
        reader.Version.ShouldBe(2u);
        reader.DocCount.ShouldBe(1u);
        reader.Find(HashA).ShouldBeNull();
        var postings = reader.Find(HashB);
        postings.ShouldNotBeNull();
        postings!.DocIds.ShouldBe([0u]);
        reader.GetDoc(0).ShouldBe((1u, 11u));

        var stats = reader.SegmentStats!;
        stats.TotalSegments.ShouldBe(3);
        stats.LiveSegments.ShouldBe(1);
        stats.TombstonedSegments.ShouldBe(2);
    }

    [Fact]
    public void AppendBatch_RefusesLegacyVersion()
    {
        var path = NewIndexPath();
        WriteV1Index(path, [(7u, 100u)]);

        Should.Throw<InvalidDataException>(() =>
            Tg.TrigramIndexWriter.AppendBatch(path, [], []));
    }

    [Fact]
    public void Reader_ToleratesTruncatedTailSegment()
    {
        var path = NewIndexPath();
        Tg.TrigramIndexWriter.AppendBatch(
            path,
            [new Tg.TrigramDocument(1, 10, new Dictionary<uint, int[]> { [HashA] = [0] })],
            []);
        var fullLength = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(fullLength - 4);
        }

        using var reader = new Tg.TrigramIndexReader(path);
        reader.DocCount.ShouldBe(0u);
        reader.Find(HashA).ShouldBeNull();
    }

    [Fact]
    public void Reader_ToleratesCorruptedTrailerInTailSegment()
    {
        var path = NewIndexPath();
        Tg.TrigramIndexWriter.AppendBatch(
            path,
            [new Tg.TrigramDocument(1, 10, new Dictionary<uint, int[]> { [HashA] = [0] })],
            []);
        var length = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = length - 1;
            stream.WriteByte(0xFF);
        }

        using var reader = new Tg.TrigramIndexReader(path);
        reader.DocCount.ShouldBe(0u);
    }

    [Fact]
    public void Reader_ReadsLegacyV1File()
    {
        var path = NewIndexPath();
        WriteV1Index(path, [(1u, 10u), (1u, 11u)]);

        using var reader = new Tg.TrigramIndexReader(path);
        reader.Version.ShouldBe(1u);
        reader.DocCount.ShouldBe(2u);
        reader.GetDoc(0).ShouldBe((1u, 10u));
        reader.GetDoc(1).ShouldBe((1u, 11u));

        var postings = reader.Find(HashA);
        postings.ShouldNotBeNull();
        postings!.DocIds.ShouldBe([0u, 1u]);
        reader.SegmentStats.ShouldBeNull();
    }

    [Fact]
    public void Scan_ReportsTombstoneRatio()
    {
        var path = NewIndexPath();
        Tg.TrigramIndexWriter.AppendBatch(
            path,
            [new Tg.TrigramDocument(1, 10, new Dictionary<uint, int[]> { [HashA] = Enumerable.Range(0, 200).ToArray() })],
            []);

        var stats = Tg.TrigramSegment.Scan(path);
        stats.TombstoneRatio.ShouldBe(0.0);

        Tg.TrigramIndexWriter.AppendBatch(path, [], [(1u, 10u)]);
        stats = Tg.TrigramSegment.Scan(path);
        stats.TotalSegments.ShouldBe(2);
        stats.LiveSegments.ShouldBe(0);
        stats.TombstoneRatio.ShouldBeGreaterThan(0.3);
    }

    [Fact]
    public void GetDocumentPostings_ReturnsLiveSegmentPayload()
    {
        var path = NewIndexPath();
        var writer = new Tg.TrigramIndexWriter();
        writer.AddDocument(new Tg.TrigramDocument(1, 10, new Dictionary<uint, int[]> { [HashA] = [0, 7], [HashB] = [3] }));
        writer.Write(path);

        using var reader = new Tg.TrigramIndexReader(path);
        var postings = reader.GetDocumentPostings(0);
        postings[HashA].ShouldBe([0, 7]);
        postings[HashB].ShouldBe([3]);
    }

    private static string NewIndexPath() =>
        Path.Combine(TestDirs.NewTempDir(), "trigram.idx");

    private static void WriteV1Index(string path, IReadOnlyList<(uint RepoId, uint FileId)> docs)
    {
        var postingsBuffer = new MemoryStream();
        var index = new List<(uint Key, uint Offset)>();
        var hashA = Tg.TrigramHasher.ComputeHash('a', 'b', 'c');
        var matches = docs.Select((d, i) => ((uint)i, new[] { i * 3 })).ToList();
        index.Add((hashA, (uint)postingsBuffer.Position));
        WriteV1Postings(postingsBuffer, matches);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var header = new byte[Tg.TrigramIndexFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Tg.TrigramIndexFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)docs.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), (ulong)Tg.TrigramIndexFormat.HeaderSize);
        var postingsOffset = (ulong)Tg.TrigramIndexFormat.HeaderSize + (ulong)docs.Count * Tg.TrigramIndexFormat.DocEntrySize;
        var indexOffset = postingsOffset + (ulong)postingsBuffer.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20), postingsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(28), indexOffset);
        stream.Write(header);

        var docEntry = new byte[Tg.TrigramIndexFormat.DocEntrySize];
        foreach (var (repoId, fileId) in docs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(docEntry.AsSpan(0), repoId);
            BinaryPrimitives.WriteUInt32LittleEndian(docEntry.AsSpan(4), fileId);
            stream.Write(docEntry);
        }

        postingsBuffer.WriteTo(stream);
        var indexEntry = new byte[Tg.TrigramIndexFormat.IndexEntrySize];
        foreach (var (key, offset) in index)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(indexEntry.AsSpan(0), key);
            BinaryPrimitives.WriteUInt64LittleEndian(indexEntry.AsSpan(4), offset);
            stream.Write(indexEntry);
        }
    }

    private static void WriteV1Postings(MemoryStream target, List<(uint DocId, int[] Positions)> matches)
    {
        WriteVarInt(target, (uint)matches.Count);
        var previousDocId = 0u;
        for (var i = 0; i < matches.Count; i++)
        {
            var (docId, positions) = matches[i];
            WriteVarInt(target, docId - (i % Tg.TrigramIndexFormat.PostingBlockSize == 0 ? 0u : previousDocId));
            WriteVarInt(target, (uint)positions.Length);
            var previousPosition = 0;
            foreach (var position in positions)
            {
                WriteVarInt(target, (uint)(position - previousPosition));
                previousPosition = position;
            }

            previousDocId = docId;
        }
    }

    private static void WriteVarInt(MemoryStream target, uint value)
    {
        while (value >= 0x80)
        {
            target.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        target.WriteByte((byte)value);
    }
}
