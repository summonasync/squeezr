using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

internal sealed record WorktreeFoldOutcome(
    IReadOnlyList<WorktreeDroppedStore> Stores,
    Exception? Halted,
    int NotAttempted);

// The fold of one tree's claims into removal facts, kept out of the command so a half-fold is measurable
// without racing a child process: the loop can stop at a claim the door refuses, and everything the pass
// knows afterwards is in this outcome. Claims already gone from the registry are facts of the run, not lost
// traces; claims after the failure are counted, because silence about them would read as "the fold was two
// rows wide" while the plan was three. The handle is opened before the row goes, the collection is deleted
// after: a store whose config cannot be read is dropped anyway and says so, because the vectors are the
// smaller loss to leave behind than a registry row that names a directory nothing can open. One door for
// both verbs that end a store, so a worktree drop and `drop --store-key` cannot drift into two different
// kinds of death.
internal static class WorktreeFolder
{
    public static async Task<WorktreeFoldOutcome> RunAsync(
        IReadOnlyList<WorktreeDropClaim> claims,
        CancellationToken cancellationToken)
    {
        var stores = new List<WorktreeDroppedStore>();
        Exception? halted = null;
        foreach (var claim in claims)
        {
            try
            {
                var result = await WorkspaceStoreDropper.DropByStoreKeyAsync(claim.StoreKey, cancellationToken);
                stores.Add(new WorktreeDroppedStore(
                    claim.StoreKey,
                    claim.Name,
                    claim.RegisteredRootPath,
                    result.RemovedPaths,
                    result.Collection,
                    result.CollectionState));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceException)
            {
                // A row that vanished between Plan and this loop step is the door's own refusal; losing it
                // up the stack would hide every removal the pass already performed.
                stores.Add(FailedClaim(claim, ex));
                halted = ex;
                break;
            }
        }

        return new WorktreeFoldOutcome(stores, halted, claims.Count - stores.Count);
    }

    // The removal primitive tags both phases, and the tag is what the reader needs: a store-side tag means
    // nothing disappeared, an unverified tag means a store dir did disappear while its row stayed. Raw
    // exception text carries paths this pass did not remove, so the wording stays with the tag; the door's
    // refusal sentences name a store key, never a path, and pass through.
    private static WorktreeDroppedStore FailedClaim(WorktreeDropClaim claim, Exception ex)
    {
        IReadOnlyList<string> gone = ex is WorkspaceStoreRemovalUnverifiedException unverified
            ? unverified.RemovedStoreDirs
            : [];
        var reason = ex switch
        {
            WorkspaceStoreRemovalUnverifiedException => "the store dir was removed, the registry row could not be pruned",
            WorkspaceStoreRemovalException => "a store dir could not be removed",
            UnauthorizedAccessException => "permission denied on the store path",
            WorkspaceException => ex.Message,
            _ => $"{ex.GetType().Name}: the store could not be removed",
        };
        return new WorktreeDroppedStore(
            claim.StoreKey,
            claim.Name,
            claim.RegisteredRootPath,
            gone,
            null,
            WorkspaceStoreDropper.CollectionNotConfigured,
            reason);
    }
}
