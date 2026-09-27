using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed record VectorReconcileReport(
    long PointsBefore,
    int Chunks,
    int LiveHashes,
    int Orphans,
    int ChunksWithoutPoints,
    bool Applied,
    long? PointsAfter,
    IReadOnlyList<ulong> OrphanSample);

public static class VectorPointReconciler
{
    private const int SampleSize = 5;

    public static async Task<VectorReconcileReport> ReconcileAsync(
        string workspaceRoot,
        IVectorStore vectorStore,
        bool apply,
        Action<string>? onStaleTakeover = null,
        CancellationToken ct = default)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr init' first.",
                WorkspaceException.NotInitializedCode);
        }

        var dbPath = SqueezrPaths.MetaDbPath(workspaceRoot);
        if (!File.Exists(dbPath))
        {
            throw new WorkspaceException(
                "Index is not built. Run 'squeezr index' first.",
                WorkspaceException.IndexNotBuiltCode);
        }

        if (!await vectorStore.IsAvailableAsync(ct))
        {
            throw new WorkspaceException(
                "Qdrant is unavailable — start it with 'squeezr services up' and retry.");
        }

        // The lock keeps an index pass from re-chunking files under the comparison: chunk rows rewritten
        // mid-scroll would leave their freshly upserted points looking like orphans.
        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspaceRoot), onStaleTakeover))
        using (var store = new MetaStore(dbPath))
        {
            var coverage = store.GetSemanticCoverage();
            var liveIds = store.ListDistinctChunkHashes().Select(ChunkPointId.FromHash).ToHashSet();
            var pointIds = await vectorStore.ScrollPointIdsAsync(ct);

            if (coverage.TotalChunks == 0 && pointIds.Count > 0)
            {
                throw new WorkspaceException(
                    $"The index holds no chunks, but the collection carries {pointIds.Count} point(s) — " +
                    "refusing to treat every point as an orphan. Run 'squeezr index' first.");
            }

            var orphans = pointIds.Where(id => !liveIds.Contains(id)).ToList();
            var chunksWithoutPoints = liveIds.Count - (pointIds.Count - orphans.Count);
            long? pointsAfter = null;

            if (apply)
            {
                try
                {
                    if (orphans.Count > 0)
                    {
                        await vectorStore.DeleteByPointIdsAsync(orphans, ct);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
                {
                    throw new WorkspaceException(
                        $"Failed to delete orphan points: {ex.Message}. Some may already be gone — " +
                        "re-run 'squeezr vectors reconcile --apply' to finish; the pass is idempotent.",
                        innerException: ex);
                }

                pointsAfter = await vectorStore.CountAsync(ct);
            }

            return new VectorReconcileReport(
                pointIds.Count,
                coverage.TotalChunks,
                liveIds.Count,
                orphans.Count,
                chunksWithoutPoints,
                apply,
                pointsAfter,
                orphans.Take(SampleSize).ToList());
        }
    }
}
