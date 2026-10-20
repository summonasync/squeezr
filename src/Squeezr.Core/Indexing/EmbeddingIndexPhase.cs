using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

public sealed record EmbeddingPhaseResult(int Embedded, bool Completed = true);

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
        IIndexProgress? progress = null,
        int? maxChunks = null)
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
        var totalPending = store.CountPendingChunks(repoDoc.Id, modelKey);
        if (totalPending == 0)
        {
            return new EmbeddingPhaseResult(0);
        }

        var rekeyed = store.CountChunksWithOtherModelKey(repoDoc.Id, modelKey);
        if (rekeyed > 0)
        {
            warning(
                $"model key of repo '{repoDoc.Name}' changed: {rekeyed} chunk(s) carry vectors from another " +
                "embedding model and are re-embedded once");
        }

        progress?.PhaseStarted(repoDoc.Name, IndexPhase.Embeddings, totalPending);

        var remaining = maxChunks;
        while (true)
        {
            var pending = store.ListPendingChunks(repoDoc.Id, modelKey, PendingQueryLimit);
            if (pending.Count == 0)
            {
                break;
            }

            var vectorsByHash = new Dictionary<string, float[]>(StringComparer.Ordinal);
            for (var i = 0; i < pending.Count; i += EmbedBatchSize)
            {
                var count = Math.Min(EmbedBatchSize, pending.Count - i);
                if (remaining is { } left)
                {
                    if (left == 0)
                    {
                        return new EmbeddingPhaseResult(embedded);
                    }

                    count = Math.Min(count, left);
                }

                var batch = new List<ChunkDocument>(count);
                for (var j = i; j < i + count; j++)
                {
                    batch.Add(pending[j]);
                }

                try
                {
                    EmbedNewHashes(batch, vectorsByHash, provider);
                    UpsertBatch(batch, vectorsByHash, vectorStore);
                    store.MarkChunksEmbedded(batch.Select(c => c.Id), modelKey);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException and not VectorDimensionMismatchException)
                {
                    warning($"embeddings skipped for repo '{repoDoc.Name}': {ex.Message}");
                    return new EmbeddingPhaseResult(embedded, Completed: false);
                }

                embedded += batch.Count;
                if (remaining is not null)
                {
                    remaining -= batch.Count;
                }

                if (progress is not null)
                {
                    foreach (var chunk in batch)
                    {
                        progress.ItemProcessed(repoDoc.Name, chunk.ChunkHash);
                    }
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
        IReadOnlyList<ChunkDocument> batch,
        Dictionary<string, float[]> vectorsByHash,
        IEmbeddingProvider provider)
    {
        var newHashes = new List<string>();
        var texts = new List<string>();
        foreach (var chunk in batch)
        {
            if (vectorsByHash.ContainsKey(chunk.ChunkHash))
            {
                continue;
            }

            vectorsByHash[chunk.ChunkHash] = [];
            newHashes.Add(chunk.ChunkHash);
            texts.Add(chunk.Text);
        }

        if (texts.Count == 0)
        {
            return;
        }

        var vectors = provider.EmbedAsync(texts, EmbeddingUsage.Document, CancellationToken.None).GetAwaiter().GetResult();
        for (var j = 0; j < texts.Count; j++)
        {
            vectorsByHash[newHashes[j]] = vectors[j];
        }
    }

    private static void UpsertBatch(
        IReadOnlyList<ChunkDocument> batch,
        IReadOnlyDictionary<string, float[]> vectorsByHash,
        IVectorStore vectorStore)
    {
        var pointsByHash = new Dictionary<string, VectorPoint>(StringComparer.Ordinal);
        foreach (var chunk in batch)
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
