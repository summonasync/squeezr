namespace Squeezr.Core.Embeddings;

public interface IVectorStore
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);

    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);

    Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct);

    // Which of THESE ids the collection does not hold. The claim is asked in the unit it is made of instead
    // of being weighed against the collection's size: a full scan answers the same question at the price of
    // the collection, and review-116-10 measured that comparing counts answers it falsely — an equal number
    // of foreign points reads as reconciled. The default is the walk this method exists to replace, so a
    // store that cannot answer by id still answers correctly; the store that can pays for the claim instead.
    async Task<IReadOnlyList<ulong>> MissingPointIdsAsync(
        IReadOnlyList<ulong> pointIds,
        CancellationToken ct)
    {
        var present = (await ScrollPointIdsAsync(ct)).ToHashSet();
        return [.. pointIds.Where(id => !present.Contains(id))];
    }

    Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct);

    Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct);

    Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct);

    Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct);

    Task DeleteCollectionAsync(CancellationToken ct);
}
