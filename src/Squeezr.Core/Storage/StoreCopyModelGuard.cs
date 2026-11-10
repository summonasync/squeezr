using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

// A vector answer is a distance, and distances only compare inside one embedding model, so the model
// question is settled from the source store's own rows before a byte moves. The read is meta.db-only on
// purpose: a Qdrant that is down must not be able to wave a known-incompatible copy through the door.
//
// It runs against an open store inside the source lock, so the rows the guard counts are the rows the copy
// moves — an unlocked pre-read would admit a parent pass that re-embeds between the check and the copy and
// ship foreign vectors under a clean check. The destination's own model key is NOT asked here: it comes
// from the lane's config.json, which nothing else writes, so the caller settles it before any lock and
// hands the answer in. A pass that arrives empty-handed while the source turns out to hold vectors is the
// race where asking would put a provider round trip inside the window that freezes the parent — so the
// guard abstains loudly and the next run, which looks first, completes the fill.
public static class StoreCopyModelGuard
{
    public static SourceStoreAudit Audit(MetaStore source, string? targetModelKey)
    {
        var coverage = source.GetSemanticCoverage();
        var files = source.CountAllFiles();
        var embedded = coverage.TotalChunks - coverage.PendingChunks;
        if (embedded == 0)
        {
            return new SourceStoreAudit(files, coverage.TotalChunks, EmbeddedChunks: 0, ForeignChunks: 0);
        }

        if (targetModelKey is null)
        {
            throw new WorkspaceException(
                $"The source store holds {embedded} embedded chunk(s) and this pass did not name the model " +
                "this worktree resolves to before taking the source lock, so no byte was copied. The main " +
                "worktree gained its vectors after this verb looked; re-run it and the model is settled " +
                "first.",
                WorkspaceException.WorktreeModelUnresolvedCode);
        }

        var foreign = source.CountChunksWithOtherModelKey(null, targetModelKey);
        if (foreign == 0)
        {
            return new SourceStoreAudit(files, coverage.TotalChunks, embedded, ForeignChunks: 0);
        }

        throw new WorkspaceException(
            $"The source store holds {foreign} embedded chunk(s) produced by a model other than " +
            $"'{targetModelKey}', which is what this worktree's config resolves to. Their vectors are not " +
            "comparable to the ones this store would search, so no byte was copied. Remedy: re-run with " +
            "'--reindex' to fill this worktree by indexing its own tree, or set this store's embeddings " +
            "model to the one the main worktree was indexed with and re-run.",
            WorkspaceException.WorktreeModelMismatchCode);
    }
}
