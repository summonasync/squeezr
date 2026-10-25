namespace Squeezr.Core.Embeddings;

public interface IVectorStore
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);

    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);

    Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct);

    Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct);

    Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct);

    Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct);

    Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct);

    Task DeleteCollectionAsync(CancellationToken ct);
}
