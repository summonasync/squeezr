namespace Squeezr.Core.Embeddings;

public interface IVectorStore
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);

    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, uint? repoId, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);

    Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct);

    Task DeleteCollectionAsync(CancellationToken ct);
}
