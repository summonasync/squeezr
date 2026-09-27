using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class QdrantStatusProbe
{
    public static async Task<QdrantStatus> ProbeAsync(IVectorStore? store, MetaStore metaStore, CancellationToken ct)
    {
        var coverage = metaStore.GetSemanticCoverage();
        if (store is null)
        {
            return new QdrantStatus(false, null, coverage.PendingChunks, coverage.TotalChunks);
        }

        var available = await store.IsAvailableAsync(ct);
        long? points = null;
        if (available)
        {
            try
            {
                points = await store.CountAsync(ct);
            }
            catch
            {
                available = false;
            }
        }

        return new QdrantStatus(available, points, coverage.PendingChunks, coverage.TotalChunks);
    }
}
