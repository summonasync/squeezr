namespace Squeezr.Semantic.Workspace;

public sealed record QdrantStatus(bool Available, long? Points, int PendingChunks, int TotalChunks = 0)
{
    // Points can only ever be a subset of the distinct chunk hashes, so anything above the chunk count is
    // drift; the slack keeps a mid-pass read quiet while a re-chunked file's old points await their reap.
    private const int DriftTolerance = 16;

    public long? ExcessPoints =>
        Points is { } points && points > TotalChunks + DriftTolerance
            ? points - TotalChunks
            : null;
}
