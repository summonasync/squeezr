using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

public sealed record EmbeddingPhaseResult(int Embedded);

public static class EmbeddingIndexPhase
{
    private const int EmbedBatchSize = 32;
    private const int PendingQueryLimit = 4096;

    public static EmbeddingPhaseResult Run(
        RepoDocument repoDoc,
        MetaStore store,
        string modelKey,
        IEmbeddingProvider provider,
        IVectorStore vectorStore,
        Action<string> warning,
        IIndexProgress? progress = null)
    {
        if (!IsStoreAvailable(vectorStore, repoDoc, warning))
        {
            return new EmbeddingPhaseResult(0);
        }

        try
        {
            vectorStore.EnsureCollectionAsync(provider.Dimensions, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (VectorDimensionMismatchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warning($"embeddings skipped for repo '{repoDoc.Name}': {ex.Message}");
            return new EmbeddingPhaseResult(0);
        }

        var embedded = 0;
        var vectorsByHash = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var totalPending = store.CountPendingChunks(repoDoc.Id, modelKey);
        if (totalPending == 0)
        {
            return new EmbeddingPhaseResult(0);
        }

        progress?.PhaseStarted(repoDoc.Name, IndexPhase.Embeddings, totalPending);

        while (true)
        {
            var pending = store.ListPendingChunks(repoDoc.Id, modelKey, PendingQueryLimit);
            if (pending.Count == 0)
            {
                break;
            }

            try
            {
                EmbedNewHashes(pending, vectorsByHash, provider);
                UpsertPending(pending, vectorsByHash, vectorStore);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
            {
                warning($"embeddings skipped for repo '{repoDoc.Name}': {ex.Message}");
                return new EmbeddingPhaseResult(embedded);
            }

            store.MarkChunksEmbedded(pending.Select(c => c.Id), modelKey);
            embedded += pending.Count;

            if (progress is not null)
            {
                foreach (var chunk in pending)
                {
                    progress.ItemProcessed(repoDoc.Name, chunk.ChunkHash);
                }
            }
        }

        return new EmbeddingPhaseResult(embedded);
    }

    private static bool IsStoreAvailable(IVectorStore vectorStore, RepoDocument repoDoc, Action<string> warning)
    {
        try
        {
            if (vectorStore.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warning($"embeddings skipped for repo '{repoDoc.Name}': {ex.Message}");
            return false;
        }

        warning($"embeddings skipped for repo '{repoDoc.Name}': vector store is unavailable");
        return false;
    }

    private static void EmbedNewHashes(
        IReadOnlyList<ChunkDocument> pending,
        Dictionary<string, float[]> vectorsByHash,
        IEmbeddingProvider provider)
    {
        var newHashes = new List<string>();
        var texts = new List<string>();
        foreach (var chunk in pending)
        {
            if (vectorsByHash.ContainsKey(chunk.ChunkHash))
            {
                continue;
            }

            vectorsByHash[chunk.ChunkHash] = [];
            newHashes.Add(chunk.ChunkHash);
            texts.Add(chunk.Text);
        }

        for (var i = 0; i < texts.Count; i += EmbedBatchSize)
        {
            var count = Math.Min(EmbedBatchSize, texts.Count - i);
            var batch = texts.GetRange(i, count);
            var vectors = provider.EmbedAsync(batch, EmbeddingUsage.Document, CancellationToken.None).GetAwaiter().GetResult();
            for (var j = 0; j < count; j++)
            {
                vectorsByHash[newHashes[i + j]] = vectors[j];
            }
        }
    }

    private static void UpsertPending(
        IReadOnlyList<ChunkDocument> pending,
        IReadOnlyDictionary<string, float[]> vectorsByHash,
        IVectorStore vectorStore)
    {
        var pointsByHash = new Dictionary<string, VectorPoint>(StringComparer.Ordinal);
        foreach (var chunk in pending)
        {
            pointsByHash.TryAdd(chunk.ChunkHash, new VectorPoint(
                ChunkPointId.FromHash(chunk.ChunkHash),
                vectorsByHash[chunk.ChunkHash],
                chunk.ChunkHash,
                chunk.FileId,
                chunk.RepoId,
                chunk.StartLine,
                chunk.EndLine));
        }

        vectorStore.UpsertAsync(pointsByHash.Values.ToList(), CancellationToken.None).GetAwaiter().GetResult();
    }
}
