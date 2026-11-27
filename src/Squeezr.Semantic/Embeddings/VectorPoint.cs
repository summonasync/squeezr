namespace Squeezr.Semantic.Embeddings;

// Kind is null when the point carries no kind payload at all — the shape a point written before the kind
// feature had. An empty string is a value Qdrant answers for, so mapping absent kind to "" would make the
// backfill pass that looks for points WITHOUT kind report a repaired collection that was never repaired.
public sealed record VectorPoint(
    ulong Id,
    float[] Vector,
    string ChunkHash,
    uint FileId,
    uint RepoId,
    int StartLine,
    int EndLine,
    string? Kind);
