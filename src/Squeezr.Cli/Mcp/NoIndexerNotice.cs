using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Cli.Mcp;

// 070/T5 flipped the registration default: `serve` in a client config no longer breeds a watcher, so a session
// can legitimately be the only live process on a store nothing indexes. That is the new silent-staleness shape,
// and the cure is the same one R2 applied to demoted serves: say it. The agent hears it once, in the first
// answer of the session; the operator sees the same standing on stderr at startup.
internal static class NoIndexerNotice
{
    public static string ForAgent(WatchStoreHealth health) => health.State switch
    {
        WatchIndexerState.Wedged =>
            $"no working background indexer on this store: pid {health.OwnerPid} holds the watch claim with a stale " +
            "beat and runs no passes; this session answers queries and refreshes only what a query touches, so the " +
            "index misses disk edits until the watcher restarts ('squeezr fleet stop', then 'squeezr watch')",
        WatchIndexerState.Gone =>
            $"no background indexer is running on this store: the old watch claim named pid {health.OwnerPid}, " +
            "which is gone; this session answers queries and refreshes only what a query touches, so the index " +
            "misses disk edits until a watcher starts ('squeezr watch' in this workspace)",
        _ =>
            "no background indexer is running on this store: this session answers queries and refreshes only what " +
            "a query touches, so the index misses disk edits until a watcher starts ('squeezr watch' in this " +
            "workspace, or 'squeezr mcp doctor' for the exact command)",
    };

    public static string StartupLine(WatchStoreHealth health) => health.State switch
    {
        WatchIndexerState.Wedged =>
            $"warning: watch claim held by wedged pid {health.OwnerPid}: no indexing passes run; " +
            "queries refresh only what they touch",
        WatchIndexerState.Gone =>
            $"warning: no background indexer on this store (the old claim named pid {health.OwnerPid}, now gone): " +
            "queries refresh only what they touch; run 'squeezr watch' to chase edits",
        _ =>
            "warning: no background indexer on this store: queries refresh only what they touch; " +
            "run 'squeezr watch' to chase edits",
    };
}
