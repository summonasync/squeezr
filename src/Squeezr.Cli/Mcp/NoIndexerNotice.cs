using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Mcp;

// 070/T5 flipped the registration default: `serve` in a client config no longer breeds a watcher, so a session
// can legitimately be the only live process on a store nothing indexes. That is the new silent-staleness shape,
// and the cure is the same one R2 applied to demoted serves: say it. The agent hears it once, in the first
// answer of the session; the operator sees the same standing on stderr at startup. 108/T4 adds the supervision
// reading: a store under a live watchd has an owner coming, so "run 'squeezr watch'" would breed exactly the
// duplicate watcher the supervisor exists to prevent — and a wedged claim under supervision gets the one
// remedy the supervisor cannot promise, a manual fleet restart.
internal static class NoIndexerNotice
{
    public static string ForAgent(WatchStoreHealth health) => ForAgent(health, supervised: false);

    public static string ForAgent(WatchStoreHealth health, bool supervised) => health.State switch
    {
        WatchIndexerState.Wedged when supervised =>
            $"watcher on this store is wedged under watchd supervision: pid {health.OwnerPid} holds the watch " +
            "claim with a stale beat and runs no passes, and the supervisor may be blocked from restarting it; " +
            "this session answers queries and refreshes only what a query touches, so the index misses disk " +
            "edits until the store is restarted by hand ('squeezr fleet restart')",
        WatchIndexerState.Wedged =>
            $"no working background indexer on this store: pid {health.OwnerPid} holds the watch claim with a stale " +
            "beat and runs no passes; this session answers queries and refreshes only what a query touches, so the " +
            "index misses disk edits until the watcher restarts ('squeezr fleet stop', then 'squeezr watch')",
        _ when supervised && !health.IndexerRunning =>
            "no background indexer is running on this store yet: the store is under watchd supervision and the " +
            "supervisor will bring the watcher up itself, so a manual 'squeezr watch' is not needed; until then " +
            "this session answers queries and refreshes only what a query touches, so the index may briefly miss " +
            "disk edits",
        WatchIndexerState.Gone =>
            $"no background indexer is running on this store: the old watch claim named pid {health.OwnerPid}, " +
            "which is gone; this session answers queries and refreshes only what a query touches, so the index " +
            "misses disk edits until a watcher starts ('squeezr watch' in this workspace)",
        _ =>
            "no background indexer is running on this store: this session answers queries and refreshes only what " +
            "a query touches, so the index misses disk edits until a watcher starts ('squeezr watch' in this " +
            "workspace, or 'squeezr mcp doctor' for the exact command)",
    };

    public static string StartupLine(WatchStoreHealth health) => StartupLine(health, supervised: false);

    public static string StartupLine(WatchStoreHealth health, bool supervised) => health.State switch
    {
        WatchIndexerState.Wedged when supervised =>
            $"warning: watch claim held by wedged pid {health.OwnerPid}, store under watchd supervision: " +
            "no indexing passes run; if the supervisor does not clear it, run 'squeezr fleet restart'",
        WatchIndexerState.Wedged =>
            $"warning: watch claim held by wedged pid {health.OwnerPid}: no indexing passes run; " +
            "queries refresh only what they touch",
        _ when supervised && !health.IndexerRunning =>
            "warning: no background indexer on this store: watchd supervises it and will start the watcher; " +
            "a manual 'squeezr watch' is not needed",
        WatchIndexerState.Gone =>
            $"warning: no background indexer on this store (the old claim named pid {health.OwnerPid}, now gone): " +
            "queries refresh only what they touch; run 'squeezr watch' to chase edits",
        _ =>
            "warning: no background indexer on this store: queries refresh only what they touch; " +
            "run 'squeezr watch' to chase edits",
    };

    // The liveness test is the supervisor's own claim: acquire-or-refuse is what WatchdLock offers, and a refusal
    // names a live watchd without polling any process table. A serve probing a dead supervisor holds the lock for
    // microseconds and drops it — once per startup, and only on a store that already has no indexer.
    public static bool WatchdSupervises(string workspaceRoot)
    {
        try
        {
            if (WorkspaceRegistry.FindByRootPath(workspaceRoot) is not { } entry)
            {
                return false;
            }

            using var claim = new WatchdLock();
            if (claim.TryAcquire().Acquired)
            {
                return false;
            }

            return new SupervisionState().DesireOf(entry.StoreKey) == WatchDesire.Running;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
