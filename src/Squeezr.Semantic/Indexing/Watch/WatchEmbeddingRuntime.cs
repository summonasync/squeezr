using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Semantic.Indexing;

namespace Squeezr.Semantic.Indexing.Watch;

// Built once per serve process and handed to the watcher as an already-resolved task: provider
// construction may load a model or probe a resolver endpoint, so it must never sit on the serve
// startup path. The resolution rides along because the pass derives its model key from the config
// provider, which may still say "auto" — an unresolved auto would make every pass throw. A null
// result means the drain is off for this serve — passes stay embed-free.
// The record is also the implementing half of Core's pass and probe seams, so a caller that built
// one runtime hands the index-pass lifecycle and the worktree probe over as the same object.
public sealed record WatchEmbeddingRuntime(
    IEmbeddingProvider Provider,
    IVectorStore Store,
    EmbeddingProviderResolution? Resolution) : IIndexVectorPhase, IWorktreeVectorProbe
{
    private EmbeddingExecutor? _executor;

    public string ProviderName => Resolution?.Provider ?? "unknown";

    public EmbeddingExecutor Executor => _executor ??= Provider is IEmbeddingExecutorEvidence evidenced
        ? ProbeEvidenced(evidenced)
        : new EmbeddingExecutor("unknown", $"provider '{ProviderName}' offers no executor evidence");

    string IIndexVectorPhase.ExecutorDevice => Executor.Device;

    public void DropCollection(List<string> warnings)
    {
        Store.DeleteCollectionAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Preflight(List<string> warnings)
    {
        try
        {
            if (!Store.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return;
            }

            Store.EnsureCollectionAsync(Provider.Dimensions, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (VectorDimensionMismatchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add($"vector preflight skipped: {ex.Message}");
        }
    }

    public void ReplayJournal(MetaStore store, List<string> warnings)
    {
        var entries = store.ListPendingReaps();
        if (entries.Count == 0)
        {
            return;
        }

        var owed = new List<uint>();
        var recycled = new List<uint>();
        foreach (var entry in entries)
        {
            var fileId = (uint)entry.FileId;
            var live = store.FindFileById(fileId);
            if (live is null
                || (entry.Path is not null && live.RepoId == entry.RepoId &&
                    string.Equals(live.Path, entry.Path, StringComparison.Ordinal)))
            {
                owed.Add(fileId);
            }
            else
            {
                recycled.Add(fileId);
            }
        }

        if (recycled.Count > 0)
        {
            store.ClearPendingReaps(recycled);
            warnings.Add(
                $"{recycled.Count} deferred point cleanup(s) dropped: those file ids now name different files — " +
                "run 'squeezr vectors reconcile --apply' to remove the points their deletions left behind");
        }

        if (owed.Count == 0)
        {
            return;
        }

        try
        {
            if (!Store.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return;
            }

            Store.DeleteByFileIdsAsync(owed, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add($"deferred point cleanup for {owed.Count} file(s) failed again: {ex.Message}");
            return;
        }

        var requeued = owed.Sum(store.MarkFileChunksPending);
        store.ClearPendingReaps(owed);
        warnings.Add(
            $"replayed deferred point cleanup for {owed.Count} file(s); {requeued} chunk(s) re-queued for embedding");
    }

    public int RetirePoints(MetaStore store, IReadOnlyList<PendingReapTarget> files, List<string> warnings, SecretGuardIngestion guard)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        var fileIds = files.Select(f => f.FileId).ToList();
        try
        {
            if (!Store.IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return VectorReapJournal.Defer(store, files, warnings, "vector store unavailable", guard);
            }

            Store.DeleteByFileIdsAsync(fileIds, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            return VectorReapJournal.Defer(
                store, files, warnings, $"failed to remove vectors for {fileIds.Count} file(s): {ex.Message}", guard);
        }

        return 0;
    }

    public (int Embedded, bool Completed) DrainRepo(
        RepoDocument repo, MetaStore store, SqueezrConfig config, List<string> warnings, IIndexProgress? progress, int? budget)
    {
        var phase = EmbeddingIndexPhase.Run(
            repo, store, EmbeddingModelKey.From(ResolvedEmbeddings(config)), Provider, Store, warnings.Add, progress, budget);
        return (phase.Embedded, phase.Completed);
    }

    public void DrainFile(RepoDocument repo, MetaStore store, SqueezrConfig config, List<string> warnings)
    {
        EmbeddingIndexPhase.Run(
            repo, store, EmbeddingModelKey.From(ResolvedEmbeddings(config)), Provider, Store, warnings.Add);
    }

    VectorClaimCheck IWorktreeVectorProbe.Measure(IReadOnlyList<string> chunkHashes, CancellationToken ct)
    {
        if (chunkHashes.Count > ReconcileProbeBudget.FullClaimIdCeiling)
        {
            return new VectorClaimCheck(
                null, null,
                $"this store names {chunkHashes.Count} chunk hash(es), past the "
                + $"{ReconcileProbeBudget.FullClaimIdCeiling} ids one reconcile pass will ask a collection "
                + "for",
                Probed: chunkHashes.Count);
        }

        try
        {
            var missing = Store.MissingPointIdsAsync(ChunkPointId.Ids(chunkHashes), ct).GetAwaiter().GetResult();
            var points = Store.CountAsync(ct).GetAwaiter().GetResult();
            return new VectorClaimCheck(points, missing.Count, null, Probed: chunkHashes.Count);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new VectorClaimCheck(null, null, ex.Message, Probed: chunkHashes.Count);
        }
    }

    VectorClaimCheck IWorktreeVectorProbe.Attest(IReadOnlyList<string> chunkHashes, CancellationToken ct)
    {
        var ids = ChunkPointId.Sample(chunkHashes, ReconcileProbeBudget.AttestationSampleIds);
        try
        {
            var missing = Store.MissingPointIdsAsync(ids, ct).GetAwaiter().GetResult();
            return new VectorClaimCheck(null, missing.Count, null, Probed: ids.Count);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new VectorClaimCheck(null, null, ex.Message, Probed: ids.Count);
        }
    }

    // The drain receives the config as Core holds it, so a provider resolution a watcher or session
    // start already paid for overrides the config's unresolved "auto".
    private EmbeddingsConfig ResolvedEmbeddings(SqueezrConfig config)
    {
        var embeddings = SemanticConfig.Bind(config).Embeddings;
        return Resolution is null ? embeddings : embeddings with { Provider = Resolution.Provider };
    }

    // The seam owns the failure: an evidence source is expected to degrade on its own, but a future one
    // that throws must cost the verdict, never the pass that reads it.
    private static EmbeddingExecutor ProbeEvidenced(IEmbeddingExecutorEvidence evidenced)
    {
        try
        {
            return evidenced.ProbeExecutor();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new EmbeddingExecutor("unknown", $"executor evidence failed: {ex.Message}");
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;
}
