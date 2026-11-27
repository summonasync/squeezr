namespace Squeezr.Semantic.Embeddings;

public sealed record VectorHit(
    ulong Id,
    string ChunkHash,
    uint FileId,
    uint RepoId,
    int StartLine,
    int EndLine,
    double Score);
