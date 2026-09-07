namespace Squeezr.Core.Embeddings;

public sealed record VectorPoint(
    ulong Id,
    float[] Vector,
    string ChunkHash,
    uint FileId,
    uint RepoId,
    int StartLine,
    int EndLine);
