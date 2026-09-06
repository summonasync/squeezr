using System.Buffers.Binary;

namespace Squeezr.Core.Indexing.Trigram;

public sealed class TrigramIndexWriter
{
    private readonly List<TrigramDocument> _documents = [];

    public int DocumentCount => _documents.Count;

    public void AddDocument(TrigramDocument document) => _documents.Add(document);

    public void Write(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteHeader(stream, (uint)_documents.Count);
        foreach (var document in _documents)
        {
            TrigramSegment.WriteRecord(stream, document.FileId, document.RepoId, 0u, document.Postings);
        }
    }

    public static void AppendBatch(
        string path,
        IEnumerable<TrigramDocument> documents,
        IEnumerable<(uint RepoId, uint FileId)> tombstones)
    {
        var version = TrigramSegment.TryReadVersion(path);
        if (version is null)
        {
            using (var create = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteHeader(create, 0);
            }
        }
        else if (version != TrigramIndexFormat.CurrentVersion)
        {
            throw new InvalidDataException(
                $"'{path}' is a version {version} index; rebuild it before appending segments.");
        }
        else
        {
            var stats = TrigramSegment.Scan(path);
            if (stats.ValidEnd < stats.FileBytes)
            {
                using var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                truncate.SetLength(stats.ValidEnd);
            }
        }

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        foreach (var document in documents)
        {
            TrigramSegment.WriteRecord(stream, document.FileId, document.RepoId, 0u, document.Postings);
        }

        foreach (var (repoId, fileId) in tombstones)
        {
            TrigramSegment.WriteRecord(stream, fileId, repoId, TrigramSegment.TombstoneFlag, null);
        }

        stream.Flush();
    }

    private static void WriteHeader(Stream stream, uint liveDocCount)
    {
        var header = new byte[TrigramIndexFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), TrigramIndexFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), TrigramIndexFormat.CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), liveDocCount);
        stream.Write(header);
    }
}
